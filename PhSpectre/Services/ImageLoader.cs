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
    // Platform hook: fast native decode of (path, maxDimension) to a downscaled Image<Rgb24>, without EXIF orientation (LoadWorkingCopy applies it).
    // Set by PhSpectre.Android at startup; null elsewhere, where the managed decode below is used.
    // Why: ImageSharp's JPEG decoder is managed and unaccelerated; a ~40MP phone photo took ~13s on an ARM CPU. Android's BitmapFactory
    // decodes downsampled via inSampleSize with the hardware-backed codec.
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

    // Decodes once and returns the working copy (caller owns disposal), downscaled to maxDimension if larger and auto-oriented.
    // Full-resolution processing on mobile CPUs turns a sub-second render into minutes, so callers reuse this image for preview, extraction and render.
    public static Image<Rgb24> LoadWorkingCopy(string path, int maxDimension)
    {
        Image<Rgb24> img;
        bool usedFastPath = false;

        if (FastWorkingCopyDecoder?.Invoke(path, maxDimension) is { } fast)
        {
            // The native decoder returns raw pixels with no EXIF, so attach the file's profile (a header-only read) and let AutoOrient rotate as the managed path would.
            fast.Metadata.ExifProfile = ReadExifProfileForOrientation(path);
            fast.Mutate(ctx => ctx.AutoOrient());
            img = fast;
            usedFastPath = true;
        }
        else
        {
            var info = Image.Identify(path);
            bool needsDownscale = info != null && Math.Max(info.Width, info.Height) > maxDimension;

            // TargetSize skips unneeded DCT blocks during the decode itself.
            var opts = needsDownscale
                ? new DecoderOptions { TargetSize = new Size(maxDimension, maxDimension) }
                : new DecoderOptions();

            img = Image.Load<Rgb24>(opts, path);
            img.Mutate(ctx => ctx.AutoOrient());
        }

        // Both paths only get close to maxDimension: TargetSize is a hint, and the native decoder only supports power-of-two InSampleSize steps
        // (7728x5152 capped at 2000 comes back 3864x2576). This final Resize guarantees the cap, so k-means and the render don't run on more pixels than chosen.
        if (Math.Max(img.Width, img.Height) > maxDimension)
            img.Mutate(ctx => ctx.Resize(new ResizeOptions
            {
                Mode    = ResizeMode.Max,
                Size    = new Size(maxDimension, maxDimension),
                // The native overshoot is under 2x per dimension, where an area-average filter looks like Lanczos3 but is faster (this is the mobile hot path).
                // The managed fallback below can downscale by a large factor, where Lanczos3 matters.
                Sampler = usedFastPath ? KnownResamplers.Box : KnownResamplers.Lanczos3
            }));

        return img;
    }

    // Only attaches EXIF (for AutoOrient) to pixels the native decoder already produced. HEIC is sniffed by magic bytes and read via HeifContainerReader:
    // Android, the only platform with FastWorkingCopyDecoder, has no ImageSharp HEIF decoder (see PhSpectre.Heif), so Image.Identify would throw and discard those pixels.
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
