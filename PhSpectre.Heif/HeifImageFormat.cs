using SixLabors.ImageSharp.Formats;

namespace PhSpectre.Heif;

// Minimal IImageFormat — read-only support (no encoder), so this only needs enough identity
// for ImageFormatsManager's decoder/detector registries to key off.
public sealed class HeifImageFormat : IImageFormat
{
    public static readonly HeifImageFormat Instance = new();

    private HeifImageFormat() { }

    public string Name => "HEIF";
    public string DefaultMimeType => "image/heif";
    public IEnumerable<string> MimeTypes => ["image/heif", "image/heic"];
    public IEnumerable<string> FileExtensions => ["heif", "heic", "hif"];
}
