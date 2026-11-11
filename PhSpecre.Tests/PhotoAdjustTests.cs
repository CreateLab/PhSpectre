using PhSpectre.Rendering;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Xunit;

namespace PhSpecre.Tests;

public class PhotoAdjustTests
{
    private static Image<Rgb24> Photo(int w, int h, Rgb24 c) => new(w, h, c);

    [Fact]
    public void Default_IsNoOp_AndKeepsPixels()
    {
        var a = new PhotoAdjust();
        Assert.True(a.IsNoOp);
        using var src = Photo(40, 30, new Rgb24(10, 20, 30));
        using var o = a.Render(src);
        Assert.Equal((40, 30), (o.Width, o.Height));
        Assert.Equal(new Rgb24(10, 20, 30), o[5, 5]);
    }

    [Fact]
    public void Quarter_SwapsDimensions()
    {
        var a = new PhotoAdjust { Quarter = 1 };
        using var src = Photo(40, 30, new Rgb24(1, 2, 3));
        using var o = a.Render(src);
        Assert.Equal((30, 40), (o.Width, o.Height));
        Assert.Equal((30, 40), a.OutputSize(40, 30));
    }

    [Fact]
    public void Quarter_WrapsAndNegativeIsClamped()
    {
        Assert.Equal(3, new PhotoAdjust { Quarter = -1 }.Normalized().Quarter);
        Assert.Equal(1, new PhotoAdjust { Quarter = 5 }.Normalized().Quarter);
    }

    [Theory]
    [InlineData(300, 200, 1.0)]
    [InlineData(200, 300, 4.0 / 5)]
    [InlineData(300, 200, 16.0 / 9)]
    public void Aspect_CropsToRatio_NeverGrows(int pw, int ph, double ratio)
    {
        var a = new PhotoAdjust { Aspect = ratio };
        using var src = Photo(pw, ph, new Rgb24(9, 9, 9));
        using var o = a.Render(src);
        Assert.InRange((double)o.Width / o.Height, ratio - 0.02, ratio + 0.02);
        Assert.True(o.Width <= pw && o.Height <= ph);
        Assert.True(o.Width == pw || o.Height == ph); // the largest crop of that ratio
    }

    [Fact]
    public void Straighten_LeavesNoEmptyCorners()
    {
        var a = new PhotoAdjust { Straighten = 10 };
        using var src = Photo(300, 200, new Rgb24(200, 100, 50));
        using var o = a.Render(src);
        Assert.True(o.Width < 300 && o.Height < 200);
        Assert.InRange((double)o.Width / o.Height, 1.49, 1.51); // keeps the photo's shape
        foreach (var (x, y) in new[] { (0, 0), (o.Width - 1, 0), (0, o.Height - 1), (o.Width - 1, o.Height - 1) })
        {
            var p = o[x, y];
            Assert.True(p.R > 150 && p.G > 70, $"corner {x},{y} is {p}"); // no black fill from the rotation
        }
    }

    [Fact]
    public void Pan_MovesCrop_AndIsClampedInsideThePhoto()
    {
        using var src = new Image<Rgb24>(400, 200);
        for (int y = 0; y < 200; y++) for (int x = 0; x < 400; x++) src[x, y] = new Rgb24((byte)(x / 2), 0, 0);
        using var centre = new PhotoAdjust { Zoom = 2 }.Render(src);
        using var right = new PhotoAdjust { Zoom = 2, PanX = 0.4f }.Render(src);
        Assert.True(right[0, 0].R > centre[0, 0].R);
        using var far = new PhotoAdjust { Zoom = 2, PanX = 4f }.Render(src); // far past the edge
        Assert.Equal((200, 100), (far.Width, far.Height));
        Assert.InRange(far[0, 0].R, 98, 100); // crop sits against the right edge (1px safety margin)
    }

