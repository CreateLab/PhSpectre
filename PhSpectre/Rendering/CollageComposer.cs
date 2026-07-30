using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace PhSpectre.Rendering;

// GutterColor/GutterThickness control the background strip between and around photos.
// TargetAspect is the desired width/height of the composed collage (before
// PaletteImageRenderer adds its swatch panel / metadata strip). MaxLongEdge caps the
// composed canvas resolution — the collage is just the "photo" fed into the existing
// render pipeline, so it doesn't need to exceed what that pipeline already targets.
public sealed record CollageOptions(Color GutterColor, int GutterThickness, double TargetAspect, int MaxLongEdge = 2400);

// AreaWeights are parallel to the input photo list and sum to 1 — each is the fraction of
// the collage's total displayed photo area that photo occupies, used to weight palette
// extraction so photos shown larger contribute proportionally more color.
public sealed record CollageResult(Image<Rgb24> Image, IReadOnlyList<double> AreaWeights);

public static class CollageComposer
{
    // One row of the layout: photos [StartIndex, StartIndex+Count) laid out side by side,
    // each keeping its own aspect ratio, sharing the row's height. HeightFraction is this
    // row's share of the total canvas height (all rows' fractions sum to 1).
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

            // Rows are planned in gutter-free normalized space (HeightFraction sums to 1 over
            // canvasH); the gutter is then carved out of each cell's pixel rect so photo
            // content never touches the gutter color, rather than being centered around it.
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

    // Resizes+crops the photo to exactly fill the cell (cover+center, via ImageSharp's
    // built-in ResizeMode.Crop) after insetting the cell by half the gutter on each edge —
    // that's what gives a `gutter`-px gap between neighbors and around the collage edge.
    // Returns the rect actually drawn into, which may equal the un-inset cell if the gutter
    // is larger than the cell itself (pathological input; better to draw edge-to-edge than
    // to vanish the photo).
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

    // Finds the contiguous row partition of `aspectRatios` whose resulting overall canvas
    // aspect ratio is closest to `targetAspect` (a "justified gallery" layout: each row's
    // height is whatever makes its photos, side by side at their own aspect ratios, exactly
    // fill the row width). With the ~2-9 photo soft cap this feature targets, there are at
    // most 2^(n-1) contiguous partitions — cheap enough to search exhaustively for the true
    // best fit instead of a greedy approximation.
    internal static IReadOnlyList<PlannedRow> PlanRows(IReadOnlyList<double> aspectRatios, double targetAspect)
    {
        int n = aspectRatios.Count;
        if (n == 0) return [];
        if (n == 1) return [new PlannedRow(0, 1, 1.0)];

        var prefix = new double[n + 1];
        for (int i = 0; i < n; i++) prefix[i + 1] = prefix[i] + aspectRatios[i];

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
            // Log space makes the score symmetric for equally "wrong" over/under-wide results.
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
}
