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
using PhSpectre.Models;
using PhSpectre.Recipes;
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

    // Session-local "recent photos" — mobile has no folder browser (no Android SAF
    // integration in this pass), so this is the practical middle ground per the base spec's
    // "browser instead of Pick a Photo into the void" ask: every single photo ingested this
    // session (not collage adds) stays reachable via its own stable temp copy, most-recent
    // first, capped so temp disk usage doesn't grow unbounded.
    [ObservableProperty] private ObservableCollection<FileEntry> _recentPhotos = [];
    private const int MaxRecentPhotos = 8;
    public bool HasRecentPhotos => RecentPhotos.Count > 0;

    // Cheap "just the layout" preview (no palette/card) that updates live as the tray is
    // edited — PaletteBitmap only ever holds a real, expensive full render, triggered
    // explicitly via Generate/Regenerate. DisplayedCollageBitmap is what the view actually
    // shows: the full render once one exists, falling back to the live preview otherwise.
    [ObservableProperty] private Bitmap? _collagePreviewBitmap;
    public Bitmap? DisplayedCollageBitmap => PaletteBitmap ?? CollagePreviewBitmap;

    // Compare-to-original — single-photo modes only (collage has no single "original" to
    // fall back to), toggled by a tap on the result frame. Mirrors MainWindowViewModel.
    [ObservableProperty] private bool _isComparingToOriginal;
    public Bitmap? DisplayedResultBitmap =>
        IsComparingToOriginal && !IsCollageMode && OriginalBitmap != null ? OriginalBitmap : DisplayedCollageBitmap;
    public bool CanCompareToOriginal => !IsCollageMode && OriginalBitmap != null;

    partial void OnIsComparingToOriginalChanged(bool value) => OnPropertyChanged(nameof(DisplayedResultBitmap));

    [RelayCommand]
    private void ToggleCompareToOriginal() => IsComparingToOriginal = !IsComparingToOriginal;

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

    // Single-slot memoization, mirrors MainWindowViewModel — reuse the last successful
    // render instead of recomputing (skips k-means again) when the (photo/tray, settings —
    // ExportMode included) combination hasn't actually changed since last time.
    private (string Path, PaletteExportSettings Settings, FilmRecipe? Recipe)? _lastSingleRenderKey;
    private string?                                        _cachedSingleRenderOutput;
    private (string Paths, PaletteExportSettings Settings)? _lastCollageRenderKey;
    private string?                                          _cachedCollageRenderOutput;

    // Kept on disk across renders (unlike the old single-shot tmpIn) so a settings/metadata
    // change can be re-rendered without asking the user to pick the same photo again.
    private string? _currentSourcePath;
    private string? _collageExifSourcePath;
    private bool     _suspendTrayNotifications;

    public MainViewModel()
    {
        // No photo and no collage tray yet — every Export Mode chip is shown (bugfix
        // "mobile Export Mode на главный экран" §1's "0 photos → all 5 chips" rule).
        Settings.SelectedPhotoCount = 0;

        _recentPhotos.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasRecentPhotos));
            OnPropertyChanged(nameof(ShowEmptyBrowserGrid));
            OnPropertyChanged(nameof(ShowEmptyDashedPlaceholder));
            OnPropertyChanged(nameof(ShowRecentStrip));
            OnPropertyChanged(nameof(ShowFullWidthStartButtons));
            OnPropertyChanged(nameof(ShowCompactActionBar));
        };

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

        // Floored at 2 rather than the raw tray count: while a collage session is active
        // (IsCollageMode true), the chip row should keep showing Collage/Collage (fast) even
        // with 0 or 1 photos in the tray (mid add, or mid manual removal) — exiting collage
        // is now an explicit chip tap or the toolbar "X" (SyncCollageModeWithExportModeAsync),
        // not something a shrinking tray should trigger on its own. This also sidesteps the
        // old bug where migrating a single photo in created a momentary 1-item tray that
        // ExportModeRules.ClosestValidMode would immediately bounce back out of Collage.
        Settings.SelectedPhotoCount = Math.Max(CollageItems.Count, 2);

        // The first tray item's EXIF represents the whole collage and feeds the editable
        // "Edit metadata" fields in Settings, same as picking a single photo does — only
        // reload when that first item's identity actually changes, so reordering the rest
        // of the tray doesn't clobber an in-progress edit.
        var newExifSource = CollageItems.Count > 0 ? CollageItems[0].FullPath : null;
        if (newExifSource != _collageExifSourcePath)
        {
            _collageExifSourcePath = newExifSource;
            Settings.LoadMetadataFields(
                newExifSource != null ? PaletteImageRenderer.ReadMetadata(newExifSource) : null,
                newExifSource != null ? RecipeReader.Read(newExifSource) : null);
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
        if (e.PropertyName is nameof(SettingsViewModel.ExportMode) or nameof(SettingsViewModel.OutputFormat))
            OnPropertyChanged(nameof(SaveButtonText));

        // Single-button pick/add entry point (bugfix "одна кнопка выбора фото вместо двух,
        // управляется ExportMode"): the chip is now the only thing that decides single vs
        // collage, so every ExportMode change re-syncs IsCollageMode/CollageItems against it
        // — migrating whatever's currently on screen rather than losing it — instead of
        // requiring a separate explicit Create/Exit Collage action to stay in sync.
        if (e.PropertyName == nameof(SettingsViewModel.ExportMode))
        {
            OnPropertyChanged(nameof(IsCollageExportMode));
            OnPropertyChanged(nameof(MainPhotoButtonLabel));
            _ = SyncCollageModeWithExportModeAsync();
            return;
        }

        if (!HasActiveContent || IsGenerating || Settings.IsBulkLoading) return;

        // "Show camera info plate" bugfix §2 (mirrors MainWindowViewModel): in Recipe mode
        // the card render is cheap, so this toggle re-renders immediately instead of only
        // surfacing the Regenerate button like every other settings change does.
        if (e.PropertyName == nameof(SettingsViewModel.ShowCameraInfo) && Settings.ExportMode == ExportMode.Recipe && !IsCollageMode)
        {
            _ = RenderCurrentAsync();
            return;
        }

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

    // Empty-state redesign (bugfix "mobile Export Mode/empty state" §2): the dashed
    // "No photo selected" box is reserved for the app's very first use — once there's any
    // picking history, a thumbnail grid fills the same space instead of sitting empty.
    // RecentPhotos already stands in for "browser" per the base mobile spec's scoped-down
    // photo browser (no Android SAF folder access yet). Excluded in collage mode: an empty
    // tray already has its own "Add photos to the collage" prompt, and SelectRecentPhotoAsync
    // is a no-op while IsCollageMode anyway, so showing the grid there would tap-to-nothing.
    public bool ShowEmptyBrowserGrid       => ShowPlaceholder && HasRecentPhotos && !IsCollageMode;
    public bool ShowEmptyDashedPlaceholder => ShowPlaceholder && (!HasRecentPhotos || IsCollageMode);

    // The old always-visible horizontal RECENT strip stays for quick photo-switching once a
    // result is already showing; once the empty-state grid takes over instead it would just
    // duplicate the same thumbnails a second time.
    public bool ShowRecentStrip => HasRecentPhotos && !ShowPlaceholder;

    // "Pick a Photo"/"Create Collage" are the sole content only for a true first-time empty
    // state; once the browser grid exists they become a compact bar above it instead.
    public bool ShowFullWidthStartButtons => ShowStartButtons && !HasRecentPhotos;
    public bool ShowCompactActionBar      => ShowStartButtons && HasRecentPhotos;

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

    // True whenever the ExportMode chip currently selected is one of the two collage modes —
    // independent of whether a tray actually has any photos in it yet, since switching the
    // chip alone (before picking anything) only changes the button label/behavior, per the
    // bugfix's own "никакого отдельного экрана не открывает" rule.
    public bool IsCollageExportMode => Settings.ExportMode is ExportMode.Collage or ExportMode.CollageInfoOnly;
    public string MainPhotoButtonLabel => IsCollageExportMode ? "Add Photos" : "Pick a Photo";

    [RelayCommand]
    private async Task PickMainPhoto()
    {
        if (IsCollageExportMode) await AddPhotosToCollageAsync();
        else await PickPhotoAsync();
    }

    [RelayCommand]
    private async Task PickForEmptyState() => await PickMainPhoto();

    public MaterialIconKind SaveIconKind  => IsSaved ? MaterialIconKind.Check : MaterialIconKind.ContentSaveOutline;
    public string           SaveButtonText => IsSaved ? "Saved!" : Settings.SaveButtonLabel;

    private DispatcherTimer? _saveConfirmTimer;

    partial void OnIsGeneratingChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowPlaceholder));
        OnPropertyChanged(nameof(ShowEmptyBrowserGrid));
        OnPropertyChanged(nameof(ShowEmptyDashedPlaceholder));
        OnPropertyChanged(nameof(ShowRecentStrip));
        RegenerateCommand.NotifyCanExecuteChanged();
    }
    partial void OnIsRegenerateAvailableChanged(bool value) => RegenerateCommand.NotifyCanExecuteChanged();
    partial void OnPaletteBitmapChanged(Bitmap? value)
    {
        OnPropertyChanged(nameof(ShowPlaceholder));
        OnPropertyChanged(nameof(HasResult));
        OnPropertyChanged(nameof(ShowPickPhotoToolbarButton));
        OnPropertyChanged(nameof(ShowStartButtons));
        OnPropertyChanged(nameof(ShowEmptyBrowserGrid));
        OnPropertyChanged(nameof(ShowEmptyDashedPlaceholder));
        OnPropertyChanged(nameof(ShowRecentStrip));
        OnPropertyChanged(nameof(ShowFullWidthStartButtons));
        OnPropertyChanged(nameof(ShowCompactActionBar));
        OnPropertyChanged(nameof(DisplayedCollageBitmap));
        OnPropertyChanged(nameof(DisplayedResultBitmap));
        OnPropertyChanged(nameof(RegenerateButtonLabel));
    }
    partial void OnCollagePreviewBitmapChanged(Bitmap? value)
    {
        OnPropertyChanged(nameof(ShowPlaceholder));
        OnPropertyChanged(nameof(HasResult));
        OnPropertyChanged(nameof(ShowStartButtons));
        OnPropertyChanged(nameof(ShowEmptyBrowserGrid));
        OnPropertyChanged(nameof(ShowEmptyDashedPlaceholder));
        OnPropertyChanged(nameof(ShowRecentStrip));
        OnPropertyChanged(nameof(ShowFullWidthStartButtons));
        OnPropertyChanged(nameof(ShowCompactActionBar));
        OnPropertyChanged(nameof(DisplayedCollageBitmap));
        OnPropertyChanged(nameof(DisplayedResultBitmap));
    }
    partial void OnOriginalBitmapChanged(Bitmap? value)
    {
        OnPropertyChanged(nameof(DisplayedResultBitmap));
        OnPropertyChanged(nameof(CanCompareToOriginal));
    }
    partial void OnErrorMessageChanged(string? value)
    {
        OnPropertyChanged(nameof(ShowPlaceholder));
        OnPropertyChanged(nameof(ShowEmptyBrowserGrid));
        OnPropertyChanged(nameof(ShowEmptyDashedPlaceholder));
        OnPropertyChanged(nameof(ShowRecentStrip));
    }
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
        OnPropertyChanged(nameof(ShowEmptyBrowserGrid));
        OnPropertyChanged(nameof(ShowEmptyDashedPlaceholder));
        OnPropertyChanged(nameof(ShowRecentStrip));
        OnPropertyChanged(nameof(ShowFullWidthStartButtons));
        OnPropertyChanged(nameof(ShowCompactActionBar));
        OnPropertyChanged(nameof(DisplayedResultBitmap));
        OnPropertyChanged(nameof(CanCompareToOriginal));
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
    // No separate Create/Exit Collage flow any more (bugfix "одна кнопка выбора фото вместо
    // двух, управляется ExportMode") — the ExportMode chip is the single source of truth for
    // single-vs-collage, synced by SyncCollageModeWithExportModeAsync below whenever it
    // changes. AddPhotosToCollageAsync is what actually flips IsCollageMode on, the first
    // time photos really land in the tray (not just because the chip says Collage).

    private static async Task<FileEntry> BuildCollageEntryAsync(string tempPath, string fileName)
    {
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
        return entry;
    }

    [RelayCommand]
    private async Task AddPhotosToCollageAsync()
    {
        if (PickImagesAsync == null) return;
        var picked = await PickImagesAsync();
        if (picked == null || picked.Count == 0) return;

        IsCollageMode = true;
        Settings.ShowCollageOptions = true;

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

                CollageItems.Add(await BuildCollageEntryAsync(tempPath, fileName));
            }
        }
        finally
        {
            _suspendTrayNotifications = false;
        }

        OnCollageTrayChanged();
    }

    // Exit shortcut (toolbar "X" once a tray exists) — just switches the chip back to Card,
    // same as tapping the Card chip directly, so the single<->collage migration rule in
    // SyncCollageModeWithExportModeAsync is the only place that logic lives.
    [RelayCommand]
    private void ExitCollage() => Settings.ExportMode = ExportMode.Card;

    // Keeps IsCollageMode/CollageItems in sync whenever the ExportMode chip crosses the
    // single<->collage boundary, migrating whatever's already on screen instead of losing it.
    // Only acts when there's actually content to migrate either way — a chip flip with
    // nothing picked yet (or an already-matching state) is a no-op here, matching the "chip
    // alone never opens a separate screen" rule.
    private async Task SyncCollageModeWithExportModeAsync()
    {
        bool wantsCollage = IsCollageExportMode;

        if (wantsCollage && !IsCollageMode && _currentSourcePath != null && File.Exists(_currentSourcePath))
        {
            // A single photo was already selected — it becomes the tray's first item rather
            // than being discarded.
            var migrated = await BuildCollageEntryAsync(_currentSourcePath, _lastFileName ?? Path.GetFileName(_currentSourcePath));

            OriginalBitmap = null;
            PaletteBitmap  = null;
            _lastTempPng   = null;
            OutputSizeText = null;
            _collageExifSourcePath = null;
            _currentSourcePath = null;
            _lastFileName  = null;
            SavePngCommand.NotifyCanExecuteChanged();

            IsCollageMode = true;
            Settings.ShowCollageOptions = true;
            CollageItems.Add(migrated);
            OnCollageTrayChanged();
        }
        else if (!wantsCollage && IsCollageMode)
        {
            // Default per the author's own note: keep the tray's first photo, drop the rest
            // from this session (their temp copies are deleted; nothing on-device/gallery is
            // touched — the tray never held originals to begin with).
            var first = CollageItems.Count > 0 ? CollageItems[0] : null;

            _suspendTrayNotifications = true;
            try
            {
                for (int i = CollageItems.Count - 1; i >= 0; i--)
                {
                    if (ReferenceEquals(CollageItems[i], first)) continue;
                    try { File.Delete(CollageItems[i].FullPath); } catch { /* best effort */ }
                    CollageItems.RemoveAt(i);
                }
            }
            finally { _suspendTrayNotifications = false; }

            IsCollageMode = false;
            Settings.ShowCollageOptions = false;
            CollageItems.Clear();
            CollagePreviewBitmap = null;
            ErrorMessage   = null;
            FileInfoText   = null;
            OutputSizeText = null;
            _lastTempPng   = null;
            IsRegenerateAvailable = false;
            SavePngCommand.NotifyCanExecuteChanged();

            if (first != null)
                await IngestFromStablePathAsync(first.FullPath, first.FileName, addToRecent: true);
            else
            {
                Settings.SelectedPhotoCount = 0;
                Settings.ExportMode = ExportModeRules.ClosestValidMode(Settings.ExportMode, 0);
            }
        }
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
            var exportSettings = PaletteExportSettings.SnapshotFrom(Settings);
            var cacheKey = (string.Join('', sourcePaths), exportSettings);

            if (Equals(_lastCollageRenderKey, cacheKey) && _cachedCollageRenderOutput != null && File.Exists(_cachedCollageRenderOutput))
            {
                _lastTempPng   = _cachedCollageRenderOutput;
                var cachedSize = new FileInfo(_lastTempPng).Length;
                var cachedFmt  = exportSettings.Format == OutputFormat.Jpeg ? "JPG" : "PNG";
                OutputSizeText = $"{cachedSize / 1_048_576.0:F1} MB {cachedFmt}";
                PaletteBitmap  = new Bitmap(_lastTempPng);
                return;
            }

            var tmpOut = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}{_lastExtension}");
            // ExportCollageAsync already offloads its own work to the thread pool, so this
            // doesn't need (and shouldn't double up) an outer Task.Run.
            await PaletteExportService.ExportCollageAsync(sourcePaths, tmpOut, exportSettings, token,
                metadataOverride: Settings.BuildMetadataOverride());

            token.ThrowIfCancellationRequested();

            _lastTempPng               = tmpOut;
            _lastCollageRenderKey      = cacheKey;
            _cachedCollageRenderOutput = tmpOut;
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

    // Single unified save button (bugfix §1, mirrors MainWindowViewModel.SavePngAsync2) —
    // one command for Save PNG/JPEG and Save recipe card alike. _lastTempPng already holds
    // whichever composite is currently on screen, so saving it verbatim guarantees the saved
    // file always matches the preview.
    [RelayCommand(CanExecute = nameof(CanSavePng))]
    private async Task SavePng()
    {
        if (SavePngAsync == null || _lastTempPng == null) return;
        string suggested;
        if (IsCollageMode)
        {
            suggested = "collage_palette" + _lastExtension;
        }
        else
        {
            var suffix = Settings.IsRecipeMode ? "_recipe" : "_palette";
            suggested = _lastFileName != null
                ? Path.GetFileNameWithoutExtension(_lastFileName) + suffix + _lastExtension
                : "palette" + _lastExtension;
        }
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

    // Copies a newly picked photo to a stable temp path and renders it. Unlike a one-shot
    // render, this path survives afterwards — see _currentSourcePath — so a later settings/
    // metadata change can re-render without asking the user to pick the same photo again.
    // The old current photo isn't deleted outright any more — it's kept alive as long as
    // it's still referenced by RecentPhotos (see IngestFromStablePathAsync).
    private async Task IngestNewPhotoAsync(Stream sourceStream, string fileName)
    {
        var newSourcePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}_{fileName}");
        await using (var fs = File.Create(newSourcePath))
            await sourceStream.CopyToAsync(fs);
        await sourceStream.DisposeAsync();

        await IngestFromStablePathAsync(newSourcePath, fileName, addToRecent: true);
    }

    // Re-selects a photo already sitting in RecentPhotos — no copy needed, the temp file
    // from its original pick is still on disk (kept alive precisely so this works).
    [RelayCommand]
    private async Task SelectRecentPhotoAsync(FileEntry entry)
    {
        if (IsCollageMode) return;
        if (!File.Exists(entry.FullPath))
        {
            RecentPhotos.Remove(entry); // temp file vanished under us (OS cleanup) — drop the dead entry
            return;
        }
        // Re-promote to the front of the "most recent" order, same as picking it fresh would.
        var idx = RecentPhotos.IndexOf(entry);
        if (idx > 0) RecentPhotos.Move(idx, 0);
        await IngestFromStablePathAsync(entry.FullPath, entry.FileName, addToRecent: false);
    }

    private async Task IngestFromStablePathAsync(string path, string fileName, bool addToRecent)
    {
        _renderCts?.Cancel();

        _currentSourcePath = path;
        _lastFileName = fileName;

        if (addToRecent)
        {
            var entry = new FileEntry(fileName, path);
            RecentPhotos.Insert(0, entry);
            _ = LoadRecentThumbnailAsync(entry);

            while (RecentPhotos.Count > MaxRecentPhotos)
            {
                var evicted = RecentPhotos[^1];
                RecentPhotos.RemoveAt(RecentPhotos.Count - 1);
                if (evicted.FullPath != _currentSourcePath)
                    try { File.Delete(evicted.FullPath); } catch { /* best effort */ }
            }
        }

        Settings.LoadMetadataFields(
            PaletteImageRenderer.ReadMetadata(_currentSourcePath),
            RecipeReader.Read(_currentSourcePath));
        IsRegenerateAvailable = false; // the render below is already current — nothing to regenerate yet
        Settings.SelectedPhotoCount = 1;
        Settings.ExportMode = ExportModeRules.ClosestValidMode(Settings.ExportMode, 1);

        await RenderCurrentAsync();
    }

    private static async Task LoadRecentThumbnailAsync(FileEntry entry)
    {
        try
        {
            var bmp = await Task.Run(() =>
            {
                using var ms = ImageLoader.LoadThumbnail(entry.FullPath, 120, 120);
                return new Bitmap(ms);
            });
            entry.ThumbnailBitmap = bmp;
        }
        catch { /* ignore unreadable files — thumbnail just stays blank */ }
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
        IsComparingToOriginal = false;
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

            _lastExtension = Settings.FileExtension;
            var exportSettings = PaletteExportSettings.SnapshotFrom(Settings);
            // Recipe mode's composite depends on the current recipe too — see the matching
            // comment in MainWindowViewModel.GeneratePaletteAsync.
            var recipeForRender = Settings.ExportMode == ExportMode.Recipe ? Settings.DetectedRecipe : null;
            var cacheKey = (sourcePath, exportSettings, recipeForRender);

            if (Equals(_lastSingleRenderKey, cacheKey) && _cachedSingleRenderOutput != null && File.Exists(_cachedSingleRenderOutput))
            {
                _lastTempPng   = _cachedSingleRenderOutput;
                var cachedSize = new FileInfo(_lastTempPng).Length;
                var cachedFmt  = exportSettings.Format == OutputFormat.Jpeg ? "JPG" : "PNG";
                OutputSizeText = $"{cachedSize / 1_048_576.0:F1} MB {cachedFmt}";
                PaletteBitmap  = new Bitmap(_lastTempPng);
                return;
            }

            var tmpOut = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}{_lastExtension}");

            if (recipeForRender != null)
            {
                // Recipe mode: main preview = the exact same composite Save recipe card
                // exports — reuse RecipeCardRenderer directly, same as desktop.
                // exportSettings.MetaVerbosity already reflects the "Show camera info plate"
                // toggle (Off when unchecked — see PaletteExportSettings.SnapshotFrom).
                await Task.Run(() => RecipeCardRenderer.Render(working, recipeForRender, tmpOut,
                    theme: exportSettings.Theme, format: exportSettings.Format, customBackground: exportSettings.CustomBackground,
                    metaVerbosity: exportSettings.MetaVerbosity, metadataOverride: Settings.BuildMetadataOverride()), token);
            }
            else
            {
                // ComputeColors false (InfoOnly) — skip k-means entirely, same as the shared
                // PaletteExportService.ExportAsync path used elsewhere; this method stays a
                // separate inline implementation (working copy is already decoded above) but
                // must not run the expensive extraction the mode exists to avoid.
                PhSpectre.Models.ColorPalette palette;
                if (exportSettings.ComputeColors)
                {
                    palette = await new PaletteExtractor().ExtractAsync(working, Settings.Colors, Settings.SamplingMode, token);
                    token.ThrowIfCancellationRequested();
                }
                else
                {
                    palette = new PhSpectre.Models.ColorPalette([new PhSpectre.Models.ColorSwatch("#000000", (0, 0, 0), 1f)]);
                }

                var metadataOverride = Settings.BuildMetadataOverride();
                await Task.Run(() => PaletteImageRenderer.Render(
                    working, palette, tmpOut,
                    showHex:          exportSettings.ShowHex,
                    metaVerbosity:    exportSettings.MetaVerbosity,
                    metaStyle:        exportSettings.MetaStyle,
                    theme:            exportSettings.Theme,
                    hexBelow:         exportSettings.HexBelow,
                    showSwatches:     exportSettings.ComputeColors,
                    downscale:        1, // resolution already capped via WorkingQuality above
                    format:           exportSettings.Format,
                    exportPreset:     exportSettings.ExportPreset,
                    metadataOverride: metadataOverride,
                    labelScale:       exportSettings.LabelScale,
                    swatchScale:      exportSettings.SwatchScale,
                    showPercent:      exportSettings.ShowPercent,
                    swatchShape:      exportSettings.SwatchShape,
                    sortOrder:        exportSettings.SortOrder,
                    customBackground: exportSettings.CustomBackground,
                    compositionGuide: exportSettings.CompositionGuide), token);
            }

            token.ThrowIfCancellationRequested();

            _lastTempPng              = tmpOut;
            _lastSingleRenderKey      = cacheKey;
            _cachedSingleRenderOutput = tmpOut;
            var sizeBytes  = new FileInfo(tmpOut).Length;
            var formatLabel = exportSettings.Format == OutputFormat.Jpeg ? "JPG" : "PNG";
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
