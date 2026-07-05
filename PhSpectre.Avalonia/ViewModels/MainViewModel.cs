using System;
using System.ComponentModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Material.Icons;
using PhSpectre;
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

    public Func<Task<(Stream Stream, string FileName)?>>? PickImageAsync { get; set; }
    public Func<string, string, Task<bool>>?               SavePngAsync  { get; set; }

    private string?                  _lastTempPng;
    private string?                  _lastFileName;
    private string                   _lastExtension = ".png";
    private CancellationTokenSource? _renderCts;

    // Kept on disk across renders (unlike the old single-shot tmpIn) so a settings/metadata
    // change can be re-rendered without asking the user to pick the same photo again.
    private string? _currentSourcePath;

    public MainViewModel()
    {
        // Mirrors Desktop's MainWindowViewModel: a settings change never re-renders on its
        // own — it only surfaces the Regenerate button. Actual re-render is user-triggered.
        Settings.PropertyChanged += OnSettingsPropertyChanged;
    }

    private void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Settings.LoadMetadataFields (called right after a new photo is ingested) bulk-assigns
        // the metadata text fields, which would otherwise look like a user edit and pop up
        // Regenerate for a photo that's already about to render fresh.
        if (_currentSourcePath == null || IsGenerating || Settings.IsBulkLoading) return;
        IsRegenerateAvailable = true;
    }

    private bool CanRegenerate() => IsRegenerateAvailable && _currentSourcePath != null && !IsGenerating;

    [RelayCommand(CanExecute = nameof(CanRegenerate))]
    private async Task Regenerate()
    {
        await RenderCurrentAsync();
        IsRegenerateAvailable = false;
    }

    // Photos above the chosen cap (long edge) get downscaled before processing — running
    // the full extract/render/PNG-encode pipeline at native mobile-camera resolution is
    // what makes it take minutes. Settings.WorkingQuality (Settings screen → "Working
    // size") picks which cap: faster & smaller, or slower & closer to desktop detail.
    private int MaxWorkingDimension => Settings.WorkingQuality switch
    {
        WorkingQuality.Fast     => 2000,
        WorkingQuality.Balanced => 3400,
        WorkingQuality.Best     => 4800,
        _                       => 3400
    };

    public bool ShowPlaceholder => !IsGenerating && PaletteBitmap == null && string.IsNullOrEmpty(ErrorMessage);
    public bool HasResult       => PaletteBitmap != null;

    public MaterialIconKind SaveIconKind  => IsSaved ? MaterialIconKind.Check : MaterialIconKind.ContentSaveOutline;
    public string           SaveButtonText => IsSaved ? "Saved!" : "Save to Gallery";

    private DispatcherTimer? _saveConfirmTimer;

    partial void OnIsGeneratingChanged(bool value)      { OnPropertyChanged(nameof(ShowPlaceholder)); RegenerateCommand.NotifyCanExecuteChanged(); }
    partial void OnIsRegenerateAvailableChanged(bool value) => RegenerateCommand.NotifyCanExecuteChanged();
    partial void OnPaletteBitmapChanged(Bitmap? value)
    {
        OnPropertyChanged(nameof(ShowPlaceholder));
        OnPropertyChanged(nameof(HasResult));
    }
    partial void OnErrorMessageChanged(string? value)   => OnPropertyChanged(nameof(ShowPlaceholder));
    partial void OnIsSavedChanged(bool value)
    {
        OnPropertyChanged(nameof(SaveIconKind));
        OnPropertyChanged(nameof(SaveButtonText));
    }

    [RelayCommand]
    private void ToggleSettings() => IsSettingsOpen = !IsSettingsOpen;

    [RelayCommand]
    private async Task PickPhotoAsync()
    {
        if (PickImageAsync == null) return;
        var picked = await PickImageAsync();
        if (picked == null) return;

        await IngestNewPhotoAsync(picked.Value.Stream, picked.Value.FileName);
    }

    [RelayCommand(CanExecute = nameof(CanSavePng))]
    private async Task SavePng()
    {
        if (SavePngAsync == null || _lastTempPng == null || _lastFileName == null) return;
        var suggested = Path.GetFileNameWithoutExtension(_lastFileName) + "_palette" + _lastExtension;
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
                        Settings.OutputFormat, Settings.ExportPreset);
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
                metadataOverride: metadataOverride), token);

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
