using System;
using System.Runtime.InteropServices;
using Android.Graphics;
using PhSpectre.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace PhSpectre.Android;

/// <summary>
/// Decodes via Android's BitmapFactory with <c>InSampleSize</c> (hardware-backed codec) instead of ImageSharp's managed JPEG decoder
/// plus a later downscale; see <see cref="ImageLoader.FastWorkingCopyDecoder"/>. Registered against that hook at startup, like <see cref="GallerySaver"/>.
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

            // InSampleSize must be a power of two and the decoder returns floor(dimension / sampleSize), so pick the largest power of two
            // that leaves both dimensions >= maxDimension (LoadWorkingCopy does the exact trim later).
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

            // GetPixels(int[]) rather than CopyPixelsToBuffer(ByteBuffer): ByteBuffer.Wrap(managedArray) crosses JNI as a copy, so the native write never reached
            // the managed byte[] (silently, an all-zero black image). GetPixels is marshaled as an [Out] array.
            int w = bitmap.Width, h = bitmap.Height;
            var argb = new int[w * h];
            bitmap.GetPixels(argb, 0, w, 0, 0, w, h);

            // Each int is 0xAARRGGBB, which on little-endian (every Android device) is bytes B,G,R,A, ImageSharp's Bgra32 layout.
            // The zero-copy reinterpret lets LoadPixelData do one bulk copy instead of a per-pixel loop (a real cost at ~10M pixels).
            using var bgra = Image.LoadPixelData<Bgra32>(MemoryMarshal.AsBytes<int>(argb), w, h);

            // InSampleSize steps are powers of two, so the decode usually lands above maxDimension. Trimming here on the Bgra32 image, before CloneAs<Rgb24>
            // and the caller's AutoOrient, means they only touch the small image (~2.7MP instead of ~10MP for a 40MP source).
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
