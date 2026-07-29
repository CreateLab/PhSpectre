using PhSpectre.Models;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using System.Globalization;

namespace PhSpectre.Rendering;

public enum MetaVerbosity { Off, Short, Default, Detail, Full }
public enum MetaStyle    { FilmStrip, Overlay }
public enum Theme        { Dark, Light }
public enum OutputFormat { Png, Jpeg }

// Fixed-size frames for sharing to social apps. The finished card (photo + swatches +
// metadata) is letterboxed into the frame at its native aspect ratio rather than cropped,
// so nothing in the composition gets cut off. Instagram and Telegram don't actually agree
// on one universal size — IG's feed default moved to 4:5, Telegram's chat-photo sweet spot
// is a different square/landscape pair, and stories (shared by both) reserve top/bottom
// space for UI chrome — so these are kept as distinct, platform-accurate presets rather
// than one generic "square/story" pair.
public enum ExportPreset { Original, Square, InstagramPost, Story, TelegramLandscape }

public enum SwatchShape { Rectangle, Rounded, Circle }
public enum SortOrder   { None, Hue, Luminance, Percent }

public static class PaletteImageRenderer
{
    private record ThemeColors(Color Background, Color Text);

    private static ThemeColors GetThemeColors(Theme theme, Color? customBackground = null)
    {
        if (customBackground is { } bg)
        {
            var px = bg.ToPixel<Rgb24>();
            return new ThemeColors(bg, ContrastColor(px.R, px.G, px.B));
        }
        return theme == Theme.Light
            ? new ThemeColors(Color.ParseHex("F5F5F5"), Color.ParseHex("1A1A1A"))
            : new ThemeColors(Color.ParseHex("111111"), Color.White);
    }

    // 12 display-formatted metadata fields drawn into the strip/overlay. Camera, Lens, Focal,
    // Aperture, Shutter, Iso, and Date are user-editable in the settings UI; the rest
    // (FocalEq, ExposureBias, WhiteBalance, ExpProgram, Serial) are always auto-read from EXIF.
    public record PhotoMetadata(
        string Camera, string Lens,
        string Focal,  string FocalEq,
        string Aperture, string Shutter, string Iso,
        string Date, string ExposureBias,
        string WhiteBalance, string ExpProgram, string Serial);

    public static void Render(
        string sourceImagePath,
        ColorPalette palette,
        string outputPath,
        bool showHex = true,
        MetaVerbosity metaVerbosity = MetaVerbosity.Default,
        MetaStyle metaStyle = MetaStyle.FilmStrip,
        Theme theme = Theme.Dark,
        bool hexBelow = false,
        bool showSwatches = true,
        int downscale = 1,
        OutputFormat format = OutputFormat.Png,
        ExportPreset exportPreset = ExportPreset.Original,
        PhotoMetadata? metadataOverride = null,
        float labelScale = 1.0f,
        float swatchScale = 1.0f,
        bool showPercent = false,
        SwatchShape swatchShape = SwatchShape.Rectangle,
        SortOrder sortOrder = SortOrder.None,
        Color? customBackground = null)
    {
        using var original = Image.Load<Rgb24>(sourceImagePath);
        original.Mutate(ctx => ctx.AutoOrient());
        RenderCore(original, palette, outputPath, showHex, metaVerbosity, metaStyle, theme, hexBelow, showSwatches, downscale, format, exportPreset, metadataOverride,
            labelScale, swatchScale, showPercent, swatchShape, sortOrder, customBackground);
    }

    // Renders from an already-decoded image (caller owns disposal) — skips a redundant
    // re-decode when the caller (e.g. the mobile pipeline) already holds the working copy
    // in memory. The image is assumed already auto-oriented by the caller.
    public static void Render(
        Image<Rgb24> original,
        ColorPalette palette,
        string outputPath,
        bool showHex = true,
        MetaVerbosity metaVerbosity = MetaVerbosity.Default,
        MetaStyle metaStyle = MetaStyle.FilmStrip,
        Theme theme = Theme.Dark,
        bool hexBelow = false,
        bool showSwatches = true,
        int downscale = 1,
        OutputFormat format = OutputFormat.Png,
        ExportPreset exportPreset = ExportPreset.Original,
        PhotoMetadata? metadataOverride = null,
        float labelScale = 1.0f,
        float swatchScale = 1.0f,
        bool showPercent = false,
        SwatchShape swatchShape = SwatchShape.Rectangle,
        SortOrder sortOrder = SortOrder.None,
        Color? customBackground = null)
        => RenderCore(original, palette, outputPath, showHex, metaVerbosity, metaStyle, theme, hexBelow, showSwatches, downscale, format, exportPreset, metadataOverride,
            labelScale, swatchScale, showPercent, swatchShape, sortOrder, customBackground);

