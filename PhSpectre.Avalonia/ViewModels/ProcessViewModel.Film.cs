using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhSpectre.Rendering;

namespace PhSpectre.Avalonia.ViewModels;

// Frame type Film: the shot data is printed on a film edge (see FilmStyle / FilmFrameRenderer). Like Border it is the
// last layer on the graded photo, and the two are mutually exclusive (the frame type picks one).
public partial class ProcessViewModel
{
    /// <summary>A film edge to draw: the style, the photo's metadata and the frame number printed on it.</summary>
    private sealed record FilmLayer(FilmStyle Style, PaletteImageRenderer.PhotoMetadata? Meta, int Number, string? LookName, string Label);

    /// <summary>What a zone can print. Index 0 leaves the zone empty.</summary>
    public static IReadOnlyList<(string Label, FilmToken[] Tokens)> FilmContentChoices { get; } =
    [
        ("Nothing", []),
        ("Look / film simulation", [FilmToken.FilmSim]),
        ("Look + ISO", [FilmToken.FilmSim, FilmToken.Iso]),
        ("Shutter + aperture", [FilmToken.Shutter, FilmToken.Aperture]),
        ("Shutter, aperture, comp.", [FilmToken.Shutter, FilmToken.Aperture, FilmToken.ExposureBias]),
        ("ISO, shutter, aperture", [FilmToken.Iso, FilmToken.Shutter, FilmToken.Aperture]),
        ("Aperture + shutter", [FilmToken.Aperture, FilmToken.Shutter]),
        ("Focal + ISO", [FilmToken.Focal, FilmToken.Iso]),
        ("ISO + colour profile", [FilmToken.Iso, FilmToken.ColorProfile]),
        ("ISO", [FilmToken.Iso]),
        ("Shutter", [FilmToken.Shutter]),
        ("Aperture", [FilmToken.Aperture]),
        ("Focal length", [FilmToken.Focal]),
        ("Exposure comp.", [FilmToken.ExposureBias]),
        ("Colour profile", [FilmToken.ColorProfile]),
        ("Frame number", [FilmToken.FrameNumber]),
        ("Frame number + A", [FilmToken.FrameNumberA]),
        ("Date", [FilmToken.Date]),
        ("Camera", [FilmToken.Camera]),
        ("Lens", [FilmToken.Lens]),
        ("Custom text", [FilmToken.Custom]),
    ];

    public IReadOnlyList<string> FilmContentLabels { get; } = FilmContentChoices.Select(c => c.Label).ToList();
    public IReadOnlyList<string> FilmPerforationLabels { get; } = ["None", "Both sides", "Top & bottom"];

    private FilmStyleStore? _filmStore;
    private FilmStyleStore FilmStore => _filmStore ??= new FilmStyleStore(Path.Combine(LibraryRoot, "film-styles.json"));
    private bool _applyingFilm;
    private string _filmHole = "#000000";   // hole colour of the last chosen style (not edited in the UI)
    private FilmGauge _filmGauge;           // stock of the last chosen style (real hole geometry / strip direction)

    public ObservableCollection<FilmStyle> FilmStyles { get; } = [];

    [ObservableProperty] private bool _filmEnabled;
    [ObservableProperty] private FilmStyle? _selectedFilm;
    [ObservableProperty] private double _filmSide = 3;
    [ObservableProperty] private double _filmTop = 3;
    [ObservableProperty] private double _filmBottom = 8;
    [ObservableProperty] private string _filmEdgeHex = "#0B0B0B";
    [ObservableProperty] private string _filmTextHex = "#F2F2F2";
    [ObservableProperty] private bool _filmGlow;
    [ObservableProperty] private int _filmPerforationIndex;
    [ObservableProperty] private string _filmCustomText = "";
    [ObservableProperty] private string _filmName = "";
    [ObservableProperty] private int _filmTopCenterIndex;
    [ObservableProperty] private int _filmTopLeftIndex;
    [ObservableProperty] private int _filmTopRightIndex;
    [ObservableProperty] private int _filmBottomLeftIndex;
    [ObservableProperty] private int _filmBottomCenterIndex;
    [ObservableProperty] private int _filmBottomRightIndex;
    [ObservableProperty] private int _filmSideLeftIndex;
    [ObservableProperty] private int _filmSideRightIndex;
    [ObservableProperty] private int _filmHoleGapsIndex;
    [ObservableProperty] private double _filmRealism = 60;
    [ObservableProperty] private double _filmDust = 30;
    [ObservableProperty] private double _filmCorner = 1;

