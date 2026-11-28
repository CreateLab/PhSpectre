using PhSpectre.Localization;
using System;
using System.Diagnostics;
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
using PhSpectre.Models;
using PhSpectre.Recipes;
using PhSpectre.Rendering;
using PhSpectre.Services;
using PhSpectre.Avalonia.Models;
using PhSpectre.Avalonia.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

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
    [ObservableProperty] private bool       _isComparingToOriginal;

    public Bitmap? DisplayedSingleBitmap => IsComparingToOriginal ? OriginalBitmap : PaletteBitmap;

    partial void OnIsComparingToOriginalChanged(bool value) { OnPropertyChanged(nameof(DisplayedSingleBitmap)); OnPropertyChanged(nameof(CompareIndex)); }
    partial void OnOriginalBitmapChanged(Bitmap? value)      { OnPropertyChanged(nameof(DisplayedSingleBitmap)); OnPropertyChanged(nameof(CanCompare)); OnPropertyChanged(nameof(ShowPill)); }

    [RelayCommand]
    private void ToggleCompareToOriginal() => IsComparingToOriginal = !IsComparingToOriginal;

    // Result | Original switch.
    public int CompareIndex
    {
        get => IsComparingToOriginal ? 1 : 0;
        set => IsComparingToOriginal = value == 1;
    }
    public bool CanCompare => !IsCollageMode && OriginalBitmap != null;

    // The error stays off the photo, with the last good result visible.
    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    [RelayCommand]
    private void DismissError() => ErrorMessage = null;

    [RelayCommand]
    private void RetryRender()
    {
        if (IsCollageMode) _ = GenerateCollagePaletteAsync();
        else if (SelectedFile != null) _ = GeneratePaletteAsync(SelectedFile, keepPrevious: true);
    }

    public string PhotosHeaderText
    {
        get
        {
            var exported = Files.Count(f => f.IsExported);
            return exported > 0
                ? $"{Loc.N(Files.Count, "{0} PHOTO", "{0} PHOTOS")} · {Loc.F("{0} EXPORTED", exported)}"
                : Loc.N(Files.Count, "{0} PHOTO", "{0} PHOTOS");
        }
    }
    public bool HasPhotos => Files.Count > 0;
    public string EmptyStageText => !HasPhotos ? Loc.T("Open a folder of photos to begin")
        : IsCollageMode ? Loc.T("Tick two or more photos in the strip below") : Loc.T("Pick a photo from the strip below");

    public string SuggestedNameText => IsCollageMode
        ? SuggestedFileName.BuildCollage(Settings.FileExtension)
        : SuggestedFileName.Build(SelectedFile?.FileName, Settings.ExportMode, Settings.FileExtension);

    private void RaiseSuggestedNameChanged() => OnPropertyChanged(nameof(SuggestedNameText));
    [ObservableProperty] private string?    _errorMessage;
    [ObservableProperty] private bool       _isListView = false;
    [ObservableProperty] private string?    _fileInfoText;
    [ObservableProperty] private string?    _outputSizeText;
    [ObservableProperty] private bool       _isRegenerateAvailable;

    // Order is the packing order; the first item supplies the EXIF.
    [ObservableProperty] private bool _isCollageMode;
    [ObservableProperty] private ObservableCollection<FileEntry> _collageItems = [];

    // Layout-only preview, shown until the full render is ready.
    [ObservableProperty] private Bitmap? _collagePreviewBitmap;
    public Bitmap? DisplayedCollageBitmap => PaletteBitmap ?? CollagePreviewBitmap;

    // Loc.T("Generate") before the first render, then Loc.T("Regenerate").
    public string RegenerateButtonLabel => IsCollageMode && PaletteBitmap == null ? Loc.T("Generate") : Loc.T("Regenerate");

    [ObservableProperty] private BatchExportState _batchState = BatchExportState.Idle;
    [ObservableProperty] private int       _batchTotal;
    [ObservableProperty] private int       _batchProcessed;
    [ObservableProperty] private string?   _batchCurrentFileName;
    [ObservableProperty] private string?   _batchResultText;
    [ObservableProperty] private ObservableCollection<BatchExportError> _batchErrors = [];

    // Same check as the Android settings row.
    public bool    IsUpdateAvailable  => AppUpdateService.Instance.IsAvailable;
    public string  UpdateBannerText   => Loc.F("Update available: {0}", AppUpdateService.Instance.LatestVersionText);
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

    public bool ShowResultPlaceholder =>
        !IsGenerating && string.IsNullOrEmpty(ErrorMessage) &&
        (IsCollageMode ? DisplayedCollageBitmap == null : PaletteBitmap == null);

    // Hidden in collage mode: there is no single original.
    public string OriginalPanePlaceholderText => Loc.T("No photo selected");

    // The previous result stays dimmed during a re-render; "Generating…" is for the first one.
    public bool ShowUpdatingPill        => IsGenerating && PaletteBitmap != null && !IsCollageMode;
    // Collage is regenerated from the pill.
    public bool ShowRegenerate => IsCollageMode && IsRegenerateAvailable;
    public bool ShowPill => ShowUpdatingPill || CanCompare || ShowRegenerate;
    public bool ShowGeneratingTextSingle => IsGenerating && !ShowUpdatingPill;
    public double SinglePreviewOpacity  => ShowUpdatingPill ? 0.4 : 1.0;

    private void RaiseUpdatingStateChanged()
    {
        OnPropertyChanged(nameof(ShowUpdatingPill)); OnPropertyChanged(nameof(ShowPill));
        OnPropertyChanged(nameof(ShowGeneratingTextSingle));
        OnPropertyChanged(nameof(SinglePreviewOpacity));
    }

    partial void OnIsGeneratingChanged(bool value)    { OnPropertyChanged(nameof(ShowResultPlaceholder)); RaiseUpdatingStateChanged(); RegenerateCommand.NotifyCanExecuteChanged(); }
    partial void OnPaletteBitmapChanged(Bitmap? value)
    {
        RaiseUpdatingStateChanged();
        OnPropertyChanged(nameof(ShowResultPlaceholder));
        OnPropertyChanged(nameof(DisplayedCollageBitmap));
        OnPropertyChanged(nameof(RegenerateButtonLabel));
        OnPropertyChanged(nameof(DisplayedSingleBitmap));
    }
    partial void OnCollagePreviewBitmapChanged(Bitmap? value)
    {
        OnPropertyChanged(nameof(ShowResultPlaceholder));
        OnPropertyChanged(nameof(DisplayedCollageBitmap));
    }
    partial void OnErrorMessageChanged(string? value)  { OnPropertyChanged(nameof(ShowResultPlaceholder)); OnPropertyChanged(nameof(HasError)); }
    partial void OnIsRegenerateAvailableChanged(bool value)
    {
        RegenerateCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(ShowRegenerate)); OnPropertyChanged(nameof(ShowPill));
    }

    partial void OnIsCollageModeChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowRegenerate)); OnPropertyChanged(nameof(ShowPill));
        OnPropertyChanged(nameof(EmptyStageText));
        OnPropertyChanged(nameof(CanCompare)); OnPropertyChanged(nameof(ShowPill));
        RaiseSuggestedNameChanged();
        OnPropertyChanged(nameof(ShowSaveAllButton));
        OnPropertyChanged(nameof(OriginalPanePlaceholderText));
        OnPropertyChanged(nameof(ShowResultPlaceholder));
        OnPropertyChanged(nameof(RegenerateButtonLabel));
        RegenerateCommand.NotifyCanExecuteChanged();
        SaveAllCommand.NotifyCanExecuteChanged();
        SavePngAsync2Command.NotifyCanExecuteChanged();
        EnterCollageModeCommand.NotifyCanExecuteChanged();
    }

    private bool HasActiveContent => IsCollageMode ? CollageItems.Count > 0 : SelectedFile != null;

    private bool CanRegenerate() => IsRegenerateAvailable && HasActiveContent && !IsBatchActive && !IsGenerating;

    [RelayCommand(CanExecute = nameof(CanRegenerate))]
    private async Task Regenerate()
    {
        if (IsCollageMode)
            await GenerateCollagePaletteAsync();
        else
            await GeneratePaletteAsync(SelectedFile);
        IsRegenerateAvailable = false;
    }

    public string FilePositionText
    {
        get
        {
            if (Files.Count == 0) return Loc.T("No photos");
            if (SelectedFile == null) return Loc.N(Files.Count, "{0} photo", "{0} photos");
            var idx = Files.IndexOf(SelectedFile) + 1;
            return Loc.N(Files.Count, "{1} of {0} photo", "{1} of {0} photos", idx);
        }
    }

    public bool IsBatchActive     => BatchState is BatchExportState.Running or BatchExportState.Cancelling;
    // Single photos only.
    public bool ShowSaveAllButton => !IsBatchActive && !IsCollageMode;
    public bool ShowBatchProgress => IsBatchActive;
    public bool ShowBatchResult   => BatchState is BatchExportState.Finished or BatchExportState.Cancelled;
    public bool BatchHasErrors    => BatchErrors.Count > 0;
    public string IdleButtonText   => Loc.N(Files.Count, "Save all · {0} photo", "Save all · {0} photos");

    // Save all, repeated in the Edit inspector.
    private void SyncCardExport()
    {
        Settings.CardCanSaveAll = ShowSaveAllButton && Files.Count > 1;
        Settings.CardSaveAllText = Loc.N(Files.Count, "Export all {0} photo…", "Export all {0} photos…");
    }
    public string ActiveButtonText => BatchState == BatchExportState.Cancelling ? Loc.T("Cancelling…") : Loc.T("Cancel");
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
    partial void OnBatchProcessedChanged(int value) { OnPropertyChanged(nameof(BatchProgressPercent)); OnPropertyChanged(nameof(BatchProgressText)); OnPropertyChanged(nameof(BatchHeadline)); }
    partial void OnBatchTotalChanged(int value)     { OnPropertyChanged(nameof(BatchProgressPercent)); OnPropertyChanged(nameof(BatchProgressText)); OnPropertyChanged(nameof(BatchHeadline)); }

    public Func<Task<string?>>?                 PickFolderAsync      { get; set; }
    public Func<string, string, Task<string?>>? SavePngAsync         { get; set; }
    public Func<string, Task<string?>>?          PickBatchFolderAsync { get; set; }

    private string?                  _lastTempPng;
    private string                   _lastExtension = ".png";

    // Reuses the last render when photo and settings are unchanged.
    private CardRenderKey?                                         _lastSingleRenderKey;
    private string?                                        _cachedSingleRenderOutput;
    private CardRenderKey?                                         _lastCollageRenderKey;
    private string?                                         _cachedCollageRenderOutput;
    private string?                  _collageExifSourcePath;
    private CancellationTokenSource? _renderCts;
    private CancellationTokenSource? _thumbnailCts;
    private CancellationTokenSource? _batchCts;
    private DispatcherTimer?         _batchCancelGuardTimer;
    private DispatcherTimer?         _batchResultTimer;
    private DispatcherTimer?         _toastTimer;
    private string?                  _toastPath;
    private bool                     _batchCancelGuardActive;
    private string?                  _lastBatchDestFolder;

    public MainWindowViewModel()
    {
        _files.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(PhotosHeaderText));
            OnPropertyChanged(nameof(HasPhotos));
            OnPropertyChanged(nameof(EmptyStageText));
            OnPropertyChanged(nameof(FilePositionText));
            OnPropertyChanged(nameof(IdleButtonText));
            SaveAllCommand.NotifyCanExecuteChanged();
        };

        // Add and Clear do not raise a property change.
        _batchErrors.CollectionChanged += (_, _) => OnPropertyChanged(nameof(BatchHasErrors));

        _collageItems.CollectionChanged += (_, _) =>
        {
            for (int i = 0; i < CollageItems.Count; i++)
                CollageItems[i].IsCollageExifSource = i == 0;

            RegenerateCommand.NotifyCanExecuteChanged();
            if (!IsCollageMode) return;

            Settings.SelectedPhotoCount = CollageItems.Count;
            Settings.ExportMode = ExportModeRules.ClosestValidMode(Settings.ExportMode, CollageItems.Count);

            var newExifSource = CollageItems.Count > 0 ? CollageItems[0].FullPath : null;
            if (newExifSource != _collageExifSourcePath)
            {
                _collageExifSourcePath = newExifSource;
                Settings.LoadMetadataFields(
                    newExifSource != null ? PaletteImageRenderer.ReadMetadata(newExifSource) : null,
                    newExifSource != null ? RecipeReader.Read(newExifSource) : null);
            }

            // A full render is stale once the tray changes.
            PaletteBitmap  = null;
            _lastTempPng   = null;
            OutputSizeText = null;
            SavePngAsync2Command.NotifyCanExecuteChanged();

            if (CollageItems.Count == 0)
            {
                CollagePreviewBitmap = null;
                FileInfoText = null;
                IsRegenerateAvailable = false;
            }
            else
            {
                FileInfoText = Loc.N(CollageItems.Count, "Collage · {0} photo", "Collage · {0} photos");
                IsRegenerateAvailable = true;
                _ = RenderCollagePreviewAsync();
            }
        };

        // A settings change only offers Regenerate.
        Settings.PropertyChanged += OnSettingsPropertyChanged;
        Settings.TopModeHandler = HandleTopMode;
        Settings.PhotosProvider = () => Files;
        Settings.CurrentPhotoProvider = () => SelectedFile;
        Settings.SelectPhotoAction = f => SelectedFile = f;
        Settings.OpenPhotosCommand = OpenFolderCommand;
        Settings.CardSaveCommand = SavePngAsync2Command;
        Settings.CardSaveAllCommand = SaveAllCommand;
        PropertyChanged += (_, e) => { if (e.PropertyName is nameof(ShowSaveAllButton) or nameof(IdleButtonText)) SyncCardExport(); };
        SyncCardExport();

        SettingsStorage.Apply(Settings, SettingsStorage.Load());
        Settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != null && SettingsStorage.IsPersistedProperty(e.PropertyName))
                SettingsStorage.Save(SettingsStorage.Capture(Settings));
        };

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
        if (e.PropertyName is nameof(SettingsViewModel.ExportMode) or nameof(SettingsViewModel.OutputFormat))
            RaiseSuggestedNameChanged();

        if (SettingsViewModel.IsUiOnlyProperty(e.PropertyName)) return;
        if (!HasActiveContent || IsBatchActive || Settings.IsBulkLoading) return;

        if (IsCollageMode)
        {
            ErrorMessage = null;
            IsRegenerateAvailable = true;
            return;
        }

        // Live preview, debounced; unchanged settings come from the cache. Raising ShowCardBody brings us back
        // here.
        if (!Settings.ShowCardBody) return;
        ScheduleLiveRender();
    }

    private const int LiveRenderDebounceMs = 300;

    private bool IsPreviewCurrent(string path)
    {
        return PaletteBitmap != null && _cachedSingleRenderOutput != null
            && _lastSingleRenderKey == CardRenderKey.From(path, Settings);
    }
    private CancellationTokenSource? _liveDebounceCts;

    private async void ScheduleLiveRender()
    {
        _liveDebounceCts?.Cancel();
        var cts = _liveDebounceCts = new CancellationTokenSource();
        try
        {
            await Task.Delay(LiveRenderDebounceMs, cts.Token);
        }
        catch (OperationCanceledException) { return; }

        if (cts.IsCancellationRequested || IsCollageMode || SelectedFile == null || IsBatchActive) return;
        // Same parameters as the last render.
        if (IsPreviewCurrent(SelectedFile.FullPath)) return;
        await GeneratePaletteAsync(SelectedFile, keepPrevious: true);
    }

    partial void OnIsListViewChanged(bool value) => OnPropertyChanged(nameof(IsGridView));

    partial void OnSelectedFileChanged(FileEntry? value)
    {
        RaiseSuggestedNameChanged();
        Settings.HasPhoto = value != null;
        _liveDebounceCts?.Cancel();
        OnPropertyChanged(nameof(FilePositionText));
        // File reads run off the UI thread, or switching photos stutters on phones.
        Settings.NotifyPhotoChanged();
        _ = LoadMetadataAsync(value);
        IsRegenerateAvailable = false; // Already current.
        // In collage mode only the tray drives the preview.
        if (!IsCollageMode)
        {
            Settings.SelectedPhotoCount = value != null ? 1 : 0;
            Settings.ExportMode = ExportModeRules.ClosestValidMode(Settings.ExportMode, value != null ? 1 : 0);
            // Only the card layout shows this card.
            if (Settings.ShowCardBody) _ = GeneratePaletteAsync(value);
            else { PaletteBitmap = null; }
        }
    }

    private async Task LoadMetadataAsync(FileEntry? file)
    {
        PaletteImageRenderer.PhotoMetadata? meta = null; FilmRecipe? recipe = null;
        if (file != null)
            (meta, recipe) = await Task.Run(() => (PaletteImageRenderer.ReadMetadata(file.FullPath), RecipeReader.Read(file.FullPath)));
        if (SelectedFile != file) return; // Switched again meanwhile.
        Settings.LoadMetadataFields(meta, recipe);
        Settings.NotifyPhotoChanged();
    }

    // Collage seeds the tray with the current photo; Card and Recipe close it first.
    private void HandleTopMode(int index)
    {
        if (index == 2)
        {
            if (!IsCollageMode)
            {
                EnterCollageMode();
                if (SelectedFile != null) SelectedFile.IsChecked = true;
            }
            return;
        }

        if (IsCollageMode) ExitCollageMode();
        Settings.ApplyTopMode(index);
    }

    [RelayCommand]
    private void ToggleView() => IsListView = !IsListView;

    [RelayCommand(CanExecute = nameof(CanOpenFolder))]
    private async Task OpenFolderAsync()
    {
        if (PickFolderAsync == null) return;
        var folder = await PickFolderAsync();
        if (folder == null) return;
        LoadFolder(folder);
    }

    public void LoadFolder(string folder)
    {
        // The tray refers to entries about to be cleared.
        if (IsCollageMode) ExitCollageMode();

        _thumbnailCts?.Cancel();
        // Files.Clear() alone would keep the old selection and render.
        SelectedFile = null;
        Files.Clear();

        foreach (var path in Directory.EnumerateFiles(folder)
            .Where(p => p.EndsWith(".jpg",  StringComparison.OrdinalIgnoreCase) ||
                        p.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                        p.EndsWith(".heif", StringComparison.OrdinalIgnoreCase) ||
                        p.EndsWith(".heic", StringComparison.OrdinalIgnoreCase) ||
                        p.EndsWith(".hif",  StringComparison.OrdinalIgnoreCase))
            .Where(p => !SuggestedFileName.IsGeneratedCard(p))
            .OrderBy(p => p))
        {
            var entry = new FileEntry(Path.GetFileName(path), path);
            entry.PropertyChanged += OnFileEntryPropertyChanged;
            Files.Add(entry);
        }

        _thumbnailCts = new CancellationTokenSource();
        _ = LoadThumbnailsAsync(_thumbnailCts.Token);

        // Show something after opening a folder.
        if (Files.Count > 0) SelectedFile = Files[0];
    }

    private void OnFileEntryPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FileEntry.IsExported)) { OnPropertyChanged(nameof(PhotosHeaderText)); return; }
        if (e.PropertyName != nameof(FileEntry.IsChecked) || sender is not FileEntry entry) return;

        if (entry.IsChecked)
        {
            if (!CollageItems.Contains(entry)) CollageItems.Add(entry);
        }
        else
        {
            CollageItems.Remove(entry);
        }
    }

    // Collage mode

    private bool CanEnterCollageMode() => !IsBatchActive && !IsCollageMode;

    [RelayCommand(CanExecute = nameof(CanEnterCollageMode))]
    private void EnterCollageMode()
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
        SavePngAsync2Command.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void ExitCollageMode()
    {
        IsCollageMode = false;
        Settings.ShowCollageOptions = false;
        foreach (var item in CollageItems.ToList()) item.IsChecked = false;
        CollageItems.Clear();
        IsRegenerateAvailable = false;
        _ = GeneratePaletteAsync(SelectedFile); // restore the single-photo preview
    }

    [RelayCommand]
    private void RemoveFromCollage(FileEntry entry) => entry.IsChecked = false; // → OnFileEntryPropertyChanged removes it

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

    // Layout-only preview, run on every tray edit. The newest of it and the full render wins.
    private async Task RenderCollagePreviewAsync()
    {
        _renderCts?.Cancel();
        _renderCts = new CancellationTokenSource();
        var token = _renderCts.Token;

        // A cancelled render leaves IsGenerating set, so clear it here.
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

        // A collage has no original; the previews are kept so the screen does not blank.
        OriginalBitmap = null;
        ErrorMessage   = null;
        _lastTempPng   = null;
        SavePngAsync2Command.NotifyCanExecuteChanged();
        RegenerateCommand.NotifyCanExecuteChanged();

        if (CollageItems.Count == 0) return;

        IsGenerating = true;
        try
        {
            var sourcePaths = CollageItems.Select(i => i.FullPath).ToList();
            FileInfoText = Loc.N(sourcePaths.Count, "Collage · {0} photo", "Collage · {0} photos");
            _lastExtension = Settings.FileExtension;
            var cacheKey = CardRenderKey.From(string.Join("\n", sourcePaths), Settings) with { Look = "" };
            var exportSettings = cacheKey.Settings;

            if (_lastCollageRenderKey == cacheKey && _cachedCollageRenderOutput != null && File.Exists(_cachedCollageRenderOutput))
            {
                _lastTempPng   = _cachedCollageRenderOutput;
                var cachedSize = new FileInfo(_lastTempPng).Length;
                var cachedFmt  = exportSettings.Format == OutputFormat.Jpeg ? "JPG" : "PNG";
                OutputSizeText = $"{cachedSize / 1_048_576.0:F1} MB {cachedFmt}";
                PaletteBitmap  = new Bitmap(_lastTempPng);
                return;
            }

            var tmpOut = NewTempCard();
            await PaletteExportService.ExportCollageAsync(sourcePaths, tmpOut, exportSettings, token,
                metadataOverride: cacheKey.Metadata, recipe: cacheKey.Recipe);

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
            ErrorMessage = ex.ToString();
            AppLogger.LogError("Collage generation failed", ex);
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

    private string NewTempCard() =>
        CardTempFiles.New(_lastExtension, _lastTempPng, _cachedSingleRenderOutput, _cachedCollageRenderOutput);

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

    public void SelectFirstFile() { if (Files.Count > 0) SelectedFile = Files[0]; }
    public void SelectLastFile()  { if (Files.Count > 0) SelectedFile = Files[^1]; }

    public void SelectNextFile()
    {
        if (Files.Count == 0) return;
        var idx = SelectedFile == null ? -1 : Files.IndexOf(SelectedFile);
        if (idx < Files.Count - 1) SelectedFile = Files[idx + 1];
    }

    // _lastTempPng is what is on screen, so the file matches the preview.
    [RelayCommand(CanExecute = nameof(CanSavePng))]
    private async Task SavePngAsync2()
    {
        // Not generated yet: build the file on demand.
        if (IsCollageMode && CollageItems.Count > 0 && (_lastTempPng == null || IsRegenerateAvailable))
        {
            await GenerateCollagePaletteAsync();
            IsRegenerateAvailable = false;
        }
        if (SavePngAsync == null || _lastTempPng is not { } rendered) return;

        string suggested, folder;
        if (IsCollageMode)
        {
            if (CollageItems.Count == 0) return;
            suggested = SuggestedFileName.BuildCollage(_lastExtension);
            folder = ResolveSaveFolder();
        }
        else
        {
            if (SelectedFile == null) return;
            suggested = SuggestedFileName.Build(SelectedFile.FileName, Settings.ExportMode, _lastExtension);
            folder = ResolveSaveFolder();
        }

        var dest = await SavePngAsync(suggested, folder);
        if (dest != null)
        {
            try { File.Copy(rendered, dest, overwrite: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                ErrorMessage = Loc.F("Couldn't save: {0}", e.Message);
                AppLogger.LogError("Saving the card failed", e);
                return;
            }
            ShowSavedToast(dest);
            if (!IsCollageMode && SelectedFile != null) SelectedFile.IsExported = true;
            if (Settings.ShowInFolderAfterSave) RevealInFolder(dest);
        }
    }

    private string ResolveSaveFolder()
    {
        if (!string.IsNullOrEmpty(Settings.SaveFolder) && Directory.Exists(Settings.SaveFolder)) return Settings.SaveFolder;
        var def = SuggestedFileName.DefaultOutputFolder();
        Settings.SaveFolder = def;
        return def;
    }

    [ObservableProperty] private bool   _isToastVisible;
    [ObservableProperty] private string _toastTitle = "";
    [ObservableProperty] private string _toastPathText = "";

    private void ShowSavedToast(string savedPath)
    {
        _toastPath = savedPath;
        var mb = new FileInfo(savedPath).Length / (1024.0 * 1024.0);
        ToastTitle = Loc.F("Saved · {0:0.0} MB", mb);
        ToastPathText = savedPath;
        IsToastVisible = true;

        _toastTimer?.Stop();
        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer!.Stop();
            IsToastVisible = false;
        };
        _toastTimer.Start();
    }

    [RelayCommand]
    private void DismissToast()
    {
        _toastTimer?.Stop();
        IsToastVisible = false;
    }

    [RelayCommand]
    private void ShowInFolder()
    {
        RevealInFolder(_toastPath);
        DismissToast();
    }

    internal static void RevealInFolder(string? path)
    {
        if (path == null || !File.Exists(path)) return;
        try
        {
            if (OperatingSystem.IsWindows())
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            else if (OperatingSystem.IsMacOS())
                Process.Start(new ProcessStartInfo("open", new[] { "-R", path }));
            else
                Process.Start(new ProcessStartInfo("xdg-open", new[] { Path.GetDirectoryName(path)! }));
        }
        catch { /* nothing useful to do if the file manager can't be launched */ }
    }

    private bool CanSavePng() =>
        (IsCollageMode ? CollageItems.Count > 0 : PaletteBitmap != null) && !IsGenerating && !IsBatchActive;

    private async Task GeneratePaletteAsync(FileEntry? entry, bool keepPrevious = false)
    {
        _renderCts?.Cancel();
        _renderCts = new CancellationTokenSource();
        var token = _renderCts.Token;

        ErrorMessage    = null;
        if (!keepPrevious || entry == null)
        {
            OriginalBitmap  = null;
            PaletteBitmap   = null;
            FileInfoText    = null;
            OutputSizeText  = null;
            _lastTempPng    = null;
            IsComparingToOriginal = false;
        }
        SavePngAsync2Command.NotifyCanExecuteChanged();
        RegenerateCommand.NotifyCanExecuteChanged();

        if (entry == null) return;

        IsGenerating = true;
        Image<Rgb24>? working = null;
        try
        {
            var filePath = entry.FullPath;

            // Decoded once for preview and render.
            working = await Task.Run(() =>
            {
                return Services.FastDecode.LoadUpright(filePath);
            }, token);
            token.ThrowIfCancellationRequested();
            // Keep the original preview for the same photo.
            if (!keepPrevious || OriginalBitmap == null)
            {
                using var previewMs = ImageLoader.ToJpegStream(working);
                OriginalBitmap = new Bitmap(previewMs);
                var ps = OriginalBitmap.PixelSize;
                Settings.SourceWidth  = working.Width;
                Settings.SourceHeight = working.Height;
                FileInfoText = $"{entry.FileName}  ·  {working.Width}×{working.Height}";
            }

            _lastExtension = Settings.FileExtension;
            var cacheKey = CardRenderKey.From(filePath, Settings);
            var exportSettings = cacheKey.Settings;
            var recipeForRender = Settings.ExportMode == ExportMode.Recipe ? cacheKey.Recipe : null;

            if (_lastSingleRenderKey == cacheKey && _cachedSingleRenderOutput != null && File.Exists(_cachedSingleRenderOutput))
            {
                _lastTempPng   = _cachedSingleRenderOutput;
                var cachedSize = new FileInfo(_lastTempPng).Length;
                var cachedFmt  = exportSettings.Format == OutputFormat.Jpeg ? "JPG" : "PNG";
                OutputSizeText = $"{cachedSize / 1_048_576.0:F1} MB {cachedFmt}";
                PaletteBitmap  = new Bitmap(_lastTempPng);
                return;
            }

            // Frame → Card is built from the cropped and graded photo.
            var cardAdjust = Settings.CardAdjust;
            var cardLook = Settings.CardLook;
            var source = working;
            var prepared = await Task.Run(() => PaletteExportService.Prepare(source, cardAdjust, cardLook));
            if (!ReferenceEquals(prepared, working)) { working.Dispose(); working = prepared; }
            token.ThrowIfCancellationRequested();

            var tmpOut = NewTempCard();
            if (recipeForRender != null)
            {
                var metadataOverride = cacheKey.Metadata;
                var qr = exportSettings.BuildQrRenderOptions(metadataOverride, recipeForRender);
                await Task.Run(() => RecipeCardRenderer.Render(working, recipeForRender, tmpOut,
                    theme: exportSettings.Theme, format: exportSettings.Format, customBackground: exportSettings.CustomBackground,
                    metaVerbosity: exportSettings.MetaVerbosity, metadataOverride: metadataOverride,
                    labelScale: exportSettings.LabelScale, useBlurredBackground: exportSettings.UseBlurredBackground, qr: qr,
                    showRecipeCard: exportSettings.ShowRecipeCard,
                    compositionGuide: exportSettings.CompositionGuide,
                    hiddenRecipeFields: exportSettings.HiddenRecipeFields, metaStyle: exportSettings.MetaStyle), token);
            }
            else
            {
                await PaletteExportService.ExportAsync(working, tmpOut, exportSettings, token,
                    metadataOverride: cacheKey.Metadata, recipe: cacheKey.Recipe);
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
            ErrorMessage = ex.ToString();
            AppLogger.LogError("Palette generation failed", ex);
        }
        finally
        {
            working?.Dispose();
            if (!token.IsCancellationRequested)
            {
                IsGenerating = false;
                SavePngAsync2Command.NotifyCanExecuteChanged();
            }
        }
    }

    [RelayCommand(CanExecute = nameof(CanSaveAll))]
    private async Task SaveAllAsync()
    {
        if (PickBatchFolderAsync == null || Files.Count == 0) return;

        var sourceFolder = Path.GetDirectoryName(Files[0].FullPath) ?? "";
        var defaultDir = _lastBatchDestFolder ?? Path.Combine(sourceFolder, "palettes");
        Directory.CreateDirectory(defaultDir);

        // Cancelling the picker changes nothing.
        var destFolder = await PickBatchFolderAsync(defaultDir);
        if (destFolder == null) return;

        _lastBatchDestFolder = destFolder;
        await RunBatchAsync(destFolder);
    }

    private bool CanSaveAll() => Files.Count > 0 && !IsBatchActive && !IsCollageMode;

    [RelayCommand(CanExecute = nameof(CanCancelBatch))]
    private void CancelBatch()
    {
        BatchState = BatchExportState.Cancelling;
        _batchCts?.Cancel();
    }

    private bool CanCancelBatch() => BatchState == BatchExportState.Running && !_batchCancelGuardActive;
    [ObservableProperty] private bool _showBatchErrorDetails;

    [RelayCommand]
    private void ShowBatchErrors() => ShowBatchErrorDetails = !ShowBatchErrorDetails;

    private DateTime _batchStartedAt;
    [ObservableProperty] private string _batchEtaText = "";

    public string BatchHeadline => Loc.F("Exporting {0} of {1}", Math.Min(BatchProcessed + 1, Math.Max(BatchTotal, 1)), BatchTotal);

    private void UpdateBatchEta()
    {
        OnPropertyChanged(nameof(BatchHeadline));
        if (BatchProcessed <= 0 || BatchProcessed >= BatchTotal) { BatchEtaText = ""; return; }
        var perItem = (DateTime.UtcNow - _batchStartedAt).TotalSeconds / BatchProcessed;
        var left = (int)Math.Ceiling(perItem * (BatchTotal - BatchProcessed));
        BatchEtaText = left >= 90 ? Loc.F("≈ {0} min left", left / 60) : Loc.F("≈ {0} s left", Math.Max(left, 1));
    }

    // A photo without a recipe is a failure, not a different card.
    private static void RenderRecipeCardFile(string sourcePath, string destPath, PaletteExportSettings s,
        PhotoAdjust? adjust)
    {
        var recipe = RecipeReader.Read(sourcePath)
            ?? throw new InvalidOperationException(Loc.T("No film recipe found in this photo"));
        using var loaded = Services.FastDecode.LoadUpright(sourcePath);
        using var adjusted = adjust is { IsNoOp: false } ? adjust.Render(loaded) : null;
        var img = adjusted ?? loaded;
        var metadata = PaletteImageRenderer.ReadMetadata(sourcePath);
        var qr = s.BuildQrRenderOptions(metadata, recipe);
        RecipeCardRenderer.Render(img, recipe, destPath,
            theme: s.Theme, format: s.Format, customBackground: s.CustomBackground,
            metaVerbosity: s.MetaVerbosity, metadataOverride: null,
            labelScale: s.LabelScale, useBlurredBackground: s.UseBlurredBackground, qr: qr,
            showRecipeCard: s.ShowRecipeCard, compositionGuide: s.CompositionGuide,
            hiddenRecipeFields: s.HiddenRecipeFields, metaStyle: s.MetaStyle);
    }

    private async Task RunBatchAsync(string destFolder)
    {
        _batchResultTimer?.Stop();

        var snapshot = Files.Select(f => f.FullPath).ToList();
        var exportedLookup = Files.ToDictionary(f => f.FullPath, f => f);
        var exportSettings = PaletteExportSettings.SnapshotFrom(Settings);
        var batchLook = Settings.CardLook;
        // Rotation, zoom and position belong to one photo; ratio and exposure apply to all.
        var batchAdjust = Settings.CardAdjust is { } ca ? ca with { Quarter = 0, Straighten = 0, Zoom = 1, PanX = 0, PanY = 0 } : null;
        _batchStartedAt = DateTime.UtcNow;
        BatchEtaText = "";

        _batchCts = new CancellationTokenSource();
        var cancelToken = _batchCts.Token;

        BatchErrors.Clear();
        ShowBatchErrorDetails = false;
        BatchTotal = snapshot.Count;
        BatchProcessed = 0;
        BatchCurrentFileName = snapshot.Count > 0 ? Path.GetFileName(snapshot[0]) : null;
        _batchCancelGuardActive = true;
        BatchState = BatchExportState.Running;
        StartCancelGuard();

        int saved = 0, failed = 0, overwritten = 0, processed = 0;
        bool diskFull = false;

        // Bounded: unlimited parallelism thrashes on 40 MP photos. Cancel only stops new files from starting.
        var degree = Math.Clamp(Environment.ProcessorCount / 2, 1, 4);
        using var gate = new SemaphoreSlim(degree);
        var tasks = new List<Task>();
        // Two sources with one name (a.jpg and a.heic) must not write the same card.
        var taken = new HashSet<string>();

        foreach (var sourcePath in snapshot)
        {
            if (cancelToken.IsCancellationRequested) break;

            try { await gate.WaitAsync(cancelToken); }
            catch (OperationCanceledException) { break; }

            var fileName = Path.GetFileName(sourcePath);
            var destPath = PhSpectre.Services.SafeFiles.Unused(destFolder,
                Path.GetFileNameWithoutExtension(sourcePath) + SuggestedFileName.ModeSuffix(exportSettings.Mode)
                + exportSettings.FileExtension, taken, avoidExisting: false);
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    bool existed = File.Exists(destPath);

                    if (exportSettings.Mode == ExportMode.Recipe)
                        await Task.Run(() => RenderRecipeCardFile(sourcePath, destPath, exportSettings, batchAdjust));
                    else
                        await PaletteExportService.ExportAsync(sourcePath, destPath, exportSettings, CancellationToken.None, look: batchLook, adjust: batchAdjust);

                    var exportedEntry = exportedLookup.GetValueOrDefault(sourcePath);
                    if (exportedEntry != null) await Dispatcher.UIThread.InvokeAsync(() => exportedEntry.IsExported = true);

                    Interlocked.Increment(ref saved);
                    if (existed) Interlocked.Increment(ref overwritten);
                }
                catch (IOException ex) when (IsDiskFull(ex))
                {
                    Interlocked.Increment(ref failed);
                    diskFull = true;
                    _batchCts?.Cancel();
                    AppLogger.LogError($"Batch export failed (disk full): {fileName}", ex);
                    await Dispatcher.UIThread.InvokeAsync(() => BatchErrors.Add(new BatchExportError(fileName, ex.Message)));
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref failed);
                    AppLogger.LogError($"Batch export failed: {fileName}", ex);
                    await Dispatcher.UIThread.InvokeAsync(() => BatchErrors.Add(new BatchExportError(fileName, ex.Message)));
                }
                finally
                {
                    int done = Interlocked.Increment(ref processed);
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        if (done > BatchProcessed) BatchProcessed = done;
                        UpdateBatchEta();
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
            BatchResultText = Loc.F("Stopped — disk full · saved {0} of {1}", saved, snapshot.Count);
            BatchState = BatchExportState.Finished;
        }
        else if (cancelToken.IsCancellationRequested)
        {
            BatchResultText = Loc.F("Cancelled · saved {0} of {1}", saved, snapshot.Count);
            BatchState = BatchExportState.Cancelled;
        }
        else
        {
            var overwriteNote = overwritten > 0 ? Loc.F(", overwritten: {0}", overwritten) : "";
            BatchResultText = failed > 0
                ? Loc.F("Saved {0} of {1} · {2} failed{3}", saved, snapshot.Count, failed, overwriteNote)
                : Loc.F("Saved {0} of {1}{2}", saved, snapshot.Count, overwriteNote);
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

    // ERROR_HANDLE_DISK_FULL and ERROR_DISK_FULL
    private static bool IsDiskFull(IOException ex) => (ex.HResult & 0xFFFF) is 39 or 112;
}
