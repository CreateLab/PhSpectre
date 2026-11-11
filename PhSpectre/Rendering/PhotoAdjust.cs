using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Advanced;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace PhSpectre.Rendering;

// Geometry and exposure applied to the photo before any Look or frame: rotate by 90°, straighten,
// crop to a ratio (with zoom), then exposure. The straightened picture is always cropped to the largest
// rectangle that fits inside it, so no empty corners ever appear. Pure data + Render: the same settings
// give the same framing on the preview copy and on the full-size export, because everything is relative.
public sealed record PhotoAdjust
{
    public const float MaxStraighten = 15f;
    public const float MaxExposure = 3f;
    public const float MaxZoom = 4f;

    // Quarter turns clockwise (0..3).
    public int Quarter { get; init; }
    // Degrees, positive turns the picture clockwise.
    public float Straighten { get; init; }
    // Crop ratio width / height of the result; null keeps the shape of the photo.
    public double? Aspect { get; init; }
    // 1 = the whole frame, 2 = the middle half of it, and so on.
    public float Zoom { get; init; } = 1f;
    // Exposure compensation in stops, applied in linear light.
    public float Exposure { get; init; }
    // Where the crop sits, as a shift of its centre in crop widths / heights (0 = centred). Always pulled
    // back inside the picture when rendering, so it can never reveal the empty corners.
    public float PanX { get; init; }
    public float PanY { get; init; }

    public static readonly (string Label, double? Ratio)[] CropChoices =
    [
        ("Original", null), ("1:1", 1.0), ("4:5", 4.0 / 5), ("5:4", 5.0 / 4), ("3:2", 3.0 / 2), ("2:3", 2.0 / 3),
        ("16:9", 16.0 / 9), ("9:16", 9.0 / 16)
    ];

    public PhotoAdjust Normalized() => this with
    {
        Quarter = ((Quarter % 4) + 4) % 4,
        Straighten = Math.Clamp(float.IsFinite(Straighten) ? Straighten : 0f, -MaxStraighten, MaxStraighten),
        Aspect = Aspect is { } a && double.IsFinite(a) && a > 0 ? Math.Clamp(a, PhotoFrame.MinAspect, PhotoFrame.MaxAspect) : null,
        Zoom = Math.Clamp(float.IsFinite(Zoom) ? Zoom : 1f, 1f, MaxZoom),
        Exposure = Math.Clamp(float.IsFinite(Exposure) ? Exposure : 0f, -MaxExposure, MaxExposure),
        PanX = Math.Clamp(float.IsFinite(PanX) ? PanX : 0f, -MaxZoom, MaxZoom),
        PanY = Math.Clamp(float.IsFinite(PanY) ? PanY : 0f, -MaxZoom, MaxZoom),
    };

    public bool HasGeometry => Normalized() is { Quarter: not 0 } or { Straighten: not 0f } or { Aspect: not null } or { Zoom: > 1f } or { PanX: not 0f } or { PanY: not 0f };
    public bool HasExposure => Normalized().Exposure != 0f;
    public bool IsNoOp => !HasGeometry && !HasExposure;

    // Size of the cropped result for a photo of the given size (before any resizing).
    public (int Width, int Height) OutputSize(int photoW, int photoH)
    {
        var a = Normalized();
        int w = a.Quarter % 2 == 1 ? photoH : photoW;
        int h = a.Quarter % 2 == 1 ? photoW : photoH;
        return CropSize(w, h, a);
    }

    // The pan after it has been pulled back so the crop stays inside the straightened picture.
    public (float X, float Y) ClampedPan(int photoW, int photoH)
    {
        var a = Normalized();
        int w = a.Quarter % 2 == 1 ? photoH : photoW;
        int h = a.Quarter % 2 == 1 ? photoW : photoH;
        var (cw, ch) = CropSize(w, h, a);
        var (dx, dy) = FitOffset(w, h, cw, ch, a.Straighten, a.PanX * cw, a.PanY * ch);
        return (cw == 0 ? 0f : (float)(dx / cw), ch == 0 ? 0f : (float)(dy / ch));
    }

    // Scales the wanted offset down until all four corners of the crop lie inside the original picture
    // (coordinates of the picture are the output's rotated back by the straighten angle).
    private static (double, double) FitOffset(int w, int h, int cw, int ch, float degrees, double dx, double dy)
    {
        if (dx == 0 && dy == 0) return (0, 0);
        double t = degrees * Math.PI / 180.0, cos = Math.Cos(t), sin = Math.Sin(t);
        bool Fits(double k)
        {
            double x = dx * k, y = dy * k;
            double ex = Math.Abs(x * cos + y * sin) + cw / 2.0 * Math.Abs(cos) + ch / 2.0 * Math.Abs(sin);
            double ey = Math.Abs(-x * sin + y * cos) + cw / 2.0 * Math.Abs(sin) + ch / 2.0 * Math.Abs(cos);
            return ex <= w / 2.0 - 1 && ey <= h / 2.0 - 1;
        }
        if (Fits(1)) return (dx, dy);
        double lo = 0, hi = 1;
        for (int i = 0; i < 24; i++) { double mid = (lo + hi) / 2; if (Fits(mid)) lo = mid; else hi = mid; }
        return (dx * lo, dy * lo);
    }

