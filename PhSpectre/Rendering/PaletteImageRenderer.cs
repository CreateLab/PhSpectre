using PhSpectre.Models;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using System.Globalization;

namespace PhSpectre.Rendering;

public enum MetaVerbosity { Off, Short, Default, Detail, Full }
public enum MetaStyle    { FilmStrip, Overlay }
public enum Theme        { Dark, Light }

public static class PaletteImageRenderer
{
    private record ThemeColors(Color Background, Color Text);

    private static ThemeColors GetThemeColors(Theme theme) => theme == Theme.Light
        ? new ThemeColors(Color.ParseHex("F5F5F5"), Color.ParseHex("1A1A1A"))
        : new ThemeColors(Color.ParseHex("111111"), Color.White);

    private record ExifData(
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
        Theme theme = Theme.Dark)
    {
        using var original = Image.Load<Rgb24>(sourceImagePath);
        original.Mutate(ctx => ctx.AutoOrient());

        ExifData? exif = metaVerbosity != MetaVerbosity.Off ? ReadExif(original) : null;

        bool landscape = original.Width >= original.Height;
        using var canvas = landscape
            ? BuildLandscapeCanvas(original, palette, showHex, exif, metaVerbosity, metaStyle, theme)
            : BuildPortraitCanvas(original, palette, showHex, exif, metaVerbosity, metaStyle, theme);

        canvas.SaveAsPng(outputPath);
    }

    // ── Landscape: photo → [filmstrip] → swatches ──────────────────────────

    private static Image<Rgb24> BuildLandscapeCanvas(
        Image<Rgb24> original, ColorPalette palette, bool showHex,
        ExifData? exif, MetaVerbosity verbosity, MetaStyle style, Theme theme)
    {
        int n = palette.Swatches.Count;
        var tc = GetThemeColors(theme);

        // Swatch panel
        int panelH   = Math.Max(120, original.Height / 8);
        int margin   = Math.Max(10, original.Width / 80);
        int gap      = Math.Max(4, panelH / 20);
        int swatchH  = panelH * 6 / 10;
        int swatchW  = (original.Width - 2 * margin - (n - 1) * gap) / n;
        float swatchFs = Math.Clamp(panelH / 14f * 6f, 60f, 168f);
        int textPad  = Math.Max(3, panelH / 25);
        Font? swatchFont = showHex ? ResolveMetaFont(swatchFs) : null;

        // Meta strip
        (string[] lines, int stripH, float metaFs, Font? metaFont) = PrepareStrip(exif, verbosity, original.Width);

        // Canvas layout
        int canvasH       = original.Height + (style == MetaStyle.FilmStrip ? stripH : 0) + panelH;
        int swatchPanelY  = original.Height + (style == MetaStyle.FilmStrip ? stripH : 0);
        int labelH        = showHex ? (int)swatchFs + textPad : 0;
        int swatchY       = swatchPanelY + (panelH - swatchH - labelH) / 2;

        var canvas = new Image<Rgb24>(original.Width, canvasH);
        canvas.Mutate(ctx =>
        {
            ctx.Fill(tc.Background);
            ctx.DrawImage(original, new Point(0, 0), 1f);

            DrawStrip(ctx, lines, stripH, metaFs, metaFont, style, theme,
                x: 0, y: style == MetaStyle.FilmStrip ? original.Height : original.Height - stripH,
                w: original.Width);

            DrawSwatches(ctx, palette, n, swatchW, swatchH, margin, gap, swatchY, textPad, showHex, swatchFont, tc);
        });
        return canvas;
    }

    // ── Portrait: photo+[filmstrip below] | swatches on right ──────────────

    private static Image<Rgb24> BuildPortraitCanvas(
        Image<Rgb24> original, ColorPalette palette, bool showHex,
        ExifData? exif, MetaVerbosity verbosity, MetaStyle style, Theme theme)
    {
        int n = palette.Swatches.Count;
        var tc = GetThemeColors(theme);

        // Swatch panel
        int panelW   = Math.Max(100, original.Width / 5);
        int margin   = Math.Max(10, original.Height / 80);
        int gap      = Math.Max(4, panelW / 20);
        int swatchH  = (original.Height - 2 * margin - (n - 1) * gap) / n;
        int swatchW  = panelW * 7 / 10;
        float swatchFs = Math.Clamp(panelW / 8f * 6f, 60f, 144f);
        Font? swatchFont = showHex ? ResolveMetaFont(swatchFs) : null;
        int swatchX  = original.Width + (panelW - swatchW) / 2;

        // Meta strip
        (string[] lines, int stripH, float metaFs, Font? metaFont) = PrepareStrip(exif, verbosity, original.Width);

        int canvasW = original.Width + panelW;
        int canvasH = style == MetaStyle.FilmStrip && stripH > 0
            ? original.Height + stripH
            : original.Height;

        var canvas = new Image<Rgb24>(canvasW, canvasH);
        canvas.Mutate(ctx =>
        {
            ctx.Fill(tc.Background);
            ctx.DrawImage(original, new Point(0, 0), 1f);

            DrawStrip(ctx, lines, stripH, metaFs, metaFont, style, theme,
                x: 0, y: style == MetaStyle.FilmStrip ? original.Height : original.Height - stripH,
                w: original.Width);

            for (int i = 0; i < n; i++)
            {
                var (r, g, b) = palette.Swatches[i].Rgb;
                int y = margin + i * (swatchH + gap);
                ctx.Fill(Color.FromRgb(r, g, b), new RectangleF(swatchX, y, swatchW, swatchH));
                if (showHex && swatchFont != null)
                    ctx.DrawText(new RichTextOptions(swatchFont)
                    {
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment   = VerticalAlignment.Center,
                        Origin = new PointF(swatchX + swatchW / 2f, y + swatchH / 2f)
                    }, palette.Swatches[i].Hex, ContrastColor(r, g, b));
            }
        });
        return canvas;
    }

