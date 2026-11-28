using System.Text.Json;
using System.Text.Json.Serialization;

namespace PhSpectre.Rendering;

// Where data is printed; each zone is one short line.
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FilmZone { TopCenter, BottomLeft, BottomCenter, BottomRight, SideLeft, SideRight, HoleGaps, TopLeft, TopRight }

// What gets printed in a zone.
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FilmToken { FilmSim, Shutter, Aperture, Iso, Focal, ExposureBias, ColorProfile, FrameNumber, Date, Camera, Lens, Custom, FrameNumberA }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FilmPerforation { None, Sides, Rows }

// Real stock to imitate; all but Free use true geometry.
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FilmGauge { Free, Mm135, Roll120, Mm135Half }

// A film-edge frame. Sizes are percent of the photo's short side.
public sealed record FilmStyle
{
    public const float MaxMarginPercent = 35f;
    public const float MaxCornerPercent = 4f;

    // 135 in mm: 36 x 24 frame, 5.5 mm rebate each side, 2 mm between frames (ISO 491, SMPTE ST 139).
    public const float Mm135Across = 5.5f, Mm135HoleEdge = 2.36f, Mm135HoleAcross = 2.794f, Mm135HoleAlong = 1.981f,
        Mm135Pitch = 4.75f, Mm135Frame = 24f, Mm135Gap = 2f, HalfFrameShort = 18f;
    // 120: 56 mm picture, about 2.5 mm rebate, 5 mm between frames, no holes.
    public const float Roll120Across = 2.5f, Roll120Frame = 56f, Roll120Gap = 5f;

    /// <summary>Margin (percent of the picture's short side) across the strip and between frames for a gauge.</summary>
    public static (float Across, float Along) GaugeMargins(FilmGauge g) => g switch
    {
        FilmGauge.Mm135 => (Mm135Across / Mm135Frame * 100f, Mm135Gap / 2 / Mm135Frame * 100f),
        // Half-frame: 18 x 24 mm, the 24 mm side across the film.
        FilmGauge.Mm135Half => (Mm135Across / HalfFrameShort * 100f, Mm135Gap / 2 / 2 / HalfFrameShort * 100f),
        FilmGauge.Roll120 => (Roll120Across / Roll120Frame * 100f, Roll120Gap / 2 / Roll120Frame * 100f),
        _ => (0f, 0f)
    };

    /// <summary>True when the strip runs vertically (holes / rebate text on the left and right) in the style's own orientation.</summary>
    public bool VerticalStrip => Perforation == FilmPerforation.Sides || (Perforation == FilmPerforation.None && Gauge == FilmGauge.Roll120);

    /// <summary>135 stock (full or half frame): real KS perforations.</summary>
    public bool IsMm135 => Gauge is FilmGauge.Mm135 or FilmGauge.Mm135Half;

    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public bool IsBuiltIn { get; init; }

    public float SidePercent { get; init; } = 3f;
    public float TopPercent { get; init; } = 3f;
    public float BottomPercent { get; init; } = 8f;
    public string EdgeColorHex { get; init; } = "#0B0B0B";
    public string HoleColorHex { get; init; } = "#000000";
    public string TextColorHex { get; init; } = "#F2F2F2";
    // Soft light bloom behind the text (LED / backlit-film look).
    public bool Glow { get; init; }
    public FilmPerforation Perforation { get; init; } = FilmPerforation.None;
    public FilmGauge Gauge { get; init; }
    // Free text for FilmToken.Custom (your own caption; none of the presets use it).
    public string CustomText { get; init; } = "";
    // Film feel, 0..100: grain, uneven light, vignette, soft/irregular sprocket holes, slightly inked text.
    public float Realism { get; init; }
    // Dust and scratches, 0..100.
    public float Dust { get; init; }
    // Radius of the photo's corners, percent of the short side (0..4).
    public float CornerPercent { get; init; }
    // Tokens per zone, printed left to right (or bottom to top on the sides) separated by two spaces.
    public Dictionary<FilmZone, List<FilmToken>> Zones { get; init; } = new();

