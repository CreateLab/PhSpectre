using System.Linq;
using PhSpectre.Models;
using PhSpectre.Recipes;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace PhSpectre.Rendering;

// Renders a standalone film-recipe card: the source photo (downscaled, rounded corners) on
// top, a grid of parameter plates below. Sibling to PaletteImageRenderer, reusing its Theme/
// OutputFormat enums and a few of its drawing helpers (promoted to internal) rather than
// duplicating rounded-rect/theme/font logic.
public static class RecipeCardRenderer
{
    private const int CardWidth = 1200;

    public static void Render(
        string sourceImagePath,
        FilmRecipe recipe,
        string outputPath,
        Theme theme = Theme.Dark,
        OutputFormat format = OutputFormat.Png,
        Color? customBackground = null,
        MetaVerbosity metaVerbosity = MetaVerbosity.Off,
        PaletteImageRenderer.PhotoMetadata? metadataOverride = null)
    {
        using var original = Image.Load<Rgb24>(sourceImagePath);
        original.Mutate(ctx => ctx.AutoOrient());
        using var canvas = BuildCanvas(original, recipe, theme, customBackground, metaVerbosity, metadataOverride);
        Save(canvas, outputPath, format);
    }

    // From an already-decoded image (caller owns disposal) — mirrors PaletteImageRenderer's
    // equivalent overload for callers that already hold the working copy in memory.
    public static void Render(
        Image<Rgb24> original,
        FilmRecipe recipe,
        string outputPath,
        Theme theme = Theme.Dark,
        OutputFormat format = OutputFormat.Png,
        Color? customBackground = null,
        MetaVerbosity metaVerbosity = MetaVerbosity.Off,
        PaletteImageRenderer.PhotoMetadata? metadataOverride = null)
    {
        using var canvas = BuildCanvas(original, recipe, theme, customBackground, metaVerbosity, metadataOverride);
        Save(canvas, outputPath, format);
    }

    // In-memory variant — used by the app's live preview so what's shown on screen for
    // Recipe mode is pixel-for-pixel the same composite Save recipe card exports, not just a
    // visually-similar approximation. Caller owns disposal of the returned image.
    public static Image<Rgb24> RenderToImage(
        Image<Rgb24> original,
        FilmRecipe recipe,
        Theme theme = Theme.Dark,
        Color? customBackground = null,
        MetaVerbosity metaVerbosity = MetaVerbosity.Off,
        PaletteImageRenderer.PhotoMetadata? metadataOverride = null)
        => BuildCanvas(original, recipe, theme, customBackground, metaVerbosity, metadataOverride);

    private static void Save(Image<Rgb24> canvas, string outputPath, OutputFormat format)
    {
        if (format == OutputFormat.Jpeg)
            canvas.SaveAsJpeg(outputPath, new JpegEncoder { Quality = 92 });
        else
            canvas.SaveAsPng(outputPath, new PngEncoder { FilterMethod = PngFilterMethod.None });
    }

