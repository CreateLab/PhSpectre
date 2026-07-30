using System.Linq;
using PhSpectre.Rendering;
using SixLabors.ImageSharp;
using Xunit;

namespace PhSpecre.Tests;

public class CompositionGuideTests
{
    [Theory]
    [InlineData(CompositionGuide.None, 0)]
    [InlineData(CompositionGuide.RuleOfThirds, 4)]
    [InlineData(CompositionGuide.GoldenRatio, 4)]
    [InlineData(CompositionGuide.Diagonal, 3)]
    [InlineData(CompositionGuide.CenterCross, 2)]
    public void BuildGuideLines_ReturnsExpectedLineCount(CompositionGuide guide, int expectedCount)
    {
        var lines = PaletteImageRenderer.BuildGuideLines(guide, 0, 0, 300, 200).ToList();
        Assert.Equal(expectedCount, lines.Count);
    }

    [Fact]
    public void BuildGuideLines_RuleOfThirds_PlacesLinesAtThirds()
    {
        var lines = PaletteImageRenderer.BuildGuideLines(CompositionGuide.RuleOfThirds, 0, 0, 300, 300).ToList();
        var verticalXs = lines.Where(l => l.Item1.X == l.Item2.X).Select(l => l.Item1.X).OrderBy(x => x).ToList();
        Assert.Equal([100f, 200f], verticalXs);
    }

    [Fact]
    public void BuildGuideLines_CenterCross_PassesThroughCenter()
    {
        var lines = PaletteImageRenderer.BuildGuideLines(CompositionGuide.CenterCross, 0, 0, 300, 200).ToList();
        Assert.Contains(lines, l => l.Item1.X == 150 && l.Item2.X == 150);
        Assert.Contains(lines, l => l.Item1.Y == 100 && l.Item2.Y == 100);
    }

    [Fact]
    public void PerpendicularFoot_OnAxisAlignedLine_ProjectsCorrectly()
    {
        // Horizontal line a-b along y=0; foot of p=(5,10) should land at (5,0).
        var foot = PaletteImageRenderer.PerpendicularFoot(new PointF(5, 10), new PointF(0, 0), new PointF(10, 0));
        Assert.Equal(5f, foot.X, 0.01f);
        Assert.Equal(0f, foot.Y, 0.01f);
    }

    [Fact]
    public void PerpendicularFoot_OnDiagonalLine_LandsOnLine()
    {
        // Main diagonal of a 100x100 square: a=(0,0), b=(100,100). Foot of the opposite
        // corner (100,0) should be the midpoint (50,50).
        var foot = PaletteImageRenderer.PerpendicularFoot(new PointF(100, 0), new PointF(0, 0), new PointF(100, 100));
        Assert.Equal(50f, foot.X, 0.01f);
        Assert.Equal(50f, foot.Y, 0.01f);
    }
}