    [Theory]
    [InlineData(10f, 2f, 3f, 3f)]
    [InlineData(-8f, 1.5f, -3f, 2f)]
    [InlineData(12f, 1f, 4f, -4f)]
    public void Pan_WithStraighten_NeverShowsEmptyCorners(float angle, float zoom, float px, float py)
    {
        var a = new PhotoAdjust { Straighten = angle, Zoom = zoom, PanX = px, PanY = py };
        using var src = Photo(300, 200, new Rgb24(200, 100, 50));
        using var o = a.Render(src);
        foreach (var (x, y) in new[] { (0, 0), (o.Width - 1, 0), (0, o.Height - 1), (o.Width - 1, o.Height - 1) })
        {
            var p = o[x, y];
            Assert.True(p.R > 150 && p.G > 70, $"corner {x},{y} is {p}");
        }
    }

    [Theory]
    [InlineData(7f, 2f, 0.3f, -0.2f)]
    [InlineData(-12f, 3f, -0.5f, 0.4f)]
    public void Straighten_RegionRotate_MatchesFullRotate(float angle, float zoom, float px, float py)
    {
        using var src = new Image<Rgb24>(600, 400);
        for (int y = 0; y < 400; y++) for (int x = 0; x < 600; x++) src[x, y] = new Rgb24((byte)(x / 3), (byte)(y / 2), (byte)((x + y) / 5));
        var a = new PhotoAdjust { Straighten = angle, Zoom = zoom, PanX = px, PanY = py };
        using var fast = a.Render(src);
        // Reference: rotate the whole picture, then cut the same rectangle.
        var (cw, ch) = a.OutputSize(600, 400);
        var (pxc, pyc) = a.ClampedPan(600, 400);
        using var full = src.Clone(x => x.Rotate(angle));
        int rx = (int)Math.Round((full.Width - cw) / 2.0 + pxc * cw), ry = (int)Math.Round((full.Height - ch) / 2.0 + pyc * ch);
        using var refImg = full.Clone(x => x.Crop(new Rectangle(rx, ry, cw, ch)));
        Assert.Equal((cw, ch), (fast.Width, fast.Height));
        long diff = 0;
        for (int y = 0; y < ch; y += 5) for (int x = 0; x < cw; x += 5) diff += Math.Abs(fast[x, y].R - refImg[x, y].R) + Math.Abs(fast[x, y].G - refImg[x, y].G);
        Assert.True(diff / (double)((cw / 5 + 1) * (ch / 5 + 1)) < 4, $"mean diff {diff}");
    }

    [Fact]
    public void ClampedPan_ZeroWhenThereIsNoSlack()
    {
        var (x, y) = new PhotoAdjust { PanX = 2, PanY = 2 }.ClampedPan(300, 200);
        Assert.Equal((0f, 0f), (x, y));
    }

    [Fact]
    public void Zoom_ShrinksCrop()
    {
        using var src = Photo(400, 200, new Rgb24(1, 1, 1));
        using var o = new PhotoAdjust { Zoom = 2 }.Render(src);
        Assert.Equal((200, 100), (o.Width, o.Height));
    }

    [Fact]
    public void Exposure_PlusOneStop_Brightens_MinusDarkens_ZeroKeeps()
    {
        using var src = Photo(4, 4, new Rgb24(100, 100, 100));
        using var up = new PhotoAdjust { Exposure = 1 }.Render(src);
        using var down = new PhotoAdjust { Exposure = -1 }.Render(src);
        Assert.True(up[0, 0].R > 100 && down[0, 0].R < 100);
        using var same = new PhotoAdjust { Exposure = 0 }.Render(src);
        Assert.Equal(new Rgb24(100, 100, 100), same[0, 0]);
    }

    [Fact]
    public void Exposure_ClipsWhiteAndKeepsBlack()
    {
        using var src = Photo(2, 1, new Rgb24(250, 0, 0));
        using var o = new PhotoAdjust { Exposure = 3 }.Render(src);
        Assert.Equal(new Rgb24(255, 0, 0), o[0, 0]);
    }

    [Fact]
    public void OutOfRange_IsClamped_NotRejected()
    {
        var n = new PhotoAdjust { Straighten = 90, Zoom = 50, Exposure = float.NaN, Aspect = 99 }.Normalized();
        Assert.Equal(PhotoAdjust.MaxStraighten, n.Straighten);
        Assert.Equal(PhotoAdjust.MaxZoom, n.Zoom);
        Assert.Equal(0f, n.Exposure);
        Assert.Equal(PhotoFrame.MaxAspect, n.Aspect);
    }

