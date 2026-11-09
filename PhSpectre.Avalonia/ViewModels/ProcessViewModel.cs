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
    public string Badge => Entry.Kind == GradeKind.Preset ? "PRESET" : "LUT";
    public bool HasThumb => Thumb != null;
    partial void OnThumbChanged(Bitmap? value) => OnPropertyChanged(nameof(HasThumb));
    public GradeItemViewModel(GradeEntry e, ProcessViewModel owner) { Entry = e; Owner = owner; _isFavorite = e.Favorite; }
}

// State of the Process screen: the photo, the user's LUT / preset library, the two grading layers
// (Lightroom preset, then LUT), a live preview on a downscaled copy, undo/redo and export at full size.
public partial class ProcessViewModel : ViewModelBase
{
    private const int PreviewLongSide = 1600;
    private readonly SettingsViewModel _settings;
    private GradeLibrary? _grades;

    public ProcessViewModel(SettingsViewModel settings)
    {
        _settings = settings;
    }

    public SettingsViewModel Settings => _settings;

    [RelayCommand] private void OpenPhotos() => _settings.OpenPhotosCommand?.Execute(null);

    /// <summary>Frame type Border on/off: the border is the last layer on the graded photo.</summary>
    public void SetBorder(bool on) => FrameEnabled = on;

    public static string LibraryRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PhSpectre", "library");

    // Created on first use so the folders are not made for people who never open Process.
    public GradeLibrary Grades => _grades ??= new GradeLibrary(LibraryRoot, Services.BundledLooks.All);

    // ── Photo ───────────────────────────────────────────────────────────────────────────
    [ObservableProperty] private Bitmap? _original;
    [ObservableProperty] private Bitmap? _graded;
    [ObservableProperty] private string _photoName = "";
    [ObservableProperty] private string _errorText = "";
    [ObservableProperty] private bool _showOriginal;
    private string? _loadedPath;
    private Image<Rgb24>? _source; // downscaled, EXIF-oriented working copy

    public bool HasPhoto => Original != null;
    public bool HasError => ErrorText.Length > 0;
    public bool ShowEmpty => !HasPhoto && !HasError;
    public Bitmap? Shown => ShowOriginal || Graded == null ? Original : Graded;
    public string? PhotoPath => _loadedPath;

    partial void OnOriginalChanged(Bitmap? value) { OnPropertyChanged(nameof(FilmFrameAuto)); OnPropertyChanged(nameof(FilmFrameWatermark)); OnPropertyChanged(nameof(HasPhoto)); OnPropertyChanged(nameof(ShowEmpty)); OnPropertyChanged(nameof(Shown)); OnPropertyChanged(nameof(CanExport)); OnPropertyChanged(nameof(CanExportAll)); }
    partial void OnGradedChanged(Bitmap? value) => OnPropertyChanged(nameof(Shown));
    partial void OnShowOriginalChanged(bool value) { OnPropertyChanged(nameof(Shown)); OnPropertyChanged(nameof(CompareIndex)); }

    // Result | Original segment (same control as the pill on the Main screen).
    public int CompareIndex
    {
        get => ShowOriginal ? 1 : 0;
        set => ShowOriginal = value == 1;
    }
    partial void OnErrorTextChanged(string value) { OnPropertyChanged(nameof(HasError)); OnPropertyChanged(nameof(ShowEmpty)); }

