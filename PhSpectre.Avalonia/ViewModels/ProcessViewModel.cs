using PhSpectre.Localization;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using global::Avalonia.Media.Imaging;
using global::Avalonia.Threading;
using PhSpectre.ColorGrading;
using PhSpectre.Rendering;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Advanced;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace PhSpectre.Avalonia.ViewModels;

// A category heading with its tiles.
public sealed record GradeGroup(string Name, IReadOnlyList<GradeItemViewModel> Items);

// One row in the library list.
public partial class GradeItemViewModel : ObservableObject
{
    public GradeEntry Entry { get; }
    public ProcessViewModel Owner { get; }
    [ObservableProperty] private bool _isFavorite;
    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private Bitmap? _thumb;
    public string Name => Entry.Name;
    public string Detail => string.IsNullOrEmpty(Entry.Category) ? "" : Entry.Category;
    public bool CanDelete => !Entry.Bundled;
    public string Badge => Entry.Kind == GradeKind.Preset ? Loc.T("PRESET") : "LUT";
    public string FavoriteMenuText => IsFavorite ? Loc.T("Remove from favorites") : Loc.T("Add to favorites");
    partial void OnIsFavoriteChanged(bool value) => OnPropertyChanged(nameof(FavoriteMenuText));
    public bool HasThumb => Thumb != null;
    partial void OnThumbChanged(Bitmap? value) => OnPropertyChanged(nameof(HasThumb));
    public GradeItemViewModel(GradeEntry e, ProcessViewModel owner) { Entry = e; Owner = owner; _isFavorite = e.Favorite; }
}

// The Edit screen: photo, Look library, adjustments, live preview, undo and export.
public partial class ProcessViewModel : ViewModelBase
{
    private const int PreviewLongSide = 1600;
    private readonly SettingsViewModel _settings;
    private GradeLibrary? _grades;

    public ProcessViewModel(SettingsViewModel settings)
    {
        _settings = settings;
        // The Export tab starts from the saved format.
        ExportFormatIndex = settings.OutputFormat == PhSpectre.Rendering.OutputFormat.Png ? 1 : 0;
    }

    public SettingsViewModel Settings => _settings;

    [RelayCommand] private void OpenPhotos() => _settings.OpenPhotosCommand?.Execute(null);

    /// <summary>Frame type Border on/off: the border is the last layer on the graded photo.</summary>
    public void SetBorder(bool on) => FrameEnabled = on;

    public static string LibraryRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PhSpectre", "library");

    // Lazy, so folders are only created when used.
    public GradeLibrary Grades => _grades ??= new GradeLibrary(LibraryRoot, Services.BundledLooks.All);

    [ObservableProperty] private Bitmap? _original;
    [ObservableProperty] private Bitmap? _graded;
    [ObservableProperty] private string _photoName = "";
    [ObservableProperty] private string _errorText = "";
    [ObservableProperty] private bool _showOriginal;
    private string? _loadedPath;
    private Image<Rgb24>? _source; // downscaled, EXIF-oriented working copy

