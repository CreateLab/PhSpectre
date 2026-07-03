using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace PhSpectre.Services;

internal sealed class ImageSamplerService
{
    private const int SampleSize = 150;

    public async Task<(byte R, byte G, byte B)[]> SampleAsync(
        Stream imageStream,
        CancellationToken cancellationToken = default)
    {
        using var image = await Image.LoadAsync<Rgb24>(imageStream, cancellationToken);
        image.Mutate(ctx => ctx.AutoOrient().Resize(SampleSize, SampleSize));
        return ExtractPixels(image);
    }

    // Samples an already-decoded image directly — avoids re-decoding from disk when the
    // caller (e.g. the mobile pipeline) already has the working copy in memory. Clones
    // before resizing so the caller's image, still needed for rendering, is untouched.
    public Task<(byte R, byte G, byte B)[]> SampleAsync(
        Image<Rgb24> image,
        CancellationToken cancellationToken = default)
    {
        using var clone = image.Clone(ctx => ctx.AutoOrient().Resize(SampleSize, SampleSize));
        return Task.FromResult(ExtractPixels(clone));
    }

    private static (byte R, byte G, byte B)[] ExtractPixels(Image<Rgb24> image)
    {
        var pixels = new (byte R, byte G, byte B)[SampleSize * SampleSize];
        image.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (int x = 0; x < row.Length; x++)
                {
                    ref Rgb24 p = ref row[x];
                    pixels[y * SampleSize + x] = (p.R, p.G, p.B);
                }
            }
        });

        return pixels;
    }
}
