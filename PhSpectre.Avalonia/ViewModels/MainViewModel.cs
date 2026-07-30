using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Material.Icons;
using PhSpectre;
using PhSpectre.Avalonia.Models;
using PhSpectre.Avalonia.Services;
using PhSpectre.Rendering;
using PhSpectre.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace PhSpectre.Avalonia.ViewModels;

/// <summary>
/// Single-photo mobile flow: pick one image from the gallery, generate its
/// palette, save it. No file browser and no separate settings window — Android
/// is single-activity, so settings live in an in-view panel instead.
/// </summary>
public partial class MainViewModel : ViewModelBase
{
    public SettingsViewModel Settings { get; } = new();

    [ObservableProperty] private Bitmap? _originalBitmap;
    [ObservableProperty] private Bitmap? _paletteBitmap;
    [ObservableProperty] private bool    _isGenerating;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private string? _fileInfoText;
    [ObservableProperty] private string? _outputSizeText;
    [ObservableProperty] private bool    _isSettingsOpen;
    [ObservableProperty] private bool    _isSaved;
    [ObservableProperty] private bool    _isRegenerateAvailable;

    // Collage mode — CollageItems is the tray: each picked photo is copied to a stable temp
    // path (same technique as _currentSourcePath below) and wrapped in a FileEntry, the same
    // model Desktop's tray uses. Order matters twice over: it's both the row-packing order
    // and (for the first item) the EXIF source, so there's no separate "main photo" control.
    [ObservableProperty] private bool _isCollageMode;
    [ObservableProperty] private ObservableCollection<FileEntry> _collageItems = [];

    // Cheap "just the layout" preview (no palette/card) that updates live as the tray is
    // edited — PaletteBitmap only ever holds a real, expensive full render, triggered
    // explicitly via Generate/Regenerate. DisplayedCollageBitmap is what the view actually
    // shows: the full render once one exists, falling back to the live preview otherwise.
    [ObservableProperty] private Bitmap? _collagePreviewBitmap;
    public Bitmap? DisplayedCollageBitmap => PaletteBitmap ?? CollagePreviewBitmap;

    // The collage Regenerate button reads "Generate" until the first real render exists,
    // then "Regenerate" from then on — a materially different action the first time.
    public string RegenerateButtonLabel => IsCollageMode && PaletteBitmap == null ? "Generate" : "Regenerate";

    public Func<Task<(Stream Stream, string FileName)?>>?              PickImageAsync  { get; set; }
    public Func<Task<IReadOnlyList<(Stream Stream, string FileName)>>>? PickImagesAsync { get; set; }
    public Func<string, string, Task<bool>>?                            SavePngAsync    { get; set; }

    private string?                  _lastTempPng;
    private string?                  _lastFileName;
    private string                   _lastExtension = ".png";
    private CancellationTokenSource? _renderCts;

    // Kept on disk across renders (unlike the old single-shot tmpIn) so a settings/metadata
    // change can be re-rendered without asking the user to pick the same photo again.
    private string? _currentSourcePath;
    private string? _collageExifSourcePath;
    private bool     _suspendTrayNotifications;

    public MainViewModel()
    {
        // Mirrors Desktop's MainWindowViewModel: a settings change never re-renders on its
        // own — it only surfaces the Regenerate button. Actual re-render is user-triggered.
        Settings.PropertyChanged += OnSettingsPropertyChanged;

        // Mirrors OnSelectedFileChanged/IngestNewPhotoAsync's "picking new content re-renders
        // immediately" rule: editing the tray (add/remove/reorder) is a content change, not a
        // settings tweak, so it renders right away rather than waiting on Regenerate.
        // Editing the tray does update the on-screen collage instantly — but only the cheap
        // layout preview (RenderCollagePreviewAsync: decode + arrange, no palette). The
        // expensive part (pooled k-means over all sources) never runs automatically; it's
        // gated behind Generate/Regenerate, same as any other settings change, since running
        // it on every single add made the tray feel like it could only take one photo at a time.
        // _suspendTrayNotifications additionally batches a multi-photo pick into one update —
        // without it, adding N photos fires N of these in a row, each re-decoding every photo
        // added so far for its own preview, which is what actually made adds feel serialized.
        _collageItems.CollectionChanged += (_, _) =>
        {
            if (!_suspendTrayNotifications) OnCollageTrayChanged();
        };
    }