    // Non-null (Label, Value) rows only — shared by the renderer and any UI code wanting the
    // exact same "what's shown" logic (kept here since it's a rendering concern, not a
    // ViewModel concern; the Avalonia ViewModel builds an equivalent list independently for
    // its own live display, since it doesn't reference PhSpectre.Rendering's raster types).
    internal static List<(string Label, string Value)> BuildRows(FilmRecipe r)
    {
        var rows = new List<(string, string)>();
        // Shortened first — a plate cell is a fixed width, and the full text of values like
        // "Auto (white priority)" doesn't fit at a legible size (bugfix-pass §2).
        void Add(string label, string? value) { if (!string.IsNullOrEmpty(value)) rows.Add((label, RecipeValueAbbreviations.Shorten(value))); }

        Add("White Balance", r.WhiteBalance);
        if (r.WhiteBalanceShift is { } shift) Add("WB Shift", $"R{shift.Red:+0;-0;0} B{shift.Blue:+0;-0;0}");
        Add("Dynamic Range", r.DynamicRange);
        if (r.HighlightTone is { } hi) Add("Highlight", hi.ToString("+0;-0;0"));
        if (r.ShadowTone is { } sh) Add("Shadow", sh.ToString("+0;-0;0"));
        if (r.Color is { } col) Add("Color", col.ToString());
        if (r.Sharpness is { } sharp) Add("Sharpness", sharp.ToString("+0;-0;0"));
        if (r.NoiseReduction is { } nr) Add("Noise Reduction", nr.ToString("+0;-0;0"));
        if (r.Clarity is { } clarity) Add("Clarity", clarity.ToString("+0.#;-0.#;0"));
        Add("Grain Effect", r.GrainEffect);
        Add("Color Chrome", r.ColorChromeEffect);
        Add("Color Chrome Blue", r.ColorChromeFxBlue);
        if (r.BwAdjustment is { } bw) Add("B&W Adjustment", bw.ToString("+0;-0;0"));

        return rows;
    }

