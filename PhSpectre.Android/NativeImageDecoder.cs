using System;
using System.Runtime.InteropServices;
using Android.Graphics;
using PhSpectre.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace PhSpectre.Android;

/// <summary>Decodes with BitmapFactory and <c>InSampleSize</c>, much faster than ImageSharp plus a downscale.</summary>
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

            // InSampleSize is a power of two: take the largest that keeps both sides >= maxDimension; the exact
            // trim comes later.
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

            // GetPixels, not CopyPixelsToBuffer: a wrapped managed array is copied across JNI and stays black.
            int w = bitmap.Width, h = bitmap.Height;
            var argb = new int[w * h];
            bitmap.GetPixels(argb, 0, w, 0, 0, w, h);

            // 0xAARRGGBB ints are B,G,R,A bytes on little-endian, which is ImageSharp's Bgra32.
            using var bgra = Image.LoadPixelData<Bgra32>(MemoryMarshal.AsBytes<int>(argb), w, h);

            // Trim before converting and rotating, so those work on the small image.
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
            return null; // Best effort: LoadWorkingCopy falls back to the managed decode.
        }
    }
}
