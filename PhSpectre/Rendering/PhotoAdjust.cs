using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Advanced;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace PhSpectre.Rendering;

// Geometry, exposure, tone and effects applied before the Look and frame. Straightening crops to the largest inner
// rectangle. Everything is relative, so preview and export match.
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
    // Crop position as a shift of its centre in crop sizes; always kept inside the picture.
    public float PanX { get; init; }
    public float PanY { get; init; }

    // 0 = untouched. -100..100; Sharpness, Grain, Glow and Halation 0..100.
    public float Contrast { get; init; }
    public float Highlights { get; init; }
    public float Shadows { get; init; }
    public float Whites { get; init; }
    public float Blacks { get; init; }
    public float Saturation { get; init; }
    public float Vibrance { get; init; }
    public float Temperature { get; init; }
    public float Tint { get; init; }
    public float Sharpness { get; init; }
    public float Vignette { get; init; }
    public float Grain { get; init; }
    public float Glow { get; init; }
    public float Halation { get; init; }
    // Noise reduction 0..100, on the photo before geometry.
    public float NoiseLuma { get; init; }
    public float NoiseColor { get; init; }
    // Skin smoothing 0..100, also before geometry.
    public float SkinSmoothing { get; init; }

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
        Contrast = Slider(Contrast), Highlights = Slider(Highlights), Shadows = Slider(Shadows), Whites = Slider(Whites), Blacks = Slider(Blacks),
        Saturation = Slider(Saturation), Vibrance = Slider(Vibrance), Temperature = Slider(Temperature), Tint = Slider(Tint),
        Vignette = Slider(Vignette), Sharpness = Slider(Sharpness, 0f), Grain = Slider(Grain, 0f),
        Glow = Slider(Glow, 0f), Halation = Slider(Halation, 0f),
        NoiseLuma = Slider(NoiseLuma, 0f), NoiseColor = Slider(NoiseColor, 0f), SkinSmoothing = Slider(SkinSmoothing, 0f),
    };

    private static float Slider(float v, float min = -100f) => Math.Clamp(float.IsFinite(v) ? v : 0f, min, 100f);

    public bool HasGeometry => Normalized() is { Quarter: not 0 } or { Straighten: not 0f } or { Aspect: not null } or { Zoom: > 1f } or { PanX: not 0f } or { PanY: not 0f };
    public bool HasExposure => Normalized().Exposure != 0f;
    // Anything the tone pass does: exposure, light, colour, sharpness, glow, halation, vignette, grain.
    public bool HasTone => Normalized() is { Exposure: not 0f } or { Contrast: not 0f } or { Highlights: not 0f } or { Shadows: not 0f }
        or { Whites: not 0f } or { Blacks: not 0f } or { Saturation: not 0f } or { Vibrance: not 0f } or { Temperature: not 0f }
        or { Tint: not 0f } or { Sharpness: not 0f } or { Vignette: not 0f } or { Grain: not 0f }
        or { Glow: not 0f } or { Halation: not 0f };
    public bool HasLight => Normalized() is { Exposure: not 0f } or { Contrast: not 0f } or { Highlights: not 0f } or { Shadows: not 0f } or { Whites: not 0f } or { Blacks: not 0f };
    public bool HasColor => Normalized() is { Saturation: not 0f } or { Vibrance: not 0f } or { Temperature: not 0f } or { Tint: not 0f };
    public bool HasEffects => Normalized() is { Sharpness: not 0f } or { Vignette: not 0f } or { Grain: not 0f } or { Glow: not 0f } or { Halation: not 0f } or { NoiseLuma: not 0f } or { NoiseColor: not 0f } or { SkinSmoothing: not 0f };
    public bool HasNoiseReduction => Normalized() is { NoiseLuma: > 0f } or { NoiseColor: > 0f };
    public bool HasSkinSmoothing => Normalized() is { SkinSmoothing: > 0f };
    // Cleaning is what happens to the pixels of the photo itself: noise reduction and skin smoothing.
    public bool HasCleaning => HasNoiseReduction || HasSkinSmoothing;
    public bool IsNoOp => !HasGeometry && !HasTone && !HasCleaning;

    // Cleaning is slow and independent of everything else, so a preview can run it once and keep the result.
    public PhotoAdjust CleaningOnly() => new() { NoiseLuma = NoiseLuma, NoiseColor = NoiseColor, SkinSmoothing = SkinSmoothing };
    public PhotoAdjust WithoutCleaning() => this with { NoiseLuma = 0, NoiseColor = 0, SkinSmoothing = 0 };

    // The geometry half and the tone half, so a preview can cache the slow geometry and redo only the tone.
    public PhotoAdjust WithoutTone() => this with
    {
        Exposure = 0, Contrast = 0, Highlights = 0, Shadows = 0, Whites = 0, Blacks = 0, Saturation = 0, Vibrance = 0,
        Temperature = 0, Tint = 0, Sharpness = 0, Vignette = 0, Grain = 0, Glow = 0, Halation = 0
    };
    public PhotoAdjust ToneOnly() => new()
    {
        Exposure = Exposure, Contrast = Contrast, Highlights = Highlights, Shadows = Shadows, Whites = Whites, Blacks = Blacks,
        Saturation = Saturation, Vibrance = Vibrance, Temperature = Temperature, Tint = Tint, Sharpness = Sharpness, Vignette = Vignette, Grain = Grain, Glow = Glow, Halation = Halation
    };

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

    // Shrinks the offset until the crop's corners lie inside the picture.
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

    // Largest centred rectangle of the ratio inside the rotated picture, shrunk by zoom.
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

    // Returns a new image; the source is untouched. A preview passes the skin mask it has already found.
    public Image<Rgb24> Render(Image<Rgb24> photo, SkinMask? skin = null)
    {
        ArgumentNullException.ThrowIfNull(photo);
        var a = Normalized();
        var img = photo.Clone();
        try
        {
            a.Clean(ref img, null, skin);
            if (a.HasGeometry)
            {
                if (a.Quarter != 0)
                    img.Mutate(x => x.Rotate(a.Quarter switch { 1 => RotateMode.Rotate90, 2 => RotateMode.Rotate180, _ => RotateMode.Rotate270 }));
                var (cw, ch) = CropSize(img.Width, img.Height, a);
                var (dx, dy) = FitOffset(img.Width, img.Height, cw, ch, a.Straighten, a.PanX * cw, a.PanY * ch);
                if (a.Straighten != 0f)
                {
                    // Rotate only the area the crop needs: rotating the whole frame is slow.
                    double t = a.Straighten * Math.PI / 180.0, cos = Math.Cos(t), sin = Math.Sin(t);
                    double sx = img.Width / 2.0 + dx * cos + dy * sin, sy = img.Height / 2.0 - dx * sin + dy * cos;
                    double bw = cw * Math.Abs(cos) + ch * Math.Abs(sin), bh = cw * Math.Abs(sin) + ch * Math.Abs(cos);
                    int hw = (int)Math.Ceiling(bw / 2) + 4, hh = (int)Math.Ceiling(bh / 2) + 4;
                    int px = (int)Math.Round(sx), py = (int)Math.Round(sy);
                    var region = new Rectangle(px - hw, py - hh, hw * 2, hh * 2);
                    if (region.X >= 0 && region.Y >= 0 && region.Right <= img.Width && region.Bottom <= img.Height
                        && (region.Width < img.Width || region.Height < img.Height))
                    {
                        // Keep the sub-pixel part of the centre.
                        img.Mutate(x => x.Crop(region).Rotate(a.Straighten));
                        double ox = (sx - px) * cos - (sy - py) * sin, oy = (sx - px) * sin + (sy - py) * cos;
                        int rx = Math.Clamp((int)Math.Round((img.Width - cw) / 2.0 + ox), 0, Math.Max(0, img.Width - cw));
                        int ry = Math.Clamp((int)Math.Round((img.Height - ch) / 2.0 + oy), 0, Math.Max(0, img.Height - ch));
                        img.Mutate(x => x.Crop(new Rectangle(rx, ry, Math.Min(cw, img.Width - rx), Math.Min(ch, img.Height - ry))));
                        ApplyTone(img, a);
                        return img;
                    }
                    img.Mutate(x => x.Rotate(a.Straighten));
                }
                int cx = Math.Clamp((int)Math.Round((img.Width - cw) / 2.0 + dx), 0, Math.Max(0, img.Width - cw));
                int cy = Math.Clamp((int)Math.Round((img.Height - ch) / 2.0 + dy), 0, Math.Max(0, img.Height - ch));
                var rect = new Rectangle(cx, cy, Math.Min(cw, img.Width - cx), Math.Min(ch, img.Height - cy));
                if (rect.Width != img.Width || rect.Height != img.Height) img.Mutate(x => x.Crop(rect));
            }
            ApplyTone(img, a);
            return img;
        }
        catch { img.Dispose(); throw; }
    }

    // A piece shown at 100%, processed like the export. Whole-frame effects and geometry are left out; `profile`,
    // `photoLongSide` and `skin` describe the whole photo.
    public Image<Rgb24> RenderDetail(Image<Rgb24> piece, int photoLongSide, NoiseReduction.NoiseProfile profile, SkinMask? skin = null)
    {
        ArgumentNullException.ThrowIfNull(piece);
        var a = Normalized();
        var img = piece.Clone();
        try
        {
            a.Clean(ref img, profile, skin);
            ApplyTone(img, a with { Glow = 0, Halation = 0, Vignette = 0, Grain = 0 }, photoLongSide);
            return img;
        }
        catch { img.Dispose(); throw; }
    }

    private void Clean(ref Image<Rgb24> img, NoiseReduction.NoiseProfile? profile, SkinMask? skin)
    {
        if (HasNoiseReduction)
        {
            var cleaned = NoiseReduction.Apply(img, NoiseLuma, NoiseColor, profile: profile);
            img.Dispose();
            img = cleaned;
        }
        if (HasSkinSmoothing) SkinRetouch.Apply(img, SkinSmoothing / 100f, skin);
    }

    // Order: exposure and white balance (linear), light, colour, sharpen, glow and halation, vignette and grain.
    private static void ApplyTone(Image<Rgb24> img, PhotoAdjust a, int photoLongSide = 0)
    {
        bool light = a.Contrast != 0f || a.Highlights != 0f || a.Shadows != 0f || a.Whites != 0f || a.Blacks != 0f;
        bool colour = a.Saturation != 0f || a.Vibrance != 0f;
        bool wbOn = a.Temperature != 0f || a.Tint != 0f;
        if (!light && !colour && !wbOn) { if (a.Exposure != 0f) ApplyExposure(img, a.Exposure); }
        else
        {
            var gain = ToneMath.WhiteBalanceGain(a.Temperature, a.Tint) * MathF.Pow(2f, a.Exposure);
            bool linear = gain != System.Numerics.Vector3.One;
            float con = a.Contrast / 100f, hi = a.Highlights / 100f, sh = a.Shadows / 100f, wh = a.Whites / 100f, bl = a.Blacks / 100f;
            float sat = a.Saturation / 100f, vib = a.Vibrance / 100f;
            var dec = ToneMath.Decode;
            Parallel.For(0, img.Height, y =>
            {
                var row = img.Frames.RootFrame.DangerousGetPixelRowMemory(y).Span;
                for (int x = 0; x < row.Length; x++)
                {
                    ref var px = ref row[x];
                    System.Numerics.Vector3 c;
                    if (linear)
                    {
                        var l = new System.Numerics.Vector3(dec[px.R], dec[px.G], dec[px.B]) * gain;
                        c = new System.Numerics.Vector3(ToneMath.Encode(l.X), ToneMath.Encode(l.Y), ToneMath.Encode(l.Z));
                    }
                    else c = new System.Numerics.Vector3(px.R, px.G, px.B) / 255f;
                    if (light) c = ToneMath.Tone(c, con, hi, sh, wh, bl);
                    if (colour) c = ToneMath.VibranceSaturation(c, sat, vib);
                    px = new Rgb24(ToneMath.ToByte(c.X), ToneMath.ToByte(c.Y), ToneMath.ToByte(c.Z));
                }
            });
        }
        if (a.Sharpness > 0f) Sharpen(img, a.Sharpness / 100f, photoLongSide > 0 ? photoLongSide : Math.Max(img.Width, img.Height));
        LightEffects.Apply(img, a.Glow / 100f, a.Halation / 100f);
        if (a.Vignette != 0f || a.Grain > 0f)
            ApplyVignetteGrain(img, a.Vignette / 100f, a.Grain / 100f,
                FilmGrain.CellSize(photoLongSide > 0 ? photoLongSide : Math.Max(img.Width, img.Height)));
    }

    // Unsharp mask; the blur radius follows the picture size so the same slider looks the same on preview and full size.
    internal static void Sharpen(Image<Rgb24> img, float amount, int photoLongSide)
    {
        float sigma = Math.Max(0.6f, photoLongSide / 2200f);
        using var blur = img.Clone(x => x.GaussianBlur(sigma));
        float k = amount * 1.5f;
        Parallel.For(0, img.Height, y =>
        {
            var row = img.Frames.RootFrame.DangerousGetPixelRowMemory(y).Span;
            var bl = blur.Frames.RootFrame.DangerousGetPixelRowMemory(y).Span;
            for (int x = 0; x < row.Length; x++)
            {
                var p = row[x]; var b = bl[x];
                row[x] = new Rgb24(ToneMath.ToByte((p.R + (p.R - b.R) * k) / 255f), ToneMath.ToByte((p.G + (p.G - b.G) * k) / 255f), ToneMath.ToByte((p.B + (p.B - b.B) * k) / 255f));
            }
        });
    }

    private static void ApplyVignetteGrain(Image<Rgb24> img, float vig, float grain, float grainCell)
    {
        int w = img.Width, h = img.Height;
        float cx = (w - 1) / 2f, cy = (h - 1) / 2f, inv = 1f / MathF.Sqrt(cx * cx + cy * cy + 1e-6f);
        Parallel.For(0, h, y =>
        {
            var row = img.Frames.RootFrame.DangerousGetPixelRowMemory(y).Span;
            for (int x = 0; x < row.Length; x++)
            {
                var c = new System.Numerics.Vector3(row[x].R, row[x].G, row[x].B) / 255f;
                if (vig != 0f)
                {
                    float dx = x - cx, dy = y - cy;
                    float t = ToneMath.Smooth(0.5f, 1f, MathF.Sqrt(dx * dx + dy * dy) * inv) * 0.8f;
                    c = vig < 0 ? c * (1f + vig * t) : c + (System.Numerics.Vector3.One - c) * (vig * t);
                }
                if (grain > 0f)
                {
                    float luma = ToneMath.Luma(c);
                    c += new System.Numerics.Vector3(FilmGrain.Sample(x, y, grainCell) * grain * 0.6f * luma * (1f - luma));
                }
                row[x] = new Rgb24(ToneMath.ToByte(c.X), ToneMath.ToByte(c.Y), ToneMath.ToByte(c.Z));
            }
        });
    }

    // Exposure is applied in linear light through one table.
    private static void ApplyExposure(Image<Rgb24> img, float stops)
    {
        var table = new byte[256];
        float gain = MathF.Pow(2f, stops);
        for (int i = 0; i < 256; i++)
        {
            float lin = SrgbToLinear(i / 255f) * gain;
            table[i] = (byte)Math.Clamp((int)MathF.Round(LinearToSrgb(Math.Min(lin, 1f)) * 255f), 0, 255);
        }
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
