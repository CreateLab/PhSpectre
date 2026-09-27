using LibHeifSharp;
using PhSpectre.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.PixelFormats;

namespace PhSpectre.Heif;

// Decodes HEIF/HEIC pixels for Desktop/CLI/API via LibHeifSharp (native libheif) — Android
// deliberately never references this project; it already decodes HEIC natively through
// Android.Graphics.BitmapFactory (see PhSpectre.Android/NativeImageDecoder.cs), and no NuGet
// package here ships an Android native asset anyway, so pulling libheif into the APK would be
// a real build-it-yourself effort for no benefit.
//
// This decoder — not HeyRed.ImageSharp.Heif, the more obvious "just add a package" choice —
// exists because HeyRed.ImageSharp.Heif 2.1.3 was measured against the 4 real Fuji X-T5 HEIC
// spike files and found to silently corrupt 10-bit pixel data: full-frame sampled averages
// came back at ~1-2 out of 255 (i.e. almost entirely black) for every one of the 4 files.
// Decoding the *same* files directly through LibHeifSharp (the lower-level wrapper
// HeyRed.ImageSharp.Heif itself is built on) with HeifChroma.InterleavedRgb24 produced correct,
// sane exposure values (averages in the 70-120/255 range, max values reaching 255) — proving
// libheif's own 10-bit-to-8-bit conversion is correct and the bug is isolated to that adapter
// layer. Requesting InterleavedRgb24 directly, as below, lets libheif do that conversion
// itself rather than trusting a third-party adapter to do it.
public sealed class HeifPixelDecoder : ImageDecoder
{
    public static readonly HeifPixelDecoder Instance = new();

    protected override ImageInfo Identify(DecoderOptions options, Stream stream, CancellationToken cancellationToken)
    {
        using var context = new HeifContext(stream, leaveOpen: true);
        using var handle = context.GetPrimaryImageHandle();

        var metadata = new SixLabors.ImageSharp.Metadata.ImageMetadata();
        var exif = ReadExifRestoringPosition(stream);
        if (exif != null) metadata.ExifProfile = exif;

        return new ImageInfo(new PixelTypeInfo(24), new Size(handle.Width, handle.Height), metadata);
    }

    protected override Image<TPixel> Decode<TPixel>(DecoderOptions options, Stream stream, CancellationToken cancellationToken)
    {
        using var context = new HeifContext(stream, leaveOpen: true);
        using var handle = context.GetPrimaryImageHandle();
        using var heifImage = handle.Decode(HeifColorspace.Rgb, HeifChroma.InterleavedRgb24);

        var plane = heifImage.GetPlane(HeifChannel.Interleaved);
        int width = heifImage.Width, height = heifImage.Height, stride = plane.Stride;

        // Pack into a tight width*3-byte-per-row buffer — Image.LoadPixelData assumes no row
        // padding, but libheif's stride can be wider than width*3.
        byte[] packed = new byte[width * height * 3];
        for (int y = 0; y < height; y++)
            System.Runtime.InteropServices.Marshal.Copy(
                plane.Scan0 + y * stride, packed, y * width * 3, width * 3);

        using var rgbImage = Image.LoadPixelData<Rgb24>(packed, width, height);

        var exif = ReadExifRestoringPosition(stream);
        if (exif != null) rgbImage.Metadata.ExifProfile = exif;

        return rgbImage.CloneAs<TPixel>();
    }

    protected override Image Decode(DecoderOptions options, Stream stream, CancellationToken cancellationToken) =>
        Decode<Rgb24>(options, stream, cancellationToken);

    // HeifContainerReader reads from the current stream position forward; save/restore it so
    // this decoder leaves the stream exactly as it found it, matching every other ImageSharp
    // decoder's contract.
    private static SixLabors.ImageSharp.Metadata.Profiles.Exif.ExifProfile? ReadExifRestoringPosition(Stream stream)
    {
        if (!stream.CanSeek) return null;
        long entryPos = stream.Position;
        try
        {
            stream.Position = 0;
            return HeifContainerReader.TryReadExifProfile(stream);
        }
        finally
        {
            stream.Position = entryPos;
        }
    }
}