    private static readonly HashSet<string> FilmControlNames =
    [
        nameof(FilmEnabled), nameof(FilmSide), nameof(FilmTop), nameof(FilmBottom), nameof(FilmEdgeHex), nameof(FilmTextHex),
        nameof(FilmGlow), nameof(FilmPerforationIndex), nameof(FilmCustomText), nameof(FilmTopCenterIndex),
        nameof(FilmBottomLeftIndex), nameof(FilmBottomCenterIndex), nameof(FilmBottomRightIndex),
        nameof(FilmSideLeftIndex), nameof(FilmSideRightIndex), nameof(FilmHoleGapsIndex), nameof(FilmTopLeftIndex), nameof(FilmTopRightIndex),
        nameof(FilmRealism), nameof(FilmDust), nameof(FilmCorner)
    ];

    public bool FilmEdgeInvalid => !HexColorInput.TryParse(FilmEdgeHex, out _);
    public bool FilmTextInvalid => !HexColorInput.TryParse(FilmTextHex, out _);
    public string FilmSideText => $"{FilmSide:0.#}%";
    public string FilmTopText => $"{FilmTop:0.#}%";
    public string FilmBottomText => $"{FilmBottom:0.#}%";
    public string FilmRealismText => $"{FilmRealism:0}";
    public string FilmDustText => $"{FilmDust:0}";
    public string FilmCornerText => $"{FilmCorner:0.#}%";
    public bool CanDeleteFilm => SelectedFilm is { IsBuiltIn: false };

