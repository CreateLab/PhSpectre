using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace PhSpectre.ColorGrading;

/// <summary>Renders a Look tile from a small photo, as BMP bytes.</summary>
public static class ThumbnailRenderer
{
    public static string CacheKey(string photoPath, string gradeId) => $"{photoPath}|{gradeId}";

    public static byte[] Render(Image<Rgb24> smallSource, object grade)
    {
        ArgumentNullException.ThrowIfNull(smallSource);
        var stack = new GradeStack();
        switch (grade)
        {
            case LrPreset p: stack.Preset = p; break;
            case CubeLut l: stack.Lut = l; break;
            default: throw new ArgumentException("Expected an LrPreset or a CubeLut.", nameof(grade));
        }
        using var img = smallSource.Clone();
        stack.Apply(img, detail: false);
        using var ms = new MemoryStream();
        img.SaveAsBmp(ms);
        return ms.ToArray();
    }
}
