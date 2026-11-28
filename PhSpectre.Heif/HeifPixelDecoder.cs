using LibHeifSharp;
using PhSpectre.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.PixelFormats;

namespace PhSpectre.Heif;

// HEIF decoding for Desktop, CLI and API. LibHeifSharp is used directly because HeyRed.ImageSharp.Heif 2.1.3 turned
// 10-bit files from an X-T5 almost black; asking libheif for InterleavedRgb24 converts correctly.
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

        // libheif rows may be padded; LoadPixelData expects tight rows.
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

    // Leave the stream where it was.
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
