using PhSpectre.Rendering;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Xunit;
using Xunit.Abstractions;

namespace PhSpecre.Tests;

public class NoiseReductionTests(ITestOutputHelper output)
{
    // Flat areas, a hard brightness edge, an edge of colour only and a soft ramp.
    private static Image<Rgb24> Scene(int w, int h)
    {
        var img = new Image<Rgb24>(w, h);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                img[x, y] = x < w / 4 ? new Rgb24(40, 40, 40)
                    : x < w / 2 ? new Rgb24(190, 190, 190)
                    : y < h / 2 ? new Rgb24(200, 80, 80)
                    : new Rgb24((byte)(60 + 120 * x / w), 130, 90);
        return img;
    }

    // Deterministic noise: `luma` moves all channels together, `color` each one on its own, in blobs of `blob` pixels.
    private static Image<Rgb24> AddNoise(Image<Rgb24> clean, float luma, float color, int blob = 1)
    {
        var rnd = new Random(7);
        float Gauss() => (float)(Math.Sqrt(-2 * Math.Log(1 - rnd.NextDouble())) * Math.Cos(2 * Math.PI * rnd.NextDouble()));
        int bw = clean.Width / blob + 1;
        var blobs = new (float R, float G, float B)[bw * (clean.Height / blob + 1)];
        for (int i = 0; i < blobs.Length; i++)
        {
            float r = Gauss() * color, g = Gauss() * color, b = Gauss() * color;
            // Colour noise carries no brightness of its own.
            float y = 0.299f * r + 0.587f * g + 0.114f * b;
            blobs[i] = (r - y, g - y, b - y);
        }
        var noisy = clean.Clone();
        for (int y = 0; y < clean.Height; y++)
            for (int x = 0; x < clean.Width; x++)
            {
                var p = clean[x, y];
                var c = blobs[y / blob * bw + x / blob];
                float l = Gauss() * luma;
                byte B(float v) => (byte)Math.Clamp((int)MathF.Round(v), 0, 255);
                noisy[x, y] = new Rgb24(B(p.R + l + c.R), B(p.G + l + c.G), B(p.B + l + c.B));
            }
        return noisy;
    }

    private static double Psnr(Image<Rgb24> a, Image<Rgb24> b)
    {
        double sum = 0;
        for (int y = 0; y < a.Height; y++)
            for (int x = 0; x < a.Width; x++)
            {
                var p = a[x, y]; var q = b[x, y];
                sum += Math.Pow(p.R - q.R, 2) + Math.Pow(p.G - q.G, 2) + Math.Pow(p.B - q.B, 2);
            }
        return 10 * Math.Log10(255.0 * 255.0 / Math.Max(1e-9, sum / (3.0 * a.Width * a.Height)));
    }

    private static int MaxDifference(Image<Rgb24> a, Image<Rgb24> b)
    {
        int max = 0;
        for (int y = 0; y < a.Height; y++)
            for (int x = 0; x < a.Width; x++)
            {
                var p = a[x, y]; var q = b[x, y];
                max = Math.Max(max, Math.Max(Math.Abs(p.R - q.R), Math.Max(Math.Abs(p.G - q.G), Math.Abs(p.B - q.B))));
            }
        return max;
    }

    [Fact]
    public void ZeroStrength_ReturnsTheSamePixels_InANewImage()
    {
        using var clean = Scene(64, 48);
        using var noisy = AddNoise(clean, 8, 8);
        using var o = NoiseReduction.Apply(noisy, 0, 0);
        Assert.NotSame(noisy, o);
        Assert.Equal(0, MaxDifference(noisy, o));
    }

    [Theory]
    [InlineData(100, 0)]
    [InlineData(0, 100)]
    [InlineData(60, 60)]
    public void ACleanPicture_IsBarelyChanged(float luma, float color)
    {
        using var clean = Scene(256, 192);
        using var o = NoiseReduction.Apply(clean, luma, color);
        Assert.True(Psnr(clean, o) > 42, $"PSNR {Psnr(clean, o):F1}");
    }

    [Theory]
    [InlineData(10f, 0f, 1, 70f, 0f, 6.0)]    // fine luminance noise
    [InlineData(0f, 12f, 1, 0f, 80f, 4.0)]    // fine colour noise
    [InlineData(0f, 12f, 4, 0f, 100f, 2.0)]   // colour blotches
    [InlineData(10f, 12f, 2, 60f, 80f, 3.0)]  // both
    public void NoisyPictures_GetCloserToTheOriginal(float luma, float color, int blob, float lumaStrength, float colorStrength, double gain)
    {
        using var clean = Scene(256, 192);
        using var noisy = AddNoise(clean, luma, color, blob);
        using var o = NoiseReduction.Apply(noisy, lumaStrength, colorStrength);
        double before = Psnr(clean, noisy), after = Psnr(clean, o);
        output.WriteLine($"PSNR {before:F2} -> {after:F2} dB");
        Assert.True(after > before + gain, $"PSNR {before:F2} -> {after:F2}");
    }

    [Fact]
    public void TheLuminanceSlider_ActsFromTheStart_AndKeepsActingToTheEnd()
    {
        using var clean = new Image<Rgb24>(256, 192, new Rgb24(120, 120, 120));
        using var noisy = AddNoise(clean, 10, 0);
        // Share of the noise (standard deviation) that is left at each position of the slider.
        double original = Math.Pow(10, -Psnr(clean, noisy) / 20);
        var left = new[] { 10f, 25f, 50f, 75f, 100f }.Select(s =>
        {
            using var o = NoiseReduction.Apply(noisy, s, 0);
            return Math.Pow(10, -Psnr(clean, o) / 20) / original;
        }).ToList();
        output.WriteLine("noise left: " + string.Join(" ", left.Select(v => v.ToString("F2"))));
        Assert.True(left[0] < 0.9, "a low setting must already be visible");
        Assert.True(left[1] < 0.7, "a quarter of the way must remove a good part of the grain");
        for (int i = 1; i < left.Count; i++) Assert.True(left[i] < left[i - 1], $"step {i} does nothing: {left[i - 1]:F2} -> {left[i]:F2}");
    }

