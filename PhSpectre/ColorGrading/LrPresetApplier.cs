using System.Numerics;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Advanced;
using SixLabors.ImageSharp.PixelFormats;

namespace PhSpectre.ColorGrading;

// Approximates a Lightroom preset on an sRGB image. Adobe's real pipeline is proprietary, so this is
// "same direction and similar strength", not a pixel match. The colour/tone part is baked into a 33^3
// CubeLut (and reused via LutApplier); vignette and grain are position-dependent so run afterwards.
public static class LrPresetApplier
{
    private const int BakeSize = 33;
    private static readonly string[] BandNames = ["Red", "Orange", "Yellow", "Green", "Aqua", "Blue", "Purple", "Magenta"];
    private static readonly float[] BandHue = [0f, 30f, 60f, 120f, 180f, 240f, 270f, 300f];

    public static void Apply(Image<Rgb24> image, LrPreset preset, float strength = 1f)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(preset);
        strength = Math.Clamp(strength, 0f, 1f);
        if (strength == 0f) return;

        LutApplier.Apply(image, Bake(preset), strength);
        ApplyVignetteAndGrain(image, preset, strength);
    }

    // The colour/tone part of the preset as a reusable LUT (also exportable as .cube).
    public static CubeLut Bake(LrPreset p)
    {
        var f = new Grader(p);
        var data = new float[BakeSize * BakeSize * BakeSize * 3];
        int i = 0;
        for (int b = 0; b < BakeSize; b++)
        for (int g = 0; g < BakeSize; g++)
        for (int r = 0; r < BakeSize; r++)
        {
            var o = f.Grade(new Vector3(r, g, b) / (BakeSize - 1));
            data[i++] = o.X; data[i++] = o.Y; data[i++] = o.Z;
        }
        return new CubeLut(p.Name, true, BakeSize, Vector3.Zero, Vector3.One, data);
    }

    private static void ApplyVignetteAndGrain(Image<Rgb24> image, LrPreset p, float strength)
    {
        float vig = p.Get("PostCropVignetteAmount") / 100f * strength;
        float mid = p.Values.TryGetValue("PostCropVignetteMidpoint", out var m) ? m / 100f : 0.5f;
        float grain = p.Get("GrainAmount") / 100f * strength;
        if (vig == 0f && grain == 0f) return;

        int w = image.Width, h = image.Height;
        float cx = (w - 1) / 2f, cy = (h - 1) / 2f;
        float inv = 1f / MathF.Sqrt(cx * cx + cy * cy);
        float start = 0.2f + 0.6f * mid;

        Parallel.For(0, h, y =>
        {
            var row = image.Frames.RootFrame.DangerousGetPixelRowMemory(y).Span;
            for (int x = 0; x < row.Length; x++)
            {
                ref var px = ref row[x];
                var c = new Vector3(px.R, px.G, px.B) / 255f;

                if (vig != 0f)
                {
                    float dx = x - cx, dy = y - cy;
                    float t = Smooth(start, 1f, MathF.Sqrt(dx * dx + dy * dy) * inv) * 0.8f;
                    c = vig < 0 ? c * (1f + vig * t) : c + (Vector3.One - c) * (vig * t);
                }
                if (grain != 0f)
                {
                    float luma = Luma(c);
                    float n = (Hash(x, y) - 0.5f) * 2f;
                    c += new Vector3(n * grain * 0.15f * 4f * luma * (1f - luma));
                }
                px.R = ToByte(c.X); px.G = ToByte(c.Y); px.B = ToByte(c.Z);
            }
        });
    }

    // Cheap deterministic per-pixel noise in 0..1 (same output every run).
    private static float Hash(int x, int y)
    {
        uint h = (uint)x * 374761393u + (uint)y * 668265263u;
        h = (h ^ (h >> 13)) * 1274126177u;
        h ^= h >> 16;
        return (h & 0xFFFFFF) / (float)0x1000000;
    }

    private static byte ToByte(float v) => v <= 0f ? (byte)0 : v >= 1f ? (byte)255 : (byte)(v * 255f + 0.5f);
    private static float Luma(Vector3 c) => 0.2126f * c.X + 0.7152f * c.Y + 0.0722f * c.Z;
    private static float Smooth(float e0, float e1, float x)
    {
        float t = Math.Clamp((x - e0) / (e1 - e0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    private sealed class Grader
    {
        private readonly LrPreset _p;
        private readonly Vector3 _wbGain = Vector3.One;
        private readonly float _exposureGain, _contrast, _highlights, _shadows, _whites, _blacks, _vibrance, _saturation;
        private readonly float[]? _master, _red, _green, _blue;
        private readonly float[] _bandH = new float[8], _bandS = new float[8], _bandL = new float[8];
        private readonly bool _hasHsl, _hasGrading;
        private readonly (float hue, float sat, float lum)[] _grade = new (float, float, float)[4]; // shadow, mid, high, global
        private readonly float _blending, _balance;

        public Grader(LrPreset p)
        {
            _p = p;
            _wbGain = WhiteBalanceGain(p);
            _exposureGain = MathF.Pow(2f, p.Get("Exposure2012"));
            _contrast = p.Get("Contrast2012") / 100f;
            _highlights = p.Get("Highlights2012") / 100f;
            _shadows = p.Get("Shadows2012") / 100f;
            _whites = p.Get("Whites2012") / 100f;
            _blacks = p.Get("Blacks2012") / 100f;
            _vibrance = p.Get("Vibrance") / 100f;
            _saturation = p.Get("Saturation") / 100f;

            _master = BuildCurve(p, "Master");
            _red = BuildCurve(p, "Red");
            _green = BuildCurve(p, "Green");
            _blue = BuildCurve(p, "Blue");

            for (int i = 0; i < 8; i++)
            {
                _bandH[i] = p.Get("HueAdjustment" + BandNames[i]) / 100f;
                _bandS[i] = p.Get("SaturationAdjustment" + BandNames[i]) / 100f;
                _bandL[i] = p.Get("LuminanceAdjustment" + BandNames[i]) / 100f;
                _hasHsl |= _bandH[i] != 0 || _bandS[i] != 0 || _bandL[i] != 0;
            }

            string[] regions = ["Shadow", "Midtone", "Highlight", "Global"];
            for (int i = 0; i < 4; i++)
            {
                _grade[i] = (p.Get($"ColorGrade{regions[i]}Hue"), p.Get($"ColorGrade{regions[i]}Sat") / 100f,
                             p.Get($"ColorGrade{regions[i]}Lum") / 100f);
                _hasGrading |= _grade[i].sat != 0 || _grade[i].lum != 0;
            }
            _blending = p.Values.TryGetValue("ColorGradeBlending", out var bl) ? bl / 100f : 0.5f;
            _balance = p.Get("ColorGradeBalance") / 100f;
        }

        public Vector3 Grade(Vector3 c)
        {
            if (_p.Monochrome) c = new Vector3(Luma(c));

            // White balance + exposure in linear light.
            if (_exposureGain != 1f || _wbGain != Vector3.One)
                c = Encode(Decode(c) * _wbGain * _exposureGain);

            c = Tone(c);
            c = Curves(c);
            if (!_p.Monochrome) c = VibranceSaturation(c);
            if (_hasHsl && !_p.Monochrome) c = Hsl(c);
            if (_hasGrading) c = ColorGrade(c);
            return Vector3.Clamp(c, Vector3.Zero, Vector3.One);
        }

        // JPEGs carry no as-shot WB, so Temperature is interpreted relative to a neutral reference:
        // values in -100..100 are Lightroom's relative slider, larger values are Kelvin vs 5500 K.
        // Higher Kelvin / positive slider = warmer (more red, less blue); positive Tint = magenta.
        private static Vector3 WhiteBalanceGain(LrPreset p)
        {
            float warmth = 0f;
            if (p.Values.TryGetValue("Temperature", out var t) && t != 0f)
                warmth = MathF.Abs(t) <= 100f ? t / 100f * 0.6f : (1e6f / 5500f - 1e6f / Math.Clamp(t, 2000f, 50000f)) / 100f;
            float tint = p.Get("Tint") / 100f * 0.3f;
            const float k = 0.5f;
            var g = new Vector3(MathF.Pow(2f, warmth * k), MathF.Pow(2f, -tint), MathF.Pow(2f, -warmth * k));
            // Keep overall brightness: normalise so luma of white is unchanged.
            return g / Luma(g);
        }

        // Highlights/shadows/whites/blacks/contrast act on luma; colour follows by scaling.
        private Vector3 Tone(Vector3 c)
        {
            if (_contrast == 0 && _highlights == 0 && _shadows == 0 && _whites == 0 && _blacks == 0) return c;
            float l = Math.Clamp(Luma(c), 0f, 1f);
            float n = l;
            n = Push(n, _highlights, Smooth(0.5f, 1f, l), 0.35f);
            n = Push(n, _shadows, 1f - Smooth(0f, 0.5f, l), 0.35f);
            n = Push(n, _whites, Smooth(0.7f, 1f, l), 0.2f);
            n = Push(n, _blacks, 1f - Smooth(0f, 0.3f, l), 0.2f);
            if (_contrast > 0) n = n + (Smooth(0f, 1f, n) - n) * _contrast;
            else if (_contrast < 0) n = 0.5f + (n - 0.5f) * (1f + _contrast * 0.5f);
            float k = l > 1e-4f ? MathF.Min(n / l, 4f) : 1f;
            return l > 1e-4f ? c * k : c + new Vector3(n - l);
        }

        private static float Push(float l, float amt, float w, float gain) =>
            amt == 0f ? l : l + amt * gain * w * (amt > 0 ? 1f - l : l);

        private Vector3 Curves(Vector3 c)
        {
            if (_master != null) c = new Vector3(Look(_master, c.X), Look(_master, c.Y), Look(_master, c.Z));
            if (_red != null) c.X = Look(_red, c.X);
            if (_green != null) c.Y = Look(_green, c.Y);
            if (_blue != null) c.Z = Look(_blue, c.Z);
            return c;
        }

        private Vector3 VibranceSaturation(Vector3 c)
        {
            if (_vibrance == 0 && _saturation == 0) return c;
            float gray = Luma(c);
            float chroma = MathF.Max(c.X, MathF.Max(c.Y, c.Z)) - MathF.Min(c.X, MathF.Min(c.Y, c.Z));
            float scale = (1f + _saturation) * (1f + _vibrance * (1f - Math.Clamp(chroma, 0f, 1f)));
            return new Vector3(gray) + (c - new Vector3(gray)) * MathF.Max(scale, 0f);
        }

        private Vector3 Hsl(Vector3 c)
        {
            RgbToHsv(c, out float h, out float s, out float v);
            if (s < 1e-4f) return c;

            // Linear interpolation of the two neighbouring band centres around the hue circle.
            int i1 = 0;
            for (int i = 0; i < 8; i++) if (BandHue[i] <= h) i1 = i;
            int i2 = (i1 + 1) % 8;
            float span = i2 == 0 ? 360f - BandHue[i1] : BandHue[i2] - BandHue[i1];
            float t = (h - BandHue[i1]) / span;
            float dh = _bandH[i1] + (_bandH[i2] - _bandH[i1]) * t;
            float ds = _bandS[i1] + (_bandS[i2] - _bandS[i1]) * t;
            float dl = _bandL[i1] + (_bandL[i2] - _bandL[i1]) * t;

            h = (h + dh * 30f + 360f) % 360f;
            s = Math.Clamp(s * (1f + ds), 0f, 1f);
            v = Math.Clamp(v * (1f + dl * 0.5f * s), 0f, 1f);
            return HsvToRgb(h, s, v);
        }

        private Vector3 ColorGrade(Vector3 c)
        {
            float l = Math.Clamp(Luma(c), 0f, 1f);
            float hw = 0.1f + 0.3f * _blending;
            float pS = 0.33f - _balance * 0.15f, pH = 0.67f - _balance * 0.15f;
            float wS = 1f - Smooth(pS - hw, pS + hw, l);
            float wH = Smooth(pH - hw, pH + hw, l);
            float wM = Math.Clamp(1f - wS - wH, 0f, 1f);
            float[] w = [wS, wM, wH, 1f];

            for (int i = 0; i < 4; i++)
            {
                var (hue, sat, lum) = _grade[i];
                if (sat != 0)
                {
                    var tint = HsvToRgb(hue, 1f, 1f);
                    c += (tint - new Vector3(Luma(tint))) * (sat * w[i] * 0.5f);
                }
                if (lum != 0) c += new Vector3(lum * w[i] * 0.25f);
            }
            return c;
        }

        private static Vector3 Decode(Vector3 c) => new(SrgbToLinear(c.X), SrgbToLinear(c.Y), SrgbToLinear(c.Z));
        private static Vector3 Encode(Vector3 c) => new(LinearToSrgb(c.X), LinearToSrgb(c.Y), LinearToSrgb(c.Z));
        private static float SrgbToLinear(float v) => v <= 0.04045f ? v / 12.92f : MathF.Pow((v + 0.055f) / 1.055f, 2.4f);
        private static float LinearToSrgb(float v)
        {
            v = Math.Clamp(v, 0f, 1f);
            return v <= 0.0031308f ? v * 12.92f : 1.055f * MathF.Pow(v, 1f / 2.4f) - 0.055f;
        }

        // 256-entry sampled monotone cubic (Fritsch-Carlson) through the control points; null if identity.
        private static float[]? BuildCurve(LrPreset p, string name)
        {
            if (!p.Curves.TryGetValue(name, out var pts)) return null;
            if (pts.All(q => MathF.Abs(q.X - q.Y) < 1e-3f)) return null;

            int n = pts.Length;
            var d = new float[n - 1];
            var m = new float[n];
            for (int i = 0; i < n - 1; i++)
            {
                float dx = pts[i + 1].X - pts[i].X;
                d[i] = dx > 0 ? (pts[i + 1].Y - pts[i].Y) / dx : 0f;
            }
            m[0] = d[0]; m[n - 1] = d[n - 2];
            for (int i = 1; i < n - 1; i++) m[i] = d[i - 1] * d[i] <= 0 ? 0f : (d[i - 1] + d[i]) / 2f;
            for (int i = 0; i < n - 1; i++)
            {
                if (d[i] == 0) { m[i] = 0; m[i + 1] = 0; continue; }
                float a = m[i] / d[i], b = m[i + 1] / d[i], s = a * a + b * b;
                if (s > 9f) { float tau = 3f / MathF.Sqrt(s); m[i] = tau * a * d[i]; m[i + 1] = tau * b * d[i]; }
            }

            var table = new float[256];
            int seg = 0;
            for (int x = 0; x < 256; x++)
            {
                while (seg < n - 2 && x > pts[seg + 1].X) seg++;
                float x0 = pts[seg].X, x1 = pts[seg + 1].X, h = x1 - x0;
                float y;
                if (x <= pts[0].X) y = pts[0].Y;
                else if (x >= pts[n - 1].X) y = pts[n - 1].Y;
                else
                {
                    float t = (x - x0) / h, t2 = t * t, t3 = t2 * t;
                    y = (2 * t3 - 3 * t2 + 1) * pts[seg].Y + (t3 - 2 * t2 + t) * h * m[seg]
                        + (-2 * t3 + 3 * t2) * pts[seg + 1].Y + (t3 - t2) * h * m[seg + 1];
                }
                table[x] = Math.Clamp(y / 255f, 0f, 1f);
            }
            return table;
        }

        private static float Look(float[] table, float v)
        {
            float pos = Math.Clamp(v, 0f, 1f) * 255f;
            int i = (int)pos;
            if (i >= 255) return table[255];
            return table[i] + (table[i + 1] - table[i]) * (pos - i);
        }

        private static void RgbToHsv(Vector3 c, out float h, out float s, out float v)
        {
            float max = MathF.Max(c.X, MathF.Max(c.Y, c.Z)), min = MathF.Min(c.X, MathF.Min(c.Y, c.Z));
            float d = max - min;
            v = max;
            s = max <= 0f ? 0f : d / max;
            if (d <= 1e-6f) { h = 0f; return; }
            if (max == c.X) h = 60f * (((c.Y - c.Z) / d + 6f) % 6f);
            else if (max == c.Y) h = 60f * ((c.Z - c.X) / d + 2f);
            else h = 60f * ((c.X - c.Y) / d + 4f);
        }

        private static Vector3 HsvToRgb(float h, float s, float v)
        {
            h = ((h % 360f) + 360f) % 360f / 60f;
            int i = (int)h;
            float f = h - i, p = v * (1f - s), q = v * (1f - s * f), t = v * (1f - s * (1f - f));
            return i switch
            {
                0 => new Vector3(v, t, p), 1 => new Vector3(q, v, p), 2 => new Vector3(p, v, t),
                3 => new Vector3(p, q, v), 4 => new Vector3(t, p, v), _ => new Vector3(v, p, q)
            };
        }
    }
}