    private static FilmStyle Edge(string id, string name, string edge, string hole, string text,
        float side, float top, float bottom, FilmPerforation perf, bool glow, float realism, float dust, float corner,
        Dictionary<FilmZone, List<FilmToken>> zones, FilmGauge gauge = FilmGauge.Free) => new()
    {
        Id = id, Name = name, IsBuiltIn = true, EdgeColorHex = edge, HoleColorHex = hole, TextColorHex = text,
        SidePercent = side, TopPercent = top, BottomPercent = bottom, Perforation = perf, Glow = glow,
        Realism = realism, Dust = dust, CornerPercent = corner, Zones = zones, Gauge = gauge
    };

    // Presets only style the edge. FilmSim prints the applied Look or the photo's film simulation, never an
    // invented name.
    private const FilmToken Look = FilmToken.FilmSim, Sh = FilmToken.Shutter, Ap = FilmToken.Aperture, Iso = FilmToken.Iso,
        Ev = FilmToken.ExposureBias, Fn = FilmToken.FrameNumber;

    public static readonly IReadOnlyList<FilmStyle> BuiltIns =
    [
        // Camera data-back: backlit LED values along the bottom edge.
        Edge("film-led", "Camera LED", "#050505", "#000000", "#F4F4F4", 1.5f, 1.5f, 6f, FilmPerforation.None, true, 35, 15, 0,
            new() { [FilmZone.BottomLeft] = [Sh, Ap], [FilmZone.BottomCenter] = [Ev], [FilmZone.BottomRight] = [FilmToken.Focal, Iso] }),
        // Grey base, the look's name over the sprockets, exposure printed between them.
        Edge("film-grey", "Grey strip", "#8A8F96", "#FFFFFF", "#2A2C30", 4.17f, 22.92f, 22.92f, FilmPerforation.Rows, false, 70, 45, 0.8f,
            new() { [FilmZone.TopCenter] = [Look], [FilmZone.HoleGaps] = [Ap], [FilmZone.BottomCenter] = [Iso] }, FilmGauge.Mm135),
        Edge("film-dark", "Dark strip", "#0E0E0E", "#FFFFFF", "#E8E4D8", 22.92f, 4.17f, 4.17f, FilmPerforation.Sides, false, 80, 55, 1f,
            new() { [FilmZone.SideLeft] = [Look], [FilmZone.SideRight] = [Fn], [FilmZone.HoleGaps] = [Ap] }, FilmGauge.Mm135),
        // Cine edge: orange backlit marks, name and frame number up the sides, sprockets on both.
        Edge("film-cine", "Cine amber", "#16100C", "#000000", "#FFB020", 22.92f, 4.17f, 4.17f, FilmPerforation.Sides, true, 60, 35, 1.2f,
            new() { [FilmZone.SideLeft] = [Look, Iso], [FilmZone.SideRight] = [Fn], [FilmZone.TopCenter] = [Sh, Ap], [FilmZone.BottomCenter] = [Sh, Ap] }, FilmGauge.Mm135),
        // Slide strip: thick black edge, large white sprockets.
        Edge("film-slide", "Slide strip", "#0B0B0B", "#FFFFFF", "#F2A93B", 4.17f, 22.92f, 22.92f, FilmPerforation.Rows, false, 55, 35, 0,
            new() { [FilmZone.TopLeft] = [Look], [FilmZone.TopRight] = [Fn], [FilmZone.BottomLeft] = [FilmToken.FrameNumberA], [FilmZone.BottomRight] = [Fn] }, FilmGauge.Mm135),
        // Half-frame (18 x 24): the film runs along the short side, so the 24 mm side lies across the strip.
        Edge("film-half", "Half-frame", "#15120E", "#FFFFFF", "#E8E4D8", 30.56f, 2.78f, 2.78f, FilmPerforation.Sides, false, 70, 45, 1f,
            new() { [FilmZone.SideLeft] = [Look], [FilmZone.SideRight] = [Fn], [FilmZone.HoleGaps] = [Ap] }, FilmGauge.Mm135Half),
        Edge("film-half-amber", "Half-frame amber", "#4A2A14", "#000000", "#F2A93B", 30.56f, 2.78f, 2.78f, FilmPerforation.Sides, false, 65, 40, 1f,
            new() { [FilmZone.SideLeft] = [Look], [FilmZone.SideRight] = [Fn], [FilmZone.HoleGaps] = [Ap] }, FilmGauge.Mm135Half),
        Edge("film-half-slide", "Half-frame slide", "#0B0B0B", "#FFFFFF", "#F2A93B", 30.56f, 2.78f, 2.78f, FilmPerforation.Sides, false, 55, 35, 0,
            new() { [FilmZone.SideLeft] = [Look], [FilmZone.SideRight] = [Fn, FilmToken.FrameNumberA] }, FilmGauge.Mm135Half),
        // Roll 120: no sprockets, numbers and codes on the edge of the film.
        Edge("film-120", "Roll 120", "#0A0A0A", "#000000", "#EDEDED", 4.5f, 4.5f, 4.5f, FilmPerforation.None, false, 55, 40, 2.5f,
            new() { [FilmZone.SideLeft] = [Look], [FilmZone.SideRight] = [Fn], [FilmZone.BottomCenter] = [Sh, Ap, Iso] }, FilmGauge.Roll120),
        Edge("film-120-amber", "Roll 120 amber", "#4A2A14", "#000000", "#F2A93B", 4.5f, 4.5f, 4.5f, FilmPerforation.None, false, 65, 40, 2f,
            new() { [FilmZone.SideLeft] = [Look], [FilmZone.SideRight] = [Fn], [FilmZone.BottomCenter] = [Ap, Sh] }, FilmGauge.Roll120),
        Edge("film-120-bw", "Roll 120 B&W", "#B8BBBE", "#000000", "#202225", 4.5f, 4.5f, 4.5f, FilmPerforation.None, false, 60, 45, 2f,
            new() { [FilmZone.SideLeft] = [Look], [FilmZone.SideRight] = [Fn], [FilmZone.BottomCenter] = [Iso, Sh] }, FilmGauge.Roll120),
    ];

