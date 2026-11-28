using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Advanced;
using SixLabors.ImageSharp.PixelFormats;

namespace PhSpectre.Rendering;

// Smooths fine texture and colour blotches inside the skin mask with a fast guided filter (He et al.): coefficients
// come from a reduced copy, edges survive. Sizes follow the amount of skin, so preview and export match.
public static class SkinRetouch
{
    /// <summary>Set by the app at start; skin smoothing does nothing without it.</summary>
    public static ISkinSegmenter? Segmenter { get; set; }
    public static bool IsAvailable => Segmenter != null;

    /// <summary>Margin a piece of a photo needs to match the same area of the whole photo.</summary>
    public const int Context = 192;

    private const float LumaEps = 0.0022f, ChromaEps = 0.004f, MaskEps = 0.001f, ChromaShare = 0.7f;

    // `strength` is 0..1; changes the image in place.
    public static void Apply(Image<Rgb24> image, float strength, SkinMask? mask = null)
    {
        ArgumentNullException.ThrowIfNull(image);
        strength = Math.Clamp(strength, 0f, 1f);
        if (strength <= 0f) return;
        mask ??= Segmenter?.Segment(image);
        if (mask == null || mask.Coverage < 0.002f) return;

        int w = image.Width, h = image.Height;
        double photoW = w / mask.Across, photoH = h / mask.Down;
        int radius = (int)Math.Round(Math.Sqrt(mask.Coverage * photoW * photoH) * 0.025);
        radius = Math.Clamp(radius, 2, Math.Max(2, (int)(Math.Max(photoW, photoH) / 150)));
        // A power of two, so that pieces cut on the noise reduction's 16-pixel grid reduce the same way.
        int step = 1;
        while (step < 8 && step * 4 <= radius) step *= 2;
        int small = Math.Max(1, (int)Math.Round((double)radius / step));

        int lw = (w + step - 1) / step, lh = (h + step - 1) / step;
        var y = new float[lw * lh];
        var cr = new float[lw * lh];
        var cb = new float[lw * lh];
        var skin = new float[lw * lh];
        Parallel.For(0, lh, ly =>
        {
            var count = new int[lw];
            for (int sy = ly * step; sy < Math.Min(h, (ly + 1) * step); sy++)
            {
                var row = image.Frames.RootFrame.DangerousGetPixelRowMemory(sy).Span;
                for (int sx = 0; sx < row.Length; sx++)
                {
                    int i = ly * lw + sx / step;
                    ToYcc(row[sx], out float py, out float pr, out float pb);
                    y[i] += py; cr[i] += pr; cb[i] += pb;
                    count[sx / step]++;
                }
            }
            for (int lx = 0; lx < lw; lx++)
            {
                int i = ly * lw + lx;
                float inv = 1f / count[lx];
                y[i] *= inv; cr[i] *= inv; cb[i] *= inv;
                skin[i] = mask.At((lx + 0.5) * step / w, (ly + 0.5) * step / h);
            }
        });

        // Snaps the mask to the picture's edges.
        var (maskA, maskB) = Coefficients(y, skin, lw, lh, small * 2, MaskEps);
        var soft = new float[lw * lh];
        float highest = 0f;
        for (int i = 0; i < soft.Length; i++)
        {
            soft[i] = Math.Clamp(maskA[i] * y[i] + maskB[i], 0f, 1f) * strength;
            highest = Math.Max(highest, soft[i]);
        }
        if (highest < 0.004f) return;

        var (ya, yb) = Coefficients(y, y, lw, lh, small, LumaEps);
        var (ra, rb) = Coefficients(y, cr, lw, lh, small * 2, ChromaEps);
        var (ba, bb) = Coefficients(y, cb, lw, lh, small * 2, ChromaEps);

        // Stretch the coefficient planes: vertically once per row, then horizontally per pixel.
        float[][] planes = [soft, ya, yb, ra, rb, ba, bb];
        var left = new int[w];
        var right = new int[w];
        var share = new float[w];
        for (int px = 0; px < w; px++)
        {
            float fx = Math.Clamp((px + 0.5f) / step - 0.5f, 0f, lw - 1);
            left[px] = (int)fx;
            right[px] = Math.Min(left[px] + 1, lw - 1);
            share[px] = fx - left[px];
        }

        Parallel.For(0, h, () => new float[planes.Length * lw], (py, _, lines) =>
        {
            float fy = Math.Clamp((py + 0.5f) / step - 0.5f, 0f, lh - 1);
            int y0 = (int)fy, y1 = Math.Min(y0 + 1, lh - 1);
            float ty = fy - y0;
            float most = 0f;
            for (int n = 0; n < planes.Length; n++)
            {
                var plane = planes[n];
                int to = n * lw, upper = y0 * lw, lower = y1 * lw;
                for (int x = 0; x < lw; x++)
                {
                    float value = plane[upper + x];
                    value += (plane[lower + x] - value) * ty;
                    lines[to + x] = value;
                    if (n == 0 && value > most) most = value;
                }
                if (n == 0 && most < 0.004f) return lines;
            }

            var row = image.Frames.RootFrame.DangerousGetPixelRowMemory(py).Span;
            const float unit = 1f / 255f;
            for (int px = 0; px < row.Length; px++)
            {
                int a = left[px], b = right[px];
                float tx = share[px];
                float amount = lines[a] + (lines[b] - lines[a]) * tx;
                if (amount < 0.004f) continue;
                int o = lw;
                float aY = lines[o + a] + (lines[o + b] - lines[o + a]) * tx; o += lw;
                float bY = lines[o + a] + (lines[o + b] - lines[o + a]) * tx; o += lw;
                float aR = lines[o + a] + (lines[o + b] - lines[o + a]) * tx; o += lw;
                float bR = lines[o + a] + (lines[o + b] - lines[o + a]) * tx; o += lw;
                float aB = lines[o + a] + (lines[o + b] - lines[o + a]) * tx; o += lw;
                float bB = lines[o + a] + (lines[o + b] - lines[o + a]) * tx;

                ref var pixel = ref row[px];
                float r = pixel.R * unit, g = pixel.G * unit, blue = pixel.B * unit;
                float vy = 0.299f * r + 0.587f * g + 0.114f * blue;
                float vr = (r - vy) * 0.713f + 0.5f, vb = (blue - vy) * 0.564f + 0.5f;
                float ny = vy + amount * (aY * vy + bY - vy);
                float nr = vr + amount * ChromaShare * (aR * vy + bR - vr) - 0.5f;
                float nb = vb + amount * ChromaShare * (aB * vy + bB - vb) - 0.5f;
                pixel = new Rgb24(
                    ToneMath.ToByte(ny + 1.403f * nr),
                    ToneMath.ToByte(ny - 0.714f * nr - 0.344f * nb),
                    ToneMath.ToByte(ny + 1.773f * nb));
            }
            return lines;
        }, _ => { });
    }