    public bool HasPhoto => Original != null;
    private bool _compareHidden;
    // Phone: hidden while the Adjust sheet covers the stage.
    public bool CompareHidden
    {
        get => _compareHidden;
        set
        {
            if (_compareHidden == value) return;
            _compareHidden = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowCompare));
            OnPropertyChanged(nameof(StageMargin));
        }
    }
    private bool _phoneStage;
    // Phone: no overlays on the photo's corners.
    public bool PhoneStage
    {
        get => _phoneStage;
        set
        {
            if (_phoneStage == value) return;
            _phoneStage = value;
            OnPropertyChanged(); OnPropertyChanged(nameof(StageMargin));
            OnPropertyChanged(nameof(CompareMargin)); OnPropertyChanged(nameof(ShowPhotoName));
            OnPropertyChanged(nameof(HistogramWidth)); OnPropertyChanged(nameof(HistogramHeight));
        }
    }
    // Bottom room only while the compare switch is there.
    public global::Avalonia.Thickness StageMargin => _phoneStage
        ? new(4, 4, 4, _compareHidden ? 4 : 52)
        : new(32, 28, 32, _compareHidden ? 16 : 84);
    public global::Avalonia.Thickness CompareMargin => new(0, 0, 0, _phoneStage ? 3 : 18);
    public bool ShowPhotoName => HasPhoto && !_phoneStage;
    public bool ShowCompare => HasPhoto && !_compareHidden;
    public bool HasError => ErrorText.Length > 0;
    public bool ShowEmpty => !HasPhoto && !HasError;
    public Bitmap? Shown => ShowOriginal || Graded == null ? Original : Graded;
    public string? PhotoPath => _loadedPath;

    partial void OnOriginalChanged(Bitmap? value)
    {
        OnPropertyChanged(nameof(ShowCropView)); OnPropertyChanged(nameof(ShowPhotoView));
        OnPropertyChanged(nameof(FilmFrameAuto)); OnPropertyChanged(nameof(FilmFrameWatermark));
        OnPropertyChanged(nameof(HasPhoto)); OnPropertyChanged(nameof(ShowPhotoName));
        OnPropertyChanged(nameof(ShowCompare)); OnPropertyChanged(nameof(ShowEmpty)); OnPropertyChanged(nameof(Shown));
        OnPropertyChanged(nameof(CanExport)); OnPropertyChanged(nameof(CanExportAll));
        ResetHistogram();
    }
    partial void OnGradedChanged(Bitmap? value) { OnPropertyChanged(nameof(Shown)); RaiseHistogram(); }
    partial void OnShowOriginalChanged(bool value)
    {
        OnPropertyChanged(nameof(Shown)); OnPropertyChanged(nameof(CompareIndex)); RaiseHistogram();
        if (IsDetailActive) ScheduleRender();
    }

    public int CompareIndex
    {
        get => ShowOriginal ? 1 : 0;
        set => ShowOriginal = value == 1;
    }
    partial void OnErrorTextChanged(string value) { OnPropertyChanged(nameof(HasError)); OnPropertyChanged(nameof(ShowEmpty)); }

    public void Refresh()
    {
        if (!_libraryBuilt) RefreshLibrary(); else if (Items.Any(i => i.Thumb == null)) ScheduleThumbs();
        if (Frames.Count == 0) LoadFrames();
        if (FilmStyles.Count == 0) LoadFilmStyles();
        var entry = _settings.CurrentPhotoProvider?.Invoke();
        if (entry == null)
        {
            if (_loadedPath != null) ClearPhoto();
            ErrorText = "";
            return;
        }
        if (entry.FullPath == _loadedPath && Original != null) return;
        if (entry.FullPath == _loadingPath) return;
        _ = LoadPhotoAsync(entry.FullPath, entry.FileName);
    }

    private void ClearPhoto()
    {
        ReleaseDetail();
        _source?.Dispose(); _source = null;
        Original = null; Graded = null; _loadedPath = null; PhotoName = ""; _fullSize = (0, 0); UpdateExportSize();
        ResetThumbs();
        PhotoChanged?.Invoke();
        OnPropertyChanged(nameof(PhotoCount)); OnPropertyChanged(nameof(CanExportAll)); OnPropertyChanged(nameof(ExportAllText));
    }

    /// <summary>Raised when the photo on the canvas changes (loaded or cleared).</summary>
    public event Action? PhotoChanged;
    /// <summary>Raised when a preset/LUT is applied, removed, switched on or off.</summary>
    public event Action? LookChanged;

    /// <summary>True when a preset or LUT is actually changing the picture.</summary>
    public bool HasLook => !BuildStack().IsEmpty;

    /// <summary>The Look as a stack, or null when nothing is applied (used by Frame → Card).</summary>
    public GradeStack? LookStack() { var s = BuildStack(); return s.IsEmpty ? null : s; }

    // Cache key of the applied Look.
    public string LookKey => LookStack() == null ? "" :
        $"{_presetEntry?.Id}:{PresetEnabled}:{PresetStrength:0}|{_lutEntry?.Id}:{LutEnabled}:{LutStrength:0}";

    // One Amount for the selected Look: drives every layer that is on.
    public double Amount
    {
        get => HasLut ? LutStrength : HasPreset ? PresetStrength : 100;
        set
        {
            value = Math.Clamp(value, 0, 100);
            if (HasPreset) PresetStrength = value;
            if (HasLut) LutStrength = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(AmountText));
        }
    }
    public string AmountText => $"{(int)Math.Round(Amount)}%";

    private string? _loadingPath;
    private async Task LoadPhotoAsync(string path, string fileName)
    {
        _loadingPath = path;
        try
        {
            int fullW = 0, fullH = 0;
            var img = await Task.Run(() =>
            {
                // Decode near preview size: JPEG scales in the DCT, much faster on phones.
                var info = Image.Identify(path);
                fullW = info?.Width ?? 0; fullH = info?.Height ?? 0;
                int big = info == null ? 0 : Math.Max(info.Width, info.Height);
                Image<Rgb24> i;
                if (info != null && big > PreviewLongSide)
                {
                    double k0 = (double)PreviewLongSide / big;
                    var opts = new SixLabors.ImageSharp.Formats.DecoderOptions
                    {
                        TargetSize = new Size(Math.Max(1, (int)(info.Width * k0)), Math.Max(1, (int)(info.Height * k0))),
                        Sampler = KnownResamplers.Box,
                    };
                    i = Image.Load<Rgb24>(opts, path);
                }
                else i = Image.Load<Rgb24>(path);
                i.Mutate(x => x.AutoOrient());
                if (fullW > 0 && (i.Width >= i.Height) != (fullW >= fullH)) (fullW, fullH) = (fullH, fullW);
                int longSide = Math.Max(i.Width, i.Height);
                if (longSide > PreviewLongSide)
                {
                    double k = (double)PreviewLongSide / longSide;
                    i.Mutate(x => x.Resize(Math.Max(1, (int)(i.Width * k)), Math.Max(1, (int)(i.Height * k))));
                }
                return i;
            });
            if (_settings.CurrentPhotoProvider?.Invoke()?.FullPath != path) { img.Dispose(); return; }
            ReleaseDetail();
            _source?.Dispose();
            _source = img;
            _fullSize = (fullW, fullH);
            _loadedPath = path;
            PhotoName = fileName;
            ErrorText = "";
            Original = ToBitmap(img);
            Graded = null;
            ResetPhotoAdjust();
            UpdateExportSize();
            ResetThumbs();
            if (_libraryBuilt) { foreach (var it in Items) it.Thumb = null; ScheduleThumbs(); } else RefreshLibrary();
            ScheduleRender();
            PhotoChanged?.Invoke();
            OnPropertyChanged(nameof(PhotoCount)); OnPropertyChanged(nameof(CanExportAll)); OnPropertyChanged(nameof(ExportAllText));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or UnknownImageFormatException
                                       or InvalidImageContentException or NotSupportedException)
        {
            ClearPhoto();
            PhotoName = fileName;
            ErrorText = Loc.T("This photo can't be opened for grading.");
        }
        finally { if (_loadingPath == path) _loadingPath = null; }
    }

    private static Bitmap ToBitmap(Image<Rgb24> img)
    {
        using var ms = new MemoryStream();
        img.SaveAsBmp(ms); // uncompressed: fastest way into an Avalonia Bitmap
        ms.Position = 0;
        return new Bitmap(ms);
    }

    public ObservableCollection<GradeItemViewModel> Items { get; } = [];
    public ObservableCollection<GradeGroup> Groups { get; } = [];
    [ObservableProperty] private int _libraryTab; // 0 All, 1 Favorites, 2 Presets, 3 LUTs
    [ObservableProperty] private string _searchText = "";
    private bool FavoritesOnly => LibraryTab == 1;
    [ObservableProperty] private string _importStatus = "";

    public bool HasItems => Items.Count > 0;
    public bool ShowLibraryEmpty => Items.Count == 0;
    public string LibraryEmptyText => SearchText.Length > 0 || FavoritesOnly
        ? Loc.T("Nothing matches.")
        : LibraryTab switch
        {
            2 => Loc.T("No presets yet. Import .xmp files."),
            3 => Loc.T("No LUTs yet. Import .cube files."),
            _ => Loc.T("No looks yet. Import presets (.xmp) or LUTs (.cube)."),
        };
    public bool HasImportStatus => ImportStatus.Length > 0;

    partial void OnLibraryTabChanged(int value) => RefreshLibrary();
    partial void OnSearchTextChanged(string value) => RefreshLibrary();
    partial void OnImportStatusChanged(string value) => OnPropertyChanged(nameof(HasImportStatus));

    private bool _libraryBuilt;
    public void RefreshLibrary()
    {
        GradeKind? kind = LibraryTab switch { 2 => GradeKind.Preset, 3 => GradeKind.Lut, _ => null };
        _libraryBuilt = true;
        Items.Clear();
        foreach (var e in Grades.Query(kind, SearchText, null, FavoritesOnly))
        {
            var item = new GradeItemViewModel(e, this)
            {
                IsActive = e.Id == (e.Kind == GradeKind.Preset ? _presetEntry?.Id : _lutEntry?.Id)
            };
            if (_thumbs.TryGetValue(ThumbKey(e.Id), out var tb)) item.Thumb = tb;
            Items.Add(item);
        }
        Groups.Clear();
        // Mixed tabs (All, Fav) split presets and LUTs into separate sections.
        bool mixed = kind == null;
        // Portrait looks come first.
        foreach (var g in Items.GroupBy(i => (Kind: mixed ? i.Entry.Kind : GradeKind.Preset,
                                              Cat: string.IsNullOrWhiteSpace(i.Entry.Category) ? "Imported" : i.Entry.Category))
                               .OrderBy(g => g.Key.Cat == "Portrait" ? 0 : 1)
                               .ThenBy(g => g.Key.Kind).ThenBy(g => g.Key.Cat == "Imported" ? 1 : 0)
                               .ThenBy(g => g.Key.Cat, StringComparer.CurrentCultureIgnoreCase))
        {
            var category = g.Key.Cat == "Imported" ? Loc.T("Imported") : g.Key.Cat;
            string title = mixed ? $"{(g.Key.Kind == GradeKind.Preset ? Loc.T("Presets") : Loc.T("LUTs"))} · {category}" : category;
            Groups.Add(new GradeGroup(title, g.OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).ToList()));
        }
        OnPropertyChanged(nameof(HasItems)); OnPropertyChanged(nameof(ShowLibraryEmpty)); OnPropertyChanged(nameof(LibraryEmptyText));
        _ = FillThumbsAsync();
    }

    private const int ThumbLongSide = 180;
    private readonly Dictionary<string, Bitmap> _thumbs = [];
    private Image<Rgb24>? _thumbSource;
    private int _thumbGeneration;

    private string ThumbKey(string id) => $"{_loadedPath}|{id}";

    private async Task FillThumbsAsync()
    {
        int gen = ++_thumbGeneration;
        // Let the photo appear before the tiles compete for the CPU.
        await Task.Delay(250);
        if (gen != _thumbGeneration || _settings.EditTab != 0) return;
        var src = _thumbSource;
        if (src == null) return;
        foreach (var item in Items.ToList())
        {
            if (gen != _thumbGeneration) return;
            if (item.Thumb != null) continue;
            var key = ThumbKey(item.Entry.Id);
            _cache.TryGetValue(item.Entry.Id, out var cached);
            var entry = item.Entry;
            // Off the UI thread.
            var (bytes, loaded) = await Task.Run(() =>
            {
                try
                {
                    var o = cached ?? (entry.Kind == GradeKind.Preset ? (object)Grades.LoadPreset(entry) : Grades.LoadLut(entry));
                    return (ThumbnailRenderer.Render(src, o), o);
                }
                catch (Exception ex) when (ex is ObjectDisposedException or FormatException or IOException
                                               or UnauthorizedAccessException or System.Text.DecoderFallbackException)
                { return ((byte[]?)null, (object?)null); }
            });
            if (loaded != null) _cache[entry.Id] = loaded;
            if (gen != _thumbGeneration) return;
            if (bytes == null) continue;
            using var ms = new MemoryStream(bytes);
            var bmp = new Bitmap(ms);
            _thumbs[key] = bmp;
            item.Thumb = bmp;
        }
    }

    private void ScheduleThumbs() { SyncThumbsToAdjust(); _ = FillThumbsAsync(); }

    // Tiles skip cleaning: it is invisible at their size.
    private PhotoAdjust? ThumbAdjust() => CurrentAdjustSettings().WithoutCleaning() is { IsNoOp: false } a ? a : null;

    private void ResetThumbs()
    {
        _thumbGeneration++;
        _thumbs.Clear();
        var old = _thumbSource;
        _thumbSource = null;
        _thumbAdjustKey = AdjustKey;
        if (_source != null)
        {
            using var basis = ThumbAdjust()?.Render(_source);
            var from = basis ?? _source;
            int side = Math.Max(from.Width, from.Height);
            double k = Math.Min(1.0, (double)ThumbLongSide / side);
            _thumbSource = from.Clone(x => x.Resize(Math.Max(1, (int)(from.Width * k)), Math.Max(1, (int)(from.Height * k))));
        }
        old?.Dispose();
    }

    public void Import(IEnumerable<string> paths)
    {
        var results = Grades.ImportMany(paths, "");
        int ok = results.Count(r => r.Error == null);
        var errors = results.Where(r => r.Error != null).Select(r => $"{Path.GetFileName(r.Source)}: {r.Error}").ToList();
        var lines = new List<string>();
        if (ok > 0) lines.Add(Loc.F("Imported {0}.", ok));
        foreach (var r in results.Where(r => r.Entry is { Skipped.Count: > 0 }))
            lines.Add(Loc.F("“{0}”: not applied — {1}", r.Entry!.Name, string.Join(", ", r.Entry.Skipped)));
        lines.AddRange(errors.Take(5));
        if (errors.Count > 5) lines.Add(Loc.F("…and {0} more failed.", errors.Count - 5));
        ImportStatus = string.Join("\n", lines);
        // Show the tab holding the imports.
        var last = results.LastOrDefault(r => r.Entry != null)?.Entry;
        if (last != null && LibraryTab == 1) LibraryTab = 0;
        RefreshLibrary();
    }

    [RelayCommand] private void DismissImportStatus() => ImportStatus = "";

    [RelayCommand]
    private void ToggleFavorite(GradeItemViewModel item)
    {
        item.IsFavorite = !item.IsFavorite;
        Grades.SetFavorite(item.Entry.Id, item.IsFavorite);
        item.Entry.Favorite = item.IsFavorite;
        if (FavoritesOnly) RefreshLibrary();
    }

    [RelayCommand]
    private void DeleteItem(GradeItemViewModel item)
    {
        bool wasActive = item.IsActive;
        Grades.Delete(item.Entry.Id);
        _cache.Remove(item.Entry.Id);
        if (wasActive) { if (item.Entry.Kind == GradeKind.Preset) ClearPreset(); else ClearLut(); }
        RefreshLibrary();
    }

    [RelayCommand]
    private void UseItem(GradeItemViewModel item)
    {
        if (item.IsActive)
        {
            if (item.Entry.Kind == GradeKind.Preset) ClearPreset(); else ClearLut();
            return;
        }
        if (!TryLoad(item.Entry, out var obj, out var err)) { ImportStatus = err; return; }
        // Each newly picked look starts at 100%.
        if (item.Entry.Kind == GradeKind.Preset)
        {
            _preset = (LrPreset)obj!; _presetEntry = item.Entry; PresetEnabled = true; PresetStrength = 100;
        }
        else
        {
            _lut = (CubeLut)obj!; _lutEntry = item.Entry; LutEnabled = true; LutStrength = 100;
        }
        Grades.MarkUsed(item.Entry.Id);
        LayersChanged();
        CommitNow();
        RefreshLibrary();
    }

    private readonly Dictionary<string, object> _cache = [];

    private bool TryLoad(GradeEntry e, out object? obj, out string error)
    {
        obj = null; error = "";
        if (_cache.TryGetValue(e.Id, out obj)) return true;
        try
        {
            obj = e.Kind == GradeKind.Preset ? Grades.LoadPreset(e) : Grades.LoadLut(e);
            _cache[e.Id] = obj;
            return true;
        }
        catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException or System.Text.DecoderFallbackException)
        {
            error = Loc.F("“{0}” can't be loaded: {1}", e.Name, ex.Message);
            return false;
        }
    }

    // Layers: preset, then LUT
    private LrPreset? _preset; private GradeEntry? _presetEntry;
    private CubeLut? _lut; private GradeEntry? _lutEntry;

    [ObservableProperty] private double _presetStrength = 100;
    [ObservableProperty] private bool _presetEnabled = true;
    [ObservableProperty] private double _lutStrength = 100;
    [ObservableProperty] private bool _lutEnabled = true;

    public bool HasPreset => _presetEntry != null;
    public bool HasLut => _lutEntry != null;
    public bool HasLayers => HasPreset || HasLut;
    public string PresetName => _presetEntry?.Name ?? "";
    public string LutName => _lutEntry?.Name ?? "";
    // From the loaded preset: the list saved at import may be outdated.
    public string SkippedText => _preset is { Skipped.Count: > 0 } preset
        ? Loc.T("Not applied:") + " " + string.Join(", ", preset.Skipped) : "";
    public bool HasSkipped => SkippedText.Length > 0;
    public string PresetStrengthText => $"{(int)Math.Round(PresetStrength)}%";
    public string LutStrengthText => $"{(int)Math.Round(LutStrength)}%";

    partial void OnPresetStrengthChanged(double value) { OnPropertyChanged(nameof(PresetStrengthText)); LayersChanged(commit: false); }
    partial void OnLutStrengthChanged(double value) { OnPropertyChanged(nameof(LutStrengthText)); LayersChanged(commit: false); }
    partial void OnPresetEnabledChanged(bool value) => LayersChanged();
    partial void OnLutEnabledChanged(bool value) => LayersChanged();

    [RelayCommand] private void ClearPreset() { _preset = null; _presetEntry = null; PresetStrength = 100; LayersChanged(); CommitNow(); RefreshLibrary(); }
    [RelayCommand] private void ClearLut() { _lut = null; _lutEntry = null; LutStrength = 100; LayersChanged(); CommitNow(); RefreshLibrary(); }

    /// <summary>Removes preset and LUT in one step.</summary>
    [RelayCommand]
    private void ClearLook()
    {
        _preset = null; _presetEntry = null; PresetStrength = 100;
        _lut = null; _lutEntry = null; LutStrength = 100;
        LayersChanged(); CommitNow(); RefreshLibrary();
    }

    private void LayersChanged(bool commit = true)
    {
        OnPropertyChanged(nameof(HasPreset)); OnPropertyChanged(nameof(HasLut)); OnPropertyChanged(nameof(HasLayers));
        OnPropertyChanged(nameof(PresetName)); OnPropertyChanged(nameof(LutName));
        OnPropertyChanged(nameof(SkippedText)); OnPropertyChanged(nameof(HasSkipped));
        OnPropertyChanged(nameof(Amount)); OnPropertyChanged(nameof(AmountText)); OnPropertyChanged(nameof(HasLook));
        LookChanged?.Invoke();
        if (_restoring) { ScheduleRender(); return; }
        if (commit) CommitNow(); else CommitSoon();
        ScheduleRender();
    }

    private GradeStack BuildStack() => new()
    {
        Preset = _preset, PresetStrength = (float)(PresetStrength / 100), PresetEnabled = PresetEnabled,
        Lut = _lut, LutStrength = (float)(LutStrength / 100), LutEnabled = LutEnabled
    };

    // Frame, the last layer
    private PhotoFrameStore? _frameStore;
    private PhotoFrameStore FrameStore => _frameStore ??= new PhotoFrameStore(Path.Combine(LibraryRoot, "frames.json"));
    private bool _applyingFrame;
    private string _validColorHex = "#FFFFFF";

    public ObservableCollection<PhotoFrame> Frames { get; } = [];
    public IReadOnlyList<string> AspectLabels { get; } = PhotoFrame.AspectChoices.Select(a => Loc.T(a.Label)).ToList();

    [ObservableProperty] private bool _frameEnabled;
    [ObservableProperty] private PhotoFrame? _selectedFrame;
    [ObservableProperty] private double _frameThickness = 4;
    [ObservableProperty] private double _frameBottom;
    [ObservableProperty] private string _frameColorHex = "#FFFFFF";
    [ObservableProperty] private int _frameAspectIndex;
    [ObservableProperty] private string _frameName = "";

    public bool FrameColorInvalid => !HexColorInput.TryParse(FrameColorHex, out _);
    public string FrameThicknessText => $"{FrameThickness:0.#}%";
    public string FrameBottomText => $"{FrameBottom:0.#}%";
    public bool CanDeleteFrame => SelectedFrame is { IsBuiltIn: false };

    private void LoadFrames()
    {
        var keep = SelectedFrame?.Id;
        Frames.Clear();
        foreach (var f in PhotoFrame.BuiltIns) Frames.Add(f);
        foreach (var f in FrameStore.Frames.OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase)) Frames.Add(f);
        _applyingFrame = true;
        try { SelectedFrame = Frames.FirstOrDefault(f => f.Id == keep); }
        finally { _applyingFrame = false; }
        OnPropertyChanged(nameof(CanDeleteFrame));
    }

    // Id and Name stay empty so identical settings compare equal in undo history.
    private PhotoFrame? CurrentFrame(bool ignoreEnabled = false)
    {
        if (!FrameEnabled && !ignoreEnabled) return null;
        if (HexColorInput.TryParse(FrameColorHex, out _)) _validColorHex = FrameColorHex.Trim().StartsWith('#') ? FrameColorHex.Trim() : "#" + FrameColorHex.Trim();
        return new PhotoFrame
        {
            ThicknessPercent = (float)FrameThickness, BottomExtraPercent = (float)FrameBottom, ColorHex = _validColorHex,
            Aspect = PhotoFrame.AspectChoices[Math.Clamp(FrameAspectIndex, 0, PhotoFrame.AspectChoices.Length - 1)].Ratio
        }.Normalized();
    }

    private void FrameChanged(bool commit)
    {
        OnPropertyChanged(nameof(FrameColorInvalid));
        OnPropertyChanged(nameof(FrameThicknessText)); OnPropertyChanged(nameof(FrameBottomText));
        if (_restoring || _applyingFrame) { ScheduleRender(); return; }
        if (commit) CommitNow(); else CommitSoon();
        ScheduleRender();
    }

    partial void OnFrameEnabledChanged(bool value) => FrameChanged(true);
    partial void OnFrameThicknessChanged(double value) { SelectedFrameDetach(); FrameChanged(false); }
    partial void OnFrameBottomChanged(double value) { SelectedFrameDetach(); FrameChanged(false); }
    partial void OnFrameColorHexChanged(string value) { SelectedFrameDetach(); FrameChanged(false); }
    partial void OnFrameAspectIndexChanged(int value) { SelectedFrameDetach(); FrameChanged(true); }

    // A changed control no longer matches the chosen frame.
    private void SelectedFrameDetach()
    {
        if (_restoring || _applyingFrame || SelectedFrame == null) return;
        _applyingFrame = true;
        try { SelectedFrame = null; } finally { _applyingFrame = false; }
        OnPropertyChanged(nameof(CanDeleteFrame));
    }

    partial void OnSelectedFrameChanged(PhotoFrame? value)
    {
        OnPropertyChanged(nameof(CanDeleteFrame));
        if (value == null || _applyingFrame) return;
        _applyingFrame = true;
        try
        {
            FrameThickness = value.ThicknessPercent;
            FrameBottom = value.BottomExtraPercent;
            FrameColorHex = value.ColorHex;
            FrameAspectIndex = Math.Max(0, Array.FindIndex(PhotoFrame.AspectChoices, a => a.Ratio == value.Aspect));
            FrameName = value.IsBuiltIn ? "" : value.Name;
        }
        finally { _applyingFrame = false; }
        FrameEnabled = true; // choosing a frame turns the layer on (commits and re-renders)
        FrameChanged(true);
    }

    [RelayCommand]
    private void SetFrameColor(string hex) => FrameColorHex = hex;

    [RelayCommand]
    private void SaveFrame()
    {
        if (CurrentFrame(ignoreEnabled: true) is not { } spec) return;
        var saved = FrameStore.Add(FrameName, spec);
        LoadFrames();
        _applyingFrame = true;
        try { SelectedFrame = Frames.FirstOrDefault(f => f.Id == saved.Id); }
        finally { _applyingFrame = false; }
        OnPropertyChanged(nameof(CanDeleteFrame));
    }

    [RelayCommand]
    private void DeleteFrame()
    {
        if (SelectedFrame is not { IsBuiltIn: false } f) return;
        FrameStore.Delete(f.Id);
        _applyingFrame = true;
        try { SelectedFrame = null; } finally { _applyingFrame = false; }
        LoadFrames();
    }

    // Adjust, Look, then frame. Returns a new image. `beforeFrame` sees the picture without the frame.
    private static Image<Rgb24> RenderAll(Image<Rgb24> src, GradeStack stack, PhotoFrame? frame, FilmLayer? film = null, PhotoAdjust? adjust = null,
        Action<Image<Rgb24>>? beforeFrame = null, SkinMask? lookSkin = null)
    {
        // Adjustments come first, so Look and frame see the final picture.
        using var adjusted = adjust is { IsNoOp: false } ? adjust.Render(src) : null;
        var basis = adjusted ?? src;
        using var graded = stack.IsEmpty ? null : stack.Render(basis, lookSkin);
        beforeFrame?.Invoke(graded ?? basis);
        if (film != null) return FilmFrameRenderer.Render(graded ?? basis, film.Style, film.Meta, film.Number, film.LookName, film.Label);
        return frame is { IsNoOp: false } ? frame.Render(graded ?? basis) : (graded ?? basis).Clone();
    }

    public IReadOnlyList<string> CropLabels { get; } = PhotoAdjust.CropChoices.Select(c => Loc.T(c.Label)).ToList();
    public double MaxStraighten => PhotoAdjust.MaxStraighten;
    public double MaxExposure => PhotoAdjust.MaxExposure;
    public double MaxZoom => PhotoAdjust.MaxZoom;

    [ObservableProperty] private int _quarter;
    [ObservableProperty] private double _straighten;
    [ObservableProperty] private int _cropIndex;
    [ObservableProperty] private double _zoom = 1;
    [ObservableProperty] private double _exposure;
    [ObservableProperty] private double _panX;
    [ObservableProperty] private double _panY;
    [ObservableProperty] private double _contrast;
    [ObservableProperty] private double _highlights;
    [ObservableProperty] private double _shadows;
    [ObservableProperty] private double _whites;
    [ObservableProperty] private double _blacks;
    [ObservableProperty] private double _saturation;
    [ObservableProperty] private double _vibrance;
    [ObservableProperty] private double _temperature;
    [ObservableProperty] private double _tint;
    [ObservableProperty] private double _sharpness;
    [ObservableProperty] private double _vignette;
    [ObservableProperty] private double _grain;
    [ObservableProperty] private double _glow;
    [ObservableProperty] private double _halation;
    [ObservableProperty] private double _noiseLuma;
    [ObservableProperty] private double _noiseColor;
    [ObservableProperty] private double _skinSmoothing;
    public bool HasSkinSupport => SkinRetouch.IsAvailable;
    private bool _noReclamp;

    // Sections of the Look tab: Looks, Crop, Light, Color, Effects.
    [ObservableProperty] private int _lookSection;
    public bool LookIsLooks => LookSection == 0;
    public bool LookIsCrop => LookSection == 1;
    public bool LookIsLight => LookSection == 2;
    public bool LookIsColor => LookSection == 3;
    public bool LookIsEffects => LookSection == 4;
    // 0-4 are the Look sections, 5 Frame, 6 Export.
    public int EditSection
    {
        get => _settings.EditTab == 0 ? LookSection : _settings.EditTab + 4;
        set
        {
            if (value < 0) return;
            if (value <= 4) { _settings.EditTab = 0; LookSection = value; }
            else _settings.EditTab = value - 4;
            OnPropertyChanged();
        }
    }
    public void RaiseEditSection()
    {
        OnPropertyChanged(nameof(EditSection));
        DetailStateChanged();
    }

    partial void OnLookSectionChanged(int value)
    {
        OnPropertyChanged(nameof(EditSection));
        OnPropertyChanged(nameof(LookIsLooks)); OnPropertyChanged(nameof(LookIsCrop));
        OnPropertyChanged(nameof(LookIsLight)); OnPropertyChanged(nameof(LookIsColor));
        OnPropertyChanged(nameof(LookIsEffects));
        OnPropertyChanged(nameof(ShowCropView)); RaiseHistogram();
        if (!IsDetailActive) DropDetailPixels();
        RaiseDetailVisibility();
        // Crop moves the photo by transforms; the result is rendered on leaving.
        ScheduleRender();
        LookChanged?.Invoke();
        if (value == 0) ScheduleThumbs();
    }

    /// <summary>The photo before cropping (exposure and Look applied), shown in the Crop section and moved by transforms.</summary>
    [ObservableProperty] private Bitmap? _cropBase;
    public bool ShowCropView => LookIsCrop && HasPhoto && CropBase != null;
    public bool ShowPhotoView => HasPhoto && !ShowCropView && !ShowDetail;
    partial void OnCropBaseChanged(Bitmap? value) { OnPropertyChanged(nameof(ShowCropView)); OnPropertyChanged(nameof(ShowPhotoView)); }

    /// <summary>Size of the crop in photo pixels (the shape the crop window has on screen).</summary>
    public (int Width, int Height) CropOutputSize() => _source == null ? (0, 0) : CurrentAdjustSettings().OutputSize(_source.Width, _source.Height);

    // Rebuild the tiles when crop or exposure changed.
    private string _thumbAdjustKey = "";
    private string AdjustKey => ThumbAdjust()?.ToString() ?? "";
    private void SyncThumbsToAdjust()
    {
        if (_source == null || AdjustKey == _thumbAdjustKey) return;
        ResetThumbs();
        foreach (var it in Items) it.Thumb = null;
    }

    public string StraightenText => $"{Straighten:+0.0;-0.0;0.0}°";
    public string ZoomText => $"{Zoom * 100:0}%";
    public string ExposureText => $"{Exposure:+0.0;-0.0;0.0} EV";
    public bool HasAdjust => CurrentAdjust() != null;
    public bool HasGeometryAdjust => CurrentAdjustSettings().HasGeometry;
    public bool HasExposureAdjust => CurrentAdjustSettings().HasExposure;
    public bool HasLightAdjust => CurrentAdjustSettings().HasLight;
    public bool HasColorAdjust => CurrentAdjustSettings().HasColor;
    public bool HasEffectsAdjust => CurrentAdjustSettings().HasEffects;

    private PhotoAdjust CurrentAdjustSettings() => new PhotoAdjust
    {
        Quarter = Quarter, Straighten = (float)Straighten, Zoom = (float)Zoom, Exposure = (float)Exposure, PanX = (float)PanX, PanY = (float)PanY,
        Contrast = (float)Contrast, Highlights = (float)Highlights, Shadows = (float)Shadows, Whites = (float)Whites,
            Blacks = (float)Blacks, Saturation = (float)Saturation, Vibrance = (float)Vibrance,
            Temperature = (float)Temperature, Tint = (float)Tint, Sharpness = (float)Sharpness,
            Vignette = (float)Vignette, Grain = (float)Grain, Glow = (float)Glow, Halation = (float)Halation,
            NoiseLuma = (float)NoiseLuma, NoiseColor = (float)NoiseColor, SkinSmoothing = (float)SkinSmoothing,
        Aspect = PhotoAdjust.CropChoices[Math.Clamp(CropIndex, 0, PhotoAdjust.CropChoices.Length - 1)].Ratio
    }.Normalized();

    private PhotoAdjust? CurrentAdjust() => CurrentAdjustSettings() is { IsNoOp: false } a ? a : null;

    private void ApplyAdjustSettings(PhotoAdjust a)
    {
        Quarter = a.Quarter; Straighten = a.Straighten; Zoom = a.Zoom; Exposure = a.Exposure; PanX = a.PanX; PanY = a.PanY;
        Contrast = a.Contrast; Highlights = a.Highlights; Shadows = a.Shadows; Whites = a.Whites; Blacks = a.Blacks;
            Saturation = a.Saturation; Vibrance = a.Vibrance; Temperature = a.Temperature; Tint = a.Tint;
            Sharpness = a.Sharpness; Vignette = a.Vignette; Grain = a.Grain; Glow = a.Glow; Halation = a.Halation;
            NoiseLuma = a.NoiseLuma; NoiseColor = a.NoiseColor; SkinSmoothing = a.SkinSmoothing;
        CropIndex = Math.Max(0, Array.FindIndex(PhotoAdjust.CropChoices, c => c.Ratio == a.Aspect));
    }

    private void RaiseAdjustTexts()
    {
        OnPropertyChanged(nameof(StraightenText)); OnPropertyChanged(nameof(ZoomText)); OnPropertyChanged(nameof(ExposureText));
        OnPropertyChanged(nameof(HasAdjust)); OnPropertyChanged(nameof(HasGeometryAdjust)); OnPropertyChanged(nameof(HasExposureAdjust));
        OnPropertyChanged(nameof(HasLightAdjust)); OnPropertyChanged(nameof(HasColorAdjust)); OnPropertyChanged(nameof(HasEffectsAdjust));
        OnPropertyChanged(nameof(SuggestedExportName));
    }

    /// <summary>The adjustment the Card frames are built from (null while nothing is changed).</summary>
    public PhotoAdjust? AdjustForCard() => CurrentAdjust();

    /// <summary>Drag on the canvas: moves the picture under the crop by a fraction of what is on screen.</summary>
    public void PanBy(double fractionX, double fractionY)
    {
        if (_source == null) return;
        var moved = CurrentAdjustSettings() with { PanX = (float)(PanX - fractionX), PanY = (float)(PanY - fractionY) };
        var (x, y) = moved.ClampedPan(_source.Width, _source.Height);
        _noReclamp = true;
        try { PanX = x; PanY = y; }
        finally { _noReclamp = false; }
    }

    private void ReclampPan()
    {
        if (_noReclamp || _source == null || (PanX == 0 && PanY == 0)) return;
        var (x, y) = CurrentAdjustSettings().ClampedPan(_source.Width, _source.Height);
        if (Math.Abs(x - PanX) < 1e-4 && Math.Abs(y - PanY) < 1e-4) return;
        _noReclamp = true;
        try { PanX = x; PanY = y; }
        finally { _noReclamp = false; }
    }

    private (int Width, int Height) _fullSize;

    // The Original size in Export is the full-resolution photo after crop and straighten.
    private void UpdateExportSize()
    {
        var (w, h) = _fullSize.Width > 0 ? CurrentAdjustSettings().OutputSize(_fullSize.Width, _fullSize.Height) : (0, 0);
        _settings.SourceWidth = w; _settings.SourceHeight = h;
    }

    private void AdjustChanged(bool commit, bool geometry = true)
    {
        if (geometry) UpdateExportSize();
        if (!_restoring) ReclampPan();
        RaiseAdjustTexts();
        // In Crop nothing is re-rendered until the section is left.
        bool live = geometry && LookIsCrop;
        if (!live) LookChanged?.Invoke(); // Card frames are built from the adjusted photo too
        if (_restoring) { if (!live) ScheduleRender(); return; }
        if (commit) CommitNow(); else CommitSoon();
        if (!live) ScheduleRender();
    }

    partial void OnQuarterChanged(int value) { _noReclamp = true; try { PanX = 0; PanY = 0; } finally { _noReclamp = false; } AdjustChanged(true); }
    partial void OnPanXChanged(double value) => AdjustChanged(false);
    partial void OnPanYChanged(double value) => AdjustChanged(false);
    partial void OnStraightenChanged(double value) => AdjustChanged(false);
    partial void OnCropIndexChanged(int value) => AdjustChanged(true);
    partial void OnZoomChanged(double value) => AdjustChanged(false);
    partial void OnExposureChanged(double value) => AdjustChanged(false, geometry: false);
    partial void OnContrastChanged(double value) => AdjustChanged(false, geometry: false);
    partial void OnHighlightsChanged(double value) => AdjustChanged(false, geometry: false);
    partial void OnShadowsChanged(double value) => AdjustChanged(false, geometry: false);
    partial void OnWhitesChanged(double value) => AdjustChanged(false, geometry: false);
    partial void OnBlacksChanged(double value) => AdjustChanged(false, geometry: false);
    partial void OnSaturationChanged(double value) => AdjustChanged(false, geometry: false);
    partial void OnVibranceChanged(double value) => AdjustChanged(false, geometry: false);
    partial void OnTemperatureChanged(double value) => AdjustChanged(false, geometry: false);
    partial void OnTintChanged(double value) => AdjustChanged(false, geometry: false);
    partial void OnSharpnessChanged(double value) => AdjustChanged(false, geometry: false);
    partial void OnVignetteChanged(double value) => AdjustChanged(false, geometry: false);
    partial void OnGrainChanged(double value) => AdjustChanged(false, geometry: false);
    partial void OnGlowChanged(double value) => AdjustChanged(false, geometry: false);
    partial void OnHalationChanged(double value) => AdjustChanged(false, geometry: false);
    partial void OnNoiseLumaChanged(double value) => AdjustChanged(false, geometry: false);
    partial void OnNoiseColorChanged(double value) => AdjustChanged(false, geometry: false);
    partial void OnSkinSmoothingChanged(double value) => AdjustChanged(false, geometry: false);

    [RelayCommand] private void RotateLeft() => Quarter = (Quarter + 3) % 4;
    [RelayCommand] private void RotateRight() => Quarter = (Quarter + 1) % 4;
    [RelayCommand] private void ResetExposure() => Exposure = 0;
    [RelayCommand] private void ResetLight() => ApplyGroup(() => { Exposure = 0; Contrast = 0; Highlights = 0; Shadows = 0; Whites = 0; Blacks = 0; });
    [RelayCommand] private void ResetColor() => ApplyGroup(() => { Saturation = 0; Vibrance = 0; Temperature = 0; Tint = 0; });
    [RelayCommand] private void ResetEffects() => ApplyGroup(() => { Sharpness = 0; Vignette = 0; Grain = 0; Glow = 0; Halation = 0; NoiseLuma = 0; NoiseColor = 0; SkinSmoothing = 0; });
    // Several sliders at once, as one undo step.
    private void ApplyGroup(Action change)
    {
        _restoring = true;
        try { change(); }
        finally { _restoring = false; }
        AdjustChanged(true, geometry: false);
    }
    [RelayCommand]
    private void ResetGeometry()
    {
        _restoring = true;
        try { Quarter = 0; Straighten = 0; Zoom = 1; CropIndex = 0; PanX = 0; PanY = 0; }
        finally { _restoring = false; }
        AdjustChanged(true);
    }

    // A new photo starts unadjusted; Look and frame carry over.
    private void ResetPhotoAdjust()
    {
        _restoring = true;
        try
        {
            Quarter = 0; Straighten = 0; Zoom = 1; Exposure = 0; CropIndex = 0; PanX = 0; PanY = 0; Contrast = 0;
            Highlights = 0; Shadows = 0; Whites = 0; Blacks = 0; Saturation = 0; Vibrance = 0; Temperature = 0;
            Tint = 0; Sharpness = 0; Vignette = 0; Grain = 0; Glow = 0; Halation = 0; NoiseLuma = 0; NoiseColor = 0;
            SkinSmoothing = 0;
        }
        finally { _restoring = false; }
        RaiseAdjustTexts();
        _commitTimer?.Stop();
        // History belongs to one photo: undo must not bring the previous photo's crop or sliders onto this one.
        _undo.Clear(); _redo.Clear();
        _committed = Capture();
        NotifyHistory();
    }

    // Live preview
    private bool _isRendering;
    /// <summary>A preview render is in flight (shown as an "Updating…" chip so a slow phone doesn't look frozen).</summary>
    public bool IsRendering { get => _isRendering; private set { if (_isRendering == value) return; _isRendering = value; OnPropertyChanged(); } }

    // One render at a time with only the newest queued, so a dragged slider shows every frame it can. UI thread
    // only.
    private Func<Task>? _pendingRender;
    private bool _renderPumping;
    private int _renderEpoch;

    private void EnqueueRender(Func<Task> job)
    {
        _pendingRender = job;
        if (_renderPumping) return;
        _renderPumping = true;
        _ = PumpRenders();
    }

    private async Task PumpRenders()
    {
        try { while (_pendingRender is { } job) { _pendingRender = null; await job(); } }
        finally { _renderPumping = false; }
    }

    private void ScheduleRender()
    {
        var src = _source;
        if (src == null) { _renderEpoch++; _pendingRender = null; return; }
        if (IsDetailActive) { ScheduleDetail(); return; }
        var stack = BuildStack();
        var frame = CurrentFrame();
        var film = CurrentFilm(_loadedPath, useSettingsMetadata: true);
        var adjust = CurrentAdjust();
        if (LookIsCrop)
        {
            // Crop view: exposure and Look, but no geometry, frame or film.
            var tone = CurrentAdjustSettings().ToneOnly() with { Sharpness = 0, Vignette = 0, Grain = 0, Glow = 0, Halation = 0 };
            if (stack.IsEmpty && !tone.HasTone) { _renderEpoch++; _pendingRender = null; CropBase = Original; IsRendering = false; return; }
            IsRendering = true;
            int ep = _renderEpoch;
            EnqueueRender(() => RenderAsync(src, stack, null, null, tone, ep, forCrop: true));
            return;
        }
        if (stack.IsEmpty && (frame == null || frame.IsNoOp) && film == null && adjust == null) { _renderEpoch++; _pendingRender = null; Graded = null; IsRendering = false; return; }
        IsRendering = true;
        int epoch = _renderEpoch;
        EnqueueRender(() => RenderAsync(src, stack, frame, film, adjust, epoch));
    }

    private async Task RenderAsync(Image<Rgb24> src, GradeStack stack, PhotoFrame? frame, FilmLayer? film, PhotoAdjust? adjust, int epoch, bool forCrop = false)
    {
        try
        {
            ImageHistogram? measured = null;
            Action<Image<Rgb24>>? measure = ShowHistogram && !forCrop ? img => measured = ImageHistogram.Compute(img) : null;
            var shown = await Task.Run(() =>
            {
                // _source is only replaced on the UI thread and stale results are dropped, so reading it here is
                // safe.
                using var graded = RenderPreview(src, stack, frame, film, adjust, measure);
                return ToWriteable(graded);
            });
            if (epoch != _renderEpoch || !ReferenceEquals(src, _source)) return;
            if (forCrop) CropBase = shown; else { _resultHistogram = measured; Graded = shown; }
        }
        catch (ObjectDisposedException) { } // photo changed mid-render
        finally { if (_pendingRender == null) IsRendering = false; }
    }

    // Cleaning and geometry are slow and rarely change, so each result is cached.
    private readonly object _geomLock = new();
    private string? _geomKey;
    private Image<Rgb24>? _geomImg, _geomSrc;
    private string? _cleanKey;
    private Image<Rgb24>? _cleanImg, _cleanSrc;
    private SkinMask? _skinMask;
    private Image<Rgb24>? _skinSrc;

    // Segmentation runs once per photo. Call under _geomLock.
    private SkinMask? PreviewSkin(Image<Rgb24> src)
    {
        if (!ReferenceEquals(_skinSrc, src)) { _skinMask = SkinRetouch.Segmenter?.Segment(src); _skinSrc = src; }
        return _skinMask;
    }

    private SkinMask? _framedSkinMask;
    private Image<Rgb24>? _framedSkinSrc;
    private string? _framedSkinKey;

    // The mask a Look uses: one per photo and crop, so moving a slider does not run the model again. Call under
    // _geomLock.
    private SkinMask? LookSkin(Image<Rgb24> src, PhotoAdjust geometry, Image<Rgb24> framed)
    {
        if (geometry.IsNoOp) return PreviewSkin(src);
        var key = geometry.ToString();
        if (!ReferenceEquals(_framedSkinSrc, src) || key != _framedSkinKey)
        {
            _framedSkinMask = SkinRetouch.Segmenter?.Segment(framed);
            _framedSkinSrc = src; _framedSkinKey = key;
        }
        return _framedSkinMask;
    }

    private Image<Rgb24> RenderPreview(Image<Rgb24> src, GradeStack stack, PhotoFrame? frame, FilmLayer? film, PhotoAdjust? adjust,
        Action<Image<Rgb24>>? measure)
    {
        var geometry = adjust == null ? new PhotoAdjust() : adjust.WithoutTone().WithoutCleaning();
        var cleaning = adjust?.CleaningOnly();
        var tone = adjust?.ToneOnly();
        lock (_geomLock)
        {
            var basis = src;
            if (cleaning is { IsNoOp: false })
            {
                var key = cleaning.ToString();
                if (!ReferenceEquals(_cleanSrc, src) || key != _cleanKey)
                {
                    _cleanImg?.Dispose();
                    _cleanImg = cleaning.Render(src, cleaning.HasSkinSmoothing ? PreviewSkin(src) : null);
                    _cleanKey = key; _cleanSrc = src;
                    _geomSrc = null; // Cut from the previous cleaned picture.
                }
                basis = _cleanImg!;
            }
            if (!geometry.IsNoOp)
            {
                var key = geometry.ToString();
                if (!ReferenceEquals(_geomSrc, basis) || key != _geomKey)
                {
                    _geomImg?.Dispose();
                    _geomImg = geometry.Render(basis);
                    _geomKey = key; _geomSrc = basis;
                }
                basis = _geomImg!;
            }
            var lookSkin = stack.SmoothsSkin ? LookSkin(src, geometry, basis) : null;
            using var exposed = tone is { HasTone: true } ? tone.Render(basis) : null;
            return RenderAll(exposed ?? basis, stack, frame, film, beforeFrame: measure, lookSkin: lookSkin);
        }
    }

    // Raw BGRA, no encode and decode per slider tick.
    private static unsafe WriteableBitmap ToWriteable(Image<Rgb24> img)
    {
        int w = img.Width, h = img.Height;
        var wb = new WriteableBitmap(new global::Avalonia.PixelSize(w, h), new global::Avalonia.Vector(96, 96),
            global::Avalonia.Platform.PixelFormat.Bgra8888, global::Avalonia.Platform.AlphaFormat.Opaque);
        using var fb = wb.Lock();
        byte* basePtr = (byte*)fb.Address;
        int stride = fb.RowBytes;
        System.Threading.Tasks.Parallel.For(0, h, y =>
        {
            var row = img.Frames.RootFrame.DangerousGetPixelRowMemory(y).Span;
            byte* o = basePtr + (long)y * stride;
            for (int x = 0; x < row.Length; x++, o += 4)
            {
                o[0] = row[x].B; o[1] = row[x].G; o[2] = row[x].R; o[3] = 255;
            }
        });
        return wb;
    }

    private sealed record Snapshot(string? PresetId, double PresetStrength, bool PresetOn,
        string? LutId, double LutStrength, bool LutOn, PhotoFrame? Frame, bool FrameOn, PhotoAdjust Adjust);

    private readonly List<Snapshot> _undo = [];
    private readonly List<Snapshot> _redo = [];
    private Snapshot _committed = new(null, 100, true, null, 100, true, null, false, new PhotoAdjust());
    private DispatcherTimer? _commitTimer;
    private bool _restoring;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    // Captured while off too, so re-enabling keeps the settings.
    private Snapshot Capture() => new(_presetEntry?.Id, PresetStrength, PresetEnabled, _lutEntry?.Id, LutStrength, LutEnabled,
        CurrentFrame(ignoreEnabled: true), FrameEnabled, CurrentAdjustSettings());

    // One history step once a slider drag settles.
    private void CommitSoon()
    {
        _commitTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _commitTimer.Stop();
        _commitTimer.Tick -= OnCommitTick;
        _commitTimer.Tick += OnCommitTick;
        _commitTimer.Start();
    }

    private void OnCommitTick(object? s, EventArgs e) { _commitTimer?.Stop(); CommitNow(); }

    private void CommitNow()
    {
        _commitTimer?.Stop();
        var now = Capture();
        if (now == _committed) return;
        _undo.Add(_committed);
        if (_undo.Count > 100) _undo.RemoveAt(0);
        _redo.Clear();
        _committed = now;
        NotifyHistory();
    }

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo()
    {
        CommitNow();
        if (_undo.Count == 0) return;
        _redo.Add(_committed);
        Restore(_undo[^1]);
        _undo.RemoveAt(_undo.Count - 1);
    }

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo()
    {
        if (_redo.Count == 0) return;
        _undo.Add(_committed);
        Restore(_redo[^1]);
        _redo.RemoveAt(_redo.Count - 1);
    }

    private void Restore(Snapshot s)
    {
        _restoring = true;
        try
        {
            SetLayer(s.PresetId, GradeKind.Preset);
            SetLayer(s.LutId, GradeKind.Lut);
            PresetStrength = s.PresetStrength; PresetEnabled = s.PresetOn;
            LutStrength = s.LutStrength; LutEnabled = s.LutOn;
            if (s.Frame is { } fr)
            {
                FrameThickness = fr.ThicknessPercent; FrameBottom = fr.BottomExtraPercent; FrameColorHex = fr.ColorHex;
                FrameAspectIndex = Math.Max(0, Array.FindIndex(PhotoFrame.AspectChoices, a => a.Ratio == fr.Aspect));
            }
            FrameEnabled = s.FrameOn;
            ApplyAdjustSettings(s.Adjust);
            _applyingFrame = true; SelectedFrame = null; _applyingFrame = false;
        }
        finally { _restoring = false; }
        _committed = s;
        AdjustChanged(false);
        FrameChanged(false);
        LayersChanged();
        RefreshLibrary();
        NotifyHistory();
    }

    // A deleted library file restores as an empty layer.
    private void SetLayer(string? id, GradeKind kind)
    {
        var entry = id == null ? null : Grades.Entries.FirstOrDefault(e => e.Id == id);
        object? obj = null;
        if (entry != null && !TryLoad(entry, out obj, out _)) entry = null;
        if (kind == GradeKind.Preset) { _presetEntry = entry; _preset = obj as LrPreset; }
        else { _lutEntry = entry; _lut = obj as CubeLut; }
    }

    private void NotifyHistory()
    {
        OnPropertyChanged(nameof(CanUndo)); OnPropertyChanged(nameof(CanRedo));
        UndoCommand.NotifyCanExecuteChanged(); RedoCommand.NotifyCanExecuteChanged();
    }

    [ObservableProperty] private bool _isExporting;
    [ObservableProperty] private string _exportStatus = "";
    public bool HasExportStatus => ExportStatus.Length > 0;
    partial void OnExportStatusChanged(string value) => OnPropertyChanged(nameof(HasExportStatus));
    [ObservableProperty] private int _exportFormatIndex; // 0 JPEG, 1 PNG
    [ObservableProperty] private double _exportQuality = 95;
    public bool ExportIsJpeg => ExportFormatIndex == 0;
    public string ExportButtonText => ExportFormatIndex == 1 ? Loc.T("Export PNG") : Loc.T("Export JPEG");
    public string ExportQualityText => $"{(int)Math.Round(ExportQuality)}";
    partial void OnExportFormatIndexChanged(int value)
    {
        _settings.OutputFormat = value == 1 ? PhSpectre.Rendering.OutputFormat.Png : PhSpectre.Rendering.OutputFormat.Jpeg;
        OnPropertyChanged(nameof(ExportIsJpeg)); OnPropertyChanged(nameof(ExportButtonText));
        OnPropertyChanged(nameof(SuggestedExportName));
    }
    partial void OnExportQualityChanged(double value) { PhSpectre.Rendering.ExportDefaults.JpegQuality = (int)Math.Round(value); OnPropertyChanged(nameof(ExportQualityText)); }
    public bool CanExport => HasPhoto && !IsExporting;
    partial void OnIsExportingChanged(bool value) { OnPropertyChanged(nameof(CanExport)); OnPropertyChanged(nameof(CanExportAll)); }

    public string SuggestedExportName =>
        Path.GetFileNameWithoutExtension(PhotoName) + (HasLayers || FrameEnabled || FilmEnabled || CurrentAdjust() != null ? "_graded" : "") + (ExportFormatIndex == 1 ? ".png" : ".jpg");

    // The Save to folder, else next to the photo.
    public string DefaultExportDir =>
        !string.IsNullOrEmpty(_settings.SaveFolder) && Directory.Exists(_settings.SaveFolder) ? _settings.SaveFolder
        : _loadedPath != null ? Path.GetDirectoryName(_loadedPath)! : Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);

    // Same size choices as for cards.
    private static Image<Rgb24> ApplyOutputSize(Image<Rgb24> img, PhSpectre.Rendering.ExportPreset preset, bool half)
    {
        (int W, int H)? canvas = preset switch
        {
            PhSpectre.Rendering.ExportPreset.Square => (1080, 1080),
            PhSpectre.Rendering.ExportPreset.InstagramPost => (1080, 1350),
            PhSpectre.Rendering.ExportPreset.Story => (1080, 1920),
            PhSpectre.Rendering.ExportPreset.TelegramLandscape => (1920, 1080),
            _ => null,
        };
        if (canvas is null && !half) return img;
        img.Mutate(x =>
        {
            if (canvas is { } c)
                x.Resize(new ResizeOptions { Size = new Size(c.W, c.H), Mode = ResizeMode.Pad, PadColor = SixLabors.ImageSharp.Color.Black });
            else x.Resize(Math.Max(1, img.Width / 2), Math.Max(1, img.Height / 2));
        });
        return img;
    }

    // Export all: the current Look and Border on every photo of the folder
    public int PhotoCount => _settings.PhotosProvider?.Invoke().Count ?? 0;
    public bool CanExportAll => PhotoCount > 1 && !IsExporting;
    public string ExportAllText => Loc.N(PhotoCount, "Export all {0} photo…", "Export all {0} photos…");

    // On mobile `gallery` receives each file instead of a folder.
    public async Task ExportAllAsync(string folder, Func<string, string, Task<bool>>? gallery = null)
    {
        var files = _settings.PhotosProvider?.Invoke().ToList();
        if (files == null || files.Count == 0) return;
        var stack = BuildStack();
        var frame = CurrentFrame();
        // Rotation, zoom and position belong to one photo; ratio and exposure apply to all.
        var adjust = CurrentAdjustSettings() with { Quarter = 0, Straighten = 0, Zoom = 1, PanX = 0, PanY = 0 };
        var filmStyle = FilmEnabled ? CurrentFilmStyle() : null;
        var lookName = CurrentLookName();
        int quality = (int)Math.Round(ExportQuality);
        var preset = _settings.ExportPreset; bool half = _settings.HalfSize && !_settings.IsMobile;
        bool png = ExportFormatIndex == 1;
        IsExporting = true;
        int done = 0, failed = 0;
        try
        {
            if (gallery != null) folder = Path.Combine(Path.GetTempPath(), "phspectre-export");
            Directory.CreateDirectory(folder);
            var taken = new HashSet<string>();
            foreach (var f in files)
            {
                ExportStatus = Loc.F("Saving {0} of {1}…", done + failed + 1, files.Count);
                // Nothing in the folder is overwritten: an earlier export may be one of the source photos. The
                // gallery's folder is a temporary one.
                var dest = PhSpectre.Services.SafeFiles.Unused(folder,
                    Path.GetFileNameWithoutExtension(f.FileName) + "_graded" + (png ? ".png" : ".jpg"), taken,
                    avoidExisting: gallery == null);
                try
                {
                    await Task.Run(() =>
                    {
                        using var img = Services.FastDecode.LoadUpright(f.FullPath);
                        var film = filmStyle == null ? null
                            : new FilmLayer(filmStyle, PaletteImageRenderer.ReadMetadata(f.FullPath), files.IndexOf(f) + 1, lookName, FrameLabelFor(f.FullPath, files.IndexOf(f)));
                        using var final = RenderAll(img, stack, frame, film, adjust);
                        ApplyOutputSize(final, preset, half);
                        if (png) final.SaveAsPng(dest);
                        else final.SaveAsJpeg(dest, new JpegEncoder { Quality = quality });
                    });
                    if (gallery != null)
                    {
                        try { if (!await gallery(dest, Path.GetFileName(dest))) throw new IOException(Loc.T("gallery refused the file")); }
                        finally { try { File.Delete(dest); } catch { /* temp */ } }
                    }
                    done++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or UnknownImageFormatException
                                               or InvalidImageContentException || gallery != null)
                { failed++; }
            }
            ExportStatus = failed == 0 ? Loc.N(done, "Saved {0} photo.", "Saved {0} photos.") : Loc.F("Saved {0}, {1} failed.", done, failed);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ExportStatus = Loc.F("Couldn't save: {0}", ex.Message);
        }
        finally { IsExporting = false; }
    }

    // Grades the original at full size; the source is never modified.
    public async Task ExportAsync(string outPath, Func<string, string, Task<bool>>? gallery = null)
    {
        var src = _loadedPath;
        if (src == null) return;
        if (string.Equals(Path.GetFullPath(outPath), Path.GetFullPath(src), StringComparison.OrdinalIgnoreCase))
        {
            ExportStatus = Loc.T("Pick a different file name: the original is never overwritten.");
            return;
        }
        var stack = BuildStack();
        var frame = CurrentFrame();
        var film = CurrentFilm(src, useSettingsMetadata: true);
        var adjust = CurrentAdjust();
        int quality = (int)Math.Round(ExportQuality);
        var preset = _settings.ExportPreset; bool half = _settings.HalfSize && !_settings.IsMobile;
        IsExporting = true;
        ExportStatus = Loc.T("Saving…");
        try
        {
            await Task.Run(() =>
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                using var img = Services.FastDecode.LoadUpright(src);
                long tLoad = sw.ElapsedMilliseconds;
                using var final = RenderAll(img, stack, frame, film, adjust);
                ApplyOutputSize(final, preset, half);
                long tRender = sw.ElapsedMilliseconds;
                if (Path.GetExtension(outPath).Equals(".png", StringComparison.OrdinalIgnoreCase))
                    final.SaveAsPng(outPath);
                else
                    final.SaveAsJpeg(outPath, new JpegEncoder { Quality = quality });
                Services.AppLogger.LogInfo($"Export {img.Width}x{img.Height}: load {tLoad} ms, render {tRender - tLoad} ms, encode {sw.ElapsedMilliseconds - tRender} ms");
            });
            if (gallery != null)
            {
                var name = Path.GetFileName(outPath);
                try
                {
                    ExportStatus = await gallery(outPath, name) ? Loc.F("Saved {0} to Pictures/PhSpectre", name) : Loc.T("Couldn't save to the gallery.");
                }
                finally { try { File.Delete(outPath); } catch { /* temp */ } }
            }
            else ExportStatus = Loc.F("Saved {0}", Path.GetFileName(outPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or UnknownImageFormatException
                                       or InvalidImageContentException || gallery != null)
        {
            ExportStatus = Loc.F("Couldn't save: {0}", ex.Message);
        }
        finally { IsExporting = false; }
    }
}
