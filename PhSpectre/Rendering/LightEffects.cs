using System.Numerics;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Advanced;
using SixLabors.ImageSharp.PixelFormats;

namespace PhSpectre.Rendering;

// Glow (bloom around bright areas) and halation (the red-orange fringe of film). Computed on a small map, so cost
// and look do not depend on photo size.
internal static class LightEffects
{
    private const int MapLongSide = 480;
    private static readonly Vector3 HalationTint = new(1f, 0.22f, 0.06f);

    // glow and halation are 0..1; the image is changed in place.
    public static void Apply(Image<Rgb24> img, float glow, float halation)
    {
        if (glow <= 0f && halation <= 0f) return;
        int w = img.Width, h = img.Height;
        float scale = Math.Min(1f, MapLongSide / (float)Math.Max(w, h));
        int mw = Math.Max(1, (int)MathF.Round(w * scale)), mh = Math.Max(1, (int)MathF.Round(h * scale));
        int side = Math.Max(mw, mh);

        var (light, hot) = Collect(img, mw, mh);

        Vector3[]? near = null, far = null, fringe = null;
        if (glow > 0f)
        {
            near = Blur(light, mw, mh, side * 0.008f);
            far = Blur(light, mw, mh, side * 0.035f);
        }
        if (halation > 0f) fringe = Blur(hot, mw, mh, side * 0.007f);

        var add = new Vector3[mw * mh];
        for (int i = 0; i < add.Length; i++)
        {
            var a = Vector3.Zero;
            if (near != null) a += (near[i] * 0.7f + far![i] * 1.1f) * glow;
            if (fringe != null) a += HalationTint * (fringe[i].X * 1.4f * halation);
            add[i] = Vector3.Min(a, Vector3.One);
        }
        Screen(img, add, mw, mh);
    }

    // `light` keeps the colour of bright and saturated areas, `hot` measures near-white light.
    private static (Vector3[] Light, Vector3[] Hot) Collect(Image<Rgb24> img, int mw, int mh)
    {
        int w = img.Width, h = img.Height;
        var light = new Vector3[mw * mh];
        var hot = new Vector3[mw * mh];
        var dec = ToneMath.Decode;
        Parallel.For(0, mh, my =>
        {
            int y0 = (int)((long)my * h / mh), y1 = Math.Max(y0 + 1, (int)((long)(my + 1) * h / mh));
            var count = new int[mw];
            for (int y = y0; y < y1; y++)
            {
                var row = img.Frames.RootFrame.DangerousGetPixelRowMemory(y).Span;
                for (int mx = 0; mx < mw; mx++)
                {
                    int x0 = (int)((long)mx * w / mw), x1 = Math.Max(x0 + 1, (int)((long)(mx + 1) * w / mw));
                    var sum = Vector3.Zero;
                    float heat = 0f;
                    for (int x = x0; x < x1; x++)
                    {
                        var p = row[x];
                        float r = p.R / 255f, g = p.G / 255f, b = p.B / 255f;
                        float max = MathF.Max(r, MathF.Max(g, b)), min = MathF.Min(r, MathF.Min(g, b));
                        float luma = 0.2126f * r + 0.7152f * g + 0.0722f * b;
                        // Saturated colours glow earlier, so overcast skies stay calm.
                        float weight = MathF.Max(ToneMath.Smooth(0.68f, 1f, luma), ToneMath.Smooth(0.45f, 0.85f, max * (max - min)));
                        if (weight <= 0f) continue;
                        sum += new Vector3(dec[p.R], dec[p.G], dec[p.B]) * weight;
                        heat += ToneMath.Smooth(0.75f, 1f, luma);
                    }
                    int i = my * mw + mx;
                    light[i] += sum;
                    hot[i].X += heat;
                    count[mx] += x1 - x0;
                }
            }
            for (int mx = 0; mx < mw; mx++)
            {
                light[my * mw + mx] /= count[mx];
                hot[my * mw + mx] /= count[mx];
            }
        });
        return (light, hot);
    }

    // Three box blurs approximate a Gaussian.
    private static Vector3[] Blur(Vector3[] source, int w, int h, float sigma)
    {
        int radius = Math.Max(1, (int)MathF.Round((MathF.Sqrt(4f * sigma * sigma + 1f) - 1f) / 2f));
        var a = (Vector3[])source.Clone();
        var b = new Vector3[a.Length];
        for (int pass = 0; pass < 3; pass++)
        {
            Parallel.For(0, h, y => Box(a, b, y * w, 1, w, radius));
            Parallel.For(0, w, x => Box(b, a, x, w, h, radius));
        }
        return a;
    }

    // Running average along one line; beyond the ends the edge value repeats.
    private static void Box(Vector3[] from, Vector3[] to, int start, int step, int length, int radius)
    {
        Vector3 At(int i) => from[start + Math.Clamp(i, 0, length - 1) * step];
        var sum = Vector3.Zero;
        for (int i = -radius; i <= radius; i++) sum += At(i);
        float inv = 1f / (2 * radius + 1);
        for (int i = 0; i < length; i++)
        {
            to[start + i * step] = Vector3.Max(sum * inv, Vector3.Zero);
            sum += At(i + radius + 1) - At(i - radius);
        }
    }

    private static void Screen(Image<Rgb24> img, Vector3[] add, int mw, int mh)
    {
        int w = img.Width, h = img.Height;
        var dec = ToneMath.Decode;
        Parallel.For(0, h, y =>
        {
            var row = img.Frames.RootFrame.DangerousGetPixelRowMemory(y).Span;
            float fy = Math.Clamp((y + 0.5f) * mh / h - 0.5f, 0f, mh - 1);
            int y0 = (int)fy, y1 = Math.Min(y0 + 1, mh - 1);
            float ty = fy - y0;
            for (int x = 0; x < row.Length; x++)
            {
                float fx = Math.Clamp((x + 0.5f) * mw / w - 0.5f, 0f, mw - 1);
                int x0 = (int)fx, x1 = Math.Min(x0 + 1, mw - 1);
                float tx = fx - x0;
                var a = Vector3.Lerp(Vector3.Lerp(add[y0 * mw + x0], add[y0 * mw + x1], tx),
                                     Vector3.Lerp(add[y1 * mw + x0], add[y1 * mw + x1], tx), ty);
                // Too faint to change a byte.
                if (a.X < 2e-4f && a.Y < 2e-4f && a.Z < 2e-4f) continue;
                ref var p = ref row[x];
                var c = new Vector3(dec[p.R], dec[p.G], dec[p.B]);
                c = Vector3.One - (Vector3.One - c) * (Vector3.One - a);
                p = new Rgb24(ToneMath.ToByte(ToneMath.Encode(c.X)), ToneMath.ToByte(ToneMath.Encode(c.Y)), ToneMath.ToByte(ToneMath.Encode(c.Z)));
            }
        });
    }
}
