using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhSpectre;
using PhSpectre.Rendering;
using PhSpectre.Services;
using PhSpectre.App.Models;

namespace PhSpectre.App.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    public SettingsViewModel Settings { get; } = new();

    [ObservableProperty] private ObservableCollection<FileEntry> _files = [];
    [ObservableProperty] private FileEntry? _selectedFile;
    [ObservableProperty] private Bitmap?    _originalBitmap;
    [ObservableProperty] private Bitmap?    _paletteBitmap;
    [ObservableProperty] private bool       _isGenerating;
    [ObservableProperty] private string?    _errorMessage;
    [ObservableProperty] private bool       _isListView = false;
    [ObservableProperty] private string?    _fileInfoText;
    [ObservableProperty] private string?    _outputSizeText;

    public bool IsGridView
    {
        get => !IsListView;
        set => IsListView = !value;
    }

    public string FilePositionText
    {
        get
        {
            if (Files.Count == 0) return "No photos";
            if (SelectedFile == null) return $"{Files.Count} photos";
            var idx = Files.IndexOf(SelectedFile) + 1;
            return $"{idx} of {Files.Count} photos";
        }
    }

    public Func<Task<string?>>?                 PickFolderAsync { get; set; }
    public Func<string, string, Task<string?>>? SavePngAsync    { get; set; }

    private string?                  _lastTempPng;
    private CancellationTokenSource? _renderCts;
    private CancellationTokenSource? _thumbnailCts;

    public MainWindowViewModel()
    {
        _files.CollectionChanged += (_, _) => OnPropertyChanged(nameof(FilePositionText));
    }

    partial void OnIsListViewChanged(bool value) => OnPropertyChanged(nameof(IsGridView));

    partial void OnSelectedFileChanged(FileEntry? value)
    {
        OnPropertyChanged(nameof(FilePositionText));
        _ = GeneratePaletteAsync(value);
    }

    [RelayCommand]
    private void ToggleView() => IsListView = !IsListView;

    [RelayCommand]
    private async Task OpenFolderAsync()
    {
        if (PickFolderAsync == null) return;
        var folder = await PickFolderAsync();
        if (folder == null) return;

        _thumbnailCts?.Cancel();
        Files.Clear();

        foreach (var path in Directory.EnumerateFiles(folder)
            .Where(p => p.EndsWith(".jpg",  StringComparison.OrdinalIgnoreCase) ||
                        p.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p))
        {
            Files.Add(new FileEntry(Path.GetFileName(path), path));
        }

        _thumbnailCts = new CancellationTokenSource();
        _ = LoadThumbnailsAsync(_thumbnailCts.Token);
    }

    private async Task LoadThumbnailsAsync(CancellationToken token)
    {
        foreach (var entry in Files.ToList())
        {
            if (token.IsCancellationRequested) break;
            try
            {
                var path = entry.FullPath;
                var bmp = await Task.Run(() =>
                {
                    using var ms = ImageLoader.LoadThumbnail(path, 120, 120);
                    return new Bitmap(ms);
                }, token);
                entry.ThumbnailBitmap = bmp;
            }
            catch { /* ignore unreadable files */ }
        }
    }

    public void SelectPreviousFile()
    {
        if (Files.Count == 0) return;
        var idx = SelectedFile == null ? 0 : Files.IndexOf(SelectedFile);
        if (idx > 0) SelectedFile = Files[idx - 1];
    }

    public void SelectNextFile()
    {
        if (Files.Count == 0) return;
        var idx = SelectedFile == null ? -1 : Files.IndexOf(SelectedFile);
        if (idx < Files.Count - 1) SelectedFile = Files[idx + 1];
    }

    [RelayCommand(CanExecute = nameof(CanSavePng))]
    private async Task SavePngAsync2()
    {
        if (SavePngAsync == null || _lastTempPng == null || SelectedFile == null) return;
        var suggested = Path.GetFileNameWithoutExtension(SelectedFile.FileName) + "_palette.png";
        var dest = await SavePngAsync(suggested, Path.GetDirectoryName(SelectedFile.FullPath)!);
        if (dest != null)
            File.Copy(_lastTempPng, dest, overwrite: true);
    }

    private bool CanSavePng() => PaletteBitmap != null && !IsGenerating;

    private async Task GeneratePaletteAsync(FileEntry? entry)
    {
        _renderCts?.Cancel();
        _renderCts = new CancellationTokenSource();
        var token = _renderCts.Token;

        OriginalBitmap  = null;
        PaletteBitmap   = null;
        ErrorMessage    = null;
        FileInfoText    = null;
        OutputSizeText  = null;
        _lastTempPng    = null;
        SavePngAsync2Command.NotifyCanExecuteChanged();

        if (entry == null) return;

        IsGenerating = true;
        try
        {
            var filePath = entry.FullPath;

            using var previewMs = await Task.Run(
                () => ImageLoader.LoadAutoOriented(filePath), token);
            token.ThrowIfCancellationRequested();
            OriginalBitmap = new Bitmap(previewMs);

            var ps = OriginalBitmap.PixelSize;
            var folder = Path.GetDirectoryName(filePath) ?? "";
            FileInfoText = $"{entry.FileName}  ·  {ps.Width}×{ps.Height}  ·  {folder}";

            PhSpectre.Models.ColorPalette palette;
            await using (var fs = File.OpenRead(filePath))
                palette = await new PaletteExtractor().ExtractAsync(fs, Settings.Colors, Settings.SamplingMode, token);

            token.ThrowIfCancellationRequested();

            var tmpOut = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.png");
            var snap = (filePath, Settings.ShowHex, Settings.HexBelow, Settings.MetaVerbosity,
                        Settings.MetaStyle, Settings.Theme, Settings.ShowSwatches, Settings.HalfSize);
            await Task.Run(() => PaletteImageRenderer.Render(
                snap.filePath, palette, tmpOut,
                showHex:       snap.ShowHex,
                metaVerbosity: snap.MetaVerbosity,
                metaStyle:     snap.MetaStyle,
                theme:         snap.Theme,
                hexBelow:      snap.HexBelow,
                showSwatches:  snap.ShowSwatches,
                downscale:     snap.HalfSize ? 2 : 1), token);

            token.ThrowIfCancellationRequested();

            _lastTempPng   = tmpOut;
            var sizeBytes  = new System.IO.FileInfo(tmpOut).Length;
            OutputSizeText = $"{sizeBytes / 1_048_576.0:F1} MB PNG";
            PaletteBitmap  = new Bitmap(tmpOut);
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
                SavePngAsync2Command.NotifyCanExecuteChanged();
            }
        }
    }
}
