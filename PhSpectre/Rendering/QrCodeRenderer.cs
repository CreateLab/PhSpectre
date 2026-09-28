using Net.Codecrete.QrCodeGenerator;
using PhSpectre.Models;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace PhSpectre.Rendering;

// Resolved, render-ready QR settings for a single export — built by
// PaletteExportSettings.BuildQrRenderOptions once the per-photo PhotoMetadata/FilmRecipe are
// known, since preset resolution needs those and this record is what actually crosses into
// the ImageSharp-only rendering layer. Caption is independent of both Placement and the QR's
// own encoded content preset (v4 spec §1b) — CaptionCamera/CaptionLens are the already-resolved
// display strings to draw, gated by Caption (None draws neither, Camera draws just the first,
// CameraAndLens draws both as two lines).
public sealed record QrRenderOptions(
    QrPlacement Placement, string TargetUrl,
    QrCaption Caption = QrCaption.None,
    string? CaptionCamera = null,
    string? CaptionLens = null);

// Draws a QR code onto the frame below the photo (see QrPlacement — Overlay was cut from v1,
// see its doc comment). Sibling to PaletteImageRenderer/RecipeCardRenderer, reusing their
// rounded-rect/theme/font helpers rather than duplicating them.
public static class QrCodeRenderer
{
    private const int QuietZoneModules = 4;

    // Legibility floor at the FINAL saved-file resolution — below this a module's edges blur
    // into its neighbors under JPEG compression or any resize, and the code stops scanning
    // regardless of how many modules it technically has (§4 of the v2 spec: a long payload +
    // high error correction can produce a module count whose per-pixel size, at a merely
    // proportional box size, rounds down to 2-3px and becomes unscannable).
    private const int MinPxPerModule = 8;

    // Suggested QR module-box size for a canvas of the given reference width — proportional,
    // same spirit as the margin/gap formulas already used elsewhere in this file's siblings.
    // This is only a starting point: RenderModules can and does grow past it to satisfy
    // MinPxPerModule, and ComputeBelowBlockHeight predicts that grown size so the canvas is
    // sized correctly before anything is drawn.
    public static int SuggestBoxSize(int referenceWidth) => Math.Clamp(referenceWidth / 6, 96, 320);

    // Extra canvas height the Below block needs, computed up front (same convention as
    // PaletteImageRenderer.PrepareStrip's stripH) so callers can size the canvas before
    // allocating it. Caption never adds height — it's drawn beside the QR, not below it.
    //
    // downscaleFactor: a later canvas-wide downscale the caller will apply AFTER this block is
    // drawn (e.g. Settings' "Export size: Half" = 2) — inflating the legibility floor by this
    // factor up front keeps the module size legible in the FINAL saved file, not just in the
    // pre-downscale canvas (the v2 spec's other §4 complaint: Half export shrinks an
    // already-marginal QR into an unscannable one).
    public static int ComputeBelowBlockHeight(QrRenderOptions? qr, int qrBoxSize, int margin, int downscaleFactor = 1)
    {
        if (qr == null) return 0;
        int imgSize = PredictModuleImageSize(qr.TargetUrl, qrBoxSize, Ecc, downscaleFactor);
        return margin + imgSize + margin;
    }

    // The spec's recommended default error-correction level — Below has dedicated card space,
    // so there's no partial-occlusion case (that was Overlay's reason for a higher level, and
    // Overlay is gone — see QrPlacement).
    private const QrCode.Ecc Ecc = QrCode.Ecc.Medium;

    // Same module/pixel math RenderModules uses, without allocating an image — lets callers
    // that must size a layout BEFORE drawing (ComputeBelowBlockHeight) predict exactly what
    // RenderModules will produce, even when MinPxPerModule inflates it past the plain
    // proportional boxSize guess.
    private static int PredictModuleImageSize(string url, int boxSize, QrCode.Ecc ecc, int downscaleFactor)
    {
        var qr = QrCode.EncodeText(url, ecc);
        int modules = qr.Size + 2 * QuietZoneModules;
        int px = Math.Max(MinPxPerModule * Math.Max(1, downscaleFactor), boxSize / modules);
        return px * modules;
    }

    // Raw QR module image: opaque black modules on a white quiet-zone background, regardless
    // of app theme — scan reliability depends on real light/dark contrast, so this never
    // follows the Dark/Light theming the rest of the card does. The white background itself is
    // a rounded rect, not a flat square (v-final spec §3 "QR-chip": a hard-cornered white
    // rectangle butted straight against the dark panel reads as a stuck-on sticker) — corners
    // outside that rounded rect stay transparent, so DrawBelowBlock's DrawImage lets the card's
    // own background show through them instead of a mismatched white corner.
    private static Image<Rgba32> RenderModules(string url, int boxSize, QrCode.Ecc ecc, int downscaleFactor)
    {
        var qr = QrCode.EncodeText(url, ecc);
        int modules = qr.Size + 2 * QuietZoneModules;
        int px = Math.Max(MinPxPerModule * Math.Max(1, downscaleFactor), boxSize / modules);
        int imgSize = px * modules;
        float chipRadius = Math.Clamp(imgSize * 0.05f, 8f, 24f);

        var img = new Image<Rgba32>(imgSize, imgSize);
        img.Mutate(ctx =>
        {
            ctx.Fill(Color.White, PaletteImageRenderer.RoundedRectPath(0, 0, imgSize, imgSize, chipRadius));
            for (int y = 0; y < qr.Size; y++)
            for (int x = 0; x < qr.Size; x++)
                if (qr.GetModule(x, y))
                    ctx.Fill(Color.Black, new RectangleF((x + QuietZoneModules) * px, (y + QuietZoneModules) * px, px, px));
        });
        return img;
    }