    private static void RenderCore(
        Image<Rgb24> original,
        ColorPalette palette,
        string outputPath,
        bool showHex,
        MetaVerbosity metaVerbosity,
        MetaStyle metaStyle,
        Theme theme,
        bool hexBelow,
        bool showSwatches,
        int downscale,
        OutputFormat format,
        ExportPreset exportPreset,
        PhotoMetadata? metadataOverride = null,
        float labelScale = 1.0f,
        float swatchScale = 1.0f,
        bool showPercent = false,
        SwatchShape swatchShape = SwatchShape.Rectangle,
        SortOrder sortOrder = SortOrder.None,
        Color? customBackground = null)
    {
        labelScale = Math.Clamp(labelScale, 0.5f, 2.0f);
        swatchScale = Math.Clamp(swatchScale, 0.5f, 2.0f);

        PhotoMetadata? exif = metaVerbosity == MetaVerbosity.Off ? null : (metadataOverride ?? ReadMetadata(original));

        ColorPalette effectivePalette = sortOrder == SortOrder.None
            ? palette
            : new ColorPalette(SortSwatches(palette.Swatches, sortOrder));

        bool landscape = original.Width >= original.Height;
        var canvas = landscape
            ? BuildLandscapeCanvas(original, effectivePalette, showHex, hexBelow, exif, metaVerbosity, metaStyle, theme, showSwatches, labelScale, swatchScale, showPercent, swatchShape, customBackground)
            : BuildPortraitCanvas(original, effectivePalette, showHex, hexBelow, exif, metaVerbosity, metaStyle, theme, showSwatches, labelScale, swatchScale, showPercent, swatchShape, customBackground);

        if (downscale > 1)
            canvas.Mutate(ctx => ctx.Resize(canvas.Width / downscale, canvas.Height / downscale));

        if (exportPreset != ExportPreset.Original)
        {
            // (width, height, safe-top, safe-bottom) — safe margins keep content clear of
            // the story UI chrome (profile/timestamp bar on top, reply box on bottom).
            var (targetW, targetH, safeTop, safeBottom) = exportPreset switch
            {
                ExportPreset.Square            => (1080, 1080, 0, 0),
                ExportPreset.InstagramPost     => (1080, 1350, 0, 0),   // IG feed default is now 4:5
                ExportPreset.Story             => (1080, 1920, 250, 340),
                ExportPreset.TelegramLandscape => (1920, 1080, 0, 0),
                _ => (canvas.Width, canvas.Height, 0, 0)
            };
            var framed = FitToFrame(canvas, targetW, targetH, GetThemeColors(theme, customBackground).Background, safeTop, safeBottom);
            canvas.Dispose();
            canvas = framed;
        }

        using (canvas)
        {
            if (format == OutputFormat.Jpeg)
                canvas.SaveAsJpeg(outputPath, new JpegEncoder { Quality = 92 });
            else
                // Adaptive filtering (ImageSharp's default) tries all 5 PNG row filters
                // per scanline and picks the smallest — ~2x+ slower than a fixed filter
                // for ~30-45% smaller output. Not worth it on multi-thousand-pixel photo
                // canvases where this encode was the single largest cost in the pipeline.
                canvas.SaveAsPng(outputPath, new PngEncoder { FilterMethod = PngFilterMethod.None });
        }
    }

    // Scales the finished card to fit inside a fixed-size frame (never cropping) and
    // centers it on the theme background — used for social presets (§ExportPreset).
    // safeTop/safeBottom shrink the region the content is allowed to occupy (and center
    // within), without changing the frame's actual pixel dimensions.
    private static Image<Rgb24> FitToFrame(Image<Rgb24> canvas, int targetW, int targetH, Color background, int safeTop = 0, int safeBottom = 0)
    {
        int innerH = targetH - safeTop - safeBottom;
        double scale = Math.Min((double)targetW / canvas.Width, (double)innerH / canvas.Height);
        int newW = Math.Max(1, (int)Math.Round(canvas.Width * scale));
        int newH = Math.Max(1, (int)Math.Round(canvas.Height * scale));

        var frame = new Image<Rgb24>(targetW, targetH);
        frame.Mutate(ctx =>
        {
            ctx.Fill(background);
            using var resized = canvas.Clone(c => c.Resize(newW, newH));
            int x = (targetW - newW) / 2;
            int y = safeTop + (innerH - newH) / 2;
            ctx.DrawImage(resized, new Point(x, y), 1f);
        });
        return frame;
    }

    // ── Landscape: photo → swatches → [filmstrip] ──────────────────────────

