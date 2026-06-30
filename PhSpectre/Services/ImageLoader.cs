using System.IO;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace PhSpectre.Services;

public static class ImageLoader
{
    /// <summary>Loads a JPEG applying EXIF auto-orient, returns a JPEG stream.</summary>
    public static MemoryStream LoadAutoOriented(string path)
    {
        using var img = Image.Load<Rgb24>(path);
        img.Mutate(ctx => ctx.AutoOrient());
        var ms = new MemoryStream();
        img.SaveAsJpeg(ms);
        ms.Position = 0;
        return ms;
    }

    /// <summary>Loads a JPEG applying EXIF auto-orient and resizing to fit within maxW×maxH.</summary>
    public static MemoryStream LoadThumbnail(string path, int maxWidth, int maxHeight)
    {
        using var img = Image.Load<Rgb24>(path);
        img.Mutate(ctx => ctx.AutoOrient().Resize(new ResizeOptions
        {
            Mode = ResizeMode.Max,
            Size = new Size(maxWidth, maxHeight)
        }));
        var ms = new MemoryStream();
        img.SaveAsJpeg(ms);
        ms.Position = 0;
        return ms;
    }
}
