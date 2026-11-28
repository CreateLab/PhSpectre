using PhSpectre.ColorGrading;
using PhSpectre.Rendering;
using PhSpectre.Skin;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Xunit;

namespace PhSpecre.Tests;

public class SkinRetouchTests
{
    private const int W = 640, H = 480;

    // A skin-coloured field with fine fixed texture and a hard dark stripe (an "eye") down the middle.
    private static Image<Rgb24> Photo()
    {
        var img = new Image<Rgb24>(W, H);
        var random = new Random(11);
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                int n = random.Next(-9, 10);
                bool stripe = x is >= 316 and < 324;
                img[x, y] = stripe ? new Rgb24(40, 30, 28) : new Rgb24((byte)(200 + n), (byte)(150 + n), (byte)(130 + n));
            }
        return img;
    }

    // Skin on the left `share` of the photo, nothing elsewhere.
    private static SkinMask Mask(double share = 1)
    {
        var values = new float[64 * 64];
        for (int y = 0; y < 64; y++)
            for (int x = 0; x < 64; x++)
                values[y * 64 + x] = x < 64 * share ? 1f : 0f;
        return new SkinMask(values, 64, 64);
    }

    private static double Roughness(Image<Rgb24> img, int fromX, int toX)
    {
        double sum = 0;
        int n = 0;
        for (int y = 20; y < H - 20; y++)
            for (int x = fromX; x < toX; x++, n++)
                sum += Math.Abs(img[x, y].G - img[x - 1, y].G);
        return sum / n;
    }

    private static int Largest(Image<Rgb24> a, Image<Rgb24> b, Rectangle area)
    {
        int worst = 0;
        for (int y = 0; y < area.Height; y++)
            for (int x = 0; x < area.Width; x++)
                worst = Math.Max(worst, Math.Abs(a[area.X + x, area.Y + y].G - b[area.X + x, area.Y + y].G));
        return worst;
    }

    [Fact]
    public void Smooths_Texture_ButKeepsStrongEdges()
    {
        using var before = Photo();
        using var after = Photo();
        SkinRetouch.Apply(after, 1f, Mask());
        Assert.True(Roughness(after, 40, 280) < Roughness(before, 40, 280) * 0.4);
        Assert.True(after[319, 200].G < 70 && after[300, 200].G > 130, $"{after[319, 200]} {after[300, 200]}");
    }

    [Fact]
    public void LeavesEverythingOutsideTheMaskAlone()
    {
        using var before = Photo();
        using var after = Photo();
        SkinRetouch.Apply(after, 1f, Mask(0.25));
        Assert.True(Roughness(after, 20, 100) < Roughness(before, 20, 100) * 0.5);
        Assert.Equal(0, Largest(before, after, new Rectangle(340, 0, W - 340, H)));
    }

    [Fact]
    public void ZeroStrength_NoMask_OrNoSkin_ChangeNothing()
    {
        using var before = Photo();
        using var after = Photo();
        SkinRetouch.Apply(after, 0f, Mask());
        SkinRetouch.Apply(after, 1f, Mask(0));
        Assert.Equal(0, Largest(before, after, new Rectangle(0, 0, W, H)));
    }

    [Fact]
    public void PieceOfAPhoto_ComesOutAsInTheWholePhoto()
    {
        using var whole = Photo();
        var rect = new Rectangle(64, 32, 480, 416);
        using var piece = whole.Clone(c => c.Crop(rect));
        var mask = Mask(0.7);
        SkinRetouch.Apply(whole, 0.8f, mask);
        SkinRetouch.Apply(piece, 0.8f, mask.Piece(rect, W, H));
        using var expected = whole.Clone(c => c.Crop(rect));
        const int edge = 48;
        int worst = Largest(expected, piece, new Rectangle(edge, edge, rect.Width - 2 * edge, rect.Height - 2 * edge));
        Assert.True(worst <= 2, worst.ToString());
    }

    [Fact]
    public void PhotoAdjust_SkinSmoothing_IsCleaning_AndUsesTheGivenMask()
    {
        var adjust = new PhotoAdjust { SkinSmoothing = 80 };
        Assert.False(adjust.IsNoOp);
        Assert.True(adjust.HasEffects && adjust.HasCleaning && !adjust.HasNoiseReduction);
        Assert.True(adjust.WithoutCleaning().IsNoOp);
        using var photo = Photo();
        using var result = adjust.Render(photo, Mask());
        Assert.True(Roughness(result, 40, 280) < Roughness(photo, 40, 280) * 0.6);
    }

    [Fact]
    public void Preset_SmoothsSkin_ByItsStrength_ButNotInThumbnails()
    {
        var preset = LrPreset.Parse("<rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\" " +
            "xmlns:crs=\"http://ns.adobe.com/camera-raw-settings/1.0/\"><rdf:Description crs:SkinSmoothing=\"80\"/></rdf:RDF>");
        Assert.True(new GradeStack { Preset = preset }.SmoothsSkin);
        Assert.False(new GradeStack { Preset = preset, PresetEnabled = false }.SmoothsSkin);
        using var full = Photo();
        using var half = Photo();
        using var thumb = Photo();
        LrPresetApplier.Apply(full, preset, skin: Mask());
        LrPresetApplier.Apply(half, preset, 0.5f, skin: Mask());
        LrPresetApplier.Apply(thumb, preset, detail: false, skin: Mask());
        Assert.True(Roughness(full, 40, 280) < Roughness(half, 40, 280));
        Assert.True(Roughness(half, 40, 280) < Roughness(thumb, 40, 280));
    }

    [Fact]
    public void BundledModel_Loads_AndAnswersForAPhotoWithoutPeople()
    {
        Exception? failure = null;
        var segmenter = new OnnxSkinSegmenter(ex => failure = ex);
        using var sky = new Image<Rgb24>(400, 300, new Rgb24(90, 140, 220));
        var mask = segmenter.Segment(sky);
        Assert.Null(failure);
        Assert.NotNull(mask);
        Assert.Equal((256, 256), (mask!.Width, mask.Height));
        Assert.True(mask.Coverage < 0.05f, mask.Coverage.ToString());
    }
}