    // Clamped, so old or hand-edited styles still render.
    public FilmStyle Normalized() => this with
    {
        SidePercent = Clamp(SidePercent, MaxMarginPercent), TopPercent = Clamp(TopPercent, MaxMarginPercent),
        BottomPercent = Clamp(BottomPercent, MaxMarginPercent),
        Realism = Clamp(Realism, 100f), Dust = Clamp(Dust, 100f), CornerPercent = Clamp(CornerPercent, MaxCornerPercent),
        Zones = Zones ?? new(),
        CustomText = CustomText ?? ""
    };

    private static float Clamp(float v, float max) => Math.Clamp(float.IsFinite(v) ? v : 0f, 0f, max);
}

// Saved custom film styles, one JSON file.
public sealed class FilmStyleStore
{
    private readonly string _path;
    private readonly object _gate = new();
    private List<FilmStyle> _styles = [];

    public FilmStyleStore(string path)
    {
        _path = path;
        try
        {
            if (File.Exists(path))
                _styles = (JsonSerializer.Deserialize<List<FilmStyle>>(File.ReadAllText(path)) ?? [])
                    .Where(s => !string.IsNullOrWhiteSpace(s.Id)).Select(s => s.Normalized() with { IsBuiltIn = false }).ToList();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            _styles = [];
            PhSpectre.Services.SafeFiles.KeepUnreadable(path);
        }
    }

    public IReadOnlyList<FilmStyle> Styles { get { lock (_gate) return _styles.ToList(); } }

    public FilmStyle Add(string name, FilmStyle settings)
    {
        lock (_gate)
        {
            var s = settings.Normalized() with
            {
                Id = Guid.NewGuid().ToString("N"), Name = string.IsNullOrWhiteSpace(name) ? "My film" : name.Trim(), IsBuiltIn = false
            };
            _styles.Add(s);
            Save();
            return s;
        }
    }

    public bool Delete(string id)
    {
        lock (_gate)
        {
            if (_styles.RemoveAll(s => s.Id == id) == 0) return false;
            Save();
            return true;
        }
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_styles, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, _path, overwrite: true);
    }
}