    private void OnCollageTrayChanged()
    {
        for (int i = 0; i < CollageItems.Count; i++)
            CollageItems[i].IsCollageExifSource = i == 0;

        RegenerateCommand.NotifyCanExecuteChanged();
        if (!IsCollageMode) return;

        // The first tray item's EXIF represents the whole collage and feeds the editable
        // "Edit metadata" fields in Settings, same as picking a single photo does — only
        // reload when that first item's identity actually changes, so reordering the rest
        // of the tray doesn't clobber an in-progress edit.
        var newExifSource = CollageItems.Count > 0 ? CollageItems[0].FullPath : null;
        if (newExifSource != _collageExifSourcePath)
        {
            _collageExifSourcePath = newExifSource;
            Settings.LoadMetadataFields(newExifSource != null ? PaletteImageRenderer.ReadMetadata(newExifSource) : null);
        }

        // A previously-generated full render no longer reflects the tray as soon as it
        // changes — clear it so DisplayedCollageBitmap falls back to the live preview.
        PaletteBitmap  = null;
        _lastTempPng   = null;
        OutputSizeText = null;
        SavePngCommand.NotifyCanExecuteChanged();

        if (CollageItems.Count == 0)
        {
            CollagePreviewBitmap = null;
            FileInfoText = null;
            IsRegenerateAvailable = false;
        }
        else
        {
            FileInfoText = $"Collage · {CollageItems.Count} photos";
            IsRegenerateAvailable = true;
            _ = RenderCollagePreviewAsync();
        }
    }

    private bool HasActiveContent => IsCollageMode ? CollageItems.Count > 0 : _currentSourcePath != null;

