using PhSpectre.Rendering;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace PhSpecre.Tests;

public class PhotoFrameTests
{
    private static Image<Rgb24> Photo(int w, int h, Rgb24 c) => new(w, h, c);

    [Fact]
    public void Layout_BorderIsRelativeToShortSide()
    {
        var f = new PhotoFrame { ThicknessPercent = 10 };
        var (w, h, x, y) = f.Layout(400, 200);
        Assert.Equal((440, 240, 20, 20), (w, h, x, y));
    }

    [Fact]
    public void Layout_BottomExtra_AddsSpaceBelowOnly()
    {
        var f = new PhotoFrame { ThicknessPercent = 5, BottomExtraPercent = 20 };
        var (w, h, x, y) = f.Layout(100, 100);
        Assert.Equal((110, 130, 5, 5), (w, h, x, y));
    }

    [Theory]
    [InlineData(300, 200, 1.0)]
    [InlineData(200, 300, 1.0)]
    [InlineData(300, 200, 0.5625)]
    public void Layout_Aspect_GrowsCanvasToRatio_AndCentresPhoto(int pw, int ph, double ratio)
    {
        var f = new PhotoFrame { ThicknessPercent = 0, Aspect = ratio };
        var (w, h, x, y) = f.Layout(pw, ph);
        Assert.InRange((double)w / h, ratio - 0.01, ratio + 0.01);
        Assert.True(w >= pw && h >= ph);          // never crops
        Assert.True(x + pw <= w && y + ph <= h);   // photo fits
        Assert.True(Math.Abs((w - pw) - 2 * x) <= 1 && Math.Abs((h - ph) - 2 * y) <= 1);
    }

    [Fact]
    public void Render_PutsPhotoInsideBorderColour_AndKeepsPixels()
    {
        var f = new PhotoFrame { ThicknessPercent = 10, ColorHex = "#FF0000" };
        using var src = Photo(100, 100, new Rgb24(0, 255, 0));
        using var o = f.Render(src);
        Assert.Equal((120, 120), (o.Width, o.Height));
        Assert.Equal(new Rgb24(255, 0, 0), o[2, 2]);
        Assert.Equal(new Rgb24(0, 255, 0), o[60, 60]);
        Assert.Equal(new Rgb24(0, 255, 0), o[10, 10]);   // first photo pixel
        Assert.Equal(new Rgb24(255, 0, 0), o[9, 60]);    // last border pixel
    }

    [Fact]
    public void Render_BadColour_FallsBackToWhite()
    {
        using var src = Photo(10, 10, new Rgb24(0, 0, 0));
        using var o = new PhotoFrame { ThicknessPercent = 20, ColorHex = "nope" }.Render(src);
        Assert.Equal(new Rgb24(255, 255, 255), o[0, 0]);
    }

    [Fact]
    public void Normalized_ClampsOutOfRangeValues()
    {
        var f = new PhotoFrame { ThicknessPercent = 500, BottomExtraPercent = -3, Aspect = 99 }.Normalized();
        Assert.Equal(PhotoFrame.MaxThicknessPercent, f.ThicknessPercent);
        Assert.Equal(0f, f.BottomExtraPercent);
        Assert.Equal(PhotoFrame.MaxAspect, f.Aspect);
        Assert.Equal(0f, new PhotoFrame { ThicknessPercent = float.NaN }.Normalized().ThicknessPercent);
    }

    [Fact]
    public void IsNoOp_OnlyWhenNothingToDraw()
    {
        Assert.True(new PhotoFrame { ThicknessPercent = 0 }.IsNoOp);
        Assert.False(new PhotoFrame { ThicknessPercent = 0, Aspect = 1 }.IsNoOp);
        Assert.False(new PhotoFrame { ThicknessPercent = 1 }.IsNoOp);
    }

    [Fact]
    public void BuiltIns_HaveUniqueIds_AndRenderWithoutError()
    {
        Assert.Equal(PhotoFrame.BuiltIns.Count, PhotoFrame.BuiltIns.Select(f => f.Id).Distinct().Count());
        using var src = Photo(60, 40, new Rgb24(9, 9, 9));
        foreach (var f in PhotoFrame.BuiltIns) { using var o = f.Render(src); Assert.True(o.Width >= 60 && o.Height >= 40); }
    }

    [Fact]
    public void Store_AddsPersistsAndDeletes_AndIgnoresCorruptFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), "phs_fr_" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "frames.json");
        try
        {
            var s = new PhotoFrameStore(path);
            var a = s.Add("  Mine ", new PhotoFrame { ThicknessPercent = 7, ColorHex = "#112233", IsBuiltIn = true });
            Assert.Equal("Mine", a.Name);
            Assert.False(a.IsBuiltIn);
            var again = new PhotoFrameStore(path);
            Assert.Equal(7f, Assert.Single(again.Frames).ThicknessPercent);
            Assert.True(again.Delete(a.Id));
            Assert.False(again.Delete(a.Id));
            Assert.Empty(new PhotoFrameStore(path).Frames);

            File.WriteAllText(path, "{ not json");
            Assert.Empty(new PhotoFrameStore(path).Frames);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