    // Largest centred rectangle of the wanted ratio inside the picture rotated by the straighten angle,
    // shrunk by the zoom. Derived from: w·cosθ + h·sinθ ≤ W and w·sinθ + h·cosθ ≤ H.
    private static (int, int) CropSize(int w, int h, PhotoAdjust a)
    {
        double r = a.Aspect ?? (double)w / h;
        double t = Math.Abs(a.Straighten) * Math.PI / 180.0;
        double cos = Math.Cos(t), sin = Math.Sin(t);
        double ch = Math.Min(h / (r * sin + cos), w / (r * cos + sin));
        ch /= a.Zoom;
        int outH = Math.Max(1, (int)Math.Floor(ch));
        int outW = Math.Max(1, (int)Math.Floor(ch * r));
        return (Math.Min(outW, w), Math.Min(outH, h));
    }

    // Returns a new image; the source is untouched.
    public Image<Rgb24> Render(Image<Rgb24> photo)
    {
        ArgumentNullException.ThrowIfNull(photo);
        var a = Normalized();
        var img = photo.Clone();
        try
        {
            if (a.HasGeometry)
            {
                if (a.Quarter != 0)
                    img.Mutate(x => x.Rotate(a.Quarter switch { 1 => RotateMode.Rotate90, 2 => RotateMode.Rotate180, _ => RotateMode.Rotate270 }));
                var (cw, ch) = CropSize(img.Width, img.Height, a);
                var (dx, dy) = FitOffset(img.Width, img.Height, cw, ch, a.Straighten, a.PanX * cw, a.PanY * ch);
                if (a.Straighten != 0f)
                {
                    // Rotating the whole frame only to throw most of it away is the slow part at full size:
                    // cut out just the area the crop needs (its rotated bounding box + a margin) and rotate that.
                    double t = a.Straighten * Math.PI / 180.0, cos = Math.Cos(t), sin = Math.Sin(t);
                    double sx = img.Width / 2.0 + dx * cos + dy * sin, sy = img.Height / 2.0 - dx * sin + dy * cos;
                    double bw = cw * Math.Abs(cos) + ch * Math.Abs(sin), bh = cw * Math.Abs(sin) + ch * Math.Abs(cos);
                    int hw = (int)Math.Ceiling(bw / 2) + 4, hh = (int)Math.Ceiling(bh / 2) + 4;
                    int px = (int)Math.Round(sx), py = (int)Math.Round(sy);
                    var region = new Rectangle(px - hw, py - hh, hw * 2, hh * 2);
                    if (region.X >= 0 && region.Y >= 0 && region.Right <= img.Width && region.Bottom <= img.Height
                        && (region.Width < img.Width || region.Height < img.Height))
                    {
                        // The region's centre is the pixel (px, py); the crop is centred on the exact point, so keep the sub-pixel part.
                        img.Mutate(x => x.Crop(region).Rotate(a.Straighten));
                        double ox = (sx - px) * cos - (sy - py) * sin, oy = (sx - px) * sin + (sy - py) * cos;
                        int rx = Math.Clamp((int)Math.Round((img.Width - cw) / 2.0 + ox), 0, Math.Max(0, img.Width - cw));
                        int ry = Math.Clamp((int)Math.Round((img.Height - ch) / 2.0 + oy), 0, Math.Max(0, img.Height - ch));
                        img.Mutate(x => x.Crop(new Rectangle(rx, ry, Math.Min(cw, img.Width - rx), Math.Min(ch, img.Height - ry))));
                        if (a.HasExposure) ApplyExposure(img, a.Exposure);
                        return img;
                    }
                    img.Mutate(x => x.Rotate(a.Straighten));
                }
                int cx = Math.Clamp((int)Math.Round((img.Width - cw) / 2.0 + dx), 0, Math.Max(0, img.Width - cw));
                int cy = Math.Clamp((int)Math.Round((img.Height - ch) / 2.0 + dy), 0, Math.Max(0, img.Height - ch));
                var rect = new Rectangle(cx, cy, Math.Min(cw, img.Width - cx), Math.Min(ch, img.Height - cy));
                if (rect.Width != img.Width || rect.Height != img.Height) img.Mutate(x => x.Crop(rect));
            }
            if (a.HasExposure) ApplyExposure(img, a.Exposure);
            return img;
        }
        catch { img.Dispose(); throw; }
    }

    // Stops are multiplications in linear light, so brightening does not wash out the picture the way a
    // plain add in sRGB would. One 256-entry table covers every pixel.
    private static void ApplyExposure(Image<Rgb24> img, float stops)
    {
        var table = new byte[256];
        float gain = MathF.Pow(2f, stops);
        for (int i = 0; i < 256; i++)
        {
            float lin = SrgbToLinear(i / 255f) * gain;
            table[i] = (byte)Math.Clamp((int)MathF.Round(LinearToSrgb(Math.Min(lin, 1f)) * 255f), 0, 255);
        }
        // Rows are independent; the accessor of ProcessPixelRows can't be captured by a parallel body, so go by row memory.
        Parallel.For(0, img.Height, y =>
        {
            var row = img.Frames.RootFrame.DangerousGetPixelRowMemory(y).Span;
            for (int x = 0; x < row.Length; x++)
                row[x] = new Rgb24(table[row[x].R], table[row[x].G], table[row[x].B]);
        });
    }

    private static float SrgbToLinear(float v) => v <= 0.04045f ? v / 12.92f : MathF.Pow((v + 0.055f) / 1.055f, 2.4f);
    private static float LinearToSrgb(float v) => v <= 0.0031308f ? v * 12.92f : 1.055f * MathF.Pow(v, 1f / 2.4f) - 0.055f;
}
