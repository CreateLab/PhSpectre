using SixLabors.ImageSharp;

namespace PhSpectre.Heif;

// Called once at startup by PhSpectre.Desktop, PhSpectre.CLI and PhSpectre.API — never by
// PhSpectre.Android, which doesn't reference this project at all (see HeifPixelDecoder's
// header comment). After this call, Image.Load<Rgb24>/Image.Identify in
// PhSpectre.Services.ImageLoader work for .heic files through their existing code paths, no
// further changes needed there.
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
