using System.Diagnostics.CodeAnalysis;
using PhSpectre.Services;
using SixLabors.ImageSharp.Formats;

namespace PhSpectre.Heif;

public sealed class HeifFormatDetector : IImageFormatDetector
{
    public int HeaderSize => 12;

    public bool TryDetectFormat(ReadOnlySpan<byte> header, [NotNullWhen(true)] out IImageFormat? format)
    {
        if (HeifContainerReader.IsHeif(header))
        {
            format = HeifImageFormat.Instance;
            return true;
        }
        format = null;
        return false;
    }
}
