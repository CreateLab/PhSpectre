using System.IO;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace PhSpectre.Services;

public static class ImageLoader
{
    public static MemoryStream LoadAutoOriented(string path)
    {
        using var img = Image.Load<Rgb24>(path);
        img.Mutate(ctx => ctx.AutoOrient());
        var ms = new MemoryStream();
        img.SaveAsJpeg(ms);
        ms.Position = 0;
        return ms;
    }

    public static MemoryStream LoadThumbnail(string path, int maxWidth, int maxHeight)
    {
        // DecoderOptions.TargetSize tells the JPEG decoder to skip unnecessary DCT blocks
        // and produce a small image directly — much faster than full decode + resize.
        var opts = new DecoderOptions { TargetSize = new Size(maxWidth, maxHeight) };
        using var img = Image.Load<Rgb24>(opts, path);
        img.Mutate(ctx => ctx.AutoOrient().Resize(new ResizeOptions
        {
            Mode    = ResizeMode.Max,
            Size    = new Size(maxWidth, maxHeight),
            Sampler = KnownResamplers.Box
        }));
        var ms = new MemoryStream();
        img.SaveAsJpeg(ms, new JpegEncoder { Quality = 75 });
        ms.Position = 0;
        return ms;
    }

    // Decodes once and hands back the working copy in memory (caller owns disposal),
    // downscaled to fit maxDimension if the source is bigger, auto-oriented either way.
    // Modern phone cameras shoot 12-50MP; decoding/rendering/PNG-encoding that at full
    // resolution on mobile CPUs is what turns a sub-second desktop render into minutes,
    // so callers should reuse the returned image for preview/extraction/render instead
    // of re-decoding the file from disk again for each step.
    public static Image<Rgb24> LoadWorkingCopy(string path, int maxDimension)
    {
        var info = Image.Identify(path);
        bool needsDownscale = info != null && Math.Max(info.Width, info.Height) > maxDimension;

        // TargetSize lets the JPEG decoder skip unneeded DCT blocks during decode itself,
        // instead of decoding at full resolution and only then throwing pixels away.
        var opts = needsDownscale
            ? new DecoderOptions { TargetSize = new Size(maxDimension, maxDimension) }
            : new DecoderOptions();

        var img = Image.Load<Rgb24>(opts, path);
        img.Mutate(ctx =>
        {
            ctx.AutoOrient();
            if (needsDownscale)
                ctx.Resize(new ResizeOptions
                {
                    Mode    = ResizeMode.Max,
                    Size    = new Size(maxDimension, maxDimension),
                    Sampler = KnownResamplers.Lanczos3
                });
        });
        return img;
    }

    // Encodes an already-decoded image straight to a JPEG stream (e.g. for an Avalonia
    // Bitmap) without touching disk or re-decoding.
    public static MemoryStream ToJpegStream(Image<Rgb24> image, int quality = 90)
    {
        var ms = new MemoryStream();
        image.SaveAsJpeg(ms, new JpegEncoder { Quality = quality });
        ms.Position = 0;
        return ms;
    }
}
