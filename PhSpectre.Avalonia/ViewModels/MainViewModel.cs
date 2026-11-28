using PhSpectre.Localization;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
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

/// <summary>The mobile flow: pick photos, edit, save. Settings live in an in-view panel.</summary>
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

    // On-screen performance overlay; set to true to show it.
    private const bool DebugPerfOverlayEnabled = false;
    [ObservableProperty] private string? _debugPerfText;
    public bool ShowDebugPerfOverlay => DebugPerfOverlayEnabled && !string.IsNullOrEmpty(DebugPerfText);
    partial void OnDebugPerfTextChanged(string? value) => OnPropertyChanged(nameof(ShowDebugPerfOverlay));

    // Collage tray of temp copies. Order is the packing order; the first item supplies the EXIF.
    [ObservableProperty] private bool _isCollageMode;
    [ObservableProperty] private ObservableCollection<FileEntry> _collageItems = [];

    // Photos picked this session, newest first, capped to bound disk use.
    [ObservableProperty] private ObservableCollection<FileEntry> _recentPhotos = [];
    private const int MaxRecentPhotos = 8;
    public bool HasRecentPhotos => RecentPhotos.Count > 0;

    // Layout-only preview, shown until the full render is ready.
    [ObservableProperty] private Bitmap? _collagePreviewBitmap;
    public Bitmap? DisplayedCollageBitmap => PaletteBitmap ?? CollagePreviewBitmap;

    [ObservableProperty] private bool _isComparingToOriginal;
    public Bitmap? DisplayedResultBitmap =>
        IsComparingToOriginal && !IsCollageMode && OriginalBitmap != null ? OriginalBitmap : DisplayedCollageBitmap;
    public bool CanCompareToOriginal => !IsCollageMode && OriginalBitmap != null;

    partial void OnIsComparingToOriginalChanged(bool value) { OnPropertyChanged(nameof(DisplayedResultBitmap)); OnPropertyChanged(nameof(CompareIndex)); }

    [RelayCommand]
    private void ToggleCompareToOriginal() => IsComparingToOriginal = !IsComparingToOriginal;

    public string RegenerateButtonLabel => IsCollageMode && PaletteBitmap == null ? Loc.T("Generate") : Loc.T("Regenerate");

    public Func<Task<(Stream Stream, string FileName)?>>?              PickImageAsync  { get; set; }
    public Func<Task<IReadOnlyList<(Stream Stream, string FileName)>>>? PickImagesAsync { get; set; }
    public Func<string, string, Task<bool>>?                            SavePngAsync    { get; set; }

    private string?                  _lastTempPng;
    private string?                  _lastFileName;
    private string                   _lastExtension = ".png";
    private CancellationTokenSource? _renderCts;

    // The last render is reused while photo, tray and settings are unchanged.
    private (string Path, PaletteExportSettings Settings, FilmRecipe? Recipe, string Look)? _lastSingleRenderKey;
    private string?                                        _cachedSingleRenderOutput;
    private (string Paths, PaletteExportSettings Settings)? _lastCollageRenderKey;
    private string?                                          _cachedCollageRenderOutput;

    private string? _currentSourcePath;
    private string? _collageExifSourcePath;
    private bool     _suspendTrayNotifications;

    // Overlapping runs would see a stale IsCollageMode and drop the second tap.
    private bool _isSyncingCollageMode;
    private bool _collageSyncPending;

    public MainViewModel()
    {
        // No photo and no tray: every export mode chip is shown.
        Settings.SelectedPhotoCount = 0;
        Settings.Process.PhoneStage = true;

        _recentPhotos.CollectionChanged += (_, _) =>
        {
            RaiseShellTextsChanged();
            OnPropertyChanged(nameof(HasRecentPhotos));
            OnPropertyChanged(nameof(ShowEmptyBrowserGrid));
            OnPropertyChanged(nameof(ShowEmptyDashedPlaceholder));
            OnPropertyChanged(nameof(ShowRecentStrip));
            OnPropertyChanged(nameof(ShowFullWidthStartButtons));
            OnPropertyChanged(nameof(ShowCompactActionBar));
            OnPropertyChanged(nameof(ShowFilmstrip));
        };

        // A settings change only offers Regenerate.
        Settings.PropertyChanged += OnSettingsPropertyChanged;
        Settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(SettingsViewModel.ShowCardBody) or nameof(SettingsViewModel.IsEditMode)) OnPropertyChanged(nameof(ShowTabletExport));
        };
        // Load the saved working quality before the handler is attached.
        Settings.WorkingQuality = SettingsStorage.Load().WorkingQuality;
        Settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SettingsViewModel.WorkingQuality))
            {
                var s = SettingsStorage.Load();
                s.WorkingQuality = Settings.WorkingQuality;
                SettingsStorage.Save(s);
            }
        };
        Settings.PhotosProvider = () => RecentPhotos;
        Settings.CurrentPhotoProvider = () => RecentPhotos.FirstOrDefault(r => r.IsCurrent);
        Settings.SelectPhotoAction = f => SelectRecentPhotoCommand.Execute(f);
        Settings.OpenPhotosCommand = PickPhotoCommand;
        Settings.CardSaveCommand = SavePngCommand;

        // Tray edits refresh the layout preview; the palette waits for Generate. _suspendTrayNotifications batches
        // a multi-photo pick.
        _collageItems.CollectionChanged += (_, _) =>
        {
            if (!_suspendTrayNotifications) OnCollageTrayChanged();
        };
    }

    // IsWide (>= 840 dp) is the tablet layout with a side inspector; narrower screens use a bottom sheet.
    [ObservableProperty] private bool _isWide;
    public bool ShowTabletExport => IsWide && (Settings.ShowCardBody || Settings.IsEditMode);
    [ObservableProperty] private bool _isSheetOpen;

    public bool IsPhone   => !IsWide;
    public bool ShowSheet => IsSheetOpen && !IsWide;
    /// <summary>Phone: the photo strip steps aside while the sheet is open, so the sheet gets the room.</summary>
    private void SyncSheetState()
    {
        OnPropertyChanged(nameof(ShowSheet)); OnPropertyChanged(nameof(ShowFilmstrip)); OnPropertyChanged(nameof(ShowPhoneActions));
        Settings.Process.CompareHidden = ShowSheet;
    }
    // A strip with one photo only takes room on a phone.
    public bool ShowFilmstrip => !IsCollageMode && !ShowSheet && (IsWide || RecentPhotos.Count != 1);
    public bool ShowPhoneActions => IsPhone && !IsSheetOpen;
    public global::Avalonia.Thickness CardStageMargin => IsPhone ? new(6, 6, 6, 52) : new(16, 16, 16, 64);

    partial void OnIsWideChanged(bool value)
    {
        OnPropertyChanged(nameof(IsPhone));
        OnPropertyChanged(nameof(CardStageMargin));
        Settings.Process.PhoneStage = !value;
        OnPropertyChanged(nameof(ShowTabletExport));
        SyncSheetState();
    }
    partial void OnIsSheetOpenChanged(bool value) { SyncSheetState(); }

    [RelayCommand]
    private void OpenAdjust()
    {
        Settings.ActiveTab = InspectorTab.Style;
        IsSheetOpen = true;
    }

    [RelayCommand]
    private void OpenExport()
    {
        if (Settings.IsEditMode) Settings.EditTab = 2;
        else Settings.ActiveTab = InspectorTab.Export;
        IsSheetOpen = true;
    }

    [RelayCommand]
    private void CloseSheet() => IsSheetOpen = false;

    public int CompareIndex
    {
        get => IsComparingToOriginal ? 1 : 0;
        set => IsComparingToOriginal = value == 1;
    }
    public bool CanCompare => CanCompareToOriginal;

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    [RelayCommand]
    private void DismissError() => ErrorMessage = null;

    [RelayCommand]
    private void RetryRender()
    {
        if (IsCollageMode) _ = GenerateCollagePaletteAsync();
        else _ = RenderCurrentAsync(keepPrevious: true);
    }

    public string PhotosHeaderText => IsCollageMode
        ? Loc.N(CollageItems.Count, "{0} PHOTO", "{0} PHOTOS")
        : Loc.N(RecentPhotos.Count, "{0} PHOTO", "{0} PHOTOS");

    public string SuggestedNameText => IsCollageMode
        ? SuggestedFileName.BuildCollage(Settings.FileExtension)
        : SuggestedFileName.Build(_lastFileName, Settings.ExportMode, Settings.FileExtension);

    // Toast after a successful save.
    public string SavedToastText => Loc.F("Saved · {0}", OutputSizeText);

    private void RaiseShellTextsChanged()
    {
        OnPropertyChanged(nameof(PhotosHeaderText));
        OnPropertyChanged(nameof(SuggestedNameText));
        OnPropertyChanged(nameof(EmptyStageText));
    }

    private void OnCollageTrayChanged()
    {
        RaiseShellTextsChanged();
        for (int i = 0; i < CollageItems.Count; i++)
            CollageItems[i].IsCollageExifSource = i == 0;

        RegenerateCommand.NotifyCanExecuteChanged();
        if (!IsCollageMode) return;

        // At least 2 in collage mode, so the chips do not flip while the tray holds 0 or 1 photos.
        Settings.SelectedPhotoCount = Math.Max(CollageItems.Count, 2);

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
        SavePngCommand.NotifyCanExecuteChanged();

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
    }

    private bool HasActiveContent => IsCollageMode ? CollageItems.Count > 0 : _currentSourcePath != null;

    private void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SettingsViewModel.ExportMode) or nameof(SettingsViewModel.OutputFormat))
            OnPropertyChanged(nameof(SaveButtonText));
        if (e.PropertyName is nameof(SettingsViewModel.ExportMode) or nameof(SettingsViewModel.OutputFormat))
            OnPropertyChanged(nameof(SuggestedNameText));

        if (e.PropertyName == nameof(SettingsViewModel.ExportMode))
        {
            OnPropertyChanged(nameof(IsCollageExportMode));
            OnPropertyChanged(nameof(MainPhotoButtonLabel));
            _ = SyncCollageModeWithExportModeAsync();
            return;
        }

        if (SettingsViewModel.IsUiOnlyProperty(e.PropertyName)) return;
        if (!HasActiveContent || Settings.IsBulkLoading) return;

        if (IsCollageMode)
        {
            // Switching tabs changes nothing in the collage.
            if (e.PropertyName == nameof(SettingsViewModel.ShowCardBody)) return;
            ErrorMessage = null;
            if (!IsGenerating) IsRegenerateAvailable = true;
            return;
        }

        // Debounced live preview; the previous result stays visible.
        if (!Settings.ShowCardBody) return;
        ScheduleLiveRender();
    }

    private const int LiveRenderDebounceMs = 300;

    private bool IsPreviewCurrent(string path)
    {
        var recipe = Settings.ExportMode == ExportMode.Recipe ? Settings.DetectedRecipe : null;
        return PaletteBitmap != null && _cachedSingleRenderOutput != null
            && Equals(_lastSingleRenderKey, (path, PaletteExportSettings.SnapshotFrom(Settings), recipe, Settings.CardLookKey));
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

        if (cts.IsCancellationRequested || IsCollageMode || _currentSourcePath == null) return;
        // Same parameters as the last render.
        if (IsPreviewCurrent(_currentSourcePath)) return;
        await RenderCurrentAsync(keepPrevious: true);
    }

    public bool ShowUpdatingPill         => IsGenerating && PaletteBitmap != null && !IsCollageMode;
    public bool ShowRegenerate => IsCollageMode && IsRegenerateAvailable;
    public bool ShowPill => ShowUpdatingPill || CanCompare || ShowRegenerate;
    public bool ShowGeneratingIndicator  => IsGenerating && !ShowUpdatingPill;
    public double ResultPreviewOpacity   => ShowUpdatingPill ? 0.4 : 1.0;

    private void RaiseUpdatingStateChanged()
    {
        OnPropertyChanged(nameof(ShowUpdatingPill)); OnPropertyChanged(nameof(ShowPill));
        OnPropertyChanged(nameof(ShowGeneratingIndicator));
        OnPropertyChanged(nameof(ResultPreviewOpacity));
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

    private int MaxWorkingDimension => Settings.WorkingMaxDimension;

    public bool ShowPlaceholder => !IsGenerating && DisplayedCollageBitmap == null && string.IsNullOrEmpty(ErrorMessage);
    public bool HasResult       => DisplayedCollageBitmap != null;

    // The empty box is for first use; with history a thumbnail grid replaces it, except in collage mode.
    public bool ShowEmptyBrowserGrid       => ShowPlaceholder && HasRecentPhotos && !IsCollageMode;
    public bool ShowEmptyDashedPlaceholder => ShowPlaceholder && (!HasRecentPhotos || IsCollageMode);

    // Hidden while the empty-state grid shows the same photos.
    public bool ShowRecentStrip => HasRecentPhotos && !ShowPlaceholder;

    // Compact bar once there is history.
    public bool ShowFullWidthStartButtons => ShowStartButtons && !HasRecentPhotos;
    public bool ShowCompactActionBar      => ShowStartButtons && HasRecentPhotos;

    public bool ShowPickPhotoToolbarButton => HasResult && !IsCollageMode;

    // Only before any flow has started.
    public bool ShowStartButtons => !HasResult && !IsCollageMode;

    // Label and action follow IsCollageMode.
    public string EmptyStateText => IsCollageMode ? Loc.T("Add photos to the collage") : Loc.T("No photo selected");
    public string EmptyStageText => IsCollageMode || IsCollageExportMode ? Loc.T("Add photos to start a collage") : Loc.T("Open a photo to start");

    // True for a collage chip even with an empty tray.
    public bool IsCollageExportMode => Settings.ExportMode is ExportMode.Collage or ExportMode.CollageInfoOnly;
    public string MainPhotoButtonLabel => IsCollageExportMode ? Loc.T("Add Photos") : Loc.T("Pick a Photo");

    [RelayCommand]
    private async Task PickMainPhoto()
    {
        if (IsCollageExportMode) await AddPhotosToCollageAsync();
        else await PickPhotoAsync();
    }

    [RelayCommand]
    private async Task PickForEmptyState() => await PickMainPhoto();

    public MaterialIconKind SaveIconKind  => IsSaved ? MaterialIconKind.Check : MaterialIconKind.ContentSaveOutline;
    public string           SaveButtonText => IsSaved ? Loc.T("Saved!") : Settings.SaveButtonLabel;

    private DispatcherTimer? _saveConfirmTimer;

    partial void OnIsGeneratingChanged(bool value)
    {
        RaiseUpdatingStateChanged();
        OnPropertyChanged(nameof(ShowPlaceholder));
        OnPropertyChanged(nameof(ShowEmptyBrowserGrid));
        OnPropertyChanged(nameof(ShowEmptyDashedPlaceholder));
        OnPropertyChanged(nameof(ShowRecentStrip));
        RegenerateCommand.NotifyCanExecuteChanged();
    }
    partial void OnIsRegenerateAvailableChanged(bool value)
    {
        RegenerateCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(ShowRegenerate)); OnPropertyChanged(nameof(ShowPill));
    }
    partial void OnPaletteBitmapChanged(Bitmap? value)
    {
        RaiseUpdatingStateChanged();
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
        OnPropertyChanged(nameof(CanCompare)); OnPropertyChanged(nameof(ShowPill));
        OnPropertyChanged(nameof(DisplayedResultBitmap));
        OnPropertyChanged(nameof(CanCompareToOriginal));
    }
    partial void OnErrorMessageChanged(string? value)
    {
        OnPropertyChanged(nameof(HasError));
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
        OnPropertyChanged(nameof(ShowRegenerate)); OnPropertyChanged(nameof(ShowFilmstrip)); OnPropertyChanged(nameof(ShowPill));
        SavePngCommand.NotifyCanExecuteChanged();
        RaiseShellTextsChanged();
        OnPropertyChanged(nameof(CanCompare)); OnPropertyChanged(nameof(ShowPill));
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
        if (IsCollageMode || PickImageAsync == null) return;
        var picked = await PickImageAsync();
        if (picked == null) return;

        await IngestNewPhotoAsync(picked.Value.Stream, picked.Value.FileName);
    }

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

        // Suspended, or every add would decode all earlier photos again.
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

    // Switch the chip back to Card; SyncCollageModeWithExportModeAsync does the rest.
    [RelayCommand]
    private void ExitCollage() => Settings.ExportMode = ExportMode.Card;

    // Moves content between single and collage mode when the chip changes.
    private async Task SyncCollageModeWithExportModeAsync()
    {
        // Reentrancy guard: a running pass re-checks ExportMode.
        if (_isSyncingCollageMode)
        {
            _collageSyncPending = true;
            return;
        }

        _isSyncingCollageMode = true;
        try
        {
            do
            {
                _collageSyncPending = false;
                await SyncCollageModeStepAsync();
            } while (_collageSyncPending);
        }
        finally
        {
            _isSyncingCollageMode = false;
        }
    }

    private async Task SyncCollageModeStepAsync()
    {
        bool wantsCollage = IsCollageExportMode;

        if (wantsCollage && !IsCollageMode && _currentSourcePath != null && File.Exists(_currentSourcePath))
        {
            // The selected photo starts the tray.
            var migrated = await BuildCollageEntryAsync(_currentSourcePath, _lastFileName ?? Path.GetFileName(_currentSourcePath));

            OriginalBitmap = null;
            PaletteBitmap  = null;
            _lastTempPng   = null;
            OutputSizeText = null;
            _collageExifSourcePath = null;
            _currentSourcePath = null;
            Settings.HasPhoto = false;
            _lastFileName  = null;
            SavePngCommand.NotifyCanExecuteChanged();

            IsCollageMode = true;
            Settings.ShowCollageOptions = true;
            CollageItems.Add(migrated);
            OnCollageTrayChanged();
        }
        else if (!wantsCollage && IsCollageMode)
        {
            // Keep the first photo, delete the other temp copies.
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
        _saveConfirmTimer?.Stop();
        IsSaved = false;
        SavePngCommand.NotifyCanExecuteChanged();

        if (CollageItems.Count == 0) return;

        IsGenerating = true;
        try
        {
            var sourcePaths = CollageItems.Select(i => i.FullPath).ToList();
            FileInfoText = Loc.N(sourcePaths.Count, "Collage · {0} photo", "Collage · {0} photos");

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

    // _lastTempPng is what is on screen, so the file matches the preview.
    [RelayCommand(CanExecute = nameof(CanSavePng))]
    private async Task SavePng()
    {
        // Not generated yet: build the file on demand.
        if (IsCollageMode && CollageItems.Count > 0 && (_lastTempPng == null || IsRegenerateAvailable))
        {
            await GenerateCollagePaletteAsync();
            IsRegenerateAvailable = false;
        }
        if (SavePngAsync == null || _lastTempPng == null) return;
        string suggested;
        if (IsCollageMode)
        {
            suggested = SuggestedFileName.BuildCollage(_lastExtension);
        }
        else
        {
            suggested = SuggestedFileName.Build(_lastFileName, Settings.ExportMode, _lastExtension);
        }
        var ok = await SavePngAsync(suggested, _lastTempPng);
        if (!ok) return;

        _saveConfirmTimer?.Stop();
        IsSaved = true;
        if (!IsCollageMode) RecentPhotos.FirstOrDefault(r => r.IsCurrent)?.IsExported = true;
        _saveConfirmTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _saveConfirmTimer.Tick += (_, _) =>
        {
            _saveConfirmTimer!.Stop();
            IsSaved = false;
        };
        _saveConfirmTimer.Start();
    }

    private bool CanSavePng() =>
        (IsCollageMode ? CollageItems.Count > 0 : PaletteBitmap != null) && !IsGenerating;

    // The temp copy survives for re-rendering; the previous one stays while RecentPhotos refers to it.
    private async Task IngestNewPhotoAsync(Stream sourceStream, string fileName)
    {
        var newSourcePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}_{fileName}");
        await using (var fs = File.Create(newSourcePath))
            await sourceStream.CopyToAsync(fs);
        await sourceStream.DisposeAsync();

        await IngestFromStablePathAsync(newSourcePath, fileName, addToRecent: true);
    }

    // The temp copy is still on disk.
    [RelayCommand]
    private async Task SelectRecentPhotoAsync(FileEntry entry)
    {
        if (IsCollageMode) return;
        if (!File.Exists(entry.FullPath))
        {
            RecentPhotos.Remove(entry); // temp file vanished under us (OS cleanup) — drop the dead entry
            return;
        }
        var idx = RecentPhotos.IndexOf(entry);
        if (idx > 0) RecentPhotos.Move(idx, 0);
        await IngestFromStablePathAsync(entry.FullPath, entry.FileName, addToRecent: false);
    }

    private static bool SameLength(string path, long len)
    {
        try { return len > 0 && new FileInfo(path).Length == len; } catch { return false; }
    }

    private async Task IngestFromStablePathAsync(string path, string fileName, bool addToRecent)
    {
        _renderCts?.Cancel();

        _currentSourcePath = path;
        Settings.HasPhoto = true;
        _lastFileName = fileName;
        foreach (var r in RecentPhotos) r.IsCurrent = false;
        if (!addToRecent) RecentPhotos.FirstOrDefault(r => r.FullPath == path)?.IsCurrent = true;

        if (addToRecent)
        {
            // Coming back from Collage re-adds the photo under a new copy: replace the old entry.
            long len = 0;
            try { len = new FileInfo(path).Length; } catch { }
            foreach (var dup in RecentPhotos
                         .Where(r => (r.FullPath == path || (r.FileName == fileName && SameLength(r.FullPath, len)))).ToList())
                RecentPhotos.Remove(dup);

            var entry = new FileEntry(fileName, path);
            entry.IsCurrent = true;
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

        var metaSw = Stopwatch.StartNew();
        var metaPath = _currentSourcePath;
        var (metaRead, recipeRead) = await Task.Run(() => (PaletteImageRenderer.ReadMetadata(metaPath), RecipeReader.Read(metaPath)));
        if (_currentSourcePath != metaPath) return; // another photo was picked meanwhile
        Settings.LoadMetadataFields(metaRead, recipeRead);
        Settings.NotifyPhotoChanged();
        // Stage timings go to app.log.
        AppLogger.LogInfo($"[perf] EXIF/recipe metadata read: {metaSw.ElapsedMilliseconds} ms ({fileName})");
        IsRegenerateAvailable = false; // Already current.
        Settings.SelectedPhotoCount = 1;
        Settings.ExportMode = ExportModeRules.ClosestValidMode(Settings.ExportMode, 1);

        // Only the card layout shows this card.
        if (Settings.ShowCardBody) await RenderCurrentAsync();
        else PaletteBitmap = null;
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

    private async Task RenderCurrentAsync(bool keepPrevious = false)
    {
        if (_currentSourcePath == null) return;
        var sourcePath = _currentSourcePath;

        if (!keepPrevious) _liveDebounceCts?.Cancel();
        _renderCts?.Cancel();
        _renderCts = new CancellationTokenSource();
        var token = _renderCts.Token;

        ErrorMessage   = null;
        if (!keepPrevious)
        {
            OriginalBitmap = null;
            PaletteBitmap  = null;
            FileInfoText   = null;
            OutputSizeText = null;
            _lastTempPng   = null;
            IsComparingToOriginal = false;
        }
        _saveConfirmTimer?.Stop();
        IsSaved = false;
        SavePngCommand.NotifyCanExecuteChanged();

        IsGenerating = true;
        Image<Rgb24>? working = null;
        var stageSw = Stopwatch.StartNew();
        var totalSw = Stopwatch.StartNew();
        var perfLog = new System.Text.StringBuilder();
        DebugPerfText = null;
        void LogStage(string stage)
        {
            var ms = stageSw.ElapsedMilliseconds;
            AppLogger.LogInfo($"[perf] {stage}: {ms} ms");
            perfLog.AppendLine($"{stage}: {ms} ms");
            DebugPerfText = perfLog.ToString();
            stageSw.Restart();
        }
        // May be called from a pool thread. Unhook only if a newer render has not replaced it.
        Action<string, long> perfHook = (label, ms) =>
        {
            AppLogger.LogInfo($"[perf] {label}: {ms} ms");
            Dispatcher.UIThread.Post(() =>
            {
                perfLog.AppendLine($"{label}: {ms} ms");
                DebugPerfText = perfLog.ToString();
            });
        };
        PhSpectre.Rendering.RenderPerfLog.OnStage = perfHook;
        try
        {
            working = await Task.Run(() => ImageLoader.LoadWorkingCopy(sourcePath, MaxWorkingDimension), token);
            token.ThrowIfCancellationRequested();
            LogStage($"decode+downscale to {MaxWorkingDimension}px cap ({working.Width}x{working.Height})");

            using var previewMs = await Task.Run(() => ImageLoader.ToJpegStream(working), token);
            token.ThrowIfCancellationRequested();
            OriginalBitmap = new Bitmap(previewMs);
            LogStage("preview JPEG re-encode + Avalonia Bitmap decode");

            var ps = OriginalBitmap.PixelSize;
            FileInfoText = $"{_lastFileName}  ·  {ps.Width}×{ps.Height}";

            var lookKey = Settings.CardLookKey;
            if (Settings.CardAdjust is { } cardAdjust) { var adjusted = cardAdjust.Render(working); working.Dispose(); working = adjusted; }
            Settings.CardLook?.Apply(working);
            _lastExtension = Settings.FileExtension;
            var exportSettings = PaletteExportSettings.SnapshotFrom(Settings);
            // The recipe card depends on the current recipe.
            var recipeForRender = Settings.ExportMode == ExportMode.Recipe ? Settings.DetectedRecipe : null;
            var cacheKey = (sourcePath, exportSettings, recipeForRender, lookKey);

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

            stageSw.Restart();
            if (recipeForRender != null)
            {
                // Same composite as Save recipe card.
                var recipeMetadataOverride = Settings.BuildMetadataOverride();
                var recipeQr = exportSettings.BuildQrRenderOptions(
                    recipeMetadataOverride ?? PaletteImageRenderer.ReadMetadata(working), recipeForRender);
                await Task.Run(() => RecipeCardRenderer.Render(working, recipeForRender, tmpOut,
                    theme: exportSettings.Theme, format: exportSettings.Format, customBackground: exportSettings.CustomBackground,
                    metaVerbosity: exportSettings.MetaVerbosity, metadataOverride: recipeMetadataOverride,
                    labelScale: exportSettings.LabelScale, useBlurredBackground: exportSettings.UseBlurredBackground, qr: recipeQr,
                    showRecipeCard: exportSettings.ShowRecipeCard,
                    compositionGuide: exportSettings.CompositionGuide,
                    hiddenRecipeFields: exportSettings.HiddenRecipeFields, metaStyle: exportSettings.MetaStyle), token);
                LogStage($"RecipeCardRenderer.Render ({exportSettings.Format})");
            }
            else
            {
                // InfoOnly skips k-means.
                PhSpectre.Models.ColorPalette palette;
                if (exportSettings.ComputeColors)
                {
                    palette = await new PaletteExtractor().ExtractAsync(working, Settings.Colors, Settings.SamplingMode, token);
                    token.ThrowIfCancellationRequested();
                    LogStage("PaletteExtractor.ExtractAsync (k-means)");
                }
                else
                {
                    palette = new PhSpectre.Models.ColorPalette([new PhSpectre.Models.ColorSwatch("#000000", (0, 0, 0), 1f)]);
                }

                var metadataOverride = Settings.BuildMetadataOverride();
                var qr = exportSettings.BuildQrRenderOptions(
                    metadataOverride ?? PaletteImageRenderer.ReadMetadata(working), Settings.DetectedRecipe);
                await Task.Run(() => PaletteImageRenderer.Render(
                    working, palette, tmpOut,
                    showHex:          exportSettings.ShowHex,
                    metaVerbosity:    exportSettings.MetaVerbosity,
                    metaStyle:        exportSettings.MetaStyle,
                    theme:            exportSettings.Theme,
                    hexBelow:         exportSettings.HexBelow,
                    showSwatches:     exportSettings.ComputeColors,
                    downscale:        1,
                    format:           exportSettings.Format,
                    exportPreset:     exportSettings.ExportPreset,
                    metadataOverride: metadataOverride,
                    labelScale:       exportSettings.LabelScale,
                    swatchScale:      exportSettings.SwatchScale,
                    showPercent:      exportSettings.ShowPercent,
                    swatchShape:      exportSettings.SwatchShape,
                    sortOrder:        exportSettings.SortOrder,
                    customBackground: exportSettings.CustomBackground,
                    compositionGuide: exportSettings.CompositionGuide,
                    useBlurredBackground: exportSettings.UseBlurredBackground,
                    qr:               qr), token);
                LogStage($"PaletteImageRenderer.Render ({exportSettings.Format})");
            }

            token.ThrowIfCancellationRequested();

            _lastTempPng              = tmpOut;
            _lastSingleRenderKey      = cacheKey;
            _cachedSingleRenderOutput = tmpOut;
            var sizeBytes  = new FileInfo(tmpOut).Length;
            var formatLabel = exportSettings.Format == OutputFormat.Jpeg ? "JPG" : "PNG";
            OutputSizeText = $"{sizeBytes / 1_048_576.0:F1} MB {formatLabel}";
            PaletteBitmap  = new Bitmap(tmpOut);
            LogStage("output Bitmap decode for display");
            var totalLine = $"TOTAL ({Settings.ExportMode}): {totalSw.ElapsedMilliseconds} ms, output={sizeBytes / 1024.0:F0} KB";
            AppLogger.LogInfo($"[perf] {totalLine}");
            perfLog.AppendLine(totalLine);
            DebugPerfText = perfLog.ToString();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            ErrorMessage = ex.ToString();
        }
        finally
        {
            if (ReferenceEquals(PhSpectre.Rendering.RenderPerfLog.OnStage, perfHook))
                PhSpectre.Rendering.RenderPerfLog.OnStage = null;
            working?.Dispose();
            if (!token.IsCancellationRequested)
            {
                IsGenerating = false;
                SavePngCommand.NotifyCanExecuteChanged();
            }
        }
    }
}
