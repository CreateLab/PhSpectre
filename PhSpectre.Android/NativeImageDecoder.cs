using System;
using System.Runtime.InteropServices;
using Android.Graphics;
using PhSpectre.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace PhSpectre.Android;

/// <summary>
/// Android's own BitmapFactory decodes a downsampled image straight off the OS's
/// hardware-backed codec via <c>InSampleSize</c>, instead of decoding at full resolution
/// through ImageSharp's pure-managed JPEG decoder and only downscaling afterwards (see
/// <see cref="ImageLoader.FastWorkingCopyDecoder"/>'s own comment for the ~13s-per-photo
/// measurement that motivated this). Registered against that hook at startup, same pattern
/// as <see cref="GallerySaver"/>.
/// </summary>
internal static class NativeImageDecoder
{
    public static Image<Rgb24>? TryDecodeWorkingCopy(string path, int maxDimension)
    {
        try
        {
            var bounds = new BitmapFactory.Options { InJustDecodeBounds = true };
            BitmapFactory.DecodeFile(path, bounds);
            int srcW = bounds.OutWidth;
            int srcH = bounds.OutHeight;
            if (srcW <= 0 || srcH <= 0) return null;

            // Standard Android downsampling recipe: InSampleSize must be a power of two: the
            // decoder returns floor(dimension / sampleSize), so this picks the largest power
            // of two that still leaves both dimensions >= maxDimension (LoadWorkingCopy's own
            // managed-path Resize below does the final exact-size trim).
            int sampleSize = 1;
            while (srcW / (sampleSize * 2) >= maxDimension && srcH / (sampleSize * 2) >= maxDimension)
                sampleSize *= 2;

            var decodeOptions = new BitmapFactory.Options
            {
                InSampleSize = sampleSize,
                InPreferredConfig = Bitmap.Config.Argb8888,
            };
            using var bitmap = BitmapFactory.DecodeFile(path, decodeOptions);
            if (bitmap == null) return null;

            // GetPixels(int[]) — not CopyPixelsToBuffer(ByteBuffer) — on purpose: a
            // ByteBuffer.Wrap(managedArray) crosses the JNI boundary as a *copy*, so the
            // native-side CopyPixelsToBuffer write never made it back into the managed byte[]
            // (silently — no exception, just an all-zero buffer, i.e. a black image). GetPixels
            // writes each 0xAARRGGBB pixel directly into a managed int[] and is marshaled
            // properly as an [Out] array, which is the reliable way to pull Bitmap pixels back
            // into managed memory.
            int w = bitmap.Width, h = bitmap.Height;
            var argb = new int[w * h];
            bitmap.GetPixels(argb, 0, w, 0, 0, w, h);

            // Each int is 0xAARRGGBB; on a little-endian CPU (every Android device) that's
            // laid out in memory as bytes B,G,R,A — exactly ImageSharp's Bgra32 layout. Casting
            // the int[] to a byte span this way is a zero-copy reinterpret, so LoadPixelData
            // does one bulk copy into the new image instead of a manual per-pixel unpack loop
            // (which, at ~10M+ pixels for a modern phone photo, was itself a real chunk of the
            // "decode+downscale" time on-device).
            using var bgra = Image.LoadPixelData<Bgra32>(MemoryMarshal.AsBytes<int>(argb), w, h);

            // InSampleSize only supports power-of-two steps, so the decode above still lands
            // above maxDimension more often than not (ImageLoader.LoadWorkingCopy trims to the
            // exact cap regardless, as a safety net) — but doing that trim here, on the Bgra32
            // image, before CloneAs<Rgb24> below, means the format conversion (and the caller's
            // later AutoOrient, which now runs on whatever this method returns) only ever
            // touches the already-small working-size image instead of the full native-decoded
            // one. For a 40MP source that's the difference between converting/orienting ~10MP
            // and ~2.7MP.
            if (Math.Max(w, h) > maxDimension)
                bgra.Mutate(ctx => ctx.Resize(new ResizeOptions
                {
                    Mode    = ResizeMode.Max,
                    Size    = new Size(maxDimension, maxDimension),
                    Sampler = KnownResamplers.Box
                }));

            return bgra.CloneAs<Rgb24>();
        }
        catch
        {
            return null; // best-effort — ImageLoader.LoadWorkingCopy falls back to its managed decode
        }
    }
}
