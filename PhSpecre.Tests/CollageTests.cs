using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using PhSpectre;
using PhSpectre.Rendering;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Xunit;

namespace PhSpecre.Tests;

public class CollageTests
{
    [Fact]
    public void PlanRows_ThreeEqualSquares_SplitsIntoOnePlusTwoRows()
    {
        // Hand-derived: for 3 equal-aspect squares against a square target, splitting one
        // photo alone (2/3 of the height) from a paired row (1/3) beats both the single-row
        // and three-row alternatives — see CollageComposer.PlanRows for the scoring formula.
        var rows = CollageComposer.PlanRows([1.0, 1.0, 1.0], targetAspect: 1.0);

        Assert.Equal(2, rows.Count);
        Assert.Equal(0, rows[0].StartIndex);
        Assert.Equal(1, rows[0].Count);
        Assert.Equal(2.0 / 3.0, rows[0].HeightFraction, 3);
        Assert.Equal(1, rows[1].StartIndex);
        Assert.Equal(2, rows[1].Count);
        Assert.Equal(1.0 / 3.0, rows[1].HeightFraction, 3);
    }

    [Theory]
    [InlineData(new[] { 1.6, 1.6, 1.6 }, 1.0)]           // all landscape, square target
    [InlineData(new[] { 0.6, 0.6, 0.6, 0.6 }, 0.5625)]   // all portrait, story target
    [InlineData(new[] { 1.6, 0.6, 1.0, 1.2 }, 1.0)]      // mixed, square target
    public void PlanRows_FindsGloballyOptimalPartition(double[] aspects, double targetAspect)
    {
        var rows = CollageComposer.PlanRows(aspects, targetAspect);

        // Structural sanity: rows are contiguous, cover every photo exactly once, in order.
        int expectedStart = 0;
        foreach (var row in rows)
        {
            Assert.Equal(expectedStart, row.StartIndex);
            Assert.True(row.Count > 0);
            expectedStart += row.Count;
        }
        Assert.Equal(aspects.Length, expectedStart);
        Assert.Equal(1.0, rows.Sum(r => r.HeightFraction), 6);

        double achievedScore = Score(rows, aspects, targetAspect);
        double bruteForceBestScore = BruteForceBestScore(aspects, targetAspect);
        Assert.Equal(bruteForceBestScore, achievedScore, 6);
    }

    private static double Score(IReadOnlyList<CollageComposer.PlannedRow> rows, double[] aspects, double targetAspect)
    {
        double totalHeight = 0;
        foreach (var row in rows)
        {
            double sumAspect = 0;
            for (int i = 0; i < row.Count; i++) sumAspect += aspects[row.StartIndex + i];
            totalHeight += 1.0 / sumAspect;
        }
        double overallAspect = 1.0 / totalHeight;
        return Math.Abs(Math.Log(overallAspect / targetAspect));
    }

    // Independent re-implementation of the same exhaustive contiguous-partition search, used
    // only to verify PlanRows actually finds the best-scoring partition, not just a valid one.
    private static double BruteForceBestScore(double[] aspects, double targetAspect)
    {
        int n = aspects.Length;
        double best = double.MaxValue;
        int partitionCount = 1 << (n - 1);
        for (int mask = 0; mask < partitionCount; mask++)
        {
            var rowSpans = new List<(int Start, int Count)>();
            int start = 0;
            for (int i = 0; i < n - 1; i++)
            {
                if ((mask & (1 << i)) != 0)
                {
                    rowSpans.Add((start, i - start + 1));
                    start = i + 1;
                }
            }
            rowSpans.Add((start, n - start));

            double totalHeight = 0;
            foreach (var (rs, rc) in rowSpans)
            {
                double sumAspect = 0;
                for (int i = 0; i < rc; i++) sumAspect += aspects[rs + i];
                totalHeight += 1.0 / sumAspect;
            }
            double overallAspect = 1.0 / totalHeight;
            double score = Math.Abs(Math.Log(overallAspect / targetAspect));
            if (score < best) best = score;
        }
        return best;
    }

    [Fact]
    public void Compose_OutputAspectMatchesTarget()
    {
        using var a = SolidImage(200, 100, Color.Red);
        using var b = SolidImage(100, 200, Color.Green);
        using var c = SolidImage(150, 150, Color.Blue);

        var options = new CollageOptions(Color.White, GutterThickness: 0, TargetAspect: 1.0, MaxLongEdge: 400);
        var result = CollageComposer.Compose([a, b, c], options);
        using (result.Image)
        {
            double actualAspect = (double)result.Image.Width / result.Image.Height;
            Assert.Equal(1.0, actualAspect, 1);
        }
    }

    [Fact]
    public void Compose_AreaWeights_SumToOne()
    {
        using var a = SolidImage(200, 100, Color.Red);
        using var b = SolidImage(100, 200, Color.Green);
        using var c = SolidImage(150, 150, Color.Blue);

        var options = new CollageOptions(Color.White, GutterThickness: 4, TargetAspect: 1.0, MaxLongEdge: 400);
        var result = CollageComposer.Compose([a, b, c], options);
        result.Image.Dispose();

        Assert.Equal(3, result.AreaWeights.Count);
        Assert.Equal(1.0, result.AreaWeights.Sum(), 3);
        Assert.All(result.AreaWeights, w => Assert.True(w > 0));
    }

    [Fact]
    public void Compose_ThreeEqualSquares_LoneRowPhotoGetsTwiceTheCombinedPairWeight()
    {
        using var a = SolidImage(100, 100, Color.Red);
        using var b = SolidImage(100, 100, Color.Green);
        using var c = SolidImage(100, 100, Color.Blue);

        var options = new CollageOptions(Color.White, GutterThickness: 0, TargetAspect: 1.0, MaxLongEdge: 300);
        var result = CollageComposer.Compose([a, b, c], options);
        result.Image.Dispose();

        // Matches the hand-derived PlanRows layout for 3 equal squares at a square target:
        // photo[0] alone in a row spanning 2/3 of the height, photos[1..2] paired in the rest.
        Assert.Equal(2.0 / 3.0, result.AreaWeights[0], 2);
        Assert.Equal(1.0 / 6.0, result.AreaWeights[1], 2);
        Assert.Equal(1.0 / 6.0, result.AreaWeights[2], 2);
    }

    [Fact]
    public async Task ExtractWeightedAsync_HeavierPhotoDominatesPalette()
    {
        using var dominant = SolidImage(300, 300, Color.Red);
        using var minor = SolidImage(300, 300, Color.Blue);

        var extractor = new PaletteExtractor();
        var palette = await extractor.ExtractWeightedAsync(
            [(dominant, 0.95), (minor, 0.05)],
            colorCount: 2);

        var top = palette.Swatches[0];
        Assert.Equal("#FF0000", top.Hex);
        Assert.True(top.Percentage > 0.8f);
    }

    private static Image<Rgb24> SolidImage(int w, int h, Color color)
    {
        var img = new Image<Rgb24>(w, h);
        img.Mutate(ctx => ctx.Fill(color));
        return img;
    }
}