    private static Image<Rgb24> BuildLandscapeCanvas(
        Image<Rgb24> original, ColorPalette palette, bool showHex, bool hexBelow,
        PhotoMetadata? exif, MetaVerbosity verbosity, MetaStyle style, Theme theme,
        bool showSwatches = true,
        float labelScale = 1.0f, float swatchScale = 1.0f, bool showPercent = false,
        SwatchShape swatchShape = SwatchShape.Rectangle, Color? customBackground = null)
    {
        int n = palette.Swatches.Count;
        var tc = GetThemeColors(theme, customBackground);

        // Swatch panel (computed always; only added to canvas when showSwatches=true)
        int panelH   = (int)(Math.Max(120, original.Height / 8) * swatchScale);
        int margin   = Math.Max(10, original.Width / 80);
        int gap      = Math.Max(4, panelH / 20);
        int swatchH  = panelH * 6 / 10;
        int swatchW  = (original.Width - 2 * margin - (n - 1) * gap) / n;
        float swatchFs = Math.Clamp(panelH / 14f * 6f, 60f, 168f) * labelScale;
        int textPad  = Math.Max(3, panelH / 25);
        int labelLines = (showHex ? 1 : 0) + (showPercent ? 1 : 0);
        Font? swatchFont = labelLines > 0
            ? FitSwatchFont(swatchFs, swatchW - 2 * textPad, showHex)
            : null;

        // Meta strip
        (string[] lines, int stripH, float metaFs, Font? metaFont) = PrepareStrip(exif, verbosity, original.Width, labelScale);

        // Canvas layout — swatch panel is optional. Swatches sit directly under the photo;
        // the filmstrip (when present) goes after the swatches, not before.
        int canvasH      = original.Height + (showSwatches ? panelH : 0) + (style == MetaStyle.FilmStrip ? stripH : 0);
        int swatchPanelY = original.Height;
        int labelH       = labelLines > 0 && hexBelow ? (int)swatchFs * labelLines + textPad : 0;
        int swatchY      = swatchPanelY + (panelH - swatchH - labelH) / 2;
        int stripY       = style == MetaStyle.FilmStrip
            ? original.Height + (showSwatches ? panelH : 0)
            : original.Height - stripH;

        Color? overlayTextColor = style == MetaStyle.Overlay ? GetOverlayTextColor(original) : null;

        var canvas = new Image<Rgb24>(original.Width, canvasH);
        canvas.Mutate(ctx =>
        {
            ctx.Fill(tc.Background);
            ctx.DrawImage(original, new Point(0, 0), 1f);

            if (showSwatches)
                DrawSwatches(ctx, palette, n, swatchW, swatchH, margin, gap, swatchY, textPad, showHex, hexBelow, swatchFont, tc, showPercent, swatchShape);

            DrawStrip(ctx, lines, stripH, metaFs, metaFont, style, theme,
                x: 0, y: stripY,
                w: original.Width, overlayTextColor: overlayTextColor, customBackground: customBackground);
        });
        return canvas;
    }

    // ── Portrait: photo+[filmstrip below] | swatches on right ──────────────

