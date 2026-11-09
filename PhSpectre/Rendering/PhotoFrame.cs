using System.Text.Json;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace PhSpectre.Rendering;

// A border around a photo. The photo itself is never resampled or cropped: the canvas grows around it.
// Thickness is relative to the photo's short side, so the same frame looks the same at preview size
// and at export size.
public sealed record PhotoFrame
{
    public const float MaxThicknessPercent = 30f;
    public const float MaxBottomPercent = 40f;
    public const double MinAspect = 0.25, MaxAspect = 4.0;

    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public bool IsBuiltIn { get; init; }
    // Border width on all sides, percent of the short side (0..30).
    public float ThicknessPercent { get; init; } = 4f;
    // Extra space under the photo (instant-film look), percent of the short side (0..40).
    public float BottomExtraPercent { get; init; }
    public string ColorHex { get; init; } = "#FFFFFF";
    // Canvas width / height. null keeps the shape of the photo plus the border; otherwise the canvas is
    // widened or heightened (never shrunk) to this ratio, with the photo centred.
    public double? Aspect { get; init; }

    public static readonly (string Label, double? Ratio)[] AspectChoices =
    [
        ("Original", null), ("1:1", 1.0), ("4:5", 4.0 / 5), ("5:4", 5.0 / 4), ("3:2", 3.0 / 2), ("2:3", 2.0 / 3),
        ("16:9", 16.0 / 9), ("9:16", 9.0 / 16), ("5:7 print", 5.0 / 7), ("A-series", 1.0 / 1.4142)
    ];

    public static readonly IReadOnlyList<PhotoFrame> BuiltIns =
    [
        new() { Id = "thin-white", Name = "Thin white", IsBuiltIn = true, ThicknessPercent = 2f, ColorHex = "#FFFFFF" },
        new() { Id = "thin-black", Name = "Thin black", IsBuiltIn = true, ThicknessPercent = 2f, ColorHex = "#000000" },
        new() { Id = "gallery-mat", Name = "Gallery mat", IsBuiltIn = true, ThicknessPercent = 10f, ColorHex = "#F4F1EA" },
        new() { Id = "instant", Name = "Instant film", IsBuiltIn = true, ThicknessPercent = 5f, BottomExtraPercent = 16f, ColorHex = "#FAF8F2" },
        new() { Id = "ig-square", Name = "Square 1:1", IsBuiltIn = true, ThicknessPercent = 0f, ColorHex = "#FFFFFF", Aspect = 1.0 },
        new() { Id = "ig-portrait", Name = "Portrait 4:5", IsBuiltIn = true, ThicknessPercent = 0f, ColorHex = "#FFFFFF", Aspect = 4.0 / 5 },
        new() { Id = "story", Name = "Story 9:16", IsBuiltIn = true, ThicknessPercent = 0f, ColorHex = "#000000", Aspect = 9.0 / 16 },
        new() { Id = "print-10x15", Name = "Print 3:2", IsBuiltIn = true, ThicknessPercent = 3f, ColorHex = "#FFFFFF", Aspect = 3.0 / 2 },
    ];

    // Out-of-range values are clamped rather than rejected, so a hand-edited or old saved frame still renders.
    public PhotoFrame Normalized() => this with
    {
        ThicknessPercent = Math.Clamp(float.IsFinite(ThicknessPercent) ? ThicknessPercent : 0f, 0f, MaxThicknessPercent),
        BottomExtraPercent = Math.Clamp(float.IsFinite(BottomExtraPercent) ? BottomExtraPercent : 0f, 0f, MaxBottomPercent),
        Aspect = Aspect is { } a && double.IsFinite(a) ? Math.Clamp(a, MinAspect, MaxAspect) : null
    };

    public bool IsNoOp =>
        Normalized() is { ThicknessPercent: 0f, BottomExtraPercent: 0f, Aspect: null };

    // Output size for a photo of the given size.
    public (int Width, int Height, int PhotoX, int PhotoY) Layout(int photoW, int photoH)
    {
        var f = Normalized();
        int shortSide = Math.Min(photoW, photoH);
        int b = (int)Math.Round(shortSide * f.ThicknessPercent / 100f);
        int bottom = (int)Math.Round(shortSide * f.BottomExtraPercent / 100f);
        int w = photoW + 2 * b;
        int h = photoH + 2 * b + bottom;
        int ox = b, oy = b;

        if (f.Aspect is { } ratio)
        {
            if ((double)w / h < ratio) { int nw = (int)Math.Ceiling(h * ratio); ox += (nw - w) / 2; w = nw; }
            else { int nh = (int)Math.Ceiling(w / ratio); oy += (nh - h) / 2; h = nh; }
        }
        return (w, h, ox, oy);
    }

    public Image<Rgb24> Render(Image<Rgb24> photo)
    {
        ArgumentNullException.ThrowIfNull(photo);
        var (w, h, x, y) = Layout(photo.Width, photo.Height);
        var color = HexColorInput.TryParse(ColorHex, out var c) ? c : Color.White;
        var canvas = new Image<Rgb24>(w, h, color.ToPixel<Rgb24>());
        canvas.Mutate(m => m.DrawImage(photo, new Point(x, y), 1f));
        return canvas;
    }
}

// Saved custom frames: one JSON file, kept apart from the built-ins (which live in code).
public sealed class PhotoFrameStore
{
    private readonly string _path;
    private readonly object _gate = new();
    private List<PhotoFrame> _frames = [];

    public PhotoFrameStore(string path)
    {
        _path = path;
        try
        {
            if (File.Exists(path))
                _frames = (JsonSerializer.Deserialize<List<PhotoFrame>>(File.ReadAllText(path)) ?? [])
                    .Where(f => !string.IsNullOrWhiteSpace(f.Id)).Select(f => f.Normalized() with { IsBuiltIn = false }).ToList();
        }
        catch (Exception ex) when (ex is JsonException or IOException) { _frames = []; }
    }

    public IReadOnlyList<PhotoFrame> Frames { get { lock (_gate) return _frames.ToList(); } }

    public PhotoFrame Add(string name, PhotoFrame settings)
    {
        lock (_gate)
        {
            var f = settings.Normalized() with
            {
                Id = Guid.NewGuid().ToString("N"), Name = string.IsNullOrWhiteSpace(name) ? "My frame" : name.Trim(), IsBuiltIn = false
            };
            _frames.Add(f);
            Save();
            return f;
        }
    }

    public bool Delete(string id)
    {
        lock (_gate)
        {
            if (_frames.RemoveAll(f => f.Id == id) == 0) return false;
            Save();
            return true;
        }
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_frames, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, _path, overwrite: true);
    }
}
