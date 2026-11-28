using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace PhSpectre.Rendering;

// TargetAspect is the collage alone, before the swatch panel and metadata strip.
public sealed record CollageOptions(Color GutterColor, int GutterThickness, double TargetAspect, int MaxLongEdge = 2400);

// Each photo's share of the displayed area; sums to 1.
public sealed record CollageResult(Image<Rgb24> Image, IReadOnlyList<double> AreaWeights);

public static class CollageComposer
{
    // A row of photos [StartIndex, StartIndex+Count) sharing its height; HeightFraction values sum to 1.
    internal readonly record struct PlannedRow(int StartIndex, int Count, double HeightFraction);

    public static CollageResult Compose(IReadOnlyList<Image<Rgb24>> photos, CollageOptions options)
    {
        if (photos == null || photos.Count == 0)
            throw new ArgumentException("At least one photo is required.", nameof(photos));

        var aspects = photos.Select(p => (double)p.Width / p.Height).ToList();
        var rows = PlanRows(aspects, options.TargetAspect);

        int canvasW, canvasH;
        if (options.TargetAspect >= 1.0)
        {
            canvasW = options.MaxLongEdge;
            canvasH = Math.Max(1, (int)Math.Round(canvasW / options.TargetAspect));
        }
        else
        {
            canvasH = options.MaxLongEdge;
            canvasW = Math.Max(1, (int)Math.Round(canvasH * options.TargetAspect));
        }

        int gutter = Math.Max(0, options.GutterThickness);
        var canvas = new Image<Rgb24>(canvasW, canvasH);
        var drawnAreas = new double[photos.Count];

        canvas.Mutate(ctx =>
        {
            ctx.Fill(options.GutterColor);

            // Rows are planned without gutters; the gutter is then cut from each cell.
            double y = 0;
            foreach (var row in rows)
            {
                double rowHeightPx = row.HeightFraction * canvasH;
                double rowTop = y;
                y += rowHeightPx;

                double sumAspect = 0;
                for (int i = 0; i < row.Count; i++) sumAspect += aspects[row.StartIndex + i];

                double x = 0;
                for (int i = 0; i < row.Count; i++)
                {
                    int photoIndex = row.StartIndex + i;
                    double cellWidthPx = aspects[photoIndex] / sumAspect * canvasW;

                    int cellX = (int)Math.Round(x);
                    int cellRight = (int)Math.Round(x + cellWidthPx);
                    int cellYTop = (int)Math.Round(rowTop);
                    int cellBottom = (int)Math.Round(rowTop + rowHeightPx);
                    var cellRect = new Rectangle(cellX, cellYTop, cellRight - cellX, cellBottom - cellYTop);
                    x += cellWidthPx;

                    var drawn = DrawPhotoIntoCell(ctx, photos[photoIndex], cellRect, gutter);
                    drawnAreas[photoIndex] = (double)drawn.Width * drawn.Height;
                }
            }
        });

        double totalArea = drawnAreas.Sum();
        var areaWeights = totalArea > 0
            ? drawnAreas.Select(a => a / totalArea).ToArray()
            : Enumerable.Repeat(1.0 / drawnAreas.Length, drawnAreas.Length).ToArray();

        return new CollageResult(canvas, areaWeights);
    }

    // Cover-crops the photo into the cell minus half a gutter per edge; a gutter larger than the cell is ignored.
    private static Rectangle DrawPhotoIntoCell(IImageProcessingContext ctx, Image<Rgb24> photo, Rectangle cellRect, int gutter)
    {
        int insetX = gutter / 2, insetY = gutter / 2;
        var inner = new Rectangle(cellRect.X + insetX, cellRect.Y + insetY,
            cellRect.Width - 2 * insetX, cellRect.Height - 2 * insetY);
        if (inner.Width <= 0 || inner.Height <= 0)
            inner = cellRect;

        using var fitted = photo.Clone(c => c.Resize(new ResizeOptions
        {
            Mode    = ResizeMode.Crop,
            Size    = new Size(inner.Width, inner.Height),
            Sampler = KnownResamplers.Lanczos3
        }));
        ctx.DrawImage(fitted, new Point(inner.X, inner.Y), 1f);
        return inner;
    }

    // Every partition is tried up to this many photos; the count doubles with each one more.
    private const int ExhaustiveLimit = 16;

    // The contiguous row partition closest to targetAspect.
    internal static IReadOnlyList<PlannedRow> PlanRows(IReadOnlyList<double> aspectRatios, double targetAspect)
    {
        int n = aspectRatios.Count;
        if (n == 0) return [];
        if (n == 1) return [new PlannedRow(0, 1, 1.0)];

        var prefix = new double[n + 1];
        for (int i = 0; i < n; i++) prefix[i + 1] = prefix[i] + aspectRatios[i];
        if (n > ExhaustiveLimit) return PlanBalancedRows(prefix, targetAspect);

        IReadOnlyList<PlannedRow>? best = null;
        double bestScore = double.MaxValue;

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

            var heights = new double[rowSpans.Count];
            double totalHeight = 0;
            for (int r = 0; r < rowSpans.Count; r++)
            {
                double sumAspect = prefix[rowSpans[r].Start + rowSpans[r].Count] - prefix[rowSpans[r].Start];
                double h = 1.0 / sumAspect;
                heights[r] = h;
                totalHeight += h;
            }

            double overallAspect = 1.0 / totalHeight;
            // Log space scores too wide and too tall alike.
            double score = Math.Abs(Math.Log(overallAspect / targetAspect));

            if (score < bestScore)
            {
                bestScore = score;
                best = rowSpans
                    .Select((row, r) => new PlannedRow(row.Start, row.Count, heights[r] / totalHeight))
                    .ToList();
            }
        }

        return best!;
    }

    // For many photos: for each number of rows, cut where the running width passes an equal share.
    private static IReadOnlyList<PlannedRow> PlanBalancedRows(double[] prefix, double targetAspect)
    {
        int n = prefix.Length - 1;
        List<PlannedRow>? best = null;
        double bestScore = double.MaxValue;

        for (int rows = 1; rows <= n; rows++)
        {
            var spans = new List<(int Start, int Count)>();
            int start = 0;
            for (int r = 1; r <= rows; r++)
            {
                double share = prefix[n] * r / rows;
                int end = start + 1;
                // Leave at least one photo for each row still to come.
                while (end < n - (rows - r) && Math.Abs(prefix[end + 1] - share) <= Math.Abs(prefix[end] - share)) end++;
                if (r == rows) end = n;
                spans.Add((start, end - start));
                start = end;
            }

            var heights = spans.Select(s => 1.0 / (prefix[s.Start + s.Count] - prefix[s.Start])).ToList();
            double total = heights.Sum();
            double score = Math.Abs(Math.Log(1.0 / total / targetAspect));
            if (score >= bestScore) continue;
            bestScore = score;
            best = spans.Select((s, i) => new PlannedRow(s.Start, s.Count, heights[i] / total)).ToList();
        }

        return best!;
    }
}