    private static Image<Rgb24> BuildPortraitCanvas(
        Image<Rgb24> original, ColorPalette palette, bool showHex, bool hexBelow,
        PhotoMetadata? exif, MetaVerbosity verbosity, MetaStyle style, Theme theme,
        bool showSwatches = true,
        float labelScale = 1.0f, float swatchScale = 1.0f, bool showPercent = false,
        SwatchShape swatchShape = SwatchShape.Rectangle, Color? customBackground = null)
    {
        int n = palette.Swatches.Count;
        var tc = GetThemeColors(theme, customBackground);

        // Swatch panel (computed always; column only added to canvas when showSwatches=true)
        int panelW   = (int)(Math.Max(100, original.Width / 5) * swatchScale);
        int margin   = Math.Max(10, original.Height / 80);
        int gap      = Math.Max(4, panelW / 20);
        int textPad  = Math.Max(3, panelW / 25);
        float swatchFs = Math.Clamp(panelW / 8f * 6f, 60f, 144f) * labelScale;
        int swatchW  = panelW * 7 / 10;
        int swatchX  = original.Width + (panelW - swatchW) / 2;
        int labelLines = (showHex ? 1 : 0) + (showPercent ? 1 : 0);
        Font? swatchFont = labelLines > 0
            ? FitSwatchFont(swatchFs, swatchW - 2 * textPad, showHex)
            : null;

        // Reserve extra space per swatch when drawing label below
        int labelH  = labelLines > 0 && hexBelow ? (int)swatchFs * labelLines + textPad : 0;
        int swatchH = (original.Height - 2 * margin - (n - 1) * gap - n * labelH) / n;

        int canvasW = showSwatches ? original.Width + panelW : original.Width;

        // In overlay mode the box covers only the photo, so fit text to photo width
        int stripWidth = style == MetaStyle.Overlay ? original.Width : canvasW;
        (string[] lines, int stripH, float metaFs, Font? metaFont) = PrepareStrip(exif, verbosity, stripWidth, labelScale);

        int canvasH = style == MetaStyle.FilmStrip && stripH > 0
            ? original.Height + stripH
            : original.Height;

        Color? overlayTextColor = style == MetaStyle.Overlay ? GetOverlayTextColor(original) : null;

        var canvas = new Image<Rgb24>(canvasW, canvasH);
        canvas.Mutate(ctx =>
        {
            ctx.Fill(tc.Background);
            ctx.DrawImage(original, new Point(0, 0), 1f);

            DrawStrip(ctx, lines, stripH, metaFs, metaFont, style, theme,
                x: 0, y: style == MetaStyle.FilmStrip ? original.Height : original.Height - stripH,
                w: canvasW, overlayTextColor: overlayTextColor, customBackground: customBackground);

            if (showSwatches)
            for (int i = 0; i < n; i++)
            {
                var swatch = palette.Swatches[i];
                var (r, g, b) = swatch.Rgb;
                int y = margin + i * (swatchH + labelH + gap);
                FillSwatchShape(ctx, Color.FromRgb(r, g, b), swatchX, y, swatchW, swatchH, swatchShape);
                if (labelLines > 0 && swatchFont != null)
                {
                    string[] labelText = BuildSwatchLabelLines(swatch, showHex, showPercent);
                    if (hexBelow)
                        DrawSwatchLabelLines(ctx, swatchFont, labelText, swatchX + swatchW / 2f, y + swatchH + textPad, swatchFs, tc.Text, down: true);
                    else
                        DrawSwatchLabelLines(ctx, swatchFont, labelText, swatchX + swatchW / 2f, y + swatchH / 2f, swatchFs, ContrastColor(r, g, b), down: false);
                }
            }
        });
        return canvas;
    }

    // ── Strip helpers ───────────────────────────────────────────────────────

    private static (string[] lines, int stripH, float fontSize, Font? font) PrepareStrip(
        PhotoMetadata? exif, MetaVerbosity verbosity, int photoWidth, float labelScale = 1.0f)
    {
        if (exif == null || verbosity == MetaVerbosity.Off)
            return ([], 0, 0f, null);

        string[] lines = BuildMetaLines(exif, verbosity);
        if (lines.Length == 0) return ([], 0, 0f, null);

        // Font size is picked so a fixed-length reference string just fills the available
        // width, instead of guessing "photoWidth / N". That keeps how much metadata text
        // fits roughly constant at any photo resolution *and* across platforms: measuring
        // against the font actually resolved here also cancels out metric differences
        // between e.g. desktop Consolas and Android Roboto, which a hardcoded ratio can't.
        const int targetChars = 55;
        var probeFont   = ResolveMetaFont(100f);
        float probeW    = TextMeasurer.MeasureSize(new string('0', targetChars), new TextOptions(probeFont)).Width;
        float fs        = Math.Max(photoWidth * 0.95f * 100f / probeW, 40f) * labelScale;
        Font font       = ResolveMetaFont(fs);

        float stripPadF = fs * 0.6f;
        float maxTextW  = photoWidth - 2 * stripPadF;
        lines = lines.Select(l => FitLine(l, font, maxTextW)).ToArray();

        int lineH    = (int)(fs * 1.5f);
        int stripPad = (int)stripPadF;
        int stripH   = lines.Length * lineH + 2 * stripPad;
        return (lines, stripH, fs, font);
    }

    private static string FitLine(string text, Font font, float maxWidth)
    {
        const string sep = "  ·  ";
        var opts = new TextOptions(font);

        // Deduplicate consecutive words (case-insensitive) within each · segment
        var segs = text.Split(sep)
            .Select(seg =>
            {
                var ws = seg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var d  = new List<string>(ws.Length);
                foreach (var w in ws)
                    if (d.Count == 0 || !w.Equals(d[^1], StringComparison.OrdinalIgnoreCase))
                        d.Add(w);
                return d;
            })
            .Where(d => d.Count > 0)
            .ToList();

        string Build() => string.Join(sep, segs.Select(s => string.Join(" ", s)));
        bool   Fits(string s) => TextMeasurer.MeasureSize(s, opts).Width <= maxWidth;

        if (Fits(Build())) return Build();

        // Trim one word at a time from the right tail; never skip a segment wholesale
        while (segs.Count > 0)
        {
            var last = segs[^1];
            last.RemoveAt(last.Count - 1);

            // Drop trailing punctuation-only tokens (|, -, /, …) — ugly before ellipsis
            while (last.Count > 0 && last[^1].All(c => !char.IsLetterOrDigit(c)))
                last.RemoveAt(last.Count - 1);

            // Drop whole segment: empty OR orphan single word (e.g. "ISO" without value — useless before "…")
            if (last.Count <= 1)
                segs.RemoveAt(segs.Count - 1);

            if (segs.Count == 0) break;

            string candidate = Build() + "…";
            if (Fits(candidate)) return candidate;
        }

        return text.Split(' ')[0];
    }

