using SixLabors.ImageSharp;

namespace PhSpectre.Heif;

// Called at startup by Desktop, CLI and API; afterwards ImageLoader handles .heic.
public static class HeifDesktopSupport
{
    public static void Register(Configuration? configuration = null)
    {
        var config = configuration ?? Configuration.Default;
        config.ImageFormatsManager.AddImageFormat(HeifImageFormat.Instance);
        config.ImageFormatsManager.AddImageFormatDetector(new HeifFormatDetector());
        config.ImageFormatsManager.SetDecoder(HeifImageFormat.Instance, HeifPixelDecoder.Instance);
    }
}