    // Picks up the photo that is current in the shell; cheap when it has not changed.
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
        if (entry.FullPath == _loadingPath) return; // already decoding this one
        _ = LoadPhotoAsync(entry.FullPath, entry.FileName);
    }

    private void ClearPhoto()
    {
        _source?.Dispose(); _source = null;
        Original = null; Graded = null; _loadedPath = null; PhotoName = "";
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

    /// <summary>Changes whenever the applied Look changes (cache key for renders built from the graded photo).</summary>
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
            var img = await Task.Run(() =>
            {
                // Decode straight to ~preview size: JPEG decoders can skip most of the work by scaling in the DCT,
                // which matters on phones (a 24 MP photo no longer gets fully decoded just to be shrunk).
                var info = Image.Identify(path);
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
                int longSide = Math.Max(i.Width, i.Height);
                if (longSide > PreviewLongSide)
                {
                    double k = (double)PreviewLongSide / longSide;
                    i.Mutate(x => x.Resize(Math.Max(1, (int)(i.Width * k)), Math.Max(1, (int)(i.Height * k))));
                }
                return i;
            });
            // The user may have switched photos while this one decoded.
            if (_settings.CurrentPhotoProvider?.Invoke()?.FullPath != path) { img.Dispose(); return; }
            _source?.Dispose();
            _source = img;
            _loadedPath = path;
            PhotoName = fileName;
            ErrorText = "";
            Original = ToBitmap(img);
            Graded = null;
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
            ErrorText = "This photo can't be opened for grading.";
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

    // ── Library ─────────────────────────────────────────────────────────────────────────
    public ObservableCollection<GradeItemViewModel> Items { get; } = [];
    public ObservableCollection<GradeGroup> Groups { get; } = [];
    [ObservableProperty] private int _libraryTab; // filter: 0 All, 1 Favorites, 2 Presets, 3 LUTs
    [ObservableProperty] private string _searchText = "";
    private bool FavoritesOnly => LibraryTab == 1;
    [ObservableProperty] private string _importStatus = "";

    public bool HasItems => Items.Count > 0;
    public bool ShowLibraryEmpty => Items.Count == 0;
    public string LibraryEmptyText => SearchText.Length > 0 || FavoritesOnly
        ? "Nothing matches."
        : LibraryTab switch
        {
            2 => "No presets yet. Import Lightroom .xmp files.",
            3 => "No LUTs yet. Import .cube files.",
            _ => "No looks yet. Import presets (.xmp) or LUTs (.cube).",
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
        foreach (var g in Items.GroupBy(i => string.IsNullOrWhiteSpace(i.Entry.Category) ? "Imported" : i.Entry.Category)
                               .OrderBy(g => g.Key == "Imported" ? 1 : 0).ThenBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase))
            Groups.Add(new GradeGroup(g.Key, g.OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).ToList()));
        OnPropertyChanged(nameof(HasItems)); OnPropertyChanged(nameof(ShowLibraryEmpty)); OnPropertyChanged(nameof(LibraryEmptyText));
        _ = FillThumbsAsync();
    }

    // ── Look thumbnails: each tile is the user's own photo with that Look at 100% ──────────
    private const int ThumbLongSide = 180;
    private readonly Dictionary<string, Bitmap> _thumbs = [];
    private Image<Rgb24>? _thumbSource;
    private int _thumbGeneration;

    private string ThumbKey(string id) => $"{_loadedPath}|{id}";

    private async Task FillThumbsAsync()
    {
        int gen = ++_thumbGeneration;
        // Let the photo and its first render reach the screen before the tiles start to compete for the CPU.
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
            // Parsing a big LUT and rendering the tile both happen off the UI thread.
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

    private void ScheduleThumbs() => _ = FillThumbsAsync();

    private void ResetThumbs()
    {
        _thumbGeneration++;
        _thumbs.Clear();
        var old = _thumbSource;
        _thumbSource = null;
        if (_source != null)
        {
            int side = Math.Max(_source.Width, _source.Height);
            double k = Math.Min(1.0, (double)ThumbLongSide / side);
            _thumbSource = _source.Clone(x => x.Resize(Math.Max(1, (int)(_source.Width * k)), Math.Max(1, (int)(_source.Height * k))));
        }
        old?.Dispose();
    }

    public void Import(IEnumerable<string> paths)
    {
        var results = Grades.ImportMany(paths, "");
        int ok = results.Count(r => r.Error == null);
        var errors = results.Where(r => r.Error != null).Select(r => $"{Path.GetFileName(r.Source)}: {r.Error}").ToList();
        var lines = new List<string>();
        if (ok > 0) lines.Add($"Imported {ok}.");
        foreach (var r in results.Where(r => r.Entry is { Skipped.Count: > 0 }))
            lines.Add($"“{r.Entry!.Name}”: not applied — {string.Join(", ", r.Entry.Skipped)}");
        lines.AddRange(errors.Take(5));
        if (errors.Count > 5) lines.Add($"…and {errors.Count - 5} more failed.");
        ImportStatus = string.Join("\n", lines);
        // Show the tab of what was just imported.
        var last = results.LastOrDefault(r => r.Entry != null)?.Entry;
        if (last != null && LibraryTab == 1) LibraryTab = 0; // show the new arrivals
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
        if (!TryLoad(item.Entry, out var obj, out var err)) { ImportStatus = err; return; }
        if (item.Entry.Kind == GradeKind.Preset)
        {
            _preset = (LrPreset)obj!; _presetEntry = item.Entry; PresetEnabled = true;
        }
        else
        {
            _lut = (CubeLut)obj!; _lutEntry = item.Entry; LutEnabled = true;
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
            error = $"“{e.Name}” can't be loaded: {ex.Message}";
            return false;
        }
    }

    // ── Layers: Lightroom preset, then LUT ──────────────────────────────────────────────
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
    public string SkippedText => _presetEntry is { Skipped.Count: > 0 } e
        ? "Not applied: " + string.Join(", ", e.Skipped) : "";
    public bool HasSkipped => SkippedText.Length > 0;
    public string PresetStrengthText => $"{(int)Math.Round(PresetStrength)}%";
    public string LutStrengthText => $"{(int)Math.Round(LutStrength)}%";

    partial void OnPresetStrengthChanged(double value) { OnPropertyChanged(nameof(PresetStrengthText)); LayersChanged(commit: false); }
    partial void OnLutStrengthChanged(double value) { OnPropertyChanged(nameof(LutStrengthText)); LayersChanged(commit: false); }
    partial void OnPresetEnabledChanged(bool value) => LayersChanged();
    partial void OnLutEnabledChanged(bool value) => LayersChanged();

    [RelayCommand] private void ClearPreset() { _preset = null; _presetEntry = null; LayersChanged(); CommitNow(); RefreshLibrary(); }
    [RelayCommand] private void ClearLut() { _lut = null; _lutEntry = null; LayersChanged(); CommitNow(); RefreshLibrary(); }

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

    // ── Frame (last layer: drawn around the graded photo) ──────────────────────────────
    private PhotoFrameStore? _frameStore;
    private PhotoFrameStore FrameStore => _frameStore ??= new PhotoFrameStore(Path.Combine(LibraryRoot, "frames.json"));
    private bool _applyingFrame;
    private string _validColorHex = "#FFFFFF";

    public ObservableCollection<PhotoFrame> Frames { get; } = [];
    public IReadOnlyList<string> AspectLabels { get; } = PhotoFrame.AspectChoices.Select(a => a.Label).ToList();

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

    // The frame as currently set in the controls; null while the layer is off. Id/Name stay empty so that
    // two identical settings compare equal in the undo history.
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

    // Touching a control means the settings no longer match the chosen frame.
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

    // Grade, then frame. Returns a new image; the source is untouched.
    private static Image<Rgb24> RenderAll(Image<Rgb24> src, GradeStack stack, PhotoFrame? frame, FilmLayer? film = null)
    {
        using var graded = stack.IsEmpty ? null : stack.Render(src);
        if (film != null) return FilmFrameRenderer.Render(graded ?? src, film.Style, film.Meta, film.Number, film.LookName, film.Label);
        return frame is { IsNoOp: false } ? frame.Render(graded ?? src) : (graded ?? src).Clone();
    }

    // ── Live preview ───────────────────────────────────────────────────────────────────
    private CancellationTokenSource? _renderCts;

    private void ScheduleRender()
    {
        _renderCts?.Cancel();
        var src = _source;
        if (src == null) return;
        var stack = BuildStack();
        var frame = CurrentFrame();
        var film = CurrentFilm(_loadedPath, useSettingsMetadata: true);
        if (stack.IsEmpty && (frame == null || frame.IsNoOp) && film == null) { Graded = null; return; }
        var cts = _renderCts = new CancellationTokenSource();
        _ = RenderAsync(src, stack, frame, film, cts.Token);
    }

    private async Task RenderAsync(Image<Rgb24> src, GradeStack stack, PhotoFrame? frame, FilmLayer? film, CancellationToken ct)
    {
        try
        {
            await Task.Delay(30, ct); // coalesce slider drags
            var bmp = await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                // _source is only replaced on the UI thread together with a new render, and a
                // stale result is dropped below, so reading it here is safe.
                using var graded = RenderAll(src, stack, frame, film);
                return ToBitmapBytes(graded);
            }, ct);
            if (ct.IsCancellationRequested || !ReferenceEquals(src, _source)) return;
            using var ms = new MemoryStream(bmp);
            Graded = new Bitmap(ms);
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { } // photo changed mid-render
    }

    private static byte[] ToBitmapBytes(Image<Rgb24> img)
    {
        using var ms = new MemoryStream();
        img.SaveAsBmp(ms);
        return ms.ToArray();
    }

    // ── Undo / redo ────────────────────────────────────────────────────────────────────
    private sealed record Snapshot(string? PresetId, double PresetStrength, bool PresetOn,
        string? LutId, double LutStrength, bool LutOn, PhotoFrame? Frame, bool FrameOn);

    private readonly List<Snapshot> _undo = [];
    private readonly List<Snapshot> _redo = [];
    private Snapshot _committed = new(null, 100, true, null, 100, true, null, false);
    private DispatcherTimer? _commitTimer;
    private bool _restoring;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    // The frame is captured even while its layer is off, so re-enabling it after an undo keeps its settings.
    private Snapshot Capture() => new(_presetEntry?.Id, PresetStrength, PresetEnabled, _lutEntry?.Id, LutStrength, LutEnabled,
        CurrentFrame(ignoreEnabled: true), FrameEnabled);

    // Slider drags fire many changes; one history step is recorded once they settle.
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
            _applyingFrame = true; SelectedFrame = null; _applyingFrame = false;
        }
        finally { _restoring = false; }
        _committed = s;
        FrameChanged(false);
        LayersChanged();
        RefreshLibrary();
        NotifyHistory();
    }

    // A layer whose library file was deleted since the snapshot simply restores as empty.
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

    // ── Export (full resolution) ───────────────────────────────────────────────────────
    [ObservableProperty] private bool _isExporting;
    [ObservableProperty] private string _exportStatus = "";
    public bool HasExportStatus => ExportStatus.Length > 0;
    partial void OnExportStatusChanged(string value) => OnPropertyChanged(nameof(HasExportStatus));
    [ObservableProperty] private int _exportFormatIndex;   // 0 JPEG, 1 PNG
    [ObservableProperty] private double _exportQuality = 95;
    public bool ExportIsJpeg => ExportFormatIndex == 0;
    public string ExportButtonText => ExportFormatIndex == 1 ? "Export PNG" : "Export JPEG";
    public string ExportQualityText => $"{(int)Math.Round(ExportQuality)}";
    partial void OnExportFormatIndexChanged(int value) { OnPropertyChanged(nameof(ExportIsJpeg)); OnPropertyChanged(nameof(ExportButtonText)); OnPropertyChanged(nameof(SuggestedExportName)); }
    partial void OnExportQualityChanged(double value) => OnPropertyChanged(nameof(ExportQualityText));
    public bool CanExport => HasPhoto && !IsExporting;
    partial void OnIsExportingChanged(bool value) { OnPropertyChanged(nameof(CanExport)); OnPropertyChanged(nameof(CanExportAll)); }

    public string SuggestedExportName =>
        Path.GetFileNameWithoutExtension(PhotoName) + (HasLayers || FrameEnabled || FilmEnabled ? "_graded" : "") + (ExportFormatIndex == 1 ? ".png" : ".jpg");

    public string DefaultExportDir =>
        _loadedPath != null ? Path.GetDirectoryName(_loadedPath)! : Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);

    // ── Export all: the current Look and Border applied to every photo of the folder ───────
    public int PhotoCount => _settings.PhotosProvider?.Invoke().Count ?? 0;
    public bool CanExportAll => PhotoCount > 1 && !IsExporting;
    public string ExportAllText => $"Export all {PhotoCount} photos…";

    public async Task ExportAllAsync(string folder)
    {
        var files = _settings.PhotosProvider?.Invoke().ToList();
        if (files == null || files.Count == 0) return;
        var stack = BuildStack();
        var frame = CurrentFrame();
        var filmStyle = FilmEnabled ? CurrentFilmStyle() : null;
        var lookName = CurrentLookName();
        int quality = (int)Math.Round(ExportQuality);
        bool png = ExportFormatIndex == 1;
        IsExporting = true;
        int done = 0, failed = 0;
        try
        {
            Directory.CreateDirectory(folder);
            foreach (var f in files)
            {
                ExportStatus = $"Saving {done + failed + 1} of {files.Count}…";
                var dest = Path.Combine(folder, Path.GetFileNameWithoutExtension(f.FileName) + "_graded" + (png ? ".png" : ".jpg"));
                try
                {
                    await Task.Run(() =>
                    {
                        using var img = Image.Load<Rgb24>(f.FullPath);
                        img.Mutate(x => x.AutoOrient());
                        // Each photo gets its own metadata and frame number.
                        var film = filmStyle == null ? null
                            : new FilmLayer(filmStyle, PaletteImageRenderer.ReadMetadata(f.FullPath), files.IndexOf(f) + 1, lookName, FrameLabelFor(f.FullPath, files.IndexOf(f)));
                        using var final = RenderAll(img, stack, frame, film);
                        if (png) final.SaveAsPng(dest);
                        else final.SaveAsJpeg(dest, new JpegEncoder { Quality = quality });
                    });
                    done++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or UnknownImageFormatException
                                               or InvalidImageContentException)
                { failed++; }
            }
            ExportStatus = failed == 0 ? $"Saved {done} photos." : $"Saved {done}, {failed} failed.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ExportStatus = $"Couldn't save: {ex.Message}";
        }
        finally { IsExporting = false; }
    }

    // Re-reads the original file, grades it at full size and writes it; the source is never modified.
    public async Task ExportAsync(string outPath)
    {
        var src = _loadedPath;
        if (src == null) return;
        if (string.Equals(Path.GetFullPath(outPath), Path.GetFullPath(src), StringComparison.OrdinalIgnoreCase))
        {
            ExportStatus = "Pick a different file name: the original is never overwritten.";
            return;
        }
        var stack = BuildStack();
        var frame = CurrentFrame();
        var film = CurrentFilm(src, useSettingsMetadata: true);
        int quality = (int)Math.Round(ExportQuality);
        IsExporting = true;
        ExportStatus = "Saving…";
        try
        {
            await Task.Run(() =>
            {
                using var img = Image.Load<Rgb24>(src);
                img.Mutate(x => x.AutoOrient());
                using var final = RenderAll(img, stack, frame, film);
                if (Path.GetExtension(outPath).Equals(".png", StringComparison.OrdinalIgnoreCase))
                    final.SaveAsPng(outPath);
                else
                    final.SaveAsJpeg(outPath, new JpegEncoder { Quality = quality });
            });
            ExportStatus = $"Saved {Path.GetFileName(outPath)}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or UnknownImageFormatException
                                       or InvalidImageContentException)
        {
            ExportStatus = $"Couldn't save: {ex.Message}";
        }
        finally { IsExporting = false; }
    }
}
