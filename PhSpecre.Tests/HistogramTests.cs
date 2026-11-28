using PhSpectre.Rendering;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace PhSpecre.Tests;

public class HistogramTests
{
    // A grey ramp between two levels.
    private static Image<Rgb24> Ramp(int from, int to)
    {
        var img = new Image<Rgb24>(256, 64);
        for (int y = 0; y < img.Height; y++)
            for (int x = 0; x < img.Width; x++)
            {
                byte v = (byte)(from + (to - from) * x / 255);
                img[x, y] = new Rgb24(v, v, v);
            }
        return img;
    }

    private static PhotoAdjust Suggest(Image<Rgb24> img) => AutoTone.Suggest(ImageHistogram.Compute(img));

    [Fact]
    public void Counts_ChannelsAndLuma()
    {
        using var img = new Image<Rgb24>(20, 10, new Rgb24(255, 0, 0));
        var h = ImageHistogram.Compute(img);
        Assert.Equal(200, h.Total);
        Assert.Equal(200, h.Red[255]);
        Assert.Equal(200, h.Green[0]);
        Assert.Equal(200, h.Luma[54]);
        Assert.Equal(1.0, h.HighlightClipping);
        Assert.Equal(0.0, h.ShadowClipping);
    }

    [Fact]
    public void Percentile_FollowsTheRamp()
    {
        using var img = Ramp(0, 255);
        var h = ImageHistogram.Compute(img);
        Assert.InRange(h.Percentile(0.5), 0.48f, 0.52f);
        Assert.InRange(h.Percentile(0.05), 0.03f, 0.07f);
        Assert.Equal(0f, h.Percentile(0));
        Assert.Equal(1f, h.Percentile(1));
    }

    [Fact]
    public void LargePicture_IsSampled()
    {
        using var img = new Image<Rgb24>(1600, 1200, new Rgb24(10, 10, 10));
        var h = ImageHistogram.Compute(img);
        Assert.InRange(h.Total, 100_000, 500_000);
        Assert.Equal(h.Total, h.Luma[10]);
    }

    [Fact]
    public void Auto_BrightensADarkPhoto()
    {
        using var img = Ramp(0, 90);
        Assert.True(Suggest(img).Exposure > 0.5f);
    }

    [Fact]
    public void Auto_TonesDownABrightPhoto()
    {
        using var img = Ramp(150, 255);
        var a = Suggest(img);
        Assert.True(a.Exposure < 0f);
        Assert.True(a.Blacks < 0f);
    }

    [Fact]
    public void Auto_BarelyMovesAFullRangePhoto()
    {
        using var img = Ramp(0, 255);
        var a = Suggest(img);
        Assert.InRange(a.Exposure, -0.25f, 0.25f);
        Assert.Equal(0f, a.Contrast);
        Assert.InRange(a.Highlights, -10f, 0f);
        Assert.InRange(a.Shadows, 0f, 25f);
    }

    [Fact]
    public void Auto_AddsContrastToAFlatPhoto_AndWidensIt()
    {
        using var img = Ramp(100, 150);
        var a = Suggest(img);
        Assert.True(a.Contrast > 10f);
        using var result = a.Render(img);
        var before = ImageHistogram.Compute(img);
        var after = ImageHistogram.Compute(result);
        Assert.True(after.Percentile(0.95) - after.Percentile(0.05) > before.Percentile(0.95) - before.Percentile(0.05));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(128)]
    [InlineData(255)]
    public void Auto_LeavesAFlatColourAlone(int level)
    {
        using var img = new Image<Rgb24>(40, 40, new Rgb24((byte)level, (byte)level, (byte)level));
        Assert.True(Suggest(img).IsNoOp);
    }

    [Theory]
    [InlineData(0, 40)]
    [InlineData(0, 180)]
    [InlineData(60, 255)]
    [InlineData(230, 255)]
    public void Auto_SetsOnlyTheLightSliders_AndDoesNotBlowOut(int from, int to)
    {
        using var img = Ramp(from, to);
        var a = Suggest(img);
        Assert.Equal(a, a.Normalized());
        Assert.False(a.HasColor || a.HasEffects || a.HasGeometry);
        using var result = a.Render(img);
        Assert.True(ImageHistogram.Compute(result).HighlightClipping <= ImageHistogram.Compute(img).HighlightClipping + 0.02);
    }
}
