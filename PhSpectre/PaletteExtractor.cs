using PhSpectre.Models;
using PhSpectre.Services;

namespace PhSpectre;

public sealed class PaletteExtractor
{
    private readonly ImageSamplerService _sampler = new();
    private readonly KMeansService _kMeans = new();

    public async Task<ColorPalette> ExtractAsync(Stream imageStream, int? colorCount = null)
    {
        ArgumentNullException.ThrowIfNull(imageStream);
        if (colorCount.HasValue && (colorCount.Value < 1 || colorCount.Value > 32))
            throw new ArgumentOutOfRangeException(nameof(colorCount), "Color count must be between 1 and 32.");

        var pixels = await _sampler.SampleAsync(imageStream);
        return _kMeans.Cluster(pixels, colorCount);
    }
}
