using System.IO;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace PhSpectre.Services;

public static class ImageLoader
{
    // Platform hook: a native downscaled decode without EXIF rotation (Android sets it). ImageSharp took about 13 s
    // for 40 MP on a phone.
    public static Func<string, int, Image<Rgb24>?>? FastWorkingCopyDecoder { get; set; }

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
        // DecoderOptions.TargetSize lets the JPEG decoder skip DCT blocks instead of decoding in full and resizing.
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

    // Decodes the working copy: downscaled to maxDimension and auto-oriented.
    public static Image<Rgb24> LoadWorkingCopy(string path, int maxDimension)
    {
        Image<Rgb24> img;
        bool usedFastPath = false;

        if (FastWorkingCopyDecoder?.Invoke(path, maxDimension) is { } fast)
        {
            // Native pixels carry no EXIF: attach the profile so AutoOrient works.
            fast.Metadata.ExifProfile = ReadExifProfileForOrientation(path);
            fast.Mutate(ctx => ctx.AutoOrient());
            img = fast;
            usedFastPath = true;
        }
        else
        {
            var info = Image.Identify(path);
            bool needsDownscale = info != null && Math.Max(info.Width, info.Height) > maxDimension;

            var opts = needsDownscale
                ? new DecoderOptions { TargetSize = new Size(maxDimension, maxDimension) }
                : new DecoderOptions();

            img = Image.Load<Rgb24>(opts, path);
            img.Mutate(ctx => ctx.AutoOrient());
        }

        // Both decoders only get near maxDimension; this resize guarantees it.
        if (Math.Max(img.Width, img.Height) > maxDimension)
            img.Mutate(ctx => ctx.Resize(new ResizeOptions
            {
                Mode    = ResizeMode.Max,
                Size    = new Size(maxDimension, maxDimension),
                // Area average is enough for the small native overshoot; the managed path may shrink a lot and
                // needs Lanczos3.
                Sampler = usedFastPath ? KnownResamplers.Box : KnownResamplers.Lanczos3
            }));

        return img;
    }

    // HEIC is read through HeifContainerReader: Android has no ImageSharp HEIF decoder.
    private static ExifProfile? ReadExifProfileForOrientation(string path) =>
        HeifContainerReader.IsHeifFile(path)
            ? HeifContainerReader.TryReadExifProfile(path)
            : Image.Identify(path)?.Metadata.ExifProfile;

    // Encodes a decoded image straight to a JPEG stream (e.g. for an Avalonia Bitmap) without touching disk.
    public static MemoryStream ToJpegStream(Image<Rgb24> image, int quality = 90)
    {
        var ms = new MemoryStream();
        image.SaveAsJpeg(ms, new JpegEncoder { Quality = quality });
        ms.Position = 0;
        return ms;
    }
}