    [Theory]
    [InlineData(333, 211, 30)]
    [InlineData(128, 96, 100)]
    [InlineData(9, 8, 60)]
    public void TheVectorCode_GivesTheSamePictureAsThePlainOne(int w, int h, int strength)
    {
        using var clean = Scene(w, h);
        using var noisy = AddNoise(clean, 11, 6, 2);
        try
        {
            NoiseReduction.ScalarOnly = true;
            using var plain = NoiseReduction.Apply(noisy, strength, 0);
            NoiseReduction.ScalarOnly = false;
            foreach (var portable in new[] { false, true })
            {
                NoiseReduction.PortableTranspose = portable;
                using var vector = NoiseReduction.Apply(noisy, strength, 0);
                Assert.True(MaxDifference(plain, vector) <= 1, $"portable {portable}: difference {MaxDifference(plain, vector)}");
            }
        }
        finally { NoiseReduction.ScalarOnly = false; NoiseReduction.PortableTranspose = false; }
    }

    [Fact]
    public void EdgesStaySharp_InBrightnessAndInColourAlone()
    {
        // Two colours of equal luminance side by side: only colour tells them apart.
        var left = new Rgb24(200, 90, 90);
        var right = new Rgb24(60, 150, 123);
        using var clean = new Image<Rgb24>(128, 128, left);
        for (int y = 0; y < 128; y++)
            for (int x = 64; x < 128; x++) clean[x, y] = right;
        using var noisy = AddNoise(clean, 4, 6);
        using var o = NoiseReduction.Apply(noisy, 60, 80);
        // Right next to the edge each side must still be its own colour.
        for (int y = 8; y < 120; y++)
        {
            Assert.InRange(o[61, y].R, left.R - 30, left.R + 30);
            Assert.InRange(o[66, y].R, right.R - 30, right.R + 30);
        }
    }

    [Theory]
    [InlineData(500, 340)]
    [InlineData(333, 211)]
    public void TheResultDoesNotDependOnTheTiles(int w, int h)
    {
        using var clean = Scene(w, h);
        using var noisy = AddNoise(clean, 9, 9, 3);
        using var whole = NoiseReduction.Apply(noisy, 70, 90, tile: 1024);
        using var tiled = NoiseReduction.Apply(noisy, 70, 90, tile: 64);
        Assert.True(MaxDifference(whole, tiled) <= 1, $"seam difference {MaxDifference(whole, tiled)}");
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(5, 40)]
    [InlineData(17, 9)]
    public void TinyPictures_DoNotFail(int w, int h)
    {
        using var img = Scene(w, h);
        using var o = NoiseReduction.Apply(img, 100, 100);
        Assert.Equal((w, h), (o.Width, o.Height));
    }

    [Fact]
    public void Cancellation_IsHonoured()
    {
        using var img = Scene(64, 64);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => NoiseReduction.Apply(img, 50, 50, cts.Token));
    }

    [Fact]
    public void TheHundredPercentPiece_MatchesTheFullExport()
    {
        using var clean = Scene(640, 480);
        using var noisy = AddNoise(clean, 9, 9, 2);
        var a = new PhotoAdjust { NoiseLuma = 70, NoiseColor = 80, Exposure = 0.5f, Sharpness = 40 };
        using var full = a.Render(noisy);

        // The same way the 100% view cuts it: Context to spare, starting on the grid.
        var view = new Rectangle(250, 170, 160, 120);
        int x0 = (view.X - NoiseReduction.Context) / NoiseReduction.Align * NoiseReduction.Align;
        int y0 = (view.Y - NoiseReduction.Context) / NoiseReduction.Align * NoiseReduction.Align;
        var rect = new Rectangle(x0, y0, view.Right + NoiseReduction.Context - x0, view.Bottom + NoiseReduction.Context - y0);
        using var piece = noisy.Clone(c => c.Crop(rect));
        using var detail = a.RenderDetail(piece, 640, NoiseReduction.Measure(noisy));
        detail.Mutate(c => c.Crop(new Rectangle(view.X - x0, view.Y - y0, view.Width, view.Height)));
        using var expected = full.Clone(c => c.Crop(view));

        Assert.True(MaxDifference(expected, detail) <= 1, $"difference {MaxDifference(expected, detail)}");
    }

    [Fact]
    public void PhotoAdjust_AppliesIt_AndCountsItAsCleaningOnly()
    {
        using var clean = Scene(128, 96);
        using var noisy = AddNoise(clean, 10, 10);
        var a = new PhotoAdjust { NoiseLuma = 60, NoiseColor = 70 };
        Assert.False(a.IsNoOp);
        Assert.True(a.HasEffects);
        Assert.False(a.HasTone);
        Assert.True(a.ToneOnly().IsNoOp);
        Assert.True(a.WithoutCleaning().IsNoOp);
        Assert.Equal(a, a.CleaningOnly());
        Assert.Equal(100f, new PhotoAdjust { NoiseLuma = 400 }.Normalized().NoiseLuma);
        Assert.Equal(0f, new PhotoAdjust { NoiseColor = -3 }.Normalized().NoiseColor);
        using var rendered = a.Render(noisy);
        using var direct = NoiseReduction.Apply(noisy, 60, 70);
        Assert.Equal(0, MaxDifference(rendered, direct));
    }
}
