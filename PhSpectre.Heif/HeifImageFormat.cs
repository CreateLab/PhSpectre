using SixLabors.ImageSharp.Formats;

namespace PhSpectre.Heif;

// Read only, so just enough identity for the decoder registry.
public sealed class HeifImageFormat : IImageFormat
{
    public static readonly HeifImageFormat Instance = new();

    private HeifImageFormat() { }

    public string Name => "HEIF";
    public string DefaultMimeType => "image/heif";
    public IEnumerable<string> MimeTypes => ["image/heif", "image/heic"];
    public IEnumerable<string> FileExtensions => ["heif", "heic", "hif"];
}