    // ── Strip helpers ───────────────────────────────────────────────────────

    private static (string[] lines, int stripH, float fontSize, Font? font) PrepareStrip(
        ExifData? exif, MetaVerbosity verbosity, int photoWidth)
    {
        if (exif == null || verbosity == MetaVerbosity.Off)
            return ([], 0, 0f, null);

        string[] lines = BuildMetaLines(exif, verbosity);
        if (lines.Length == 0) return ([], 0, 0f, null);

        float fs     = Math.Clamp(photoWidth / 20f, 40f, 180f);
        Font font    = ResolveMetaFont(fs);

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

            // Also drop trailing punctuation-only tokens (|, -, /, …) — ugly before ellipsis
            while (last.Count > 0 && last[^1].All(c => !char.IsLetterOrDigit(c)))
                last.RemoveAt(last.Count - 1);

            if (last.Count == 0)
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
        MetaStyle style, Theme theme, int x, int y, int w)
    {
        if (lines.Length == 0 || font == null || stripH == 0) return;

        var tc = GetThemeColors(theme);
        Color bg        = style == MetaStyle.FilmStrip ? tc.Background : new Color(new Rgba32(0, 0, 0, 145));
        Color textColor = style == MetaStyle.FilmStrip ? tc.Text : Color.White;

        ctx.Fill(bg, new RectangleF(x, y, w, stripH));

        int lineH    = (int)(fontSize * 1.5f);
        int stripPad = (int)(fontSize * 0.6f);
        for (int i = 0; i < lines.Length; i++)
        {
            ctx.DrawText(new RichTextOptions(font)
            {
                HorizontalAlignment = HorizontalAlignment.Left,
                Origin = new PointF(x + stripPad, y + stripPad + i * lineH)
            }, lines[i], textColor);
        }
    }

    private static void DrawSwatches(
        IImageProcessingContext ctx,
        ColorPalette palette, int n, int swatchW, int swatchH,
        int margin, int gap, int swatchY, int textPad,
        bool showHex, Font? font, ThemeColors tc)
    {
        for (int i = 0; i < n; i++)
        {
            var (r, g, b) = palette.Swatches[i].Rgb;
            int x = margin + i * (swatchW + gap);
            ctx.Fill(Color.FromRgb(r, g, b), new RectangleF(x, swatchY, swatchW, swatchH));
            if (showHex && font != null)
                ctx.DrawText(new RichTextOptions(font)
                {
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Origin = new PointF(x + swatchW / 2f, swatchY + swatchH + textPad)
                }, palette.Swatches[i].Hex, tc.Text);
        }
    }

    // ── Metadata lines ──────────────────────────────────────────────────────

    private static string[] BuildMetaLines(ExifData e, MetaVerbosity v)
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

    private static ExifData? ReadExif(Image image)
    {
        try
        {
        var p = image.Metadata.ExifProfile;
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

        return new ExifData(camera, lens, focal, focalEq, aperture, shutter, iso, date, ev, wb, ep, serial);
        }
        catch { return null; }
    }

    private static uint Gcd(uint a, uint b) => b == 0 ? a : Gcd(b, a % b);

    // ── Font helpers ────────────────────────────────────────────────────────

    private static Font ResolveMetaFont(float size)
    {
        foreach (string name in new[] { "Consolas", "Courier New", "Lucida Console", "Arial" })
        {
            try { return SystemFonts.CreateFont(name, size, FontStyle.Regular); }
            catch (FontFamilyNotFoundException) { }
        }
        return SystemFonts.Families.First().CreateFont(size, FontStyle.Regular);
    }

    private static Color ContrastColor(byte r, byte g, byte b)
    {
        static double Ch(byte c) { double s = c / 255.0; return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
        return 0.2126 * Ch(r) + 0.7152 * Ch(g) + 0.0722 * Ch(b) < 0.179 ? Color.White : Color.Black;
    }
}