    private void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Settings.LoadMetadataFields (called right after a new photo is ingested) bulk-assigns
        // the metadata text fields, which would otherwise look like a user edit and pop up
        // Regenerate for a photo that's already about to render fresh. Also guards against
        // Settings.ShowCollageOptions itself (flipped by CreateCollage) falsely surfacing
        // Regenerate before the tray has anything in it.
        if (!HasActiveContent || IsGenerating || Settings.IsBulkLoading) return;
        IsRegenerateAvailable = true;
    }

    private bool CanRegenerate() => IsRegenerateAvailable && HasActiveContent && !IsGenerating;

    [RelayCommand(CanExecute = nameof(CanRegenerate))]
    private async Task Regenerate()
    {
        if (IsCollageMode)
            await GenerateCollagePaletteAsync();
        else
            await RenderCurrentAsync();
        IsRegenerateAvailable = false;
    }

    // Photos above the chosen cap (long edge) get downscaled before processing — running
    // the full extract/render/PNG-encode pipeline at native mobile-camera resolution is
    // what makes it take minutes. Settings.WorkingQuality (Settings screen → "Working
    // size") picks which cap: faster & smaller, or slower & closer to desktop detail.
    private int MaxWorkingDimension => Settings.WorkingMaxDimension;

    public bool ShowPlaceholder => !IsGenerating && DisplayedCollageBitmap == null && string.IsNullOrEmpty(ErrorMessage);
    public bool HasResult       => DisplayedCollageBitmap != null;

    // Hidden in collage mode — picking a single replacement photo there would leave
    // IsCollageMode/CollageItems and the single-photo bitmaps in an inconsistent mix.
    public bool ShowPickPhotoToolbarButton => HasResult && !IsCollageMode;

    // The two start buttons ("Pick a Photo" / "Create Collage") only make sense before any
    // flow has been entered — once collage mode is on, the tray below is the only relevant
    // action and these would otherwise linger, with "Pick a Photo" actively breaking state
    // if tapped mid-collage.
    public bool ShowStartButtons => !HasResult && !IsCollageMode;

    // The result card's dashed empty-state box is shared by both modes — its label and
    // tap action switch with IsCollageMode rather than needing two overlapping buttons.
    public string EmptyStateText => IsCollageMode ? "Add photos to the collage" : "No photo selected";

    [RelayCommand]
    private async Task PickForEmptyState()
    {
        if (IsCollageMode) await AddPhotosToCollageAsync();
        else await PickPhotoAsync();
    }

    public MaterialIconKind SaveIconKind  => IsSaved ? MaterialIconKind.Check : MaterialIconKind.ContentSaveOutline;
    public string           SaveButtonText => IsSaved ? "Saved!" : "Save to Gallery";

    private DispatcherTimer? _saveConfirmTimer;

    partial void OnIsGeneratingChanged(bool value)      { OnPropertyChanged(nameof(ShowPlaceholder)); RegenerateCommand.NotifyCanExecuteChanged(); }
    partial void OnIsRegenerateAvailableChanged(bool value) => RegenerateCommand.NotifyCanExecuteChanged();
    partial void OnPaletteBitmapChanged(Bitmap? value)
    {
        OnPropertyChanged(nameof(ShowPlaceholder));
        OnPropertyChanged(nameof(HasResult));
        OnPropertyChanged(nameof(ShowPickPhotoToolbarButton));
        OnPropertyChanged(nameof(ShowStartButtons));
        OnPropertyChanged(nameof(DisplayedCollageBitmap));
        OnPropertyChanged(nameof(RegenerateButtonLabel));
    }
    partial void OnCollagePreviewBitmapChanged(Bitmap? value)
    {
        OnPropertyChanged(nameof(ShowPlaceholder));
        OnPropertyChanged(nameof(HasResult));
        OnPropertyChanged(nameof(ShowStartButtons));
        OnPropertyChanged(nameof(DisplayedCollageBitmap));
    }
    partial void OnErrorMessageChanged(string? value)   => OnPropertyChanged(nameof(ShowPlaceholder));
    partial void OnIsSavedChanged(bool value)
    {
        OnPropertyChanged(nameof(SaveIconKind));
        OnPropertyChanged(nameof(SaveButtonText));
    }

    partial void OnIsCollageModeChanged(bool value)
    {
        RegenerateCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(ShowPlaceholder));
        OnPropertyChanged(nameof(HasResult));
        OnPropertyChanged(nameof(ShowPickPhotoToolbarButton));
        OnPropertyChanged(nameof(ShowStartButtons));
        OnPropertyChanged(nameof(EmptyStateText));
        OnPropertyChanged(nameof(RegenerateButtonLabel));
    }

    [RelayCommand]
    private void ToggleSettings() => IsSettingsOpen = !IsSettingsOpen;

    [RelayCommand]
    private async Task PickPhotoAsync()
    {
        // Not available in collage mode — picking a single replacement photo there would
        // leave IsCollageMode/CollageItems and the single-photo bitmaps in an inconsistent
        // mix (mirrors ShowPickPhotoToolbarButton, which hides the toolbar entry point).
        if (IsCollageMode || PickImageAsync == null) return;
        var picked = await PickImageAsync();
        if (picked == null) return;

        await IngestNewPhotoAsync(picked.Value.Stream, picked.Value.FileName);
    }

    // ── Collage mode ─────────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task CreateCollageAsync()
    {
        IsCollageMode = true;
        Settings.ShowCollageOptions = true;
        OriginalBitmap = null;
        PaletteBitmap  = null;
        CollagePreviewBitmap = null;
        FileInfoText   = null;
        OutputSizeText = null;
        _lastTempPng   = null;
        _collageExifSourcePath = null;
        IsRegenerateAvailable = false;
        SavePngCommand.NotifyCanExecuteChanged();
        await AddPhotosToCollageAsync();
    }

    [RelayCommand]
    private async Task AddPhotosToCollageAsync()
    {
        if (PickImagesAsync == null) return;
        var picked = await PickImagesAsync();
        if (picked == null || picked.Count == 0) return;

        // Suspended for the whole batch — otherwise each Add fires its own tray-changed
        // preview render, which re-decodes every photo added *so far* on every single one,
        // turning an N-photo pick into an O(N²) sequence of renders that looked like photos
        // were only ever added one at a time.
        _suspendTrayNotifications = true;
        try
        {
            foreach (var (stream, fileName) in picked)
            {
                var tempPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}_{fileName}");
                await using (var fs = File.Create(tempPath))
                    await stream.CopyToAsync(fs);
                await stream.DisposeAsync();

                var entry = new FileEntry(fileName, tempPath);
                try
                {
                    var bmp = await Task.Run(() =>
                    {
                        using var ms = ImageLoader.LoadThumbnail(tempPath, 120, 120);
                        return new Bitmap(ms);
                    });
                    entry.ThumbnailBitmap = bmp;
                }
                catch { /* thumbnail is best-effort; the tray still works without it */ }

                CollageItems.Add(entry);
            }
        }
        finally
        {
            _suspendTrayNotifications = false;
        }

        OnCollageTrayChanged();
    }

    [RelayCommand]
    private void ExitCollageMode()
    {
        IsCollageMode = false;
        Settings.ShowCollageOptions = false;
        foreach (var item in CollageItems)
        {
            try { File.Delete(item.FullPath); } catch { /* best effort */ }
        }
        CollageItems.Clear();
        IsRegenerateAvailable = false;

        OriginalBitmap = null;
        PaletteBitmap  = null;
        CollagePreviewBitmap = null;
        ErrorMessage   = null;
        FileInfoText   = null;
        OutputSizeText = null;
        _lastTempPng   = null;
        SavePngCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void RemoveFromCollage(FileEntry entry)
    {
        CollageItems.Remove(entry);
        try { File.Delete(entry.FullPath); } catch { /* best effort */ }
    }

    [RelayCommand]
    private void MoveCollageItemUp(FileEntry entry)
    {
        int i = CollageItems.IndexOf(entry);
        if (i > 0) CollageItems.Move(i, i - 1);
    }

    [RelayCommand]
    private void MoveCollageItemDown(FileEntry entry)
    {
        int i = CollageItems.IndexOf(entry);
        if (i >= 0 && i < CollageItems.Count - 1) CollageItems.Move(i, i + 1);
    }

    // Cheap "just the layout" preview — no palette extraction, no card, just decode +
    // arrange with the gutter. Runs on every tray edit (see the CollectionChanged handler
    // above) so the collage always looks assembled immediately; shares _renderCts with the
    // full render so whichever starts most recently (a tray edit or a Generate tap) wins.
    private async Task RenderCollagePreviewAsync()
    {
        _renderCts?.Cancel();
        _renderCts = new CancellationTokenSource();
        var token = _renderCts.Token;

        // Whatever this cancelled (a stale single-photo/full-collage render) leaves
        // IsGenerating stuck true — its own finally block only clears it when its token
        // *wasn't* cancelled, precisely to avoid a cancelled task clobbering a newer one's
        // "Generating…" state. But this preview never sets IsGenerating itself, so nothing
        // would ever clear it otherwise.
        IsGenerating = false;

        if (CollageItems.Count == 0) return;

        try
        {
            var sourcePaths = CollageItems.Select(i => i.FullPath).ToList();
            var exportSettings = PaletteExportSettings.SnapshotFrom(Settings);
            using var composed = await PaletteExportService.ComposeCollagePreviewAsync(sourcePaths, exportSettings, token);

            token.ThrowIfCancellationRequested();

            using var ms = new MemoryStream();
            composed.SaveAsPng(ms);
            ms.Position = 0;
            CollagePreviewBitmap = new Bitmap(ms);
        }
        catch (OperationCanceledException) { }
        catch { /* best-effort live preview — Generate will surface a real error if something's actually wrong */ }
    }

    private async Task GenerateCollagePaletteAsync()
    {
        _renderCts?.Cancel();
        _renderCts = new CancellationTokenSource();
        var token = _renderCts.Token;

        // OriginalBitmap has no single-photo equivalent for a collage — the tray thumbnails
        // already show the sources, so it's just left blank. PaletteBitmap/CollagePreviewBitmap
        // are deliberately left alone here — clearing them would blank the preview for the
        // whole duration of the render instead of just showing "Generating…" over what's there.
        OriginalBitmap = null;
        ErrorMessage   = null;
        _lastTempPng   = null;
        _saveConfirmTimer?.Stop();
        IsSaved = false;
        SavePngCommand.NotifyCanExecuteChanged();

        if (CollageItems.Count == 0) return;

        IsGenerating = true;
        try
        {
            var sourcePaths = CollageItems.Select(i => i.FullPath).ToList();
            FileInfoText = $"Collage · {sourcePaths.Count} photos";

            _lastExtension = Settings.FileExtension;
            var tmpOut = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}{_lastExtension}");
            var exportSettings = PaletteExportSettings.SnapshotFrom(Settings);
            // ExportCollageAsync already offloads its own work to the thread pool, so this
            // doesn't need (and shouldn't double up) an outer Task.Run.
            await PaletteExportService.ExportCollageAsync(sourcePaths, tmpOut, exportSettings, token,
                metadataOverride: Settings.BuildMetadataOverride());

            token.ThrowIfCancellationRequested();

            _lastTempPng    = tmpOut;
            var sizeBytes   = new FileInfo(tmpOut).Length;
            var formatLabel = exportSettings.Format == OutputFormat.Jpeg ? "JPG" : "PNG";
            OutputSizeText  = $"{sizeBytes / 1_048_576.0:F1} MB {formatLabel}";
            PaletteBitmap   = new Bitmap(tmpOut);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            if (!token.IsCancellationRequested)
            {
                IsGenerating = false;
                SavePngCommand.NotifyCanExecuteChanged();
            }
        }
    }

    [RelayCommand(CanExecute = nameof(CanSavePng))]
    private async Task SavePng()
    {
        if (SavePngAsync == null || _lastTempPng == null) return;
        string suggested = IsCollageMode
            ? "collage_palette" + _lastExtension
            : _lastFileName != null
                ? Path.GetFileNameWithoutExtension(_lastFileName) + "_palette" + _lastExtension
                : "palette" + _lastExtension;
        var ok = await SavePngAsync(suggested, _lastTempPng);
        if (!ok) return;

        _saveConfirmTimer?.Stop();
        IsSaved = true;
        _saveConfirmTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _saveConfirmTimer.Tick += (_, _) =>
        {
            _saveConfirmTimer!.Stop();
            IsSaved = false;
        };
        _saveConfirmTimer.Start();
    }

    private bool CanSavePng() => PaletteBitmap != null && !IsGenerating;

    // Copies a newly picked photo to a stable temp path (replacing whatever was there before)
    // and renders it. Unlike a one-shot render, this path survives afterwards — see
    // _currentSourcePath — so a later settings/metadata change can re-render without asking
    // the user to pick the same photo again.
    private async Task IngestNewPhotoAsync(Stream sourceStream, string fileName)
    {
        _renderCts?.Cancel();

        var oldSourcePath = _currentSourcePath;
        var newSourcePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}_{fileName}");
        await using (var fs = File.Create(newSourcePath))
            await sourceStream.CopyToAsync(fs);
        await sourceStream.DisposeAsync();

        _currentSourcePath = newSourcePath;
        _lastFileName = fileName;

        if (oldSourcePath != null)
        {
            try { File.Delete(oldSourcePath); } catch { /* best effort */ }
        }

        Settings.LoadMetadataFields(PaletteImageRenderer.ReadMetadata(_currentSourcePath));
        IsRegenerateAvailable = false; // the render below is already current — nothing to regenerate yet

        await RenderCurrentAsync();
    }

    private async Task RenderCurrentAsync()
    {
        if (_currentSourcePath == null) return;
        var sourcePath = _currentSourcePath;

        _renderCts?.Cancel();
        _renderCts = new CancellationTokenSource();
        var token = _renderCts.Token;

        OriginalBitmap = null;
        PaletteBitmap  = null;
        ErrorMessage   = null;
        FileInfoText   = null;
        OutputSizeText = null;
        _lastTempPng   = null;
        _saveConfirmTimer?.Stop();
        IsSaved = false;
        SavePngCommand.NotifyCanExecuteChanged();

        IsGenerating = true;
        Image<Rgb24>? working = null;
        try
        {
            // Re-decoded from disk every time (not cached across renders) so a Working size
            // change picks up its new cap immediately instead of reusing an old downscale.
            working = await Task.Run(() => ImageLoader.LoadWorkingCopy(sourcePath, MaxWorkingDimension), token);
            token.ThrowIfCancellationRequested();

            using var previewMs = await Task.Run(() => ImageLoader.ToJpegStream(working), token);
            token.ThrowIfCancellationRequested();
            OriginalBitmap = new Bitmap(previewMs);

            var ps = OriginalBitmap.PixelSize;
            FileInfoText = $"{_lastFileName}  ·  {ps.Width}×{ps.Height}";

            var palette = await new PaletteExtractor().ExtractAsync(working, Settings.Colors, Settings.SamplingMode, token);
            token.ThrowIfCancellationRequested();

            _lastExtension = Settings.FileExtension;
            var tmpOut = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}{_lastExtension}");
            var snap = (Settings.ShowHex, Settings.HexBelow, Settings.MetaVerbosity,
                        Settings.MetaStyle, Settings.Theme, Settings.ShowSwatches,
                        Settings.OutputFormat, Settings.ExportPreset,
                        Settings.LabelScale, Settings.SwatchScale, Settings.ShowPercent,
                        Settings.SwatchShape, Settings.SortOrder, Settings.CompositionGuide,
                        CustomBackground: Settings.UseCustomBackground
                            && Color.TryParseHex(Settings.CustomBackgroundHex, out var bg) ? bg : (Color?)null);
            var metadataOverride = Settings.BuildMetadataOverride();
            await Task.Run(() => PaletteImageRenderer.Render(
                working, palette, tmpOut,
                showHex:          snap.ShowHex,
                metaVerbosity:    snap.MetaVerbosity,
                metaStyle:        snap.MetaStyle,
                theme:            snap.Theme,
                hexBelow:         snap.HexBelow,
                showSwatches:     snap.ShowSwatches,
                downscale:        1, // resolution already capped via WorkingQuality above
                format:           snap.OutputFormat,
                exportPreset:     snap.ExportPreset,
                metadataOverride: metadataOverride,
                labelScale:       snap.LabelScale,
                swatchScale:      snap.SwatchScale,
                showPercent:      snap.ShowPercent,
                swatchShape:      snap.SwatchShape,
                sortOrder:        snap.SortOrder,
                customBackground: snap.CustomBackground,
                compositionGuide: snap.CompositionGuide), token);

            token.ThrowIfCancellationRequested();

            _lastTempPng   = tmpOut;
            var sizeBytes  = new FileInfo(tmpOut).Length;
            var formatLabel = snap.OutputFormat == OutputFormat.Jpeg ? "JPG" : "PNG";
            OutputSizeText = $"{sizeBytes / 1_048_576.0:F1} MB {formatLabel}";
            PaletteBitmap  = new Bitmap(tmpOut);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            working?.Dispose();
            if (!token.IsCancellationRequested)
            {
                IsGenerating = false;
                SavePngCommand.NotifyCanExecuteChanged();
            }
        }
    }
}
