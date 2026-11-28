using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Advanced;
using SixLabors.ImageSharp.PixelFormats;

namespace PhSpectre.Rendering;

// Noise reduction in YCbCr: overlapping 8x8 DCT thresholding for luminance, pyramid shrinkage for colour. Noise is
// measured once per photo; processing is tiled.
public static partial class NoiseReduction
{
    public const int DefaultTile = 384;
    // Tests compare the vector code with the plain one.
    internal static bool ScalarOnly;
    // Margin around each tile. Tiles are multiples of Align, so there are no seams.
    public const int Context = 80;
    public const int Align = 16;
    private const int Levels = 4;
    private const int Block = 8;
    private const int Step = 2;

    // Threshold at full colour strength, in units of the measured noise.
    private const float ColorThreshold = 3.5f;

    // Luminance slider to DCT threshold (in noise units). A straight line does nothing at low values; these points,
    // measured on ISO 10000 JPEGs, make grain fall evenly.
    private static readonly (float Strength, float Threshold)[] LumaCurve =
        [(0f, 0f), (0.1f, 1.1f), (0.25f, 1.55f), (0.5f, 2.05f), (0.75f, 2.55f), (1f, 3.4f)];

    private static float LumaThreshold(float strength)
    {
        int i = 1;
        while (i < LumaCurve.Length - 1 && strength > LumaCurve[i].Strength) i++;
        var (a, b) = (LumaCurve[i - 1], LumaCurve[i]);
        return a.Threshold + (b.Threshold - a.Threshold) * (strength - a.Strength) / (b.Strength - a.Strength);
    }

    // Noise of a whole picture, to clean a part of it exactly like the whole.
    public static NoiseProfile Measure(Image<Rgb24> src)
    {
        ArgumentNullException.ThrowIfNull(src);
        return NoiseProfile.Of(src);
    }

