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

// Renders a standalone film-recipe card: the photo (downscaled, rounded corners) on top, a grid of parameter plates below.
// Reuses PaletteImageRenderer's enums and drawing helpers.
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
        PaletteImageRenderer.PhotoMetadata? metadataOverride = null,
        float labelScale = 1f,
        bool useBlurredBackground = false,
        QrRenderOptions? qr = null,
        bool showRecipeCard = true,
        CompositionGuide compositionGuide = CompositionGuide.None,
        string? hiddenRecipeFields = null,
        MetaStyle metaStyle = MetaStyle.FilmStrip)
    {
        using var original = Image.Load<Rgb24>(sourceImagePath);
        original.Mutate(ctx => ctx.AutoOrient());
        using var canvas = BuildCanvas(original, recipe, theme, customBackground, metaVerbosity, metadataOverride, labelScale, useBlurredBackground, qr, showRecipeCard, compositionGuide, hiddenRecipeFields, metaStyle);
        Save(canvas, outputPath, format);
    }

    // From an already-decoded image (caller owns disposal).
    public static void Render(
        Image<Rgb24> original,
        FilmRecipe recipe,
        string outputPath,
        Theme theme = Theme.Dark,
        OutputFormat format = OutputFormat.Png,
        Color? customBackground = null,
        MetaVerbosity metaVerbosity = MetaVerbosity.Off,
        PaletteImageRenderer.PhotoMetadata? metadataOverride = null,
        float labelScale = 1f,
        bool useBlurredBackground = false,
        QrRenderOptions? qr = null,
        bool showRecipeCard = true,
        CompositionGuide compositionGuide = CompositionGuide.None,
        string? hiddenRecipeFields = null,
        MetaStyle metaStyle = MetaStyle.FilmStrip)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var canvas = BuildCanvas(original, recipe, theme, customBackground, metaVerbosity, metadataOverride, labelScale, useBlurredBackground, qr, showRecipeCard, compositionGuide, hiddenRecipeFields, metaStyle);
        RenderPerfLog.OnStage?.Invoke("BuildCanvas total", sw.ElapsedMilliseconds);
        sw.Restart();
        Save(canvas, outputPath, format);
        RenderPerfLog.OnStage?.Invoke("Save/encode", sw.ElapsedMilliseconds);
    }

    // In-memory variant: the live preview is the same composite Save recipe card exports. Caller owns disposal of the result.
    public static Image<Rgb24> RenderToImage(
        Image<Rgb24> original,
        FilmRecipe recipe,
        Theme theme = Theme.Dark,
        Color? customBackground = null,
        MetaVerbosity metaVerbosity = MetaVerbosity.Off,
        PaletteImageRenderer.PhotoMetadata? metadataOverride = null,
        float labelScale = 1f,
        bool useBlurredBackground = false,
        QrRenderOptions? qr = null,
        bool showRecipeCard = true,
        CompositionGuide compositionGuide = CompositionGuide.None,
        string? hiddenRecipeFields = null,
        MetaStyle metaStyle = MetaStyle.FilmStrip)
        => BuildCanvas(original, recipe, theme, customBackground, metaVerbosity, metadataOverride, labelScale, useBlurredBackground, qr, showRecipeCard, compositionGuide, hiddenRecipeFields, metaStyle);

    private static void Save(Image<Rgb24> canvas, string outputPath, OutputFormat format)
    {
        if (format == OutputFormat.Jpeg)
            canvas.SaveAsJpeg(outputPath, new JpegEncoder { Quality = ExportDefaults.JpegQuality });
        else
            canvas.SaveAsPng(outputPath, new PngEncoder { FilterMethod = PngFilterMethod.None });
    }

    // Non-null (Label, Value) rows, shared by the renderer and UI code that wants the same logic.
    internal static List<(string Label, string Value)> BuildRows(FilmRecipe r, IReadOnlySet<string>? hidden = null) =>
        RecipeFields.BuildVisible(r, hidden).Select(f => (f.FullLabel, f.Value)).ToList();

    private static Image<Rgb24> BuildCanvas(
        Image<Rgb24> original, FilmRecipe recipe,
        Theme theme, Color? customBackground,
        MetaVerbosity metaVerbosity = MetaVerbosity.Off,
        PaletteImageRenderer.PhotoMetadata? metadataOverride = null,
        float labelScale = 1f,
        bool useBlurredBackground = false,
        QrRenderOptions? qr = null,
        bool showRecipeCard = true,
        CompositionGuide compositionGuide = CompositionGuide.None,
        string? hiddenRecipeFields = null,
        MetaStyle metaStyle = MetaStyle.FilmStrip)
    {
        var prepSw = System.Diagnostics.Stopwatch.StartNew();
        // Independent of QR: unchecking "Show recipe card" drops the title and plate grid, leaving just the photo and QR block.
        // The camera-info strip (metaVerbosity) is a separate toggle.
        var rows = showRecipeCard ? BuildRows(recipe, RecipeFields.ParseKeys(hiddenRecipeFields)) : [];

        // The camera plate is drawn only when metaVerbosity isn't Off; callers pass PaletteExportSettings.MetaVerbosity, already Off when the toggle is unchecked.
        PaletteImageRenderer.PhotoMetadata? exif = metaVerbosity == MetaVerbosity.Off
            ? null
            : (metadataOverride ?? PaletteImageRenderer.ReadMetadata(original));
        int margin = PaletteImageRenderer.CardPadding(CardWidth);

        // QR first: the camera plate shares the bottom row with the code, so it is fitted to the width the code leaves.
        int qrBoxSize = QrCodeRenderer.SuggestBoxSize(CardWidth);
        int qrBelowH  = QrCodeRenderer.ComputeBelowBlockHeight(qr, qrBoxSize, margin);
        int qrImgSize = qr != null ? QrCodeRenderer.PredictImageSize(qr, qrBoxSize) : 0;
        // Overlay draws the camera text over the photo, so it never takes the bottom row or a strip of its own.
        bool overlay = metaStyle == MetaStyle.Overlay;
        bool plateBesideQr = !overlay && qr != null && CardWidth - (qrImgSize + 2 * margin) >= CardWidth * 0.4;
        int plateWidth = plateBesideQr ? CardWidth - (qrImgSize + 2 * margin) : CardWidth;

        var (metaLines, metaStripH, metaFs, metaFont) = exif != null
            ? PaletteImageRenderer.PrepareStrip(exif, metaVerbosity, plateWidth)
            : ([], 0, 0f, null);
        bool plateInRow = plateBesideQr && metaLines.Length > 0;
        var text = PaletteImageRenderer.GetTextColor(theme, customBackground);
        var plateFill = theme == Theme.Light ? Color.ParseHex("EAEAEA") : Color.ParseHex("222222");
        // Over a blurred backdrop a fully opaque plate reads as a harsh cutout, so it is blended in frosted-glass style.
        // The color stays opaque here; the opacity is applied via DrawImage (ctx.Fill with a translucent color replaces pixels in this ImageSharp version).
        const float platesOpacityOnBlur = 0.4f;

        int photoH = Math.Max(1, (int)Math.Round(CardWidth * (double)original.Height / original.Width));
        float photoRadius = CardWidth * 0.02f;

        string title = string.IsNullOrEmpty(recipe.FilmSimulation) ? "Custom Recipe" : recipe.FilmSimulation!;
        // labelScale drives the title, plate label and value fonts together; plateH, titleH and the grid derive from titleFs.
        float titleFs = CardWidth / 22f * Math.Clamp(labelScale, 0.5f, 2f);
        var titleFont = PaletteImageRenderer.ResolveMetaFont(titleFs);
        int titleH = showRecipeCard ? (int)(titleFs * 1.8f) : 0;

        // Grid geometry: as many columns as fit a comfortable plate width, wrapped into rows (a recipe has up to ~11 parameters).
        const int targetPlateW = 260;
        int gap = margin / 2;
        // Always 4 columns: the plate width comes from the full grid, so with fewer visible fields
        // (or an incomplete last row) the plates keep their size instead of stretching across the card.
        const int cols = 4;
        int plateW = (CardWidth - 2 * margin - gap * (cols - 1)) / cols;

        // A value can still be too wide for one line after RecipeValueAbbreviations.Shorten; when any value needs it,
        // every plate gets height for a second line so the grid stays even.
        var valueFontProbe = PaletteImageRenderer.ResolveMetaFont(titleFs * 0.5f);
        float wrapWidth = plateW * 0.88f;
        bool anyValueWraps = rows.Any(r =>
            TextMeasurer.MeasureSize(r.Value, new TextOptions(valueFontProbe)).Width > wrapWidth);

        int plateH = (int)(titleFs * (anyValueWraps ? 3.3f : 2.4f));
        int gridRows = rows.Count == 0 ? 0 : (rows.Count + cols - 1) / cols;
        int gridH = gridRows == 0 ? 0 : gridRows * plateH + (gridRows - 1) * gap;

        // Bottom row (Cards-Recipe): camera plate on the left, QR on the right, both centered in
        // one row of the taller one's height. Without a plate the QR keeps a row of its own.
        int stripLayoutH = overlay ? 0 : metaStripH;
        int bottomRowH = plateInRow ? Math.Max(qrBelowH, metaStripH) : qrBelowH + stripLayoutH;
        int titleY   = photoH + margin;
        int canvasH  = photoH + margin + titleH + (gridH > 0 ? margin / 2 + gridH : 0) + margin + bottomRowH;
        int qrBlockY = plateInRow ? canvasH - bottomRowH : canvasH - stripLayoutH - qrBelowH;
        RenderPerfLog.OnStage?.Invoke("rows+metadata+layout prep", prepSw.ElapsedMilliseconds);
        var canvas = new Image<Rgb24>(CardWidth, canvasH);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        void Stage(string name) { RenderPerfLog.OnStage?.Invoke(name, sw.ElapsedMilliseconds); sw.Restart(); }
        canvas.Mutate(ctx =>
        {
            PaletteImageRenderer.FillCardBackground(ctx, original, CardWidth, canvasH, theme, customBackground, useBlurredBackground,
                photoBoxHeight: photoH);
            Stage("FillCardBackground");

            using var roundedPhoto = ApplyRoundedCorners(original, CardWidth, photoH, photoRadius);
            ctx.DrawImage(roundedPhoto, new Point(0, 0), 1f);
            Stage("ApplyRoundedCorners+DrawImage");

            PaletteImageRenderer.DrawCompositionGuide(ctx, compositionGuide, 0, 0, CardWidth, photoH);
            Stage("DrawCompositionGuide");

            if (showRecipeCard)
            {
                ctx.DrawText(new RichTextOptions(titleFont)
                {
                    Origin = new PointF(margin, titleY),
                    HorizontalAlignment = HorizontalAlignment.Left,
                }, title, text);
                Stage("title DrawText");

                int gridY = titleY + titleH + margin / 2;
                var labelFont = PaletteImageRenderer.ResolveMetaFont(titleFs * 0.34f);
                var valueFont = PaletteImageRenderer.ResolveMetaFont(titleFs * 0.5f);
                for (int i = 0; i < rows.Count; i++)
                {
                    int col = i % cols;
                    int row = i / cols;
                    int x = margin + col * (plateW + gap);
                    int y = gridY + row * (plateH + gap);

                    if (useBlurredBackground)
                    {
                        // Paint the plate opaque on its own transparent canvas, then composite with DrawImage's opacity,
                        // the blending path that alpha-composites correctly in this ImageSharp version (see ApplyRoundedCorners).
                        using var plateImg = new Image<Rgba32>(plateW, plateH);
                        plateImg.Mutate(pc => pc.Fill(plateFill, PaletteImageRenderer.RoundedRectPath(0, 0, plateW, plateH, plateH * 0.12f)));
                        ctx.DrawImage(plateImg, new Point(x, y), platesOpacityOnBlur);
                    }
                    else
                    {
                        ctx.Fill(plateFill, PaletteImageRenderer.RoundedRectPath(x, y, plateW, plateH, plateH * 0.12f));
                    }

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
            }
            Stage($"plates grid ({rows.Count} rows)");

            if (metaStripH > 0)
            {
                int stripY = overlay ? 0 : plateInRow ? qrBlockY + (bottomRowH - metaStripH) / 2 : canvasH - metaStripH;
                PaletteImageRenderer.DrawStrip(ctx, metaLines, metaStripH, metaFs, metaFont,
                    metaStyle, theme, x: 0, y: stripY, w: plateInRow ? plateWidth : CardWidth,
                    overlayTextColor: overlay ? PaletteImageRenderer.GetOverlayTextColor(original) : null, customBackground: customBackground,
                    useBlurredBackground: useBlurredBackground);
            }
            Stage("DrawStrip");

            if (qr != null)
                QrCodeRenderer.DrawBelowBlock(ctx, qr.TargetUrl, qrBoxSize, margin,
                    blockY: qrBlockY, blockWidth: CardWidth,
                    qr.Caption, qr.CaptionCamera, qr.CaptionLens,
                    theme, customBackground,
                    rowHeight: plateInRow ? bottomRowH : qrBelowH, plateInRow: plateInRow);
            Stage("QrCode");
        });

        return canvas;
    }

    // Downscales the photo to cover (w, h) (crop-to-fill) and clips it to a rounded rect: paint through an ImageBrush onto a transparent canvas,
    // restricted to the rounded path, so the card background shows through outside it.
    private static Image<Rgba32> ApplyRoundedCorners(Image<Rgb24> source, int w, int h, float radius)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        // The default sampler (Bicubic) took ~2s on-device for this ~1.67x downscale; Box (as in ImageLoader.LoadWorkingCopy) looks the same and is far cheaper.
        using var resized = source.Clone(ctx => ctx.Resize(new ResizeOptions
        {
            Size    = new Size(w, h),
            Mode    = ResizeMode.Crop,
            Sampler = KnownResamplers.Box
        }));
        RenderPerfLog.OnStage?.Invoke($"  ApplyRoundedCorners: Resize to {w}x{h}", sw.ElapsedMilliseconds);
        sw.Restart();

        var rounded = new Image<Rgba32>(w, h);
        rounded.Mutate(ctx =>
        {
            var path = PaletteImageRenderer.RoundedRectPath(0, 0, w, h, radius);
            ctx.Clip(path, c => c.DrawImage(resized, Point.Empty, 1f));
        });
        RenderPerfLog.OnStage?.Invoke("  ApplyRoundedCorners: Clip+DrawImage", sw.ElapsedMilliseconds);
        return rounded;
    }
}
