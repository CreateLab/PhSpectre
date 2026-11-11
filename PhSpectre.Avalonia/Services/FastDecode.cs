using System;
using System.IO;
using System.Threading.Tasks;
using global::Avalonia;
using global::Avalonia.Media.Imaging;
using global::Avalonia.Platform;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Advanced;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace PhSpectre.Avalonia.Services;

// Full-size decode for export. ImageSharp's JPEG decoder is very slow on phones (10 s for a 13 MP photo),
// while Avalonia's native Skia codec does the same in a fraction of that. Falls back to ImageSharp on any problem,
// so the result is always an upright (EXIF-applied) Rgb24 image.
internal static class FastDecode
{
    public static Image<Rgb24> LoadUpright(string path)
    {
        try
        {
            var fast = TryNative(path);
            if (fast != null) return fast;
        }
        catch (Exception ex)
        {
            AppLogger.LogError("Native decode failed, using the managed decoder", ex);
        }
        var img = Image.Load<Rgb24>(path);
        img.Mutate(x => x.AutoOrient());
        return img;
    }

    private static Image<Rgb24>? TryNative(string path)
    {
        var info = Image.Identify(path);
        ushort orientation = 1;
        if (info.Metadata.ExifProfile is { } exif && exif.TryGetValue(ExifTag.Orientation, out var o)) orientation = o.Value;

        using var stream = File.OpenRead(path);
        using var bmp = new Bitmap(stream);
        int w = bmp.PixelSize.Width, h = bmp.PixelSize.Height;
        if (w <= 0 || h <= 0) return null;

        // Copy the native pixels as they are; the channel order depends on the platform's Skia colour type.
        bool bgra = bmp.Format is { } f && f == PixelFormat.Bgra8888;
        if (!bgra && !(bmp.Format is { } f2 && f2 == PixelFormat.Rgba8888)) return null;
        int stride = w * 4;
        var buf = new byte[(long)stride * h];
        var handle = System.Runtime.InteropServices.GCHandle.Alloc(buf, System.Runtime.InteropServices.GCHandleType.Pinned);
        Image<Rgb24> img;
        try
        {
            bmp.CopyPixels(new PixelRect(0, 0, w, h), handle.AddrOfPinnedObject(), buf.Length, stride);
            img = new Image<Rgb24>(w, h);
            int ri = bgra ? 2 : 0, bi = bgra ? 0 : 2;
            Parallel.For(0, h, y =>
            {
                var row = img.Frames.RootFrame.DangerousGetPixelRowMemory(y).Span;
                int o = y * stride;
                for (int x = 0; x < row.Length; x++, o += 4)
                    row[x] = new Rgb24(buf[o + ri], buf[o + 1], buf[o + bi]);
            });
        }
        finally { handle.Free(); }
        {
            // Skia may or may not apply the EXIF turn itself; when the sides were swapped relative to the file, it did.
            bool swapped = (w != info.Width || h != info.Height) && w == info.Height && h == info.Width;
            if (!swapped && orientation > 1) ApplyOrientation(img, orientation);
            return img;
        }
    }

    private static void ApplyOrientation(Image<Rgb24> img, ushort o)
    {
        img.Mutate(x =>
        {
            switch (o)
            {
                case 2: x.Flip(FlipMode.Horizontal); break;
                case 3: x.Rotate(RotateMode.Rotate180); break;
                case 4: x.Flip(FlipMode.Vertical); break;
                case 5: x.Rotate(RotateMode.Rotate90).Flip(FlipMode.Horizontal); break;
                case 6: x.Rotate(RotateMode.Rotate90); break;
                case 7: x.Rotate(RotateMode.Rotate270).Flip(FlipMode.Horizontal); break;
                case 8: x.Rotate(RotateMode.Rotate270); break;
            }
        });
    }
}
