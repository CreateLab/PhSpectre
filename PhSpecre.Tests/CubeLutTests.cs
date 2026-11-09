using System;
using System.Globalization;
using System.Text;
using PhSpectre.ColorGrading;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace PhSpecre.Tests;

public class CubeLutTests
{
    // Builds .cube text for an N^3 table whose entry at (r,g,b) grid position is f(r/(N-1), g/(N-1), b/(N-1)).
    private static string Cube3D(int n, Func<float, float, float, (float, float, float)> f, string header = "")
    {
        var sb = new StringBuilder();
        sb.Append(header);
        sb.Append(CultureInfo.InvariantCulture, $"LUT_3D_SIZE {n}\n");
        for (int b = 0; b < n; b++)
        for (int g = 0; g < n; g++)
        for (int r = 0; r < n; r++)
        {
            var (x, y, z) = f(r / (float)(n - 1), g / (float)(n - 1), b / (float)(n - 1));
            sb.Append(CultureInfo.InvariantCulture, $"{x:R} {y:R} {z:R}\n");
        }
        return sb.ToString();
    }

    private static string Cube1D(int n, Func<float, float> f)
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"LUT_1D_SIZE {n}\n");
        for (int i = 0; i < n; i++)
        {
            float v = f(i / (float)(n - 1));
            sb.Append(CultureInfo.InvariantCulture, $"{v:R} {v:R} {v:R}\n");
        }
        return sb.ToString();
    }

    private static CubeLut Identity3D(int n = 5) => CubeLut.Parse(Cube3D(n, (r, g, b) => (r, g, b)));

    // ── Parsing ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Parse_ReadsHeaderAndIgnoresCommentsAndBlankLines()
    {
        var text = "# made by hand\nTITLE \"My Look\"\n\nDOMAIN_MIN 0 0 0\nDOMAIN_MAX 1 1 1\n"
                   + Cube3D(3, (r, g, b) => (r, g, b));
        var lut = CubeLut.Parse(text);

        Assert.True(lut.Is3D);
        Assert.Equal(3, lut.Size);
        Assert.Equal("My Look", lut.Title);
    }

    [Fact]
    public void Parse_AcceptsScientificNotation()
    {
        // Resolve emits values like 7.62951e-05; culture must not matter.
        var text = "LUT_1D_SIZE 2\n7.62951e-05 0 0\n1 1 1\n";
        var lut = CubeLut.Parse(text);
        Assert.False(lut.Is3D);
    }

    [Fact]
    public void Parse_Throws_WhenSizeMissing() =>
        Assert.Throws<FormatException>(() => CubeLut.Parse("0 0 0\n1 1 1\n"));

    [Fact]
    public void Parse_Throws_WhenRowCountWrong() =>
        Assert.Throws<FormatException>(() => CubeLut.Parse("LUT_3D_SIZE 2\n0 0 0\n1 1 1\n"));

    [Fact]
    public void Parse_Throws_WhenBoth1DAnd3DDeclared() =>
        Assert.Throws<FormatException>(() => CubeLut.Parse("LUT_1D_SIZE 2\nLUT_3D_SIZE 2\n0 0 0\n1 1 1\n"));

    [Fact]
    public void Parse_Throws_WithLineNumber_OnBadNumber()
    {
        var ex = Assert.Throws<FormatException>(() => CubeLut.Parse("LUT_1D_SIZE 2\n0 0 0\n1 x 1\n"));
        Assert.Contains("Line 3", ex.Message);
    }

    [Fact]
    public void Parse_Throws_WhenDomainInverted() =>
        Assert.Throws<FormatException>(() => CubeLut.Parse("LUT_1D_SIZE 2\nDOMAIN_MIN 1 1 1\nDOMAIN_MAX 0 0 0\n0 0 0\n1 1 1\n"));

    // ── Interpolation ────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0f, 0f, 0f)]
    [InlineData(1f, 1f, 1f)]
    [InlineData(0.13f, 0.62f, 0.9f)]
    [InlineData(0.9f, 0.62f, 0.13f)]
    [InlineData(0.5f, 0.5f, 0.5f)]
    public void Evaluate_IdentityLut_ReturnsInput(float r, float g, float b)
    {
        var o = Identity3D().Evaluate(r, g, b);
        Assert.Equal(r, o.X, 4);
        Assert.Equal(g, o.Y, 4);
        Assert.Equal(b, o.Z, 4);
    }

    [Fact]
    public void Evaluate_LinearFunction_IsReproducedExactly_InEverySixTetrahedra()
    {
        // Tetrahedral interpolation is exact for any function linear in (r,g,b). Probing points whose
        // fractional offsets take all six orderings exercises every branch of the case split.
        static (float, float, float) F(float r, float g, float b) =>
            (0.7f * r + 0.2f * g + 0.1f * b, 0.1f * r + 0.8f * g + 0.1f * b, 0.05f * r + 0.15f * g + 0.8f * b);

        var lut = CubeLut.Parse(Cube3D(4, F));
        float[][] probes =
        [
            [0.10f, 0.20f, 0.30f], [0.10f, 0.30f, 0.20f], [0.20f, 0.10f, 0.30f],
            [0.20f, 0.30f, 0.10f], [0.30f, 0.10f, 0.20f], [0.30f, 0.20f, 0.10f],
            [0.77f, 0.41f, 0.93f], [0.05f, 0.96f, 0.51f]
        ];
        foreach (var p in probes)
        {
            var expected = F(p[0], p[1], p[2]);
            var o = lut.Evaluate(p[0], p[1], p[2]);
            Assert.Equal(expected.Item1, o.X, 4);
            Assert.Equal(expected.Item2, o.Y, 4);
            Assert.Equal(expected.Item3, o.Z, 4);
        }
    }

    [Fact]
    public void Evaluate_GridNodes_ReturnStoredValues()
    {
        // A non-linear table: nodes must come back exactly regardless of interpolation.
        var lut = CubeLut.Parse(Cube3D(5, (r, g, b) => (r * r, MathF.Sqrt(g), 1f - b)));
        var o = lut.Evaluate(0.5f, 0.25f, 0.75f);
        Assert.Equal(0.25f, o.X, 5);
        Assert.Equal(0.5f, o.Y, 5);
        Assert.Equal(0.25f, o.Z, 5);
    }

    [Fact]
    public void Evaluate_ClampsOutOfRangeInput()
    {
        var lut = Identity3D();
        var o = lut.Evaluate(-0.5f, 1.5f, 0.5f);
        Assert.Equal(0f, o.X, 5);
        Assert.Equal(1f, o.Y, 5);
        Assert.Equal(0.5f, o.Z, 5);
    }

    [Fact]
    public void Evaluate_1D_InterpolatesLinearlyBetweenEntries()
    {
        var lut = CubeLut.Parse(Cube1D(3, t => t * t)); // entries 0, 0.25, 1
        Assert.Equal(0.25f, lut.Evaluate(0.5f, 0.5f, 0.5f).X, 5);
        Assert.Equal(0.625f, lut.Evaluate(0.75f, 0.75f, 0.75f).Y, 5); // halfway between 0.25 and 1
    }

    [Fact]
    public void Evaluate_HonorsDomain()
    {
        // Domain 0..0.5 means image value 0.5 already addresses the last entry.
        var text = "DOMAIN_MIN 0 0 0\nDOMAIN_MAX 0.5 0.5 0.5\n" + Cube1D(2, t => t);
        var lut = CubeLut.Parse(text);
        Assert.Equal(0.5f, lut.Evaluate(0.25f, 0.25f, 0.25f).X, 5);
        Assert.Equal(1f, lut.Evaluate(0.9f, 0.9f, 0.9f).X, 5);
    }

    // ── Applying to images ───────────────────────────────────────────────────────────────────

    private static Image<Rgb24> Gradient()
    {
        var img = new Image<Rgb24>(16, 8);
        for (int y = 0; y < img.Height; y++)
        for (int x = 0; x < img.Width; x++)
            img[x, y] = new Rgb24((byte)(x * 17), (byte)(y * 36), (byte)((x + y) * 10));
        return img;
    }

    [Fact]
    public void Apply_IdentityLut_LeavesPixelsUnchanged()
    {
        using var img = Gradient();
        using var original = img.Clone();

        LutApplier.Apply(img, Identity3D(17));

        for (int y = 0; y < img.Height; y++)
        for (int x = 0; x < img.Width; x++)
        {
            var a = img[x, y]; var b = original[x, y];
            Assert.True(Math.Abs(a.R - b.R) <= 1 && Math.Abs(a.G - b.G) <= 1 && Math.Abs(a.B - b.B) <= 1,
                $"pixel ({x},{y}) drifted: {b} -> {a}");
        }
    }

    [Fact]
    public void Apply_InvertLut_InvertsPixels()
    {
        using var img = new Image<Rgb24>(1, 1);
        img[0, 0] = new Rgb24(10, 128, 250);

        LutApplier.Apply(img, CubeLut.Parse(Cube1D(256, t => 1f - t)));

        var p = img[0, 0];
        Assert.Equal(245, p.R);
        Assert.Equal(127, p.G);
        Assert.Equal(5, p.B);
    }

    [Fact]
    public void Apply_Strength_BlendsOriginalAndGraded()
    {
        using var img = new Image<Rgb24>(1, 1);
        img[0, 0] = new Rgb24(0, 0, 0);
        var white = CubeLut.Parse(Cube1D(2, _ => 1f)); // everything -> white

        LutApplier.Apply(img, white, 0.5f);

        Assert.InRange(img[0, 0].R, 127, 128);
    }

    [Fact]
    public void Apply_ZeroStrength_IsNoOp()
    {
        using var img = Gradient();
        using var original = img.Clone();
        LutApplier.Apply(img, CubeLut.Parse(Cube1D(2, _ => 1f)), 0f);
        Assert.Equal(original[5, 3], img[5, 3]);
    }
}
