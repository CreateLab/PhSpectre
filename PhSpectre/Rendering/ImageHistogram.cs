using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Advanced;
using SixLabors.ImageSharp.PixelFormats;

namespace PhSpectre.Rendering;

// Tone statistics of a picture: 256 bins per channel and for luma, from an even grid of pixels.
public sealed class ImageHistogram
{
    private const int MaxSamples = 500_000;

    private readonly int[] _red = new int[256], _green = new int[256], _blue = new int[256], _luma = new int[256];
    private int _clippedHighlights;

    private ImageHistogram() { }

    public IReadOnlyList<int> Red => _red;
    public IReadOnlyList<int> Green => _green;
    public IReadOnlyList<int> Blue => _blue;
    public IReadOnlyList<int> Luma => _luma;
    public int Total { get; private set; }

    // Share of pixels that are pure black.
    public double ShadowClipping => Total == 0 ? 0 : (double)_luma[0] / Total;
    // Share of pixels with at least one channel at the maximum.
    public double HighlightClipping => Total == 0 ? 0 : (double)_clippedHighlights / Total;

    // Encoded luma 0..1 below which the given share (0..1) of the picture lies.
    public float Percentile(double share)
    {
        double wanted = Math.Clamp(share, 0, 1) * Total;
        long seen = 0;
        for (int i = 0; i < 256; i++)
        {
            seen += _luma[i];
            if (seen > 0 && seen >= wanted) return i / 255f;
        }
        return Total == 0 ? 0f : 1f;
    }

    public static ImageHistogram Compute(Image<Rgb24> image)
    {
        ArgumentNullException.ThrowIfNull(image);
        var h = new ImageHistogram();
        int step = Math.Max(1, (int)Math.Ceiling(Math.Sqrt((double)image.Width * image.Height / MaxSamples)));
        for (int y = step / 2; y < image.Height; y += step)
        {
            var row = image.Frames.RootFrame.DangerousGetPixelRowMemory(y).Span;
            for (int x = step / 2; x < row.Length; x += step)
            {
                var p = row[x];
                h._red[p.R]++; h._green[p.G]++; h._blue[p.B]++;
                h._luma[(int)(ToneMath.Luma(new System.Numerics.Vector3(p.R, p.G, p.B)) + 0.5f)]++;
                if (p.R == 255 || p.G == 255 || p.B == 255) h._clippedHighlights++;
                h.Total++;
            }
        }
        return h;
    }
}
