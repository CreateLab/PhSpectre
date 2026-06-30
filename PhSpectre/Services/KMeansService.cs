using PhSpectre.Models;

namespace PhSpectre.Services;

internal sealed class KMeansService
{
    private const int MaxIterations = 100;
    private const int ElbowKMin = 3;
    private const int ElbowKMax = 8;

    public ColorPalette Cluster(
        (byte R, byte G, byte B)[] pixels,
        int? colorCount,
        SamplingMode mode = SamplingMode.Vivid,
        CancellationToken cancellationToken = default)
    {
        var hsl = Array.ConvertAll(pixels, p => RgbToHsl(p.R, p.G, p.B));
        // Weighted sample (same size via weighted-with-replacement) for centroid discovery.
        // Original hsl is used for final assignments and accurate percentages.
        var sample = mode == SamplingMode.Standard ? hsl : BuildWeightedSample(hsl, mode);
        int k = colorCount ?? FindElbowK(sample, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        float[][] centroids = RunKMeans(sample, k, cancellationToken);
        return BuildPalette(hsl, centroids);
    }

    // Weighted sampling WITH replacement at same size.
    // Vivid:    saturated pixels get higher weight (s×5 bonus).
    // Contrast: stratified by lightness — dark/mid/light each get 1/3 of the sample,
    //           guaranteeing highlights (white) and shadows appear even when rare.
    private static float[][] BuildWeightedSample(float[][] hsl, SamplingMode mode)
    {
        if (mode == SamplingMode.Contrast)
            return BuildStratifiedByLightness(hsl);

        var weights = new double[hsl.Length];
        double total = 0;
        for (int i = 0; i < hsl.Length; i++)
        {
            float s = hsl[i][1];
            weights[i] = mode == SamplingMode.Vivid ? 1.0 + s * 5.0 : 1.0;
            total += weights[i];
        }

        var cdf = new double[hsl.Length];
        double cumulative = 0;
        for (int i = 0; i < hsl.Length; i++)
        {
            cumulative += weights[i] / total;
            cdf[i] = cumulative;
        }

        var rng = new Random(42);
        var sample = new float[hsl.Length][];
        for (int i = 0; i < hsl.Length; i++)
        {
            double r = rng.NextDouble();
            int idx = Array.BinarySearch(cdf, r);
            if (idx < 0) idx = ~idx;
            sample[i] = hsl[Math.Clamp(idx, 0, hsl.Length - 1)];
        }
        return sample;
    }

    // Splits pixels into 3 lightness bands [0, 0.33) / [0.33, 0.67) / [0.67, 1]
    // and draws equal quotas from each. Guarantees highlights (e.g. white petals
    // in a green photo) get 1/3 of k-means input regardless of pixel count.
    private static float[][] BuildStratifiedByLightness(float[][] hsl)
    {
        var bands = new List<int>[3];
        for (int b = 0; b < 3; b++) bands[b] = new List<int>();

        for (int i = 0; i < hsl.Length; i++)
        {
            float l = hsl[i][2];
            bands[l < 0.33f ? 0 : l < 0.67f ? 1 : 2].Add(i);
        }

        var rng    = new Random(42);
        var sample = new float[hsl.Length][];
        int si     = 0;

        for (int b = 0; b < 3; b++)
        {
            if (bands[b].Count == 0) continue;
            int quota = hsl.Length / 3;
            for (int i = 0; i < quota && si < hsl.Length; i++)
                sample[si++] = hsl[bands[b][rng.Next(bands[b].Count)]];
        }

        // Fill rounding remainder uniformly
        while (si < hsl.Length)
            sample[si++] = hsl[rng.Next(hsl.Length)];

        return sample;
    }

    private static float[][] RunKMeans(float[][] pixels, int k, CancellationToken cancellationToken = default)
    {
        var rng = new Random(42);
        float[][] centroids = InitializeCentroids(pixels, k, rng);
        int[] assignments = new int[pixels.Length];

        for (int iter = 0; iter < MaxIterations; iter++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool changed = AssignPixels(pixels, centroids, assignments);
            if (!changed) break;
            RecomputeCentroids(pixels, assignments, centroids, k);
        }

        return centroids;
    }

    private static float[][] InitializeCentroids(float[][] pixels, int k, Random rng)
    {
        var centroids = new float[k][];
        int first = rng.Next(pixels.Length);
        centroids[0] = [pixels[first][0], pixels[first][1], pixels[first][2]];

        var distances = new double[pixels.Length];

        for (int ci = 1; ci < k; ci++)
        {
            double total = 0;
            for (int i = 0; i < pixels.Length; i++)
            {
                double minDist = double.MaxValue;
                for (int j = 0; j < ci; j++)
                {
                    double d = SquaredDistHsl(pixels[i], centroids[j]);
                    if (d < minDist) minDist = d;
                }
                distances[i] = minDist;
                total += minDist;
            }

            var cdf = new double[pixels.Length];
            double cumulative = 0;
            for (int i = 0; i < pixels.Length; i++)
            {
                cumulative += distances[i];
                cdf[i] = cumulative;
            }

            double target = rng.NextDouble() * total;
            int idx = Array.BinarySearch(cdf, target);
            if (idx < 0) idx = ~idx;
            if (idx >= pixels.Length) idx = pixels.Length - 1;

            centroids[ci] = [pixels[idx][0], pixels[idx][1], pixels[idx][2]];
        }

        return centroids;
    }

    private static bool AssignPixels(float[][] pixels, float[][] centroids, int[] assignments)
    {
        bool changed = false;
        for (int i = 0; i < pixels.Length; i++)
        {
            int best = 0;
            double bestDist = double.MaxValue;
            for (int j = 0; j < centroids.Length; j++)
            {
                double d = SquaredDistHsl(pixels[i], centroids[j]);
                if (d < bestDist) { bestDist = d; best = j; }
            }
            if (assignments[i] != best) { assignments[i] = best; changed = true; }
        }
        return changed;
    }

    private static void RecomputeCentroids(float[][] pixels, int[] assignments, float[][] centroids, int k)
    {
        var sinH   = new double[k];
        var cosH   = new double[k];
        var sumS   = new double[k];
        var sumL   = new double[k];
        var counts = new int[k];

        for (int i = 0; i < pixels.Length; i++)
        {
            int j = assignments[i];
            double rad = pixels[i][0] * Math.PI / 180.0;
            sinH[j] += Math.Sin(rad);
            cosH[j] += Math.Cos(rad);
            sumS[j] += pixels[i][1];
            sumL[j] += pixels[i][2];
            counts[j]++;
        }

        for (int j = 0; j < k; j++)
        {
            if (counts[j] == 0) continue; // keep previous centroid on empty cluster
            float meanH = (float)(Math.Atan2(sinH[j] / counts[j], cosH[j] / counts[j]) * 180.0 / Math.PI);
            if (meanH < 0) meanH += 360f;
            centroids[j][0] = meanH;
            centroids[j][1] = (float)(sumS[j] / counts[j]);
            centroids[j][2] = (float)(sumL[j] / counts[j]);
        }
    }

    private int FindElbowK(float[][] pixels, CancellationToken cancellationToken = default)
    {
        int range = ElbowKMax - ElbowKMin + 1;
        var wcss = new double[range];

        for (int ki = 0; ki < range; ki++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int k = ElbowKMin + ki;
            float[][] centroids = RunKMeans(pixels, k, cancellationToken);
            int[] assignments = new int[pixels.Length];
            AssignPixels(pixels, centroids, assignments);
            wcss[ki] = ComputeWcss(pixels, assignments, centroids);
        }

        int d2Len = range - 2;
        if (d2Len < 1) return ElbowKMin;

        var d1 = new double[range - 1];
        for (int i = 0; i < d1.Length; i++)
            d1[i] = wcss[i + 1] - wcss[i];

        var d2 = new double[d2Len];
        for (int i = 0; i < d2Len; i++)
            d2[i] = d1[i + 1] - d1[i];

        int maxIdx = 0;
        double maxVal = Math.Abs(d2[0]);
        for (int i = 1; i < d2Len; i++)
        {
            double val = Math.Abs(d2[i]);
            if (val > maxVal) { maxVal = val; maxIdx = i; }
        }

        return ElbowKMin + maxIdx + 1;
    }

    private static double ComputeWcss(float[][] pixels, int[] assignments, float[][] centroids)
    {
        double wcss = 0;
        for (int i = 0; i < pixels.Length; i++)
            wcss += SquaredDistHsl(pixels[i], centroids[assignments[i]]);
        return wcss;
    }

    private static ColorPalette BuildPalette(float[][] hslPixels, float[][] centroids)
    {
        int k = centroids.Length;
        int[] assignments = new int[hslPixels.Length];
        AssignPixels(hslPixels, centroids, assignments);

        var counts   = new int[k];
        var bestDist = new double[k];
        var medoids  = new float[k][];
        Array.Fill(bestDist, double.MaxValue);
        for (int j = 0; j < k; j++) medoids[j] = centroids[j];

        for (int i = 0; i < hslPixels.Length; i++)
        {
            int j = assignments[i];
            counts[j]++;
            double d = SquaredDistHsl(hslPixels[i], centroids[j]);
            if (d < bestDist[j]) { bestDist[j] = d; medoids[j] = hslPixels[i]; }
        }

        var indices = Enumerable.Range(0, k).OrderByDescending(j => counts[j]).ToArray();
        var swatches = new ColorSwatch[k];
        for (int rank = 0; rank < k; rank++)
        {
            int j = indices[rank];
            var (r, g, b) = HslToRgb(medoids[j][0], medoids[j][1], medoids[j][2]);
            string hex = $"#{r:X2}{g:X2}{b:X2}";
            float pct = (float)counts[j] / hslPixels.Length;
            swatches[rank] = new ColorSwatch(hex, (r, g, b), pct);
        }

        return new ColorPalette(swatches);
    }

    // HSL distance: circular hue, weighted H×2, S×1, L×0.5
    private static double SquaredDistHsl(float[] a, float[] b)
    {
        float dh = Math.Abs(a[0] - b[0]);
        if (dh > 180f) dh = 360f - dh;
        double dhn = dh / 360.0 * 2.0;
        double dsn = a[1] - b[1];
        double dln = (a[2] - b[2]) * 0.5;
        return dhn * dhn + dsn * dsn + dln * dln;
    }

    // RGB [0,255] → float[3] {H∈[0,360), S∈[0,1], L∈[0,1]}
    private static float[] RgbToHsl(byte r, byte g, byte b)
    {
        float rf = r / 255f, gf = g / 255f, bf = b / 255f;
        float max   = Math.Max(rf, Math.Max(gf, bf));
        float min   = Math.Min(rf, Math.Min(gf, bf));
        float delta = max - min;
        float l     = (max + min) / 2f;
        float s     = delta < 1e-6f ? 0f : delta / (1f - Math.Abs(2f * l - 1f));

        float h = 0f;
        if (delta > 1e-6f)
        {
            if (max == rf)
                h = 60f * (((gf - bf) / delta) % 6f);
            else if (max == gf)
                h = 60f * ((bf - rf) / delta + 2f);
            else
                h = 60f * ((rf - gf) / delta + 4f);
        }
        if (h < 0) h += 360f;

        return [h, s, l];
    }

    // HSL → RGB [0,255]
    private static (byte R, byte G, byte B) HslToRgb(float h, float s, float l)
    {
        if (s < 1e-6f)
        {
            byte gray = (byte)Math.Round(Math.Clamp(l * 255f, 0f, 255f));
            return (gray, gray, gray);
        }

        float c = (1f - Math.Abs(2f * l - 1f)) * s;
        float x = c * (1f - Math.Abs((h / 60f) % 2f - 1f));
        float m = l - c / 2f;

        float rf, gf, bf;
        switch ((int)(h / 60f) % 6)
        {
            case 0:  rf = c; gf = x; bf = 0; break;
            case 1:  rf = x; gf = c; bf = 0; break;
            case 2:  rf = 0; gf = c; bf = x; break;
            case 3:  rf = 0; gf = x; bf = c; break;
            case 4:  rf = x; gf = 0; bf = c; break;
            default: rf = c; gf = 0; bf = x; break;
        }

        return (
            (byte)Math.Round(Math.Clamp((rf + m) * 255f, 0f, 255f)),
            (byte)Math.Round(Math.Clamp((gf + m) * 255f, 0f, 255f)),
            (byte)Math.Round(Math.Clamp((bf + m) * 255f, 0f, 255f))
        );
    }
}
