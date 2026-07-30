using System.Linq;
using PhSpectre.Models;
using PhSpectre.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace PhSpectre;

/// <summary>
/// Controls how pixels are weighted when building the k-means clustering sample.
/// </summary>
public enum SamplingMode
{
    /// <summary>Saturated pixels are oversampled — vivid colors appear even if small in area.</summary>
    Vivid,
    /// <summary>Uniform sampling — palette reflects the most frequent colors by area.</summary>
    Standard,
    /// <summary>Stratified evenly across dark/mid/light thirds (shadows and highlights always appear), and within each third weighted toward saturation so a vivid minority hue isn't outvoted by a duller majority at the same lightness.</summary>
    Contrast
}

public sealed class PaletteExtractor
{
    private readonly ImageSamplerService _sampler = new();
    private readonly KMeansService _kMeans = new();

    public async Task<ColorPalette> ExtractAsync(
        Stream imageStream,
        int? colorCount = null,
        SamplingMode samplingMode = SamplingMode.Vivid,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(imageStream);
        if (colorCount.HasValue && (colorCount.Value < 1 || colorCount.Value > 32))
            throw new ArgumentOutOfRangeException(nameof(colorCount), "Color count must be between 1 and 32.");

        var pixels = await _sampler.SampleAsync(imageStream, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return _kMeans.Cluster(pixels, colorCount, samplingMode, cancellationToken);
    }

    // Extracts directly from an already-decoded image, skipping a redundant re-decode
    // when the caller already holds the working copy in memory.
    public async Task<ColorPalette> ExtractAsync(
        Image<Rgb24> image,
        int? colorCount = null,
        SamplingMode samplingMode = SamplingMode.Vivid,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (colorCount.HasValue && (colorCount.Value < 1 || colorCount.Value > 32))
            throw new ArgumentOutOfRangeException(nameof(colorCount), "Color count must be between 1 and 32.");

        var pixels = await _sampler.SampleAsync(image, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return _kMeans.Cluster(pixels, colorCount, samplingMode, cancellationToken);
    }

    // Extracts a single palette pooled across several photos (e.g. a collage), each
    // contributing pixels in proportion to its Weight (typically its displayed area share)
    // rather than all photos counting equally regardless of size — a small inset photo
    // shouldn't sway the palette as much as one that fills half the collage.
    public async Task<ColorPalette> ExtractWeightedAsync(
        IReadOnlyList<(Image<Rgb24> Image, double Weight)> photos,
        int? colorCount = null,
        SamplingMode samplingMode = SamplingMode.Vivid,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(photos);
        if (photos.Count == 0)
            throw new ArgumentException("At least one photo is required.", nameof(photos));
        if (colorCount.HasValue && (colorCount.Value < 1 || colorCount.Value > 32))
            throw new ArgumentOutOfRangeException(nameof(colorCount), "Color count must be between 1 and 32.");

        var perPhotoPixels = new (byte R, byte G, byte B)[photos.Count][];
        for (int i = 0; i < photos.Count; i++)
        {
            perPhotoPixels[i] = await _sampler.SampleAsync(photos[i].Image, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
        }

        // All samples come back the same fixed size (ImageSamplerService always resizes to
        // its sample grid before sampling), so that size doubles as the total pixel budget
        // to redistribute across photos by weight.
        int totalBudget = perPhotoPixels[0].Length;
        double totalWeight = photos.Sum(p => p.Weight);
        bool equalWeights = totalWeight <= 0;

        var pool = new List<(byte R, byte G, byte B)>(totalBudget);
        for (int i = 0; i < photos.Count; i++)
        {
            var pixels = perPhotoPixels[i];
            double normalizedWeight = equalWeights ? 1.0 / photos.Count : photos[i].Weight / totalWeight;
            int budget = Math.Max(1, (int)Math.Round(normalizedWeight * totalBudget));

            // Evenly spaced picks (not just the first N) keep the subsample spatially
            // representative of the whole photo instead of biased toward one corner.
            double step = (double)pixels.Length / budget;
            for (int j = 0; j < budget; j++)
                pool.Add(pixels[Math.Min(pixels.Length - 1, (int)(j * step))]);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return _kMeans.Cluster(pool.ToArray(), colorCount, samplingMode, cancellationToken);
    }
}
