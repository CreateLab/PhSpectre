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
}
