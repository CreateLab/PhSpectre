using System.Text.Json;
using System.Text.Json.Serialization;

namespace PhSpectre.Rendering;

// Where on the film edge a piece of data is printed. Each zone is a short single line, so the edge
// never turns into a block of text. HoleGaps prints its tokens one by one in the gaps between sprocket holes.
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FilmZone { TopCenter, BottomLeft, BottomCenter, BottomRight, SideLeft, SideRight, HoleGaps, TopLeft, TopRight }

// What gets printed in a zone.
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FilmToken { FilmSim, Shutter, Aperture, Iso, Focal, ExposureBias, ColorProfile, FrameNumber, Date, Camera, Lens, Custom, FrameNumberA }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FilmPerforation { None, Sides, Rows }

// A film-edge frame: the canvas grows around the photo, the edge gets optional sprocket holes, and shot data is
// printed per zone. All sizes are percent of the photo's short side, so a preview and an export look the same.
// The photo is composited as-is; only its four corners are trimmed when CornerPercent > 0.
public sealed record FilmStyle
{
    public const float MaxMarginPercent = 30f;
    public const float MaxCornerPercent = 4f;

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
    // Free text for FilmToken.Custom (your own caption; none of the presets use it).
    public string CustomText { get; init; } = "";
    // Film feel, 0..100: grain, uneven light, vignette, soft/irregular sprocket holes, slightly inked text.
    public float Realism { get; init; }
    // Dust specks and hair-line scratches on the edge, 0..100.
    public float Dust { get; init; }
    // Radius of the photo's corners, percent of the short side (0..4).
    public float CornerPercent { get; init; }
    // Tokens per zone, printed left to right (or bottom to top on the sides) separated by two spaces.
    public Dictionary<FilmZone, List<FilmToken>> Zones { get; init; } = new();

    private static FilmStyle Edge(string id, string name, string edge, string hole, string text,
        float side, float top, float bottom, FilmPerforation perf, bool glow, float realism, float dust, float corner,
        Dictionary<FilmZone, List<FilmToken>> zones) => new()
    {
        Id = id, Name = name, IsBuiltIn = true, EdgeColorHex = edge, HoleColorHex = hole, TextColorHex = text,
        SidePercent = side, TopPercent = top, BottomPercent = bottom, Perforation = perf, Glow = glow,
        Realism = realism, Dust = dust, CornerPercent = corner, Zones = zones
    };

    // Presets are looks of the edge only. The name printed on it is never made up: FilmSim prints the applied
    // Look (LUT / preset) or, without one, the film simulation read from the photo, and nothing when neither exists.
    private const FilmToken Look = FilmToken.FilmSim, Sh = FilmToken.Shutter, Ap = FilmToken.Aperture, Iso = FilmToken.Iso,
        Ev = FilmToken.ExposureBias, Fn = FilmToken.FrameNumber;

    public static readonly IReadOnlyList<FilmStyle> BuiltIns =
    [
        // Camera data-back: backlit LED values along the bottom edge.
        Edge("film-led", "Camera LED", "#050505", "#000000", "#F4F4F4", 1.5f, 1.5f, 6f, FilmPerforation.None, true, 35, 15, 0,
            new() { [FilmZone.BottomLeft] = [Sh, Ap], [FilmZone.BottomCenter] = [Ev], [FilmZone.BottomRight] = [FilmToken.Focal, Iso] }),
        // Grey base, the look's name over the sprockets, exposure printed between them.
        Edge("film-grey", "Grey strip", "#8A8F96", "#F3F1EE", "#2A2C30", 1.5f, 7f, 7f, FilmPerforation.Rows, false, 70, 45, 0.8f,
            new() { [FilmZone.TopCenter] = [Look], [FilmZone.HoleGaps] = [Sh, Ap, Ev], [FilmZone.BottomCenter] = [Iso] }),
        Edge("film-dark", "Dark strip", "#0E0E0E", "#D9D6CE", "#E8E4D8", 12f, 3f, 3f, FilmPerforation.Sides, false, 80, 55, 1f,
            new() { [FilmZone.SideLeft] = [Look], [FilmZone.SideRight] = [Fn], [FilmZone.HoleGaps] = [Sh, Ap] }),
        // Cine edge: orange backlit marks, name and frame number up the sides, sprockets on both.
        Edge("film-cine", "Cine amber", "#16100C", "#000000", "#FFB020", 12f, 4f, 4f, FilmPerforation.Sides, true, 60, 35, 1.2f,
            new() { [FilmZone.SideLeft] = [Look, Iso], [FilmZone.SideRight] = [Fn], [FilmZone.TopCenter] = [Sh, Ap], [FilmZone.BottomCenter] = [Sh, Ap] }),
        Edge("film-amber", "Amber negative", "#4A2A14", "#1A0E06", "#F2A93B", 13f, 3.5f, 3.5f, FilmPerforation.Sides, false, 65, 40, 1f,
            new() { [FilmZone.SideLeft] = [Look], [FilmZone.SideRight] = [Fn], [FilmZone.HoleGaps] = [Ap, Sh] }),
        Edge("film-gold", "Golden negative", "#4D3711", "#1C1405", "#FFD23F", 13f, 3.5f, 3.5f, FilmPerforation.Sides, false, 65, 45, 1f,
            new() { [FilmZone.SideLeft] = [Look], [FilmZone.SideRight] = [Fn], [FilmZone.BottomCenter] = [Sh, Ap] }),
        Edge("film-olive", "Olive negative", "#2E2A1B", "#100E08", "#E6D44A", 13f, 3.5f, 3.5f, FilmPerforation.Sides, false, 65, 40, 1f,
            new() { [FilmZone.SideLeft] = [Look], [FilmZone.SideRight] = [Fn], [FilmZone.HoleGaps] = [Iso, Sh] }),
        // Slide / reversal strip: thick black edge, big white sprockets, name top-left, frame numbers right and a short code.
        Edge("film-slide", "Slide strip", "#0B0B0B", "#F4F2EC", "#F2A93B", 1.5f, 13f, 13f, FilmPerforation.Rows, false, 55, 35, 0,
            new() { [FilmZone.TopLeft] = [Look], [FilmZone.TopRight] = [Fn], [FilmZone.BottomLeft] = [FilmToken.FrameNumberA], [FilmZone.BottomRight] = [Fn] }),
        // Roll film: no sprockets, numbers and codes on the backing paper edge.
        Edge("film-120", "Roll 120", "#0A0A0A", "#000000", "#EDEDED", 6f, 3f, 3f, FilmPerforation.None, false, 55, 40, 2.5f,
            new() { [FilmZone.SideLeft] = [Look], [FilmZone.SideRight] = [Fn], [FilmZone.BottomCenter] = [Sh, Ap, Iso] }),
        // Out-of-date roll: heavy grain, dust and a green cast.
        Edge("film-expired", "Expired roll", "#1D2A1E", "#050805", "#C8E86A", 13f, 3.5f, 3.5f, FilmPerforation.Sides, true, 95, 90, 1.5f,
            new() { [FilmZone.SideLeft] = [Look], [FilmZone.SideRight] = [Fn], [FilmZone.HoleGaps] = [Ap, Sh] }),
    ];

    // Out-of-range values are clamped rather than rejected, so a hand-edited or old saved style still renders.
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

// Saved custom film styles: one JSON file, kept apart from the built-ins (which live in code).
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
        catch (Exception ex) when (ex is JsonException or IOException) { _styles = []; }
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
