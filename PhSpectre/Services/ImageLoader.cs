using System.IO;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace PhSpectre.Services;

public static class ImageLoader
{
    // Platform hook: a fast, natively-decoded (path, maxDimension) -> downscaled Image<Rgb24>
    // (no EXIF orientation applied — LoadWorkingCopy applies that itself). Set by
    // PhSpectre.Android's Application.CustomizeAppBuilder at startup, same pattern as
    // FileDialogService.GallerySaver. Null on every other platform, where LoadWorkingCopy
    // falls back to the plain managed decode below unchanged.
    //
    // Why this exists: ImageSharp's JPEG decoder is pure managed code with no hardware
    // acceleration. On desktop that's fast enough not to matter, but decoding a modern phone
    // photo (e.g. 7728x5152 — ~40MP) through it on an ARM mobile CPU measured at ~13 seconds
    // in practice, dwarfing every other stage of the render pipeline combined regardless of
    // export mode. Android's own BitmapFactory decodes progressively downsampled via
    // inSampleSize using the OS's hardware-backed codec — the standard, correct way to load a
    // large photo on Android, and dramatically faster for exactly this reason.
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
        Image<Rgb24> img;
        bool usedFastPath = false;

        if (FastWorkingCopyDecoder?.Invoke(path, maxDimension) is { } fast)
        {
            // The native decoder above only returns raw pixels — no EXIF was read through
            // it, so it has no orientation to apply. Attach the file's own profile (a cheap
            // header-only read, not a re-decode) and let ImageSharp's own AutoOrient do the
            // same rotation/flip it would have applied via the managed path below.
            fast.Metadata.ExifProfile = Image.Identify(path)?.Metadata.ExifProfile;
            fast.Mutate(ctx => ctx.AutoOrient());
            img = fast;
            usedFastPath = true;
        }
        else
        {
            var info = Image.Identify(path);
            bool needsDownscale = info != null && Math.Max(info.Width, info.Height) > maxDimension;

            // TargetSize lets the JPEG decoder skip unneeded DCT blocks during decode itself,
            // instead of decoding at full resolution and only then throwing pixels away.
            var opts = needsDownscale
                ? new DecoderOptions { TargetSize = new Size(maxDimension, maxDimension) }
                : new DecoderOptions();

            img = Image.Load<Rgb24>(opts, path);
            img.Mutate(ctx => ctx.AutoOrient());
        }

        // Both paths above only get *close* to maxDimension, not exactly at or under it:
        // TargetSize is a decoder hint (the actual output can still land above the cap
        // depending on the format's supported scale steps), and the native Android decoder
        // above only supports power-of-two InSampleSize steps, which can just as easily
        // overshoot as land exactly on target (e.g. a 7728x5152 source capped at 2000 comes
        // back 3864x2576 — the nearest power-of-two step, not 2000). This final Resize is
        // what actually guarantees the cap: without it, k-means sampling and the render+encode
        // that follow silently run against however many pixels the decoder happened to land
        // on instead of the working-size the user picked, undoing most of the point of
        // capping it in the first place.
        if (Math.Max(img.Width, img.Height) > maxDimension)
            img.Mutate(ctx => ctx.Resize(new ResizeOptions
            {
                Mode    = ResizeMode.Max,
                Size    = new Size(maxDimension, maxDimension),
                // The native path's overshoot is bounded under 2x per dimension (the next
                // InSampleSize step down would have gone under maxDimension) — at that small a
                // ratio, a cheap area-average filter is visually indistinguishable from Lanczos3
                // but meaningfully faster, and this trim is now squarely in the hot path on
                // mobile. The managed fallback below can still be downscaling by a much larger,
                // unbounded factor, where Lanczos3's quality actually matters.
                Sampler = usedFastPath ? KnownResamplers.Box : KnownResamplers.Lanczos3
            }));

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