    // Truncates to a single line ending in an ellipsis rather than let long text wrap mid-word
    // (v-final spec §3: "перенос — по словам, никогда не разрывать модель объектива посередине;
    // если не влезает — троеточие в конце"). Caption lines are short camera/lens names, so
    // forcing them to a single measured-and-truncated line sidesteps word-wrap entirely instead
    // of trying to get SixLabors' wrapper to respect a "never break this token" rule.
    private static string EllipsizeToWidth(string text, Font font, float maxWidth)
    {
        if (string.IsNullOrEmpty(text) || TextMeasurer.MeasureSize(text, new TextOptions(font)).Width <= maxWidth)
            return text;

        const string ellipsis = "…";
        for (int len = text.Length - 1; len > 0; len--)
        {
            string candidate = text[..len].TrimEnd() + ellipsis;
            if (TextMeasurer.MeasureSize(candidate, new TextOptions(font)).Width <= maxWidth)
                return candidate;
        }
        return ellipsis;
    }

    // Dedicated card space below the photo — no translucency needed, just the opaque QR
    // block at standard margin spacing, optionally with a caption beside it (v4 spec §1b:
    // Caption is independent of Content preset — None/Camera/CameraAndLens gate what's drawn
    // here, regardless of what's encoded inside the QR itself).
    public static void DrawBelowBlock(
        IImageProcessingContext ctx, string url, int boxSize, int margin,
        int blockY, int blockWidth, QrCaption caption, string? captionCamera, string? captionLens,
        Theme theme, Color? customBackground, int downscaleFactor = 1)
    {
        using var qrImg = RenderModules(url, boxSize, Ecc, downscaleFactor);
        int qrX = margin;
        int qrY = blockY + margin;
        ctx.DrawImage(qrImg, new Point(qrX, qrY), 1f);

        if (caption == QrCaption.None || string.IsNullOrEmpty(captionCamera)) return;

        var textColor = PaletteImageRenderer.GetTextColor(theme, customBackground);
        // Same column gap RecipeCardRenderer's parameter grid uses between plates — v-final
        // spec §3 "Компоновка": the QR-to-caption gap should match that grid's gap, not the
        // wider outer card margin the block itself is inset by.
        int gap = margin / 2;
        int textX = qrX + qrImg.Width + gap;
        float maxTextWidth = Math.Max(20, blockWidth - textX - margin);
        bool twoLines = caption == QrCaption.CameraAndLens && !string.IsNullOrEmpty(captionLens);

        if (!twoLines)
        {
            var font = PaletteImageRenderer.ResolveSansFont(qrImg.Height * 0.22f, FontStyle.Bold);
            string line = EllipsizeToWidth(captionCamera, font, maxTextWidth);
            ctx.DrawText(new RichTextOptions(font)
            {
                Origin = new PointF(textX, qrY + qrImg.Height / 2f),
                VerticalAlignment = VerticalAlignment.Center,
            }, line, textColor);
            return;
        }

        // Two lines: camera bold/larger on top, lens lighter/smaller below it — not one line
        // joined by " · ", so a long lens name never garbles together with the camera name
        // (v4 spec §1b). Sizes/weights follow the v-final spec §3 typography table (sans,
        // Semibold-ish/Bold camera vs Regular/muted lens); the block as a whole is centered on
        // the QR chip's vertical midpoint rather than pinned to its top (§3 "Компоновка").
        string lensLine = captionLens!; // twoLines already proved this non-null/non-empty
        var cameraFont = PaletteImageRenderer.ResolveSansFont(qrImg.Height * 0.22f, FontStyle.Bold);
        var lensFont = PaletteImageRenderer.ResolveSansFont(qrImg.Height * 0.15f);
        string cameraLine = EllipsizeToWidth(captionCamera, cameraFont, maxTextWidth);
        lensLine = EllipsizeToWidth(lensLine, lensFont, maxTextWidth);

        float cameraLineH = cameraFont.Size * 1.2f;
        float lensLineH = lensFont.Size * 1.2f;
        float lineGap = cameraFont.Size * 0.25f;
        float blockTop = qrY + qrImg.Height / 2f - (cameraLineH + lineGap + lensLineH) / 2f;
        float cameraY = blockTop + cameraLineH / 2f;
        float lensY = blockTop + cameraLineH + lineGap + lensLineH / 2f;

        ctx.DrawText(new RichTextOptions(cameraFont)
        {
            Origin = new PointF(textX, cameraY),
            VerticalAlignment = VerticalAlignment.Center,
        }, cameraLine, textColor);
        ctx.DrawText(new RichTextOptions(lensFont)
        {
            Origin = new PointF(textX, lensY),
            VerticalAlignment = VerticalAlignment.Center,
        }, lensLine, textColor.WithAlpha(0.7f));
    }
}
