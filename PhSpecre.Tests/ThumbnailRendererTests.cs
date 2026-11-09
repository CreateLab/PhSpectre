using PhSpectre.ColorGrading;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace PhSpecre.Tests;

public class ThumbnailRendererTests
{
    private static string InvertCube()
    {
        var sb = new System.Text.StringBuilder("LUT_3D_SIZE 2\n");
        for (int b = 0; b < 2; b++) for (int g = 0; g < 2; g++) for (int r = 0; r < 2; r++)
            sb.Append($"{1 - r} {1 - g} {1 - b}\n");
        return sb.ToString();
    }

    [Fact]
    public void Render_GradesCopy_AndLeavesSourceUntouched()
    {
        using var src = new Image<Rgb24>(8, 4, new Rgb24(0, 0, 0));
        var bytes = ThumbnailRenderer.Render(src, CubeLut.Parse(InvertCube()));
        using var outImg = Image.Load<Rgb24>(bytes);
        Assert.Equal(8, outImg.Width);
        Assert.Equal(4, outImg.Height);
        Assert.True(outImg[0, 0].R > 240);          // inverted
        Assert.Equal(0, src[0, 0].R);                // source unchanged
    }

    [Fact] public void Render_RejectsUnknownGrade()
    {
        using var src = new Image<Rgb24>(2, 2);
        Assert.Throws<ArgumentException>(() => ThumbnailRenderer.Render(src, "x"));
    }

    [Fact] public void CacheKey_DiffersPerPhotoAndLook() =>
        Assert.NotEqual(ThumbnailRenderer.CacheKey("a", "1"), ThumbnailRenderer.CacheKey("a", "2"));
}