    private static void DrawStrip(
        IImageProcessingContext ctx,
        string[] lines, int stripH, float fontSize, Font? font,
        MetaStyle style, Theme theme, int x, int y, int w,
        Color? overlayTextColor = null, Color? customBackground = null)
    {
        if (lines.Length == 0 || font == null || stripH == 0) return;

        var tc      = GetThemeColors(theme, customBackground);
        int lineH   = (int)(fontSize * 1.5f);
        int pad     = (int)(fontSize * 0.6f);

        if (style == MetaStyle.Overlay)
        {
            Color textClr = overlayTextColor ?? Color.White;
            for (int i = 0; i < lines.Length; i++)
                ctx.DrawText(new RichTextOptions(font)
                {
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Origin = new PointF(x + pad, pad + i * lineH)
                }, lines[i], textClr);
        }
        else
        {
            ctx.Fill(tc.Background, new RectangleF(x, y, w, stripH));
            for (int i = 0; i < lines.Length; i++)
                ctx.DrawText(new RichTextOptions(font)
                {
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Origin = new PointF(x + pad, y + pad + i * lineH)
                }, lines[i], tc.Text);
        }
    }

    private static void DrawSwatches(
        IImageProcessingContext ctx,
        ColorPalette palette, int n, int swatchW, int swatchH,
        int margin, int gap, int swatchY, int textPad,
        bool showHex, bool hexBelow, Font? font, ThemeColors tc,
        bool showPercent = false, SwatchShape swatchShape = SwatchShape.Rectangle)
    {
        bool showLabel = showHex || showPercent;
        for (int i = 0; i < n; i++)
        {
            var swatch = palette.Swatches[i];
            var (r, g, b) = swatch.Rgb;
            int x = margin + i * (swatchW + gap);
            FillSwatchShape(ctx, Color.FromRgb(r, g, b), x, swatchY, swatchW, swatchH, swatchShape);
            if (showLabel && font != null)
            {
                string[] labelText = BuildSwatchLabelLines(swatch, showHex, showPercent);
                float fs = font.Size;
                if (hexBelow)
                    DrawSwatchLabelLines(ctx, font, labelText, x + swatchW / 2f, swatchY + swatchH + textPad, fs, tc.Text, down: true);
                else
                    DrawSwatchLabelLines(ctx, font, labelText, x + swatchW / 2f, swatchY + swatchH / 2f, fs, ContrastColor(r, g, b), down: false);
            }
        }
    }

    // Builds the 1-2 label lines drawn on/under a swatch — hex on top, percentage below,
    // when both are enabled; otherwise whichever single one is active.
    private static string[] BuildSwatchLabelLines(ColorSwatch swatch, bool showHex, bool showPercent)
    {
        if (showHex && showPercent) return [swatch.Hex, swatch.Percentage.ToString("P0")];
        if (showHex) return [swatch.Hex];
        if (showPercent) return [swatch.Percentage.ToString("P0")];
        return [];
    }

    // Draws 1-2 stacked label lines either below the swatch (down: true, top-aligned at origin)
    // or centered inside it (down: false, origin is the swatch's vertical center).
    private static void DrawSwatchLabelLines(
        IImageProcessingContext ctx, Font font, string[] lines, float centerX, float originY, float lineHeight, Color color, bool down)
    {
        if (lines.Length == 0) return;

        // Single centered line keeps pixel-perfect glyph-metric centering via VerticalAlignment.
        // Below-swatch or multi-line labels are stacked top-down from a computed start instead,
        // since VerticalAlignment.Center only centers a single line, not a block of several.
        if (!down && lines.Length == 1)
        {
            ctx.DrawText(new RichTextOptions(font)
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment   = VerticalAlignment.Center,
                Origin = new PointF(centerX, originY)
            }, lines[0], color);
            return;
        }