    [Fact]
    public void Render_DoesNotTouchSource()
    {
        using var src = Photo(50, 50, new Rgb24(80, 80, 80));
        using var _ = new PhotoAdjust { Quarter = 1, Straighten = 5, Aspect = 1.0, Exposure = 1 }.Render(src);
        Assert.Equal((50, 50), (src.Width, src.Height));
        Assert.Equal(new Rgb24(80, 80, 80), src[0, 0]);
    }

    private static Rgb24 Mid(PhotoAdjust a, Rgb24 c) { using var src = Photo(8, 8, c); using var o = a.Render(src); return o[4, 4]; }

    [Fact]
    public void Contrast_SpreadsAroundMiddle()
    {
        Assert.True(Mid(new PhotoAdjust { Contrast = 80 }, new Rgb24(200, 200, 200)).R > 200);
        Assert.True(Mid(new PhotoAdjust { Contrast = 80 }, new Rgb24(60, 60, 60)).R < 60);
    }

    [Fact]
    public void Highlights_AndShadows_MoveTheirRange()
    {
        Assert.True(Mid(new PhotoAdjust { Highlights = -80 }, new Rgb24(230, 230, 230)).R < 230);
        Assert.True(Mid(new PhotoAdjust { Shadows = 80 }, new Rgb24(30, 30, 30)).R > 30);
        Assert.InRange(Mid(new PhotoAdjust { Highlights = -80 }, new Rgb24(128, 128, 128)).R, 118, 128);
    }

    [Fact]
    public void Saturation_ZeroGivesGray_AndPlusStrengthens()
    {
        var gray = Mid(new PhotoAdjust { Saturation = -100 }, new Rgb24(200, 100, 50));
        Assert.InRange(Math.Abs(gray.R - gray.B), 0, 2);
        var vivid = Mid(new PhotoAdjust { Saturation = 60 }, new Rgb24(200, 100, 50));
        Assert.True(vivid.R - vivid.B > 150);
    }

    [Fact]
    public void Temperature_WarmAddsRed_CoolAddsBlue()
    {
        var warm = Mid(new PhotoAdjust { Temperature = 60 }, new Rgb24(128, 128, 128));
        var cool = Mid(new PhotoAdjust { Temperature = -60 }, new Rgb24(128, 128, 128));
        Assert.True(warm.R > warm.B);
        Assert.True(cool.B > cool.R);
    }

    [Fact]
    public void Vignette_DarkensCorners_NotCentre()
    {
        using var src = Photo(100, 100, new Rgb24(200, 200, 200));
        using var o = new PhotoAdjust { Vignette = -80 }.Render(src);
        Assert.True(o[0, 0].R < 150);
        Assert.Equal(200, o[50, 50].R);
    }

    [Fact]
    public void Grain_IsDeterministic_AndSharpen_KeepsFlatAreas()
    {
        using var src = Photo(40, 40, new Rgb24(120, 120, 120));
        using var a = new PhotoAdjust { Grain = 50 }.Render(src);
        using var b = new PhotoAdjust { Grain = 50 }.Render(src);
        Assert.Equal(a[7, 9], b[7, 9]);
        using var s = new PhotoAdjust { Sharpness = 80 }.Render(src);
        Assert.Equal(new Rgb24(120, 120, 120), s[20, 20]);
    }

    [Fact]
    public void Tone_Zero_IsNoOp_AndOutOfRangeIsClamped()
    {
        Assert.True(new PhotoAdjust().IsNoOp);
        Assert.False(new PhotoAdjust { Contrast = 1 }.IsNoOp);
        var n = new PhotoAdjust { Contrast = 900, Grain = -5, Tint = float.NaN }.Normalized();
        Assert.Equal((100f, 0f, 0f), (n.Contrast, n.Grain, n.Tint));
    }

    [Fact]
    public void Tone_WorksAfterStraighten()
    {
        using var src = Photo(300, 200, new Rgb24(100, 100, 100));
        using var o = new PhotoAdjust { Straighten = 5, Zoom = 2, Temperature = 50 }.Render(src);
        Assert.True(o[10, 10].R > o[10, 10].B);
    }
}
