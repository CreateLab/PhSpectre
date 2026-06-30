using PhSpectre.Models;
using PhSpectre.Services;

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
    /// <summary>Pixels are weighted by colorfulness (S × mid-lightness) — maximises perceptual vividness.</summary>
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
}