    /// <summary>Frame type Film on/off.</summary>
    public void SetFilm(bool on)
    {
        FilmEnabled = on;
        if (on && SelectedFilm == null && FilmStyles.Count > 0) SelectedFilm = FilmStyles[0];
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is { } n && FilmControlNames.Contains(n)) FilmControlChanged();
    }

    private void LoadFilmStyles()
    {
        var keep = SelectedFilm?.Id;
        FilmStyles.Clear();
        foreach (var s in FilmStyle.BuiltIns) FilmStyles.Add(s);
        foreach (var s in FilmStore.Styles.OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase)) FilmStyles.Add(s);
        _applyingFilm = true;
        try { SelectedFilm = FilmStyles.FirstOrDefault(s => s.Id == keep); }
        finally { _applyingFilm = false; }
        OnPropertyChanged(nameof(CanDeleteFilm));
    }

    private static int ContentIndex(FilmStyle s, FilmZone zone)
    {
        var tokens = s.Zones.TryGetValue(zone, out var t) ? t : [];
        for (int i = 0; i < FilmContentChoices.Count; i++)
            if (FilmContentChoices[i].Tokens.SequenceEqual(tokens)) return i;
        return 0;
    }

    private int ZoneIndex(FilmZone zone) => zone switch
    {
        FilmZone.TopCenter => FilmTopCenterIndex,
        FilmZone.BottomLeft => FilmBottomLeftIndex,
        FilmZone.BottomCenter => FilmBottomCenterIndex,
        FilmZone.BottomRight => FilmBottomRightIndex,
        FilmZone.SideLeft => FilmSideLeftIndex,
        FilmZone.HoleGaps => FilmHoleGapsIndex,
        FilmZone.TopLeft => FilmTopLeftIndex,
        FilmZone.TopRight => FilmTopRightIndex,
        _ => FilmSideRightIndex
    };

    // The style as currently set in the controls; Id/Name stay empty so identical settings compare equal.
    private FilmStyle CurrentFilmStyle()
    {
        var zones = new Dictionary<FilmZone, List<FilmToken>>();
        foreach (var z in Enum.GetValues<FilmZone>())
        {
            int i = Math.Clamp(ZoneIndex(z), 0, FilmContentChoices.Count - 1);
            if (FilmContentChoices[i].Tokens.Length > 0) zones[z] = [.. FilmContentChoices[i].Tokens];
        }
        return new FilmStyle
        {
            SidePercent = (float)FilmSide, TopPercent = (float)FilmTop, BottomPercent = (float)FilmBottom,
            EdgeColorHex = ValidHex(FilmEdgeHex, "#0B0B0B"), TextColorHex = ValidHex(FilmTextHex, "#F2F2F2"),
            HoleColorHex = _filmHole, Gauge = _filmGauge,
            Realism = (float)FilmRealism, Dust = (float)FilmDust, CornerPercent = (float)FilmCorner,
            Glow = FilmGlow, Perforation = (FilmPerforation)Math.Clamp(FilmPerforationIndex, 0, 2),
            CustomText = FilmCustomText ?? "", Zones = zones
        }.Normalized();
    }

    private static string ValidHex(string hex, string fallback) =>
        HexColorInput.TryParse(hex, out _) ? (hex.Trim().StartsWith('#') ? hex.Trim() : "#" + hex.Trim()) : fallback;

    private FilmLayer? CurrentFilm(string? path, bool useSettingsMetadata)
    {
        if (!FilmEnabled) return null;
        var meta = useSettingsMetadata ? _settings.BuildMetadataOverride()
            : path == null ? null : PaletteImageRenderer.ReadMetadata(path);
        return new FilmLayer(CurrentFilmStyle(), meta, FrameNumberOf(path), CurrentLookName(), FrameLabelFor(path, 0));
    }

    // What the FilmSim token prints: the name of the Look that is actually applied (LUT and/or preset); null when
    // none is, in which case the photo's own film simulation is used.
    private string? CurrentLookName()
    {
        var names = new List<string>();
        if (_presetEntry != null && PresetEnabled) names.Add(_presetEntry.Name);
        if (_lutEntry != null && LutEnabled) names.Add(_lutEntry.Name);
        return names.Count == 0 ? null : string.Join(" + ", names);
    }

    // ── Frame number: typed by the user, else read from the file name / EXIF (see FilmFrameNumber) ──
    [ObservableProperty] private string _filmFrameText = "";

    /// <summary>The number read from the current photo, or "" when it has none.</summary>
    public string FilmFrameAuto => FilmFrameNumber.Resolve(_loadedPath);
    public string FilmFrameWatermark => FilmFrameAuto.Length > 0 ? "Auto: " + FilmFrameAuto : "Auto";
    public bool FilmFrameIsManual => !string.IsNullOrWhiteSpace(FilmFrameText);

    partial void OnFilmFrameTextChanged(string value) { OnPropertyChanged(nameof(FilmFrameIsManual)); ScheduleRender(); }

    [RelayCommand] private void FilmFrameAutoReset() => FilmFrameText = "";

    [RelayCommand]
    private void FilmFrameStep(string delta)
    {
        int d = int.TryParse(delta, out var dd) ? dd : 0;
        int cur = int.TryParse(FilmFrameText?.Trim(), out var typed) ? typed
            : int.TryParse(FilmFrameAuto, out var auto) ? auto : 0;
        FilmFrameText = Math.Max(0, cur + d).ToString();
    }

    // What the frame-number tokens print for a photo: the typed value (a number counts up with `index` when many photos
    // are exported in one go, text is repeated), else the number from the photo itself, else nothing.
    private string FrameLabelFor(string? path, int index)
    {
        var manual = FilmFrameText?.Trim() ?? "";
        if (manual.Length > 0) return int.TryParse(manual, out var n) ? (n + index).ToString() : manual;
        return FilmFrameNumber.Resolve(path);
    }

    // 1-based position of the photo in the current folder/tray, so each exported frame gets its own number.
    private int FrameNumberOf(string? path)
    {
        if (path == null) return 1;
        var files = _settings.PhotosProvider?.Invoke();
        if (files == null) return 1;
        int i = 0;
        foreach (var f in files)
        {
            i++;
            if (string.Equals(f.FullPath, path, StringComparison.OrdinalIgnoreCase)) return i;
        }
        return 1;
    }

    private void FilmControlChanged()
    {
        OnPropertyChanged(nameof(FilmEdgeInvalid)); OnPropertyChanged(nameof(FilmTextInvalid));
        OnPropertyChanged(nameof(FilmSideText)); OnPropertyChanged(nameof(FilmTopText)); OnPropertyChanged(nameof(FilmBottomText));
        OnPropertyChanged(nameof(FilmRealismText)); OnPropertyChanged(nameof(FilmDustText)); OnPropertyChanged(nameof(FilmCornerText));
        if (!_applyingFilm) DetachSelectedFilm();
        ScheduleRender();
    }

    // Touching a control means the settings no longer match the chosen style.
    private void DetachSelectedFilm()
    {
        if (SelectedFilm == null) return;
        _applyingFilm = true;
        try { SelectedFilm = null; } finally { _applyingFilm = false; }
        OnPropertyChanged(nameof(CanDeleteFilm));
    }

    partial void OnSelectedFilmChanged(FilmStyle? value)
    {
        OnPropertyChanged(nameof(CanDeleteFilm));
        if (value == null || _applyingFilm) return;
        _filmHole = value.HoleColorHex; _filmGauge = value.Gauge;
        _applyingFilm = true;
        try
        {
            FilmSide = value.SidePercent; FilmTop = value.TopPercent; FilmBottom = value.BottomPercent;
            FilmEdgeHex = value.EdgeColorHex; FilmTextHex = value.TextColorHex; FilmGlow = value.Glow;
            FilmPerforationIndex = (int)value.Perforation; FilmCustomText = value.CustomText;
            FilmTopCenterIndex = ContentIndex(value, FilmZone.TopCenter);
            FilmBottomLeftIndex = ContentIndex(value, FilmZone.BottomLeft);
            FilmBottomCenterIndex = ContentIndex(value, FilmZone.BottomCenter);
            FilmBottomRightIndex = ContentIndex(value, FilmZone.BottomRight);
            FilmSideLeftIndex = ContentIndex(value, FilmZone.SideLeft);
            FilmSideRightIndex = ContentIndex(value, FilmZone.SideRight);
            FilmHoleGapsIndex = ContentIndex(value, FilmZone.HoleGaps);
            FilmTopLeftIndex = ContentIndex(value, FilmZone.TopLeft);
            FilmTopRightIndex = ContentIndex(value, FilmZone.TopRight);
            FilmRealism = value.Realism; FilmDust = value.Dust; FilmCorner = value.CornerPercent;
            FilmName = value.IsBuiltIn ? "" : value.Name;
        }
        finally { _applyingFilm = false; }
        ScheduleRender();
    }

    [RelayCommand] private void SetFilmEdgeColor(string hex) => FilmEdgeHex = hex;
    [RelayCommand] private void SetFilmTextColor(string hex) => FilmTextHex = hex;

    [RelayCommand]
    private void SaveFilm()
    {
        var saved = FilmStore.Add(FilmName, CurrentFilmStyle());
        LoadFilmStyles();
        _applyingFilm = true;
        try { SelectedFilm = FilmStyles.FirstOrDefault(s => s.Id == saved.Id); }
        finally { _applyingFilm = false; }
        OnPropertyChanged(nameof(CanDeleteFilm));
    }

    [RelayCommand]
    private void DeleteFilm()
    {
        if (SelectedFilm is not { IsBuiltIn: false } s) return;
        FilmStore.Delete(s.Id);
        _applyingFilm = true;
        try { SelectedFilm = null; } finally { _applyingFilm = false; }
        LoadFilmStyles();
    }
}
