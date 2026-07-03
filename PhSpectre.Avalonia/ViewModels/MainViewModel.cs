using System;
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

    public Func<Task<(Stream Stream, string FileName)?>>? PickImageAsync { get; set; }
    public Func<string, string, Task<bool>>?               SavePngAsync  { get; set; }

    private string?                  _lastTempPng;
    private string?                  _lastFileName;
    private string                   _lastExtension = ".png";
    private CancellationTokenSource? _renderCts;

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

    partial void OnIsGeneratingChanged(bool value)      => OnPropertyChanged(nameof(ShowPlaceholder));
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

        await GeneratePaletteAsync(picked.Value.Stream, picked.Value.FileName);
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

    private async Task GeneratePaletteAsync(Stream sourceStream, string fileName)
    {
        _renderCts?.Cancel();
        _renderCts = new CancellationTokenSource();
        var token = _renderCts.Token;

        OriginalBitmap = null;
        PaletteBitmap  = null;
        ErrorMessage   = null;
        FileInfoText   = null;
        OutputSizeText = null;
        _lastTempPng   = null;
        _lastFileName  = fileName;
        _saveConfirmTimer?.Stop();
        IsSaved = false;
        SavePngCommand.NotifyCanExecuteChanged();

        IsGenerating = true;
        var tmpIn = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}_{fileName}");
        Image<Rgb24>? working = null;
        try
        {
            await using (var fs = File.Create(tmpIn))
                await sourceStream.CopyToAsync(fs, token);
            await sourceStream.DisposeAsync();

            // Single decode of the source photo, downscaled to the working cap in the
            // same pass. Preview/extraction/render below all reuse this one in-memory
            // image instead of each re-decoding the file from disk.
            working = await Task.Run(() => ImageLoader.LoadWorkingCopy(tmpIn, MaxWorkingDimension), token);
            token.ThrowIfCancellationRequested();

            using var previewMs = await Task.Run(() => ImageLoader.ToJpegStream(working), token);
            token.ThrowIfCancellationRequested();
            OriginalBitmap = new Bitmap(previewMs);

            var ps = OriginalBitmap.PixelSize;
            FileInfoText = $"{fileName}  ·  {ps.Width}×{ps.Height}";

            var palette = await new PaletteExtractor().ExtractAsync(working, Settings.Colors, Settings.SamplingMode, token);
            token.ThrowIfCancellationRequested();

            _lastExtension = Settings.FileExtension;
            var tmpOut = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}{_lastExtension}");
            var snap = (Settings.ShowHex, Settings.HexBelow, Settings.MetaVerbosity,
                        Settings.MetaStyle, Settings.Theme, Settings.ShowSwatches,
                        Settings.OutputFormat, Settings.ExportPreset);
            await Task.Run(() => PaletteImageRenderer.Render(
                working, palette, tmpOut,
                showHex:       snap.ShowHex,
                metaVerbosity: snap.MetaVerbosity,
                metaStyle:     snap.MetaStyle,
                theme:         snap.Theme,
                hexBelow:      snap.HexBelow,
                showSwatches:  snap.ShowSwatches,
                downscale:     1, // resolution already capped via WorkingQuality above
                format:        snap.OutputFormat,
                exportPreset:  snap.ExportPreset), token);

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
            File.Delete(tmpIn);
            if (!token.IsCancellationRequested)
            {
                IsGenerating = false;
                SavePngCommand.NotifyCanExecuteChanged();
            }
        }
    }
}
