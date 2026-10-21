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

    // Single-photo preview now shows one frame, not two — this picks which bitmap it shows.
    // Only meaningful outside collage mode (collage has no single "original" to compare to).
    public Bitmap? DisplayedSingleBitmap => IsComparingToOriginal ? OriginalBitmap : PaletteBitmap;

    partial void OnIsComparingToOriginalChanged(bool value) { OnPropertyChanged(nameof(DisplayedSingleBitmap)); OnPropertyChanged(nameof(CompareIndex)); }
    partial void OnOriginalBitmapChanged(Bitmap? value)      { OnPropertyChanged(nameof(DisplayedSingleBitmap)); OnPropertyChanged(nameof(CanCompare)); OnPropertyChanged(nameof(ShowPill)); }

    [RelayCommand]
    private void ToggleCompareToOriginal() => IsComparingToOriginal = !IsComparingToOriginal;

    // Result | Original segment in the floating pill (replaces the old Compare button).
    public int CompareIndex
    {
        get => IsComparingToOriginal ? 1 : 0;
        set => IsComparingToOriginal = value == 1;
    }
    public bool CanCompare => !IsCollageMode && OriginalBitmap != null;

    // Preview error card (V2-Errors): the message stays out of the photo, the last good
    // result stays visible underneath, and the user can copy the details or retry.
    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    [RelayCommand]
    private void DismissError() => ErrorMessage = null;

    [RelayCommand]
    private void RetryRender()
    {
        if (IsCollageMode) _ = GenerateCollagePaletteAsync();
        else if (SelectedFile != null) _ = GeneratePaletteAsync(SelectedFile, keepPrevious: true);
    }

    // Filmstrip header and footer texts.
    public string PhotosHeaderText
    {
        get
        {
            var exported = Files.Count(f => f.IsExported);
            return exported > 0
                ? $"{Files.Count} PHOTOS · {exported} EXPORTED"
                : $"{Files.Count} PHOTOS";
        }
    }
    public bool HasPhotos => Files.Count > 0;
    public string EmptyStageText => !HasPhotos ? "Open a folder of photos to begin"
        : IsCollageMode ? "Tick two or more photos in the strip below" : "Pick a photo from the strip below";

    // File name the Save dialog will propose, shown above the Save button in the Export tab.
    public string SuggestedNameText => IsCollageMode
        ? SuggestedFileName.BuildCollage(Settings.FileExtension)
        : SuggestedFileName.Build(SelectedFile?.FileName, Settings.ExportMode, Settings.FileExtension);

    private void RaiseSuggestedNameChanged() => OnPropertyChanged(nameof(SuggestedNameText));
    [ObservableProperty] private string?    _errorMessage;
    [ObservableProperty] private bool       _isListView = false;
    [ObservableProperty] private string?    _fileInfoText;
    [ObservableProperty] private string?    _outputSizeText;
    [ObservableProperty] private bool       _isRegenerateAvailable;

    // Collage mode — CollageItems is the tray: FileEntry.IsChecked (list/grid checkboxes)
    // adds/removes entries, then reordering happens independently within the tray. Order
    // matters twice over: it's both the row-packing order and (for the first item) the
    // EXIF source, so there's no separate "main photo" control.
    [ObservableProperty] private bool _isCollageMode;
    [ObservableProperty] private ObservableCollection<FileEntry> _collageItems = [];

    // Cheap "just the layout" preview (no palette/card) that updates live as the tray is
    // edited — PaletteBitmap only ever holds a real, expensive full render, triggered
    // explicitly via Generate/Regenerate. DisplayedCollageBitmap is what the view actually
    // shows: the full render once one exists, falling back to the live preview otherwise.
    [ObservableProperty] private Bitmap? _collagePreviewBitmap;
    public Bitmap? DisplayedCollageBitmap => PaletteBitmap ?? CollagePreviewBitmap;

    // The collage Regenerate button reads "Generate" until the first real render exists,
    // then "Regenerate" from then on — it's a materially different action the first time
    // (nothing has been produced yet) versus a settings/tray change touching a real result.
    public string RegenerateButtonLabel => IsCollageMode && PaletteBitmap == null ? "Generate" : "Regenerate";

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

    public bool ShowResultPlaceholder =>
        !IsGenerating && string.IsNullOrEmpty(ErrorMessage) &&
        (IsCollageMode ? DisplayedCollageBitmap == null : PaletteBitmap == null);

    // Left preview pane has no single-photo equivalent in collage mode — it's hidden
    // entirely there (the tray above already shows the sources) rather than shown with
    // an explanatory placeholder.
    public string OriginalPanePlaceholderText => "No photo selected";

    // Live preview: while a re-render runs, the previous result stays on screen dimmed with an
    // "Updating…" pill instead of blanking out. "Generating…" is only for the very first render.
    public bool ShowUpdatingPill        => IsGenerating && PaletteBitmap != null && !IsCollageMode;
    public bool ShowPill => ShowUpdatingPill || CanCompare;
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
    partial void OnIsRegenerateAvailableChanged(bool value) => RegenerateCommand.NotifyCanExecuteChanged();

    partial void OnIsCollageModeChanged(bool value)
    {
        OnPropertyChanged(nameof(EmptyStageText));
        OnPropertyChanged(nameof(CanCompare)); OnPropertyChanged(nameof(ShowPill));
        RaiseSuggestedNameChanged();
        OnPropertyChanged(nameof(ShowSaveAllButton));
        OnPropertyChanged(nameof(OriginalPanePlaceholderText));
        OnPropertyChanged(nameof(ShowResultPlaceholder));
        OnPropertyChanged(nameof(RegenerateButtonLabel));
        RegenerateCommand.NotifyCanExecuteChanged();
        SaveAllCommand.NotifyCanExecuteChanged();
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
            if (Files.Count == 0) return "No photos";
            if (SelectedFile == null) return $"{Files.Count} photos";
            var idx = Files.IndexOf(SelectedFile) + 1;
            return $"{idx} of {Files.Count} photos";
        }
    }

    // Batch computed state — the button labels/visibility/enablement all flow from
    // BatchState, never toggled directly from code-behind.
    public bool IsBatchActive     => BatchState is BatchExportState.Running or BatchExportState.Cancelling;
    // Batch export stays single-photo only for v1 — collage mode hides it entirely rather
    // than leaving a button that's always disabled.
    public bool ShowSaveAllButton => !IsBatchActive && !IsCollageMode;
    public bool ShowBatchProgress => IsBatchActive;
    public bool ShowBatchResult   => BatchState is BatchExportState.Finished or BatchExportState.Cancelled;
    public bool BatchHasErrors    => BatchErrors.Count > 0;
    public string IdleButtonText   => Files.Count == 1 ? "Save all · 1 photo" : $"Save all · {Files.Count} photos";
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
    partial void OnBatchProcessedChanged(int value) { OnPropertyChanged(nameof(BatchProgressPercent)); OnPropertyChanged(nameof(BatchProgressText)); OnPropertyChanged(nameof(BatchHeadline)); }
    partial void OnBatchTotalChanged(int value)     { OnPropertyChanged(nameof(BatchProgressPercent)); OnPropertyChanged(nameof(BatchProgressText)); OnPropertyChanged(nameof(BatchHeadline)); }

    public Func<Task<string?>>?                 PickFolderAsync      { get; set; }
    public Func<string, string, Task<string?>>? SavePngAsync         { get; set; }
    public Func<string, Task<string?>>?          PickBatchFolderAsync { get; set; }
    public Func<IReadOnlyList<BatchExportError>, Task>? ShowBatchErrorsAsync { get; set; }

    private string?                  _lastTempPng;
    private string                   _lastExtension = ".png";

    // Single-slot memoization: if a Generate/Regenerate is triggered for the exact same
    // (photo, settings — ExportMode included) combination as the last successful render,
    // reuse that output instead of recomputing (skips k-means again on a no-op click). Only
    // one slot is needed — the app always has exactly one active single-photo selection or
    // collage tray at a time, never several to remember at once.
    private (string Path, PaletteExportSettings Settings, FilmRecipe? Recipe)? _lastSingleRenderKey;
    private string?                                        _cachedSingleRenderOutput;
    private (string Paths, PaletteExportSettings Settings)? _lastCollageRenderKey;
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

        // [ObservableProperty]'s generated On{X}Changed only fires on reassigning the whole
        // BatchErrors reference — but Add()/Clear() mutate the same instance, so BatchHasErrors
        // needs its own subscription to actually track them.
        _batchErrors.CollectionChanged += (_, _) => OnPropertyChanged(nameof(BatchHasErrors));

        // Editing the tray does update the on-screen collage instantly — but only the cheap
        // layout preview (RenderCollagePreviewAsync: decode + arrange, no palette). The
        // expensive part (pooled k-means over all sources) never runs automatically; it's
        // gated behind Generate/Regenerate, same as any other settings change, since running
        // it on every single checkbox click made the UI stutter/freeze while assembling a tray.
        _collageItems.CollectionChanged += (_, _) =>
        {
            for (int i = 0; i < CollageItems.Count; i++)
                CollageItems[i].IsCollageExifSource = i == 0;

            RegenerateCommand.NotifyCanExecuteChanged();
            if (!IsCollageMode) return;

            Settings.SelectedPhotoCount = CollageItems.Count;
            Settings.ExportMode = ExportModeRules.ClosestValidMode(Settings.ExportMode, CollageItems.Count);

            // The first tray item's EXIF represents the whole collage and feeds the
            // editable "Edit metadata" fields in Settings, same as selecting a single
            // photo does — only reload when that first item's identity actually changes,
            // so reordering the rest of the tray doesn't clobber an in-progress edit.
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
            SavePngAsync2Command.NotifyCanExecuteChanged();

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
        };

        // The settings sidebar is always visible and applies live, but changing a setting
        // never re-renders on its own — that's a forced/surprising cost the user explicitly
        // didn't want. Instead a settings change just surfaces the Regenerate button; the
        // actual re-render only happens when the user clicks it.
        Settings.PropertyChanged += OnSettingsPropertyChanged;
        Settings.TopModeHandler = HandleTopMode;

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

        // Collage is a heavy render, so it keeps the manual Generate/Regenerate button.
        if (IsCollageMode)
        {
            ErrorMessage = null;
            IsRegenerateAvailable = true;
            return;
        }

        // Single photo: live preview. Any settings change re-renders after a short pause so a
        // burst of edits (typing a hex, dragging a slider) produces one render. Changes that
        // don't alter the export snapshot are served from the single-slot render cache.
        ScheduleLiveRender();
    }

    private const int LiveRenderDebounceMs = 300;

    private bool IsPreviewCurrent(string path)
    {
        var recipe = Settings.ExportMode == ExportMode.Recipe ? Settings.DetectedRecipe : null;
        return PaletteBitmap != null && _cachedSingleRenderOutput != null
            && Equals(_lastSingleRenderKey, (path, PaletteExportSettings.SnapshotFrom(Settings), recipe));
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
        // Same parameters as the last finished render: nothing to rebuild (and no "Updating…" flash).
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
        Settings.LoadMetadataFields(
            value != null ? PaletteImageRenderer.ReadMetadata(value.FullPath) : null,
            value != null ? RecipeReader.Read(value.FullPath) : null);
        IsRegenerateAvailable = false; // the fresh render below is already current — nothing to regenerate yet
        // In collage mode, clicking a row to check/inspect it shouldn't steal the big preview
        // away from the collage — only the tray (CollageItems) drives collage rendering.
        if (!IsCollageMode)
        {
            Settings.SelectedPhotoCount = value != null ? 1 : 0;
            Settings.ExportMode = ExportModeRules.ClosestValidMode(Settings.ExportMode, value != null ? 1 : 0);
            _ = GeneratePaletteAsync(value);
        }
    }

    // Top-bar Card / Recipe / Collage segment. Collage opens the tray (seeded with the photo
    // currently on screen); Card/Recipe close it first, then set the single-photo mode.
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

    private void LoadFolder(string folder)
    {
        // A different folder invalidates whatever's currently in the tray (it references
        // FileEntry objects from the folder about to be cleared below).
        if (IsCollageMode) ExitCollageMode();

        _thumbnailCts?.Cancel();
        // Drop the old selection and render state first: Files.Clear() alone leaves
        // SelectedFile/PaletteBitmap pointing at the previous folder, and the mode/Save-label
        // state then disagreed with the (now empty) list.
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

        // Never leave the centre on "No result yet" after opening a folder.
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

    // ── Collage mode ─────────────────────────────────────────────────────────────

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

    // Cheap "just the layout" preview — no palette extraction, no card, just decode +
    // arrange with the gutter. Runs on every tray edit (see the CollectionChanged handler
    // above) so the collage always looks assembled immediately; shares _renderCts with the
    // full render so whichever starts most recently (a tray edit or a Generate click) wins.
    private async Task RenderCollagePreviewAsync()
    {
        _renderCts?.Cancel();
        _renderCts = new CancellationTokenSource();
        var token = _renderCts.Token;

        // Whatever this cancelled (a stale single-photo/full-collage render) leaves
        // IsGenerating stuck true — its own finally block only clears it when its token
        // *wasn't* cancelled, precisely to avoid a cancelled task clobbering a newer one's
        // "Generating…" state. But this preview never sets IsGenerating itself, so nothing
        // would ever clear it otherwise, and both the "Generating…" text and the
        // Generate/Regenerate button (CanRegenerate requires !IsGenerating) would get stuck.
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

        // Left preview pane (single-photo "original") has no single equivalent for a
        // collage — the tray thumbnails already show the sources, so it's just left blank.
        // PaletteBitmap/CollagePreviewBitmap are deliberately left alone here — clearing
        // them would blank the preview for the whole duration of the render instead of
        // just showing "Generating…" over whatever was already there.
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
            FileInfoText = $"Collage · {sourcePaths.Count} photos";

            _lastExtension = Settings.FileExtension;
            var exportSettings = PaletteExportSettings.SnapshotFrom(Settings);
            var cacheKey = (string.Join("", sourcePaths), exportSettings);

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
            await PaletteExportService.ExportCollageAsync(sourcePaths, tmpOut, exportSettings, token,
                metadataOverride: Settings.BuildMetadataOverride(), recipe: Settings.DetectedRecipe);

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

    // Single unified save button (bugfix §1) — one command for Save PNG/JPEG and Save recipe
    // card alike. _lastTempPng already holds whichever composite is currently on screen
    // (RecipeCardRenderer's output in Recipe mode, PaletteImageRenderer's/CollageService's
    // otherwise — see GeneratePaletteAsync/GenerateCollagePaletteAsync), so saving it verbatim
    // guarantees the exported file always matches the preview, with no separate re-render.
    [RelayCommand(CanExecute = nameof(CanSavePng))]
    private async Task SavePngAsync2()
    {
        if (SavePngAsync == null || _lastTempPng == null) return;

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
            File.Copy(_lastTempPng, dest, overwrite: true);
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

    // ── "Saved" toast ───────────────────────────────────────────────────────────────

    [ObservableProperty] private bool   _isToastVisible;
    [ObservableProperty] private string _toastTitle = "";
    [ObservableProperty] private string _toastPathText = "";

    private void ShowSavedToast(string savedPath)
    {
        _toastPath = savedPath;
        var mb = new FileInfo(savedPath).Length / (1024.0 * 1024.0);
        ToastTitle = $"Saved · {mb:0.0} MB";
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

    private static void RevealInFolder(string? path)
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

    private bool CanSavePng() => PaletteBitmap != null && !IsGenerating && !IsBatchActive;

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

            // Decoded once and reused below for the render (Recipe or the normal palette
            // pipeline) instead of handing those a path and letting each re-decode the same
            // full-resolution file from disk again — see PaletteExportService.ExportAsync's
            // matching comment for why that redundant decode is worth avoiding.
            working = await Task.Run(() =>
            {
                var img = Image.Load<Rgb24>(filePath);
                img.Mutate(ctx => ctx.AutoOrient());
                return img;
            }, token);
            token.ThrowIfCancellationRequested();
            // Live re-renders of the same photo keep the already-built "original" preview.
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
            var exportSettings = PaletteExportSettings.SnapshotFrom(Settings);
            // Recipe mode's composite depends on the current recipe too (which can change
            // independently via manual edit without touching any PaletteExportSettings
            // field), so it has to be part of the cache key or an edit while staying on the
            // same photo/settings would serve a stale render.
            var recipeForRender = Settings.ExportMode == ExportMode.Recipe ? Settings.DetectedRecipe : null;
            var cacheKey = (filePath, exportSettings, recipeForRender);

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
                // Recipe mode: the main preview must be the exact same composite Save recipe
                // card would export — reuse RecipeCardRenderer directly rather than the
                // palette pipeline. exportSettings.MetaVerbosity already reflects the "Show
                // camera info plate" toggle (Off when unchecked — see PaletteExportSettings.
                // SnapshotFrom), so the checkbox reaches the render params here (bugfix §2).
                var metadataOverride = Settings.BuildMetadataOverride();
                var qr = exportSettings.BuildQrRenderOptions(
                    metadataOverride ?? PaletteImageRenderer.ReadMetadata(working), recipeForRender);
                await Task.Run(() => RecipeCardRenderer.Render(working, recipeForRender, tmpOut,
                    theme: exportSettings.Theme, format: exportSettings.Format, customBackground: exportSettings.CustomBackground,
                    metaVerbosity: exportSettings.MetaVerbosity, metadataOverride: metadataOverride,
                    labelScale: exportSettings.LabelScale, useBlurredBackground: exportSettings.UseBlurredBackground, qr: qr,
                    showRecipeCard: exportSettings.ShowRecipeCard,
                    compositionGuide: exportSettings.CompositionGuide,
                    hiddenRecipeFields: exportSettings.HiddenRecipeFields), token);
            }
            else
            {
                await PaletteExportService.ExportAsync(working, tmpOut, exportSettings, token,
                    metadataOverride: Settings.BuildMetadataOverride(), recipe: Settings.DetectedRecipe);
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

    private bool CanSaveAll() => Files.Count > 0 && !IsBatchActive && !IsCollageMode;

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

    // ── Batch progress presentation ("Exporting 2 of 4 · ≈ 12 s left") ─────────────────────
    private DateTime _batchStartedAt;
    [ObservableProperty] private string _batchEtaText = "";

    public string BatchHeadline => $"Exporting {Math.Min(BatchProcessed + 1, Math.Max(BatchTotal, 1))} of {BatchTotal}";

    private void UpdateBatchEta()
    {
        OnPropertyChanged(nameof(BatchHeadline));
        if (BatchProcessed <= 0 || BatchProcessed >= BatchTotal) { BatchEtaText = ""; return; }
        var perItem = (DateTime.UtcNow - _batchStartedAt).TotalSeconds / BatchProcessed;
        var left = (int)Math.Ceiling(perItem * (BatchTotal - BatchProcessed));
        BatchEtaText = left >= 90 ? $"≈ {left / 60} min left" : $"≈ {Math.Max(left, 1)} s left";
    }

    // One file of a Save all in Recipe mode: each photo's own film recipe, same renderer and
    // options as the single-photo Recipe preview. A photo without a recipe is reported as a
    // failure (shown under "Details") instead of silently exporting a different card.
    private static void RenderRecipeCardFile(string sourcePath, string destPath, PaletteExportSettings s)
    {
        var recipe = RecipeReader.Read(sourcePath)
            ?? throw new InvalidOperationException("No film recipe found in this photo");
        using var img = Image.Load<Rgb24>(sourcePath);
        img.Mutate(ctx => ctx.AutoOrient());
        var metadata = PaletteImageRenderer.ReadMetadata(sourcePath);
        var qr = s.BuildQrRenderOptions(metadata, recipe);
        RecipeCardRenderer.Render(img, recipe, destPath,
            theme: s.Theme, format: s.Format, customBackground: s.CustomBackground,
            metaVerbosity: s.MetaVerbosity, metadataOverride: null,
            labelScale: s.LabelScale, useBlurredBackground: s.UseBlurredBackground, qr: qr,
            showRecipeCard: s.ShowRecipeCard, compositionGuide: s.CompositionGuide,
            hiddenRecipeFields: s.HiddenRecipeFields);
    }

    private async Task RunBatchAsync(string destFolder)
    {
        _batchResultTimer?.Stop();

        var snapshot = Files.Select(f => f.FullPath).ToList();
        var exportedLookup = Files.ToDictionary(f => f.FullPath, f => f);
        var exportSettings = PaletteExportSettings.SnapshotFrom(Settings);
        _batchStartedAt = DateTime.UtcNow;
        BatchEtaText = "";

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
                    var destName = Path.GetFileNameWithoutExtension(sourcePath) + SuggestedFileName.ModeSuffix(exportSettings.Mode) + exportSettings.FileExtension;
                    var destPath = Path.Combine(destFolder, destName);
                    bool existed = File.Exists(destPath);

                    if (exportSettings.Mode == ExportMode.Recipe)
                        await Task.Run(() => RenderRecipeCardFile(sourcePath, destPath, exportSettings));
                    else
                        await PaletteExportService.ExportAsync(sourcePath, destPath, exportSettings, CancellationToken.None);

                    var exportedEntry = exportedLookup.GetValueOrDefault(sourcePath);
                    if (exportedEntry != null) await Dispatcher.UIThread.InvokeAsync(() => exportedEntry.IsExported = true);

                    Interlocked.Increment(ref saved);
                    if (existed) Interlocked.Increment(ref overwritten);
                }
                catch (IOException ex) when (IsDiskFull(ex))
                {
                    Interlocked.Increment(ref failed);
                    diskFull = true;
                    _batchCts?.Cancel(); // stop starting further files
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
            BatchResultText = $"Stopped — disk full · saved {saved} of {snapshot.Count}";
            BatchState = BatchExportState.Finished;
        }
        else if (cancelToken.IsCancellationRequested)
        {
            BatchResultText = $"Cancelled · saved {saved} of {snapshot.Count}";
            BatchState = BatchExportState.Cancelled;
        }
        else
        {
            var overwriteNote = overwritten > 0 ? $", overwritten: {overwritten}" : "";
            BatchResultText = failed > 0
                ? $"Saved {saved} of {snapshot.Count} · {failed} failed{overwriteNote}"
                : $"Saved {saved} of {snapshot.Count}{overwriteNote}";
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