        float startY = down ? originY : originY - lines.Length * lineHeight / 2f;
        for (int i = 0; i < lines.Length; i++)
        {
            ctx.DrawText(new RichTextOptions(font)
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                Origin = new PointF(centerX, startY + i * lineHeight)
            }, lines[i], color);
        }
    }

    // Fills a swatch with the requested shape — shared by the landscape row (DrawSwatches)
    // and the portrait column (BuildPortraitCanvas) so shape support lives in one place.
    private static void FillSwatchShape(IImageProcessingContext ctx, Color color, float x, float y, float w, float h, SwatchShape shape)
    {
        switch (shape)
        {
            case SwatchShape.Circle:
                float cx = x + w / 2f, cy = y + h / 2f, radius = Math.Min(w, h) / 2f;
                ctx.Fill(color, new EllipsePolygon(cx, cy, radius));
                break;
            case SwatchShape.Rounded:
                ctx.Fill(color, RoundedRectPath(x, y, w, h, Math.Min(Math.Min(w, h) * 0.18f, Math.Min(w, h) / 2f)));
                break;
            default:
                ctx.Fill(color, new RectangleF(x, y, w, h));
                break;
        }
    }

    // No built-in rounded-rectangle primitive ships with this ImageSharp.Drawing version —
    // build one from 4 corner arcs joined by straight edges.
    private static IPath RoundedRectPath(float x, float y, float w, float h, float r)
    {
        var pb = new PathBuilder();
        pb.StartFigure();
        pb.AddArc(new PointF(x + r, y + r), r, r, 0, 180, 90);
        pb.AddLine(new PointF(x + r, y), new PointF(x + w - r, y));
        pb.AddArc(new PointF(x + w - r, y + r), r, r, 0, 270, 90);
        pb.AddLine(new PointF(x + w, y + r), new PointF(x + w, y + h - r));
        pb.AddArc(new PointF(x + w - r, y + h - r), r, r, 0, 0, 90);
        pb.AddLine(new PointF(x + w - r, y + h), new PointF(x + r, y + h));
        pb.AddArc(new PointF(x + r, y + h - r), r, r, 0, 90, 90);
        pb.AddLine(new PointF(x, y + h - r), new PointF(x, y + r));
        pb.CloseFigure();
        return pb.Build();
    }

    // ── Metadata lines ──────────────────────────────────────────────────────

    private static string[] BuildMetaLines(PhotoMetadata e, MetaVerbosity v)
    {
        static string J(params string[] parts) =>
            string.Join("  ·  ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
        static string FF(string f, string eq) =>
            string.IsNullOrEmpty(eq) ? f : string.IsNullOrEmpty(f) ? eq : $"{f} ({eq})";

        string[] raw = v switch
        {
            MetaVerbosity.Short   => [J(e.Camera, e.Focal, e.Aperture, e.Shutter, e.Iso)],
            MetaVerbosity.Default => [J(e.Camera, e.Lens)],
            MetaVerbosity.Detail  => [J(e.Camera, e.Lens),
                                      J(FF(e.Focal, e.FocalEq), e.Aperture, e.Shutter, e.Iso, e.Date)],
            MetaVerbosity.Full    => [J(e.Camera, e.Lens),
                                      J(FF(e.Focal, e.FocalEq), e.Aperture, e.Shutter, e.Iso, e.Date),
                                      J(e.Serial, e.WhiteBalance, e.ExpProgram, e.ExposureBias)],
            _ => []
        };
        return raw.Where(l => !string.IsNullOrWhiteSpace(l)).ToArray();
    }

    // ── EXIF reading ────────────────────────────────────────────────────────

    // Reads metadata only, without decoding pixel data — for callers that need to show/edit
    // metadata fields separately from (and cheaper than) a full render.
    public static PhotoMetadata? ReadMetadata(string path)
    {
        try
        {
            var info = Image.Identify(path);
            return info == null ? null : ReadMetadata(info.Metadata.ExifProfile);
        }
        catch { return null; }
    }

    public static PhotoMetadata? ReadMetadata(Image image) => ReadMetadata(image.Metadata.ExifProfile);

    private static PhotoMetadata? ReadMetadata(ExifProfile? p)
    {
        try
        {
        if (p == null) return null;

        string Str(ExifTag<string> tag)
        {
            p.TryGetValue(tag, out var v);
            return v?.Value?.Trim() ?? "";
        }

        string camera = $"{Str(ExifTag.Make)} {Str(ExifTag.Model)}".Trim();
        string lens   = $"{Str(ExifTag.LensMake)} {Str(ExifTag.LensModel)}".Trim();

        string focal = "";
        if (p.TryGetValue(ExifTag.FocalLength, out var fl) && fl.Value.Denominator != 0)
            focal = $"{(int)Math.Round((double)fl.Value.Numerator / fl.Value.Denominator)}mm";

        string focalEq = "";
        if (p.TryGetValue(ExifTag.FocalLengthIn35mmFilm, out var fe) && fe.Value != 0)
            focalEq = $"{fe.Value}mm eq";

        string aperture = "";
        if (p.TryGetValue(ExifTag.FNumber, out var fn) && fn.Value.Denominator != 0)
            aperture = $"f/{(double)fn.Value.Numerator / fn.Value.Denominator:0.#}";

        string shutter = "";
        if (p.TryGetValue(ExifTag.ExposureTime, out var et) && et.Value.Denominator != 0)
        {
            uint num = et.Value.Numerator, den = et.Value.Denominator;
            uint g = Gcd(num, den); num /= g; den /= g;
            shutter = den == 1 ? $"{num}s" : $"1/{den}s";
        }

        string iso = "";
        if (p.TryGetValue(ExifTag.ISOSpeedRatings, out var isoVal) &&
            isoVal.Value is { Length: > 0 } isos)
            iso = $"ISO {isos[0]}";

        string date = "";
        if (DateTime.TryParseExact(Str(ExifTag.DateTimeOriginal),
            "yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
            date = dt.ToString("yyyy-MM-dd");

        string ev = "";
        if (p.TryGetValue(ExifTag.ExposureBiasValue, out var evTag) && evTag.Value.Denominator != 0)
        {
            double evVal = (double)evTag.Value.Numerator / evTag.Value.Denominator;
            ev = evVal == 0 ? "0 EV" : $"{evVal:+0.#;-0.#} EV";
        }

        string wb = "";
        if (p.TryGetValue(ExifTag.WhiteBalance, out var wbTag))
            wb = wbTag.Value == 0 ? "WB: Auto" : "WB: Manual";

        string ep = "";
        if (p.TryGetValue(ExifTag.ExposureProgram, out var epTag))
            ep = epTag.Value switch {
                1 => "Manual", 2 => "Auto", 3 => "Aperture Priority",
                4 => "Shutter Priority", 5 => "Creative", 6 => "Action", _ => ""
            };

        string serial = Str(ExifTag.SerialNumber);
        if (!string.IsNullOrEmpty(serial)) serial = $"S/N: {serial}";

        return new PhotoMetadata(camera, lens, focal, focalEq, aperture, shutter, iso, date, ev, wb, ep, serial);
        }
        catch { return null; }
    }

    private static uint Gcd(uint a, uint b) => b == 0 ? a : Gcd(b, a % b);

    // ── Font helpers ────────────────────────────────────────────────────────

    // Embedding JetBrains Mono guarantees the same typography on desktop and Android
    // regardless of what's installed on the OS, instead of depending on SystemFonts
    // resolving to whatever monospace family happens to be present (Consolas on Windows,
    // some Roboto variant on Android) — those differ enough in glyph width that text
    // capacity per line used to vary by platform. Lazily loaded once; null if anything
    // about the embedded-resource lookup fails, in which case ResolveMetaFont below
    // falls through to the pre-existing SystemFonts chain unchanged.
    private static readonly FontFamily? EmbeddedMonoFamily = LoadEmbeddedMonoFont();

    private static FontFamily? LoadEmbeddedMonoFont()
    {
        try
        {
            var asm = typeof(PaletteImageRenderer).Assembly;
            var resourceName = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.Contains("JetBrainsMono", StringComparison.OrdinalIgnoreCase));
            if (resourceName == null) return null;

            using var stream = asm.GetManifestResourceStream(resourceName);
            if (stream == null) return null;

            return new FontCollection().Add(stream);
        }
        catch
        {
            return null;
        }
    }

    private static Font ResolveMetaFont(float size)
    {
        if (EmbeddedMonoFamily is { } embedded)
        {
            try { return embedded.CreateFont(size, FontStyle.Regular); }
            catch { /* fall through to the system-font chain below */ }
        }

        // Desktop (Windows/Linux) names first, then the common Android system font names,
        // so mobile doesn't fall through to the "pick anything installed" last resort below.
        foreach (string name in new[]
        {
            "Consolas", "Courier New", "Lucida Console",
            "Roboto Mono", "Droid Sans Mono", "Noto Sans Mono",
            "Roboto", "Droid Sans", "Noto Sans", "Arial"
        })
        {
            try { return SystemFonts.CreateFont(name, size, FontStyle.Regular); }
            catch (FontFamilyNotFoundException) { }
        }

        // Last resort: pick any installed family, but skip emoji/symbol fonts — on some
        // platforms/devices those sort first and silently render plain text as blank glyphs.
        var families = SystemFonts.Families.ToList();
        var textFamily = families.FirstOrDefault(f =>
            !string.IsNullOrEmpty(f.Name) &&
            !f.Name.Contains("emoji",    StringComparison.OrdinalIgnoreCase) &&
            !f.Name.Contains("symbol",   StringComparison.OrdinalIgnoreCase) &&
            !f.Name.Contains("dingbat",  StringComparison.OrdinalIgnoreCase) &&
            !f.Name.Contains("wingding", StringComparison.OrdinalIgnoreCase) &&
            !f.Name.Contains("math",     StringComparison.OrdinalIgnoreCase));

        var family = !string.IsNullOrEmpty(textFamily.Name) ? textFamily : families.First();
        return family.CreateFont(size, FontStyle.Regular);
    }

    // Shrinks the swatch hex-label font until "#000000" (all hex labels are the same
    // 7-char width) actually fits inside the swatch, instead of trusting a size guessed
    // from panel geometry alone — that guess badly overflows on narrow/portrait swatches.
    private static Font FitSwatchFont(float startSize, float maxTextWidth, bool showHex = true)
    {
        const float minSize = 10f;
        if (maxTextWidth <= 0) return ResolveMetaFont(minSize);

        // "#000000" (7 chars) when a hex label is drawn, otherwise the shorter "100%" —
        // both are the widest string that style of label can ever produce.
        string probe = showHex ? "#000000" : "100%";
        for (float size = startSize; size > minSize; size -= 2f)
        {
            var font = ResolveMetaFont(size);
            if (TextMeasurer.MeasureSize(probe, new TextOptions(font)).Width <= maxTextWidth)
                return font;
        }
        return ResolveMetaFont(minSize);
    }

    private static double RelativeLuminance(byte r, byte g, byte b)
    {
        static double Ch(byte c) { double s = c / 255.0; return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
        return 0.2126 * Ch(r) + 0.7152 * Ch(g) + 0.0722 * Ch(b);
    }

    private static Color ContrastColor(byte r, byte g, byte b)
        => RelativeLuminance(r, g, b) < 0.179 ? Color.White : Color.Black;

    // Standard RGB→hue conversion (0-360, undefined/0 for achromatic colors) — only the hue
    // component is needed for sorting, so this skips computing saturation/lightness.
    internal static float RgbToHue(byte r, byte g, byte b)
    {
        float rf = r / 255f, gf = g / 255f, bf = b / 255f;
        float max = Math.Max(rf, Math.Max(gf, bf));
        float min = Math.Min(rf, Math.Min(gf, bf));
        float delta = max - min;
        if (delta == 0) return 0f;

        float hue;
        if (max == rf)      hue = 60f * (((gf - bf) / delta) % 6f);
        else if (max == gf) hue = 60f * (((bf - rf) / delta) + 2f);
        else                hue = 60f * (((rf - gf) / delta) + 4f);

        return hue < 0 ? hue + 360f : hue;
    }

    internal static List<ColorSwatch> SortSwatches(IReadOnlyList<ColorSwatch> swatches, SortOrder order) => order switch
    {
        SortOrder.Hue       => swatches.OrderBy(s => RgbToHue(s.Rgb.R, s.Rgb.G, s.Rgb.B)).ToList(),
        SortOrder.Luminance => swatches.OrderBy(s => RelativeLuminance(s.Rgb.R, s.Rgb.G, s.Rgb.B)).ToList(),
        SortOrder.Percent   => swatches.OrderByDescending(s => s.Percentage).ToList(),
        _                   => swatches.ToList()
    };

    // Samples the top-left corner of the photo to decide overlay text color.
    // Uses relative luminance threshold 0.35 on the blended result (overlay at 55%).
    private static Color GetOverlayTextColor(Image<Rgb24> image)
    {
        int sampleW = Math.Max(1, image.Width  / 4);
        int sampleH = Math.Max(1, image.Height / 6);
        int step    = Math.Max(1, sampleW / 20);
        double totalLum = 0;
        int    count    = 0;

        static double Lin(byte c) { double s = c / 255.0; return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }

        image.ProcessPixelRows(accessor =>
        {
            for (int row = 0; row < sampleH; row += step)
            {
                var span = accessor.GetRowSpan(row);
                for (int col = 0; col < sampleW; col += step)
                {
                    ref Rgb24 p = ref span[col];
                    totalLum += 0.2126 * Lin(p.R) + 0.7152 * Lin(p.G) + 0.0722 * Lin(p.B);
                    count++;
                }
            }
        });

        double avgLum = count > 0 ? totalLum / count : 0;
        return avgLum > 0.35 ? Color.Black : Color.White;
    }
}
