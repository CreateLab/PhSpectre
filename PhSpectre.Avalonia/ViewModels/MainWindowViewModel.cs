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
using PhSpectre;
using PhSpectre.Rendering;
using PhSpectre.Services;
using PhSpectre.Avalonia.Models;
using PhSpectre.Avalonia.Services;

namespace PhSpectre.Avalonia.ViewModels;

public enum BatchExportState { Idle, Running, Cancelling, Finished, Cancelled }

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
    [ObservableProperty] private bool       _isRegenerateAvailable;

    // Batch export
    [ObservableProperty] private BatchExportState _batchState = BatchExportState.Idle;
    [ObservableProperty] private int       _batchTotal;
    [ObservableProperty] private int       _batchProcessed;
    [ObservableProperty] private string?   _batchCurrentFileName;
    [ObservableProperty] private string?   _batchResultText;
    [ObservableProperty] private ObservableCollection<BatchExportError> _batchErrors = [];

    // Toolbar update banner — same underlying check as Android's Settings row
    // (AppUpdateService.Instance), just rendered differently since Desktop has a
    // toolbar and Android doesn't.
    public bool    IsUpdateAvailable  => AppUpdateService.Instance.IsAvailable;
    public string  UpdateBannerText   => $"Update available: {AppUpdateService.Instance.LatestVersionText}";
    public Func<string, Task>? OpenUrlAsync { get; set; }

    [RelayCommand]
    private async Task OpenUpdateUrl()
    {
        if (OpenUrlAsync != null && AppUpdateService.Instance.ReleaseUrl is { } url)
            await OpenUrlAsync(url);
    }

    [RelayCommand]
    private void DismissUpdate() => AppUpdateService.Instance.Dismiss();

    public bool IsGridView
    {
        get => !IsListView;
        set => IsListView = !value;
    }

    public bool ShowResultPlaceholder => !IsGenerating && PaletteBitmap == null && string.IsNullOrEmpty(ErrorMessage);

    partial void OnIsGeneratingChanged(bool value)    { OnPropertyChanged(nameof(ShowResultPlaceholder)); RegenerateCommand.NotifyCanExecuteChanged(); }
    partial void OnPaletteBitmapChanged(Bitmap? value) => OnPropertyChanged(nameof(ShowResultPlaceholder));
    partial void OnErrorMessageChanged(string? value)  => OnPropertyChanged(nameof(ShowResultPlaceholder));
    partial void OnIsRegenerateAvailableChanged(bool value) => RegenerateCommand.NotifyCanExecuteChanged();

    private bool CanRegenerate() => IsRegenerateAvailable && SelectedFile != null && !IsBatchActive && !IsGenerating;

    [RelayCommand(CanExecute = nameof(CanRegenerate))]
    private async Task Regenerate()
    {
        await GeneratePaletteAsync(SelectedFile);
        IsRegenerateAvailable = false;
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

    // Batch computed state — the button labels/visibility/enablement all flow from
    // BatchState, never toggled directly from code-behind.
    public bool IsBatchActive     => BatchState is BatchExportState.Running or BatchExportState.Cancelling;
    public bool ShowSaveAllButton => !IsBatchActive;
    public bool ShowBatchProgress => IsBatchActive;
    public bool ShowBatchResult   => BatchState is BatchExportState.Finished or BatchExportState.Cancelled;
    public bool BatchHasErrors    => BatchErrors.Count > 0;
    public string IdleButtonText   => $"Save all ({Files.Count})";
    public string ActiveButtonText => BatchState == BatchExportState.Cancelling ? "Cancelling…" : "Cancel";
    public double BatchProgressPercent => BatchTotal == 0 ? 0 : BatchProcessed * 100.0 / BatchTotal;
    public string BatchProgressText    => BatchTotal == 0 ? "" : $"{BatchProcessed} / {BatchTotal} ({(int)BatchProgressPercent}%)";

    partial void OnBatchStateChanged(BatchExportState value)
    {
        OnPropertyChanged(nameof(IsBatchActive));
        OnPropertyChanged(nameof(ShowSaveAllButton));
        OnPropertyChanged(nameof(ShowBatchProgress));
        OnPropertyChanged(nameof(ShowBatchResult));
        OnPropertyChanged(nameof(ActiveButtonText));
        SavePngAsync2Command.NotifyCanExecuteChanged();
        SaveAllCommand.NotifyCanExecuteChanged();
        CancelBatchCommand.NotifyCanExecuteChanged();
        OpenFolderCommand.NotifyCanExecuteChanged();
        RegenerateCommand.NotifyCanExecuteChanged();
    }
    partial void OnBatchProcessedChanged(int value) { OnPropertyChanged(nameof(BatchProgressPercent)); OnPropertyChanged(nameof(BatchProgressText)); }
    partial void OnBatchTotalChanged(int value)     { OnPropertyChanged(nameof(BatchProgressPercent)); OnPropertyChanged(nameof(BatchProgressText)); }

    public Func<Task<string?>>?                 PickFolderAsync      { get; set; }
    public Func<string, string, Task<string?>>? SavePngAsync         { get; set; }
    public Func<string, Task<string?>>?          PickBatchFolderAsync { get; set; }
    public Func<IReadOnlyList<BatchExportError>, Task>? ShowBatchErrorsAsync { get; set; }

    private string?                  _lastTempPng;
    private string                   _lastExtension = ".png";
    private CancellationTokenSource? _renderCts;
    private CancellationTokenSource? _thumbnailCts;
    private CancellationTokenSource? _batchCts;
    private DispatcherTimer?         _batchCancelGuardTimer;
    private DispatcherTimer?         _batchResultTimer;
    private bool                     _batchCancelGuardActive;
    private string?                  _lastBatchDestFolder;

    public MainWindowViewModel()
    {
        _files.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(FilePositionText));
            OnPropertyChanged(nameof(IdleButtonText));
            SaveAllCommand.NotifyCanExecuteChanged();
        };

        // [ObservableProperty]'s generated On{X}Changed only fires on reassigning the whole
        // BatchErrors reference — but Add()/Clear() mutate the same instance, so BatchHasErrors
        // needs its own subscription to actually track them.
        _batchErrors.CollectionChanged += (_, _) => OnPropertyChanged(nameof(BatchHasErrors));

        // The settings sidebar is always visible and applies live, but changing a setting
        // never re-renders on its own — that's a forced/surprising cost the user explicitly
        // didn't want. Instead a settings change just surfaces the Regenerate button; the
        // actual re-render only happens when the user clicks it.
        Settings.PropertyChanged += OnSettingsPropertyChanged;

        AppUpdateService.Instance.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(AppUpdateService.IsAvailable) or nameof(AppUpdateService.LatestVersionText))
            {
                OnPropertyChanged(nameof(IsUpdateAvailable));
                OnPropertyChanged(nameof(UpdateBannerText));
            }
        };
    }

    private void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Settings.LoadMetadataFields (called when a new photo is selected) bulk-assigns the
        // metadata text fields, which would otherwise look like a user edit and pop up
        // Regenerate for a photo that's already about to render fresh.
        if (SelectedFile == null || IsBatchActive || Settings.IsBulkLoading) return;
        IsRegenerateAvailable = true;
    }

    partial void OnIsListViewChanged(bool value) => OnPropertyChanged(nameof(IsGridView));

    partial void OnSelectedFileChanged(FileEntry? value)
    {
        OnPropertyChanged(nameof(FilePositionText));
        Settings.LoadMetadataFields(value != null ? PaletteImageRenderer.ReadMetadata(value.FullPath) : null);
        IsRegenerateAvailable = false; // the fresh render below is already current — nothing to regenerate yet
        _ = GeneratePaletteAsync(value);
    }

    [RelayCommand]
    private void ToggleView() => IsListView = !IsListView;

    [RelayCommand(CanExecute = nameof(CanOpenFolder))]
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

    private bool CanOpenFolder() => !IsBatchActive;

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
        var suggested = Path.GetFileNameWithoutExtension(SelectedFile.FileName) + "_palette" + _lastExtension;
        var dest = await SavePngAsync(suggested, Path.GetDirectoryName(SelectedFile.FullPath)!);
        if (dest != null)
            File.Copy(_lastTempPng, dest, overwrite: true);
    }

    private bool CanSavePng() => PaletteBitmap != null && !IsGenerating && !IsBatchActive;

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
        RegenerateCommand.NotifyCanExecuteChanged();

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

            _lastExtension = Settings.FileExtension;
            var tmpOut = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}{_lastExtension}");
            var exportSettings = PaletteExportSettings.SnapshotFrom(Settings);
            await PaletteExportService.ExportAsync(filePath, tmpOut, exportSettings, token,
                metadataOverride: Settings.BuildMetadataOverride());

            token.ThrowIfCancellationRequested();

            _lastTempPng   = tmpOut;
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
            if (!token.IsCancellationRequested)
            {
                IsGenerating = false;
                SavePngAsync2Command.NotifyCanExecuteChanged();
            }
        }
    }

    // ── Batch export ──────────────────────────────────────────────────────────────

    [RelayCommand(CanExecute = nameof(CanSaveAll))]
    private async Task SaveAllAsync()
    {
        if (PickBatchFolderAsync == null || Files.Count == 0) return;

        var sourceFolder = Path.GetDirectoryName(Files[0].FullPath) ?? "";
        var defaultDir = _lastBatchDestFolder ?? Path.Combine(sourceFolder, "palettes");
        Directory.CreateDirectory(defaultDir);

        // The folder picker itself is the "are you sure" — Esc/Cancel here leaves
        // everything untouched and the button stays "Save all (N)".
        var destFolder = await PickBatchFolderAsync(defaultDir);
        if (destFolder == null) return;

        _lastBatchDestFolder = destFolder;
        await RunBatchAsync(destFolder);
    }

    private bool CanSaveAll() => Files.Count > 0 && !IsBatchActive;

    [RelayCommand(CanExecute = nameof(CanCancelBatch))]
    private void CancelBatch()
    {
        BatchState = BatchExportState.Cancelling;
        _batchCts?.Cancel();
    }

    private bool CanCancelBatch() => BatchState == BatchExportState.Running && !_batchCancelGuardActive;

    [RelayCommand]
    private async Task ShowBatchErrors()
    {
        if (ShowBatchErrorsAsync != null)
            await ShowBatchErrorsAsync(BatchErrors);
    }

    private async Task RunBatchAsync(string destFolder)
    {
        _batchResultTimer?.Stop();

        var snapshot = Files.Select(f => f.FullPath).ToList();
        var exportSettings = PaletteExportSettings.SnapshotFrom(Settings);

        _batchCts = new CancellationTokenSource();
        var cancelToken = _batchCts.Token;

        BatchErrors.Clear();
        BatchTotal = snapshot.Count;
        BatchProcessed = 0;
        BatchCurrentFileName = snapshot.Count > 0 ? Path.GetFileName(snapshot[0]) : null;
        _batchCancelGuardActive = true; // set before the state flip so CanCancelBatch is never briefly true
        BatchState = BatchExportState.Running;
        StartCancelGuard();

        int saved = 0, failed = 0, overwritten = 0, processed = 0;
        bool diskFull = false;

        // Bounded parallelism — the pipeline (k-means clustering + full-res ImageSharp
        // draw) is CPU/memory heavy enough that unlimited concurrency would thrash on a
        // folder of 40MP photos, but strictly sequential leaves cores idle. A file that
        // has already started a export always finishes (CancellationToken.None below) —
        // cancelling only stops the loop from *starting* new ones via the semaphore wait.
        var degree = Math.Clamp(Environment.ProcessorCount / 2, 1, 4);
        using var gate = new SemaphoreSlim(degree);
        var tasks = new List<Task>();

        foreach (var sourcePath in snapshot)
        {
            if (cancelToken.IsCancellationRequested) break;

            try { await gate.WaitAsync(cancelToken); }
            catch (OperationCanceledException) { break; }

            var fileName = Path.GetFileName(sourcePath);
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    var destName = Path.GetFileNameWithoutExtension(sourcePath) + "_palette" + exportSettings.FileExtension;
                    var destPath = Path.Combine(destFolder, destName);
                    bool existed = File.Exists(destPath);

                    await PaletteExportService.ExportAsync(sourcePath, destPath, exportSettings, CancellationToken.None);

                    Interlocked.Increment(ref saved);
                    if (existed) Interlocked.Increment(ref overwritten);
                }
                catch (IOException ex) when (IsDiskFull(ex))
                {
                    Interlocked.Increment(ref failed);
                    diskFull = true;
                    _batchCts?.Cancel(); // stop starting further files
                    await Dispatcher.UIThread.InvokeAsync(() => BatchErrors.Add(new BatchExportError(fileName, ex.Message)));
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref failed);
                    await Dispatcher.UIThread.InvokeAsync(() => BatchErrors.Add(new BatchExportError(fileName, ex.Message)));
                }
                finally
                {
                    int done = Interlocked.Increment(ref processed);
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        if (done > BatchProcessed) BatchProcessed = done;
                        BatchCurrentFileName = fileName;
                    });
                    gate.Release();
                }
            }));
        }

        await Task.WhenAll(tasks);

        BatchCurrentFileName = null;

        if (diskFull)
        {
            BatchResultText = $"Stopped — disk full: {saved} saved before it ran out";
            BatchState = BatchExportState.Finished;
        }
        else if (cancelToken.IsCancellationRequested)
        {
            BatchResultText = $"Cancelled: {saved} saved";
            BatchState = BatchExportState.Cancelled;
        }
        else
        {
            var overwriteNote = overwritten > 0 ? $", overwritten: {overwritten}" : "";
            BatchResultText = failed > 0
                ? $"Done: {saved} saved, {failed} failed{overwriteNote}"
                : $"Done: {saved} saved{overwriteNote}";
            BatchState = BatchExportState.Finished;
        }

        _batchCancelGuardTimer?.Stop();

        _batchResultTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _batchResultTimer.Tick += (_, _) =>
        {
            _batchResultTimer!.Stop();
            BatchState = BatchExportState.Idle;
        };
        _batchResultTimer.Start();
    }

    private void StartCancelGuard()
    {
        _batchCancelGuardActive = true;
        CancelBatchCommand.NotifyCanExecuteChanged();
        _batchCancelGuardTimer?.Stop();
        _batchCancelGuardTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _batchCancelGuardTimer.Tick += (_, _) =>
        {
            _batchCancelGuardTimer!.Stop();
            _batchCancelGuardActive = false;
            CancelBatchCommand.NotifyCanExecuteChanged();
        };
        _batchCancelGuardTimer.Start();
    }

    // HRESULT_FROM_WIN32(ERROR_HANDLE_DISK_FULL=39) / HRESULT_FROM_WIN32(ERROR_DISK_FULL=112)
    private static bool IsDiskFull(IOException ex) => (ex.HResult & 0xFFFF) is 39 or 112;
}
