using Net.Codecrete.QrCodeGenerator;
using PhSpectre.Models;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace PhSpectre.Rendering;

// QR settings of one export, resolved from the metadata and recipe.
public sealed record QrRenderOptions(
    QrPlacement Placement, string TargetUrl,
    QrCaption Caption = QrCaption.None,
    string? CaptionCamera = null,
    string? CaptionLens = null);

// Draws a QR code below the photo (see QrPlacement). Sibling of PaletteImageRenderer/RecipeCardRenderer, reusing their helpers.
public static class QrCodeRenderer
{
    private const int QuietZoneModules = 4;

    // Smallest module size at the final resolution that still scans after JPEG or resizing.
    private const int MinPxPerModule = 4;

    // Module size for a reference width; RenderModules may enlarge it to meet MinPxPerModule.
    public static int SuggestBoxSize(int referenceWidth) => Math.Clamp(referenceWidth / 6, 96, 320);

    // Extra height of the Below block. downscaleFactor accounts for a later downscale of the whole canvas.
    public static int ComputeBelowBlockHeight(QrRenderOptions? qr, int qrBoxSize, int margin, int downscaleFactor = 1)
    {
        if (qr == null) return 0;
        int imgSize = PredictModuleImageSize(qr.TargetUrl, qrBoxSize, Ecc, downscaleFactor);
        return margin + imgSize + margin;
    }

    // Pixel size of the QR image (quiet zone included).
    public static int PredictImageSize(QrRenderOptions qr, int qrBoxSize, int downscaleFactor = 1) =>
        PredictModuleImageSize(qr.TargetUrl, qrBoxSize, Ecc, downscaleFactor);

    // Error correction level; the code has its own space.
    private const QrCode.Ecc Ecc = QrCode.Ecc.Low;

    // The module/pixel math RenderModules uses, without allocating, so layouts can be sized before drawing.
    private static int PredictModuleImageSize(string url, int boxSize, QrCode.Ecc ecc, int downscaleFactor)
    {
        var qr = QrCode.EncodeText(url, ecc);
        int modules = qr.Size + 2 * QuietZoneModules;
        int px = Math.Max(MinPxPerModule * Math.Max(1, downscaleFactor), boxSize / modules);
        return px * modules;
    }

    // Black on white in any theme, with transparent rounded corners.
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

    // Ellipsized to one line.
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

    // The QR bottom right with its label to the left, sharing the row with the camera plate; without a plate the
    // caption fills the left.
    public static void DrawBelowBlock(
        IImageProcessingContext ctx, string url, int boxSize, int margin,
        int blockY, int blockWidth, QrCaption caption, string? captionCamera, string? captionLens,
        Theme theme, Color? customBackground, int downscaleFactor = 1,
        int? rowHeight = null, bool plateInRow = false)
    {
        using var qrImg = RenderModules(url, boxSize, Ecc, downscaleFactor);
        int rowH = rowHeight ?? (qrImg.Height + 2 * margin);
        int qrX = blockWidth - margin - qrImg.Width;
        int qrY = blockY + (rowH - qrImg.Height) / 2;
        ctx.DrawImage(qrImg, new Point(qrX, qrY), 1f);

        var textColor = PaletteImageRenderer.GetTextColor(theme, customBackground);

        // "SCAN FOR / THE RECIPE", right-aligned against the code.
        var scanFont = PaletteImageRenderer.ResolveMetaFont(Math.Max(9f, Math.Min(blockWidth * 0.014f, qrImg.Height * 0.11f)));
        float scanRight = qrX - margin / 2f;
        float scanLineH = scanFont.Size * 1.35f;
        float scanTop = qrY + qrImg.Height / 2f - scanLineH;
        string[] scanLines = ["SCAN FOR", "THE RECIPE"];
        for (int i = 0; i < scanLines.Length; i++)
            ctx.DrawText(new RichTextOptions(scanFont)
            {
                Origin = new PointF(scanRight, scanTop + i * scanLineH),
                HorizontalAlignment = HorizontalAlignment.Right,
            }, scanLines[i], textColor.WithAlpha(0.6f));

        if (plateInRow || caption == QrCaption.None || string.IsNullOrEmpty(captionCamera)) return;
        float scanWidth = TextMeasurer.MeasureSize("THE RECIPE", new TextOptions(scanFont)).Width;
        // Same column gap as RecipeCardRenderer's parameter grid, not the wider outer margin.
        int gap = margin / 2;
        int textX = margin;
        float maxTextWidth = Math.Max(20, scanRight - scanWidth - gap - textX);
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

        // Two lines, centred on the QR.
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