    private static Image<Rgb24> BuildCanvas(
        Image<Rgb24> original, FilmRecipe recipe,
        Theme theme, Color? customBackground,
        MetaVerbosity metaVerbosity = MetaVerbosity.Off,
        PaletteImageRenderer.PhotoMetadata? metadataOverride = null)
    {
        var rows = BuildRows(recipe);

        // Camera info plate (bugfix §2): only actually drawn when metaVerbosity isn't Off —
        // callers pass PaletteExportSettings.MetaVerbosity, which is already forced to Off
        // whenever the "Show camera info plate" toggle is unchecked, so this one check is
        // both the "is the toggle on" check and the verbosity-level check at once.
        PaletteImageRenderer.PhotoMetadata? exif = metaVerbosity == MetaVerbosity.Off
            ? null
            : (metadataOverride ?? PaletteImageRenderer.ReadMetadata(original));
        var (metaLines, metaStripH, metaFs, metaFont) = exif != null
            ? PaletteImageRenderer.PrepareStrip(exif, metaVerbosity, CardWidth)
            : ([], 0, 0f, null);
        var bg = PaletteImageRenderer.GetBackgroundColor(theme, customBackground);
        var text = PaletteImageRenderer.GetTextColor(theme, customBackground);
        var plateFill = theme == Theme.Light ? Color.ParseHex("EAEAEA") : Color.ParseHex("222222");

        int margin = CardWidth / 30;
        int photoH = Math.Max(1, (int)Math.Round(CardWidth * (double)original.Height / original.Width));
        float photoRadius = CardWidth * 0.02f;

        string title = string.IsNullOrEmpty(recipe.FilmSimulation) ? "Custom Recipe" : recipe.FilmSimulation!;
        float titleFs = CardWidth / 22f;
        var titleFont = PaletteImageRenderer.ResolveMetaFont(titleFs);
        int titleH = (int)(titleFs * 1.8f);

        // Grid geometry: as many columns as fit a comfortable target plate width, wrapped
        // into rows — same "evenly divide available width" spirit as the swatch panel in
        // PaletteImageRenderer, generalized to multiple rows since a recipe can have up to
        // ~11 parameters versus a handful of swatches in one row.
        const int targetPlateW = 260;
        int gap = margin / 2;
        int cols = Math.Max(1, Math.Min(rows.Count, (CardWidth - 2 * margin + gap) / (targetPlateW + gap)));
        int plateW = cols > 0 ? (CardWidth - 2 * margin - gap * (cols - 1)) / cols : 0;

        // Even after RecipeValueAbbreviations.Shorten, a value can still be too wide for a
        // one-line fit at the plate's fixed width (bugfix-pass §2) — rather than let it spill
        // past the plate's rounded-rect, every plate in the grid gets extra height for a
        // second wrapped line whenever ANY value needs it, so the grid stays visually even.
        var valueFontProbe = PaletteImageRenderer.ResolveMetaFont(titleFs * 0.5f);
        float wrapWidth = plateW * 0.88f;
        bool anyValueWraps = rows.Any(r =>
            TextMeasurer.MeasureSize(r.Value, new TextOptions(valueFontProbe)).Width > wrapWidth);

        int plateH = (int)(titleFs * (anyValueWraps ? 3.3f : 2.4f));
        int gridRows = rows.Count == 0 ? 0 : (rows.Count + cols - 1) / cols;
        int gridH = gridRows == 0 ? 0 : gridRows * plateH + (gridRows - 1) * gap;

        int canvasH = photoH + margin + titleH + (gridH > 0 ? margin / 2 + gridH : 0) + margin + metaStripH;
        var canvas = new Image<Rgb24>(CardWidth, canvasH);
        canvas.Mutate(ctx =>
        {
            ctx.Fill(bg);

            using var roundedPhoto = ApplyRoundedCorners(original, CardWidth, photoH, photoRadius);
            ctx.DrawImage(roundedPhoto, new Point(0, 0), 1f);

            int titleY = photoH + margin;
            ctx.DrawText(new RichTextOptions(titleFont)
            {
                Origin = new PointF(margin, titleY),
                HorizontalAlignment = HorizontalAlignment.Left,
            }, title, text);

            int gridY = titleY + titleH + margin / 2;
            var labelFont = PaletteImageRenderer.ResolveMetaFont(titleFs * 0.34f);
            var valueFont = PaletteImageRenderer.ResolveMetaFont(titleFs * 0.5f);
            for (int i = 0; i < rows.Count; i++)
            {
                int col = i % cols;
                int row = i / cols;
                int x = margin + col * (plateW + gap);
                int y = gridY + row * (plateH + gap);

                ctx.Fill(plateFill, PaletteImageRenderer.RoundedRectPath(x, y, plateW, plateH, plateH * 0.12f));

                var (label, value) = rows[i];
                float cx = x + plateW / 2f;
                float labelY = y + plateH * (anyValueWraps ? 0.2f : 0.28f);
                float valueY = y + plateH * (anyValueWraps ? 0.62f : 0.68f);
                ctx.DrawText(new RichTextOptions(labelFont)
                {
                    Origin = new PointF(cx, labelY),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                }, label, text.WithAlpha(0.65f));
                ctx.DrawText(new RichTextOptions(valueFont)
                {
                    Origin = new PointF(cx, valueY),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    WrappingLength = wrapWidth,
                    TextAlignment = TextAlignment.Center,
                }, value, text);
            }

            if (metaStripH > 0)
            {
                int stripY = canvasH - metaStripH;
                PaletteImageRenderer.DrawStrip(ctx, metaLines, metaStripH, metaFs, metaFont,
                    MetaStyle.FilmStrip, theme, x: 0, y: stripY, w: CardWidth, customBackground: customBackground);
            }
        });

        return canvas;
    }

    // Downscales the source photo to (w, h) covering the target box (crop-to-fill, matching
    // the "cover" convention already used elsewhere in this app's cell/preview rendering)
    // and clips it to a rounded rectangle. Standard ImageSharp technique: paint the source
    // through an ImageBrush onto a fully-transparent Rgba32 canvas, restricted to the rounded
    // path — everything outside the path stays transparent and shows the card background
    // through when composited onto it.
    private static Image<Rgba32> ApplyRoundedCorners(Image<Rgb24> source, int w, int h, float radius)
    {
        using var resized = source.Clone(ctx => ctx.Resize(new ResizeOptions
        {
            Size = new Size(w, h),
            Mode = ResizeMode.Crop,
        }));

        var rounded = new Image<Rgba32>(w, h);
        rounded.Mutate(ctx =>
        {
            var path = PaletteImageRenderer.RoundedRectPath(0, 0, w, h, radius);
            ctx.Fill(new ImageBrush(resized), path);
        });
        return rounded;
    }
}
