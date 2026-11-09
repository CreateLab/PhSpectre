using PhSpectre.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace PhSpectre.Rendering;

/// <summary>
/// An approximate preview of a film recipe. The bundled sample is stored "flat" (log-like, desaturated), the way
/// a camera shoots for grading; <see cref="Flatten"/> makes such a frame from a normal photo, <see cref="Render"/>
/// develops it and applies the recipe. It is a hand-tuned look-alike, not Fujifilm's real processing.
/// </summary>
public static class RecipePreview
{
    private const double LogK = 12.0;      // strength of the log curve
    private const double FlatSaturation = 0.6;

    // ── Flat (log-like) <-> neutral ─────────────────────────────────────────────────────────
    public static void Flatten(Image<Rgb24> img)
    {
        img.Mutate(c => c.ProcessPixelRowsAsVector4((row, _) =>
        {
            for (int x = 0; x < row.Length; x++)
            {
                var p = row[x];
                double r = ToLinear(p.X), g = ToLinear(p.Y), b = ToLinear(p.Z);
                double l = 0.2126 * r + 0.7152 * g + 0.0722 * b;
                r = l + (r - l) * FlatSaturation; g = l + (g - l) * FlatSaturation; b = l + (b - l) * FlatSaturation;
                row[x] = new System.Numerics.Vector4((float)Log(r), (float)Log(g), (float)Log(b), p.W);
            }
        }));
    }

    private const double Lift = 0.09, Range = 0.62; // blacks sit at ~9%, white at ~71%: the flat, "gray" look
    private static double Log(double lin) => Lift + Range * Math.Log(1 + LogK * Math.Max(0, lin)) / Math.Log(1 + LogK);
    private static double Unlog(double y) => (Math.Exp(Math.Clamp((y - Lift) / Range, 0, 1) * Math.Log(1 + LogK)) - 1) / LogK;
    private static double ToLinear(double s) => s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
    private static double ToSrgb(double l) { l = Math.Clamp(l, 0, 1); return l <= 0.0031308 ? l * 12.92 : 1.055 * Math.Pow(l, 1 / 2.4) - 0.055; }

    // ── Recipe look ─────────────────────────────────────────────────────────────────────────
    private readonly record struct Sim(double Contrast, double Saturation, (double R, double G, double B) Shadow,
        (double R, double G, double B) Highlight, bool Mono = false, (double R, double G, double B)? Tone = null);

    private static readonly (double, double, double) None = (0, 0, 0);

    private static Sim SimFor(string? film)
    {
        var f = (film ?? "").ToLowerInvariant();
        if (f.Contains("sepia")) return new(1.05, 0, None, None, true, (1.0, 0.82, 0.6));
        if (f.Contains("acros") || f.Contains("monochrome") || f.Contains("b&w")) return new(1.15, 0, None, None, true);
        if (f.Contains("bleach")) return new(1.35, 0.5, (-0.02, 0, 0.02), (0.01, 0, -0.01));
        if (f.Contains("eterna")) return new(0.88, 0.78, (-0.01, 0.01, 0.02), None);
        if (f.Contains("classic chrome")) return new(1.12, 0.74, (-0.02, 0.01, 0.03), (0.02, 0.005, -0.015));
        if (f.Contains("classic neg") || f.Contains("classic negative")) return new(1.18, 0.92, (-0.02, 0.03, 0.03), (0.04, 0.0, -0.02));
        if (f.Contains("nostalgic")) return new(1.0, 0.9, (0.02, 0.0, -0.02), (0.05, 0.02, -0.04));
        if (f.Contains("velvia")) return new(1.18, 1.35, None, None);
        if (f.Contains("astia")) return new(0.92, 0.95, None, (0.01, 0.005, 0));
        if (f.Contains("reala")) return new(1.0, 0.92, None, None);
        if (f.Contains("pro neg. hi") || f.Contains("neg. hi") || f.Contains("pro neg hi")) return new(1.08, 0.95, None, None);
        if (f.Contains("pro neg")) return new(0.9, 0.88, None, None);
        return new(1.0, 1.0, None, None); // Provia / Standard / unknown
    }