    private static void ToYcc(Rgb24 p, out float y, out float cr, out float cb)
    {
        float r = p.R / 255f, g = p.G / 255f, b = p.B / 255f;
        y = 0.299f * r + 0.587f * g + 0.114f * b;
        cr = (r - y) * 0.713f + 0.5f;
        cb = (b - y) * 0.564f + 0.5f;
    }

    // Guided filter: source = a * guide + b per window; returns the averaged a and b.
    private static (float[] A, float[] B) Coefficients(float[] guide, float[] source, int w, int h, int radius, float eps)
    {
        var product = new float[guide.Length];
        var square = new float[guide.Length];
        for (int i = 0; i < guide.Length; i++)
        {
            product[i] = guide[i] * source[i];
            square[i] = guide[i] * guide[i];
        }
        var meanG = Box(guide, w, h, radius);
        var meanS = ReferenceEquals(guide, source) ? meanG : Box(source, w, h, radius);
        var meanP = Box(product, w, h, radius);
        var meanSq = Box(square, w, h, radius);
        var a = new float[guide.Length];
        var b = new float[guide.Length];
        for (int i = 0; i < guide.Length; i++)
        {
            float variance = Math.Max(0f, meanSq[i] - meanG[i] * meanG[i]);
            a[i] = (meanP[i] - meanG[i] * meanS[i]) / (variance + eps);
            b[i] = meanS[i] - a[i] * meanG[i];
        }
        return (Box(a, w, h, radius), Box(b, w, h, radius));
    }

    // Box mean with running sums, edge values repeated.
    private static float[] Box(float[] source, int w, int h, int radius)
    {
        var across = new float[source.Length];
        var result = new float[source.Length];
        float inv = 1f / (2 * radius + 1);
        Parallel.For(0, h, y =>
        {
            int start = y * w, last = start + w - 1;
            double sum = (double)source[start] * (radius + 1);
            for (int i = 1; i <= radius; i++) sum += source[Math.Min(start + i, last)];
            for (int i = start; i <= last; i++)
            {
                across[i] = (float)sum * inv;
                int enter = i + radius + 1, leave = i - radius;
                sum += (double)source[enter > last ? last : enter] - source[leave < start ? start : leave];
            }
        });

        // Vertical pass in strips, to keep memory access along rows.
        const int strip = 64;
        Parallel.For(0, (w + strip - 1) / strip, s =>
        {
            int from = s * strip, count = Math.Min(strip, w - from);
            var sum = new double[count];
            for (int x = 0; x < count; x++) sum[x] = (double)across[from + x] * (radius + 1);
            for (int i = 1; i <= radius; i++)
            {
                int row = Math.Min(i, h - 1) * w + from;
                for (int x = 0; x < count; x++) sum[x] += across[row + x];
            }
            for (int y = 0; y < h; y++)
            {
                int to = y * w + from;
                int enter = Math.Min(y + radius + 1, h - 1) * w + from, leave = Math.Max(y - radius, 0) * w + from;
                for (int x = 0; x < count; x++)
                {
                    result[to + x] = (float)sum[x] * inv;
                    sum[x] += (double)across[enter + x] - across[leave + x];
                }
            }
        });
        return result;
    }
}