    // Returns a new image, strengths 0..100. A part matches the whole if it starts on a multiple of 16 with Context
    // pixels to spare.
    public static Image<Rgb24> Apply(Image<Rgb24> src, float luminance, float color,
        CancellationToken ct = default, int tile = DefaultTile, NoiseProfile? profile = null)
    {
        ArgumentNullException.ThrowIfNull(src);
        if (tile < Align || tile % Align != 0) throw new ArgumentOutOfRangeException(nameof(tile), $"The tile must be a multiple of {Align}.");
        float lum = Strength(luminance), col = Strength(color);
        if (lum == 0f && col == 0f) return src.Clone();

        int w = src.Width, h = src.Height;
        profile ??= NoiseProfile.Of(src);
        var result = new Image<Rgb24>(w, h);
        try
        {
            int cols = (w + tile - 1) / tile, rows = (h + tile - 1) / tile;
            var options = new ParallelOptions
            {
                CancellationToken = ct,
                // Each worker holds its own tile buffers (about 15 MB), so the count is capped.
                MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount * 3 / 4, 1, 12),
            };
            Parallel.For(0, cols * rows, options, () => new Workspace(), (i, _, ws) =>
            {
                int cx = i % cols * tile, cy = i / cols * tile;
                int cw = Math.Min(tile, w - cx), ch = Math.Min(tile, h - cy);
                int x0 = Math.Max(0, cx - Context), y0 = Math.Max(0, cy - Context);
                int x1 = Math.Min(w, cx + cw + Context), y1 = Math.Min(h, cy + ch + Context);
                ws.Load(src, x0, y0, x1 - x0, y1 - y0);
                if (lum > 0f) ws.DenoiseLuma(profile, LumaThreshold(lum));
                if (col > 0f) ws.DenoiseColor(profile, col * ColorThreshold);
                ws.Store(result, cx, cy, cw, ch, cx - x0, cy - y0, lum > 0f, col > 0f);
                return ws;
            }, _ => { });
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    private static float Strength(float v) => Math.Clamp(float.IsFinite(v) ? v : 0f, 0f, 100f) / 100f;

    public sealed class NoiseProfile
    {
        private NoiseProfile() { }

        private const int Bins = 8;
        private const int Patch = 128;
        private const int PatchesPerSide = 8;
        // The quietest blocks are flat areas.
        private const float QuietShare = 0.3f;

        private readonly float[] _luma = new float[Bins];
        // Typical energy of colour noise in each detail layer.
        internal readonly float[] Color = new float[Levels];

        // Standard deviation of the luminance noise at this brightness (0..255), interpolated between bins.
        internal float LumaSigma(float mean)
        {
            float p = Math.Clamp(mean / 256f * Bins - 0.5f, 0f, Bins - 1);
            int i = Math.Min((int)p, Bins - 2);
            float f = p - i;
            return _luma[i] + (_luma[i + 1] - _luma[i]) * f;
        }

        internal static NoiseProfile Of(Image<Rgb24> src)
        {
            var ws = new Workspace();
            var luma = new List<float>[Bins];
            for (int i = 0; i < Bins; i++) luma[i] = [];
            var chroma = new List<float>[Levels];
            for (int i = 0; i < Levels; i++) chroma[i] = [];

            int pw = Math.Min(Patch, src.Width), ph = Math.Min(Patch, src.Height);
            int nx = Math.Clamp(src.Width / Patch, 1, PatchesPerSide), ny = Math.Clamp(src.Height / Patch, 1, PatchesPerSide);
            Span<float> block = stackalloc float[Block * Block];
            Span<float> mags = stackalloc float[Block * Block];
            for (int j = 0; j < ny; j++)
            for (int i = 0; i < nx; i++)
            {
                int x = nx == 1 ? (src.Width - pw) / 2 : (int)((long)(src.Width - pw) * i / (nx - 1));
                int y = ny == 1 ? (src.Height - ph) / 2 : (int)((long)(src.Height - ph) * j / (ny - 1));
                ws.Load(src, x, y, pw, ph);

                for (int by = 0; by + Block <= ph; by += Block)
                for (int bx = 0; bx + Block <= pw; bx += Block)
                {
                    float mean = 0;
                    for (int r = 0; r < Block; r++)
                    {
                        var row = ws.Y.AsSpan((by + r) * pw + bx, Block);
                        row.CopyTo(block.Slice(r * Block, Block));
                        foreach (var v in row) mean += v;
                    }
                    mean /= Block * Block;
                    // Clipped blocks have no noise left to measure.
                    if (mean < 3f || mean > 252f) continue;
                    Dct.Forward(block);
                    // Low and middle frequencies: camera JPEGs keep little noise in the finest.
                    int n = 0;
                    for (int v = 0; v < Block; v++)
                        for (int u = 0; u < Block; u++)
                            if (u + v is >= 1 and <= 6) mags[n++] = MathF.Abs(block[v * Block + u]);
                    mags[..n].Sort();
                    luma[Math.Clamp((int)(mean / 256f * Bins), 0, Bins - 1)].Add(mags[n / 2] / 0.6745f);
                }

                ws.Decompose();
                for (int l = 0; l < Levels; l++) ws.CollectColorEnergy(l, chroma[l]);
            }

            var profile = new NoiseProfile();
            var all = luma.SelectMany(b => b).ToList();
            float overall = Quiet(all, 1.5f);
            for (int i = 0; i < Bins; i++)
                profile._luma[i] = Math.Clamp(luma[i].Count >= 24 ? Quiet(luma[i], overall) : overall, 0.8f, 25f);
            for (int l = 0; l < Levels; l++)
                profile.Color[l] = Math.Clamp(Quiet(chroma[l], 0.25f), 0.05f, 200f);
            return profile;
        }

        private static float Quiet(List<float> values, float fallback)
        {
            if (values.Count == 0) return fallback;
            values.Sort();
            return values[(int)((values.Count - 1) * QuietShare)];
        }
    }

    private sealed partial class Workspace
    {
        private int _w, _h;
        public float[] Y = [];
        private float[] _sum = [], _weight = [];
        // Cb and Cr at every scale (0 = full size, Levels = the coarsest rest) and the detail between scales.
        private readonly float[][] _cb = new float[Levels + 1][], _cr = new float[Levels + 1][];
        private readonly float[][] _dcb = new float[Levels][], _dcr = new float[Levels][];
        private readonly int[] _lw = new int[Levels + 1], _lh = new int[Levels + 1];
        private float[] _energy = [], _gain = [];

        private static void Grow(ref float[] a, int n) { if (a == null || a.Length < n) a = new float[n]; }

        public void Load(Image<Rgb24> src, int x0, int y0, int w, int h)
        {
            _w = w; _h = h;
            int n = w * h;
            Grow(ref Y, n); Grow(ref _cb[0], n); Grow(ref _cr[0], n);
            var cb = _cb[0]; var cr = _cr[0];
            for (int y = 0; y < h; y++)
            {
                var row = src.Frames.RootFrame.DangerousGetPixelRowMemory(y0 + y).Span.Slice(x0, w);
                for (int x = 0, i = y * w; x < w; x++, i++)
                {
                    float r = row[x].R, g = row[x].G, b = row[x].B;
                    Y[i] = 0.299f * r + 0.587f * g + 0.114f * b;
                    cb[i] = -0.168736f * r - 0.331264f * g + 0.5f * b;
                    cr[i] = 0.5f * r - 0.418688f * g - 0.081312f * b;
                }
            }
        }

        public void Store(Image<Rgb24> dst, int dx, int dy, int w, int h, int ox, int oy, bool luma, bool color)
        {
            // Luminance ends up in _sum, colour in _cb[0] and _cr[0].
            var yp = luma && _w >= Block && _h >= Block ? _sum : Y;
            var cb = _cb[0]; var cr = _cr[0];
            for (int y = 0; y < h; y++)
            {
                var row = dst.Frames.RootFrame.DangerousGetPixelRowMemory(dy + y).Span.Slice(dx, w);
                for (int x = 0, i = (oy + y) * _w + ox; x < w; x++, i++)
                {
                    float l = yp[i], u = cb[i], v = cr[i];
                    row[x] = new Rgb24(Byte(l + 1.402f * v), Byte(l - 0.344136f * u - 0.714136f * v), Byte(l + 1.772f * u));
                }
            }
        }

        private static byte Byte(float v) => (byte)Math.Clamp((int)(v + 0.5f), 0, 255);

        // Each pixel is the weighted mean of the thresholded blocks covering it; flat blocks weigh more.
        public void DenoiseLuma(NoiseProfile profile, float k)
        {
            int w = _w, h = _h;
            if (w < Block || h < Block) return;
            int n = w * h;
            Grow(ref _sum, n); Grow(ref _weight, n);
            _sum.AsSpan(0, n).Clear(); _weight.AsSpan(0, n).Clear();
            if (SimdDct.IsSupported && !ScalarOnly) DenoiseLumaSimd(profile, k);
            else DenoiseLumaScalar(profile, k);
        }

        private void DenoiseLumaScalar(NoiseProfile profile, float k)
        {
            int w = _w, h = _h, n = w * h;
            Span<float> block = stackalloc float[Block * Block];
            for (int by = 0; ; by = Math.Min(by + Step, h - Block))
            {
                for (int bx = 0; ; bx = Math.Min(bx + Step, w - Block))
                {
                    for (int r = 0; r < Block; r++) Y.AsSpan((by + r) * w + bx, Block).CopyTo(block.Slice(r * Block, Block));
                    Dct.Forward(block);
                    // The DC term is the block's mean times Block.
                    float t = k * profile.LumaSigma(block[0] / Block);
                    int kept = 1;
                    for (int i = 1; i < block.Length; i++)
                    {
                        if (MathF.Abs(block[i]) < t) block[i] = 0f;
                        else kept++;
                    }
                    Dct.Inverse(block);
                    float weight = 1f / kept;
                    for (int r = 0; r < Block; r++)
                    {
                        int o = (by + r) * w + bx;
                        for (int c = 0; c < Block; c++)
                        {
                            _sum[o + c] += block[r * Block + c] * weight;
                            _weight[o + c] += weight;
                        }
                    }
                    if (bx == w - Block) break;
                }
                if (by == h - Block) break;
            }
            for (int i = 0; i < n; i++) _sum[i] /= _weight[i];
        }

        // Splits Cb and Cr into detail layers plus a coarse rest.
        public void Decompose()
        {
            _lw[0] = _w; _lh[0] = _h;
            for (int l = 0; l < Levels; l++)
            {
                int w = _lw[l], h = _lh[l], sw = (w + 1) / 2, sh = (h + 1) / 2;
                _lw[l + 1] = sw; _lh[l + 1] = sh;
                Grow(ref _cb[l + 1], sw * sh); Grow(ref _cr[l + 1], sw * sh);
                Grow(ref _dcb[l], w * h); Grow(ref _dcr[l], w * h);
                Halve(_cb[l], w, h, _cb[l + 1], sw, sh);
                Halve(_cr[l], w, h, _cr[l + 1], sw, sh);
                Expand(_cb[l + 1], sw, sh, _cb[l], _dcb[l], w, h, subtract: true);
                Expand(_cr[l + 1], sw, sh, _cr[l], _dcr[l], w, h, subtract: true);
            }
        }

        // Local energy (3x3 mean) of the two colour details together, into _energy.
        private void Energy(int level)
        {
            int w = _lw[level], h = _lh[level];
            Grow(ref _energy, w * h); Grow(ref _gain, w * h);
            var a = _dcb[level]; var b = _dcr[level];
            var raw = _gain;
            for (int i = 0; i < w * h; i++) raw[i] = a[i] * a[i] + b[i] * b[i];
            for (int y = 0; y < h; y++)
            {
                int up = Math.Max(0, y - 1) * w, mid = y * w, down = Math.Min(h - 1, y + 1) * w;
                for (int x = 0; x < w; x++)
                {
                    int l = Math.Max(0, x - 1), r = Math.Min(w - 1, x + 1);
                    _energy[mid + x] = (raw[up + l] + raw[up + x] + raw[up + r] + raw[mid + l] + raw[mid + x] + raw[mid + r]
                                        + raw[down + l] + raw[down + x] + raw[down + r]) / 9f;
                }
            }
        }

        public void CollectColorEnergy(int level, List<float> sink)
        {
            Energy(level);
            int n = _lw[level] * _lh[level];
            for (int i = 0; i < n; i += 3) sink.Add(_energy[i]);
        }

        // Shrinks detail layers by how far their energy exceeds the noise, then rebuilds. k = 0 is lossless.
        public void DenoiseColor(NoiseProfile profile, float k)
        {
            Decompose();
            for (int l = Levels - 1; l >= 0; l--)
            {
                int w = _lw[l], h = _lh[l];
                Energy(l);
                float t = k * k * profile.Color[l];
                var a = _dcb[l]; var b = _dcr[l];
                for (int i = 0; i < w * h; i++)
                {
                    float g = Math.Max(0f, 1f - t / (_energy[i] + 1e-6f));
                    a[i] *= g; b[i] *= g;
                }
                Expand(_cb[l + 1], _lw[l + 1], _lh[l + 1], a, _cb[l], w, h, subtract: false);
                Expand(_cr[l + 1], _lw[l + 1], _lh[l + 1], b, _cr[l], w, h, subtract: false);
            }
        }

        private static void Halve(float[] src, int w, int h, float[] dst, int dw, int dh)
        {
            for (int y = 0; y < dh; y++)
            {
                int r0 = 2 * y * w, r1 = Math.Min(2 * y + 1, h - 1) * w;
                for (int x = 0; x < dw; x++)
                {
                    int a = 2 * x, b = Math.Min(2 * x + 1, w - 1);
                    dst[y * dw + x] = (src[r0 + a] + src[r0 + b] + src[r1 + a] + src[r1 + b]) * 0.25f;
                }
            }
        }

        // Writes other - enlarged (subtract) or other + enlarged into dst, where enlarged is `small` doubled bilinearly.
        private static void Expand(float[] small, int sw, int sh, float[] other, float[] dst, int w, int h, bool subtract)
        {
            float sign = subtract ? -1f : 1f;
            for (int y = 0; y < h; y++)
            {
                // A pixel sits a quarter of a coarse pixel away from its nearest coarse centre.
                int near = y / 2, far = Math.Clamp(near + ((y & 1) == 0 ? -1 : 1), 0, sh - 1);
                int rn = near * sw, rf = far * sw;
                for (int x = 0, i = y * w; x < w; x++, i++)
                {
                    int cn = x / 2, cf = Math.Clamp(cn + ((x & 1) == 0 ? -1 : 1), 0, sw - 1);
                    float v = 0.5625f * small[rn + cn] + 0.1875f * (small[rn + cf] + small[rf + cn]) + 0.0625f * small[rf + cf];
                    dst[i] = other[i] + sign * v;
                }
            }
        }
    }

    // Orthonormal 8x8 DCT-II on a row-major block, split into even and odd halves to save half the multiplications.
    private static class Dct
    {
        private static readonly float A = 0.5f * MathF.Cos(MathF.PI / 4);
        private static readonly float B = 0.5f * MathF.Cos(MathF.PI / 8), C = 0.5f * MathF.Cos(3 * MathF.PI / 8);
        private static readonly float D = 0.5f * MathF.Cos(MathF.PI / 16), E = 0.5f * MathF.Cos(3 * MathF.PI / 16);
        private static readonly float F = 0.5f * MathF.Cos(5 * MathF.PI / 16), G = 0.5f * MathF.Cos(7 * MathF.PI / 16);

        public static void Forward(Span<float> block)
        {
            for (int r = 0; r < 8; r++) Forward1(block, r * 8, 1);
            for (int c = 0; c < 8; c++) Forward1(block, c, 8);
        }

        public static void Inverse(Span<float> block)
        {
            for (int c = 0; c < 8; c++) Inverse1(block, c, 8);
            for (int r = 0; r < 8; r++) Inverse1(block, r * 8, 1);
        }

        private static void Forward1(Span<float> v, int o, int s)
        {
            float x0 = v[o], x1 = v[o + s], x2 = v[o + 2 * s], x3 = v[o + 3 * s];
            float x4 = v[o + 4 * s], x5 = v[o + 5 * s], x6 = v[o + 6 * s], x7 = v[o + 7 * s];
            float s0 = x0 + x7, s1 = x1 + x6, s2 = x2 + x5, s3 = x3 + x4;
            float d0 = x0 - x7, d1 = x1 - x6, d2 = x2 - x5, d3 = x3 - x4;
            float p = s0 + s3, q = s1 + s2, m = s0 - s3, n = s1 - s2;
            v[o] = A * (p + q);
            v[o + 4 * s] = A * (p - q);
            v[o + 2 * s] = B * m + C * n;
            v[o + 6 * s] = C * m - B * n;
            v[o + s] = D * d0 + E * d1 + F * d2 + G * d3;
            v[o + 3 * s] = E * d0 - G * d1 - D * d2 - F * d3;
            v[o + 5 * s] = F * d0 - D * d1 + G * d2 + E * d3;
            v[o + 7 * s] = G * d0 - F * d1 + E * d2 - D * d3;
        }

        private static void Inverse1(Span<float> v, int o, int s)
        {
            float c0 = v[o], c1 = v[o + s], c2 = v[o + 2 * s], c3 = v[o + 3 * s];
            float c4 = v[o + 4 * s], c5 = v[o + 5 * s], c6 = v[o + 6 * s], c7 = v[o + 7 * s];
            float p = A * (c0 + c4), q = A * (c0 - c4), m = B * c2 + C * c6, n = C * c2 - B * c6;
            float s0 = p + m, s1 = q + n, s2 = q - n, s3 = p - m;
            float d0 = D * c1 + E * c3 + F * c5 + G * c7;
            float d1 = E * c1 - G * c3 - D * c5 - F * c7;
            float d2 = F * c1 - D * c3 + G * c5 + E * c7;
            float d3 = G * c1 - F * c3 + E * c5 - D * c7;
            v[o] = s0 + d0; v[o + 7 * s] = s0 - d0;
            v[o + s] = s1 + d1; v[o + 6 * s] = s1 - d1;
            v[o + 2 * s] = s2 + d2; v[o + 5 * s] = s2 - d2;
            v[o + 3 * s] = s3 + d3; v[o + 4 * s] = s3 - d3;
        }
    }
}