    /// <summary>Develops a flat source and applies the recipe. Returns a new image; the source is untouched.</summary>
    public static Image<Rgb24> Render(Image<Rgb24> flat, FilmRecipe recipe)
    {
        var sim = SimFor(recipe.FilmSimulation);
        var (wbR, wbB) = WhiteBalanceGain(recipe);
        if (recipe.WhiteBalanceShift is { } s) { wbR *= 1 + 0.02 * s.Red; wbB *= 1 + 0.02 * s.Blue; }
        double hi = (double)(recipe.HighlightTone ?? 0), sh = (double)(recipe.ShadowTone ?? 0);
        double sat = sim.Saturation * (1 + 0.08 * (recipe.Color ?? 0));
        if (recipe.ColorChromeEffect is "Strong") sat *= 1.06; else if (recipe.ColorChromeEffect is "Weak") sat *= 1.03;
        double clarity = (double)(recipe.Clarity ?? 0);
        double contrast = sim.Contrast * (1 + 0.03 * clarity);
        double dr = recipe.DynamicRange switch { "DR200" => 0.5, "DR400" => 1.0, _ => 0.0 };
        double grainAmp = recipe.GrainEffect switch { "Strong" => 0.028, "Weak" => 0.012, _ => 0.0 };
        bool bigGrain = recipe.GrainSize == "Large";

        var res = flat.Clone();
        res.Mutate(c => c.ProcessPixelRowsAsVector4((row, point) =>
        {

            for (int x = 0; x < row.Length; x++)
            {
                var p = row[x];
                // Back to a neutral frame: undo the log curve and the flat saturation.
                double r = Unlog(p.X), g = Unlog(p.Y), b = Unlog(p.Z);
                double l0 = 0.2126 * r + 0.7152 * g + 0.0722 * b;
                r = l0 + (r - l0) / FlatSaturation * 1.0; g = l0 + (g - l0) / FlatSaturation; b = l0 + (b - l0) / FlatSaturation;
                r *= wbR; b *= wbB;

                double lum = 0.2126 * r + 0.7152 * g + 0.0722 * b;
                // Saturation (or monochrome).
                if (sim.Mono) { r = g = b = lum; if (sim.Tone is { } t) { r *= t.R; g *= t.G; b *= t.B; } }
                else { r = lum + (r - lum) * sat; g = lum + (g - lum) * sat; b = lum + (b - lum) * sat; }

                // Tone in display space: contrast around mid grey, highlight/shadow sliders, DR compression.
                double cr = ToSrgb(r), cg = ToSrgb(g), cb = ToSrgb(b);
                double y = 0.2126 * cr + 0.7152 * cg + 0.0722 * cb;
                double ty = Math.Clamp((y - 0.5) * contrast + 0.5, 0, 1);
                ty += hi * 0.035 * Smooth((ty - 0.5) * 2) - sh * 0.04 * Smooth((0.5 - ty) * 2);
                ty = ty - dr * 0.06 * Smooth((ty - 0.6) * 2.5) + dr * 0.05 * Smooth((0.4 - ty) * 2.5);
                double k = y > 1e-4 ? ty / y : 1;
                cr = cr * k + sim.Shadow.R * (1 - ty) + sim.Highlight.R * ty;
                cg = cg * k + sim.Shadow.G * (1 - ty) + sim.Highlight.G * ty;
                cb = cb * k + sim.Shadow.B * (1 - ty) + sim.Highlight.B * ty;

                if (grainAmp > 0)
                {
                    int gx = bigGrain ? x / 2 : x, gy = bigGrain ? point.Y / 2 : point.Y;
                    double n = Noise(gx, gy) * grainAmp * (1 - Math.Abs(ty - 0.5) * 0.8);
                    cr += n; cg += n; cb += n;
                }
                row[x] = new System.Numerics.Vector4((float)Math.Clamp(cr, 0, 1), (float)Math.Clamp(cg, 0, 1), (float)Math.Clamp(cb, 0, 1), p.W);
            }
        }));
        return res;
    }

    // Deterministic, roughly gaussian noise in about [-1, 1] (two hashed uniforms, so the grain never "swims").
    private static double Noise(int x, int y)
    {
        static double H(uint v) { v ^= v >> 16; v *= 0x7feb352d; v ^= v >> 15; v *= 0x846ca68b; v ^= v >> 16; return v / (double)uint.MaxValue; }
        uint k = (uint)x * 73856093u ^ (uint)y * 19349663u;
        return H(k) + H(k + 0x9e3779b9u) - 1.0;
    }

    private static double Smooth(double v) { v = Math.Clamp(v, 0, 1); return v * v * (3 - 2 * v); }

    // Named white balances and Kelvin (relative to daylight, 5500 K) as red/blue gains.
    private static (double R, double B) WhiteBalanceGain(FilmRecipe r)
    {
        if (r.ColorTemperature is { } k and > 1500) return KelvinGain(k);
        var wb = (r.WhiteBalance ?? "").ToLowerInvariant();
        if (wb.Contains("kelvin")) return KelvinGain(r.ColorTemperature ?? 5500);
        if (wb.Contains("shade")) return KelvinGain(7500);
        if (wb.Contains("cloud")) return KelvinGain(6500);
        if (wb.Contains("incandescent") || wb.Contains("tungsten")) return KelvinGain(3000);
        if (wb.Contains("fluorescent")) return (1.0, 1.02);
        return (1, 1); // Auto / Daylight
    }

    // Warmer than 5500 K -> more red, less blue (the scene is rendered as if lit by that light).
    private static (double R, double B) KelvinGain(double k)
    {
        double t = Math.Clamp((k - 5500) / 3000.0, -1, 1);
        return (1 + 0.18 * t, 1 - 0.22 * t);
    }
}
