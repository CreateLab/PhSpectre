using System;
using System.IO;
using Net.Codecrete.QrCodeGenerator;
using PhSpectre.Models;
using PhSpectre.Rendering;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Xunit;

namespace PhSpecre.Tests;

// Structural smoke tests for QrCodeRenderer's integration into both card renderers — same
// "doesn't throw, produces a decodable image of the expected size" style as
// BlurredBackgroundTests, plus a check that the Below block actually grows the canvas.
public class QrCodeRendererTests
{
    private static Image<Rgb24> SolidImage(int w, int h, Color color)
    {
        var img = new Image<Rgb24>(w, h);
        img.Mutate(ctx => ctx.Fill(color));
        return img;
    }

    private static FilmRecipe MinimalRecipe() => new(
        Source: RecipeSource.AutoDetected, RecipeName: null,
        CameraMake: "FUJIFILM", CameraModel: "X-T5",
        FilmSimulation: "Provia", WhiteBalance: null, WhiteBalanceShift: null,
        DynamicRange: null, HighlightTone: null, ShadowTone: null, Color: null,
        Sharpness: null, NoiseReduction: null, Clarity: null, GrainEffect: null,
        ColorChromeEffect: null, ColorChromeFxBlue: null, BwAdjustment: null);

    private const string TestUrl = "https://createlab.github.io/phspectre-view/#b.dGVzdA";

    [Theory]
    [InlineData(QrCaption.None)]
    [InlineData(QrCaption.Camera)]
    [InlineData(QrCaption.CameraAndLens)]
    public void PaletteImageRenderer_Qr_RendersWithoutError(QrCaption caption)
    {
        using var photo = SolidImage(400, 300, Color.CornflowerBlue);
        var palette = new ColorPalette([new ColorSwatch("#6495ED", (100, 149, 237), 1f)]);
        var qr = new QrRenderOptions(QrPlacement.Below, TestUrl, caption, "FUJIFILM X-T5", "XF35mmF1.4 R");
        string outputPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.png");

        try
        {
            PaletteImageRenderer.Render(photo, palette, outputPath, qr: qr);

            using var result = Image.Load(outputPath);
            Assert.True(result.Width > 0);
            Assert.True(result.Height > 0);
        }
        finally
        {
            if (File.Exists(outputPath)) File.Delete(outputPath);
        }
    }

    [Theory]
    [InlineData(QrCaption.None)]
    [InlineData(QrCaption.Camera)]
    [InlineData(QrCaption.CameraAndLens)]
    public void RecipeCardRenderer_Qr_RendersWithoutError(QrCaption caption)
    {
        using var photo = SolidImage(400, 300, Color.CornflowerBlue);
        var qr = new QrRenderOptions(QrPlacement.Below, TestUrl, caption, "FUJIFILM X-T5", "XF35mmF1.4 R");
        string outputPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.png");

        try
        {
            RecipeCardRenderer.Render(photo, MinimalRecipe(), outputPath, qr: qr);

            using var result = Image.Load(outputPath);
            Assert.True(result.Width > 0);
            Assert.True(result.Height > 0);
        }
        finally
        {
            if (File.Exists(outputPath)) File.Delete(outputPath);
        }
    }

    // Regression for the v2 bugfix pass §1/§3: "Below" must land immediately after the
    // photo/swatches, before any text plate (metadata strip / recipe plates) — not at the very
    // bottom of the whole composition, which is where it used to end up. Dark theme is used
    // deliberately: the QR's own pixels always render white-on-black-modules regardless of
    // theme, so a near-white pixel right after the photo/swatch band is unambiguous evidence
    // the QR (not a dark strip/plate) occupies that band.
    [Fact]
    public void PaletteImageRenderer_Qr_BelowPlacement_SitsRightAfterSwatchesNotAfterFilmStrip()
    {
        using var photo = SolidImage(400, 300, Color.CornflowerBlue);
        var palette = new ColorPalette([new ColorSwatch("#6495ED", (100, 149, 237), 1f)]);
        var metadata = new PaletteImageRenderer.PhotoMetadata(
            "FUJIFILM X-T5", "XF35mmF1.4 R", "35mm", "52mm eq", "f/1.4", "1/500s", "ISO 400",
            "2026-01-01", "+0.3 EV", "WB: Auto", "Manual", "S/N: 12345");
        var qr = new QrRenderOptions(QrPlacement.Below, TestUrl);
        string outputPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.png");

        try
        {
            PaletteImageRenderer.Render(photo, palette, outputPath, theme: Theme.Dark,
                metaVerbosity: MetaVerbosity.Default, metadataOverride: metadata, qr: qr);

            using var result = Image.Load<Rgb24>(outputPath);
            int panelH = Math.Max(120, photo.Height / 8);
            int margin = Math.Max(10, photo.Width / 80);
            int qrBlockY = photo.Height + panelH;

            // Offset 27px into the quiet zone rather than 3 — the QR chip's white background is
            // now a rounded rect (v-final spec §3), which clips a few px right at the corner;
            // 27px clears the largest possible corner radius (24px, see QrCodeRenderer's
            // chipRadius) while still landing well inside the >=32px-wide quiet zone.
            var px = result[margin + 27, qrBlockY + margin + 27];
            Assert.True(px.R > 200 && px.G > 200 && px.B > 200,
                $"expected near-white QR quiet-zone pixel right after the photo/swatches, got ({px.R},{px.G},{px.B})");
        }
        finally
        {
            if (File.Exists(outputPath)) File.Delete(outputPath);
        }
    }

    [Fact]
    public void RecipeCardRenderer_Qr_BelowPlacement_SitsRightAfterPhotoNotAfterPlatesOrStrip()
    {
        using var photo = SolidImage(400, 300, Color.CornflowerBlue);
        var metadata = new PaletteImageRenderer.PhotoMetadata(
            "FUJIFILM X-T5", "XF35mmF1.4 R", "35mm", "52mm eq", "f/1.4", "1/500s", "ISO 400",
            "2026-01-01", "+0.3 EV", "WB: Auto", "Manual", "S/N: 12345");
        var qr = new QrRenderOptions(QrPlacement.Below, TestUrl);
        string outputPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.png");

        try
        {
            RecipeCardRenderer.Render(photo, MinimalRecipe(), outputPath, theme: Theme.Dark,
                metaVerbosity: MetaVerbosity.Default, metadataOverride: metadata, qr: qr);

            using var result = Image.Load<Rgb24>(outputPath);
            const int cardWidth = 1200; // mirrors RecipeCardRenderer's private CardWidth
            int photoH = (int)Math.Round(cardWidth * (double)photo.Height / photo.Width);
            int margin = cardWidth / 30;

            // See the sibling PaletteImageRenderer test above for why this is 27px, not 3.
            var px = result[margin + 27, photoH + margin + 27];
            Assert.True(px.R > 200 && px.G > 200 && px.B > 200,
                $"expected near-white QR quiet-zone pixel right after the photo, got ({px.R},{px.G},{px.B})");
        }
        finally
        {
            if (File.Exists(outputPath)) File.Delete(outputPath);
        }
    }

    // Regression for the v2 bugfix pass §4: a long payload + high error correction used to
    // produce enough modules that, at a merely proportional box size, each module rounded down
    // to 2-3px — invisible/unscannable after any JPEG compression or resize. The below-block
    // sizing (and, by construction, RenderModules itself, since ComputeBelowBlockHeight predicts
    // exactly what it draws) must never let a module go below the legibility floor.
    [Fact]
    public void ComputeBelowBlockHeight_LongUrl_KeepsModulesAtLeastMinPixelFloor()
    {
        string longUrl = "https://createlab.github.io/phspectre-view/#b." + new string('A', 150);
        var qr = new QrRenderOptions(QrPlacement.Below, longUrl);
        int boxSize = QrCodeRenderer.SuggestBoxSize(400);
        const int margin = 10;

        int belowH = QrCodeRenderer.ComputeBelowBlockHeight(qr, boxSize, margin);
        int imgSize = belowH - 2 * margin;

        var encoded = QrCode.EncodeText(longUrl, QrCode.Ecc.Medium);
        int modules = encoded.Size + 8; // 4-module quiet zone on each side
        double pxPerModule = (double)imgSize / modules;

        Assert.True(pxPerModule >= 8, $"expected >=8px/module, got {pxPerModule}");
    }

    // A later canvas-wide downscale (Settings' "Export size: Half") must inflate the reserved
    // block so the module size survives that downscale — otherwise a QR that was legible in
    // the pre-downscale canvas becomes unscannable in the actually-saved file.
    [Fact]
    public void ComputeBelowBlockHeight_WithDownscaleFactor_InflatesReservedHeight()
    {
        var qr = new QrRenderOptions(QrPlacement.Below, TestUrl);
        int boxSize = QrCodeRenderer.SuggestBoxSize(1200);
        const int margin = 10;

        int h1 = QrCodeRenderer.ComputeBelowBlockHeight(qr, boxSize, margin, downscaleFactor: 1);
        int h2 = QrCodeRenderer.ComputeBelowBlockHeight(qr, boxSize, margin, downscaleFactor: 2);

        Assert.True(h2 > h1, $"expected downscaleFactor=2 to reserve more height than 1 (got {h2} vs {h1})");
    }

    [Fact]
    public void PaletteImageRenderer_Qr_BelowPlacement_GrowsCanvasVsNoQr()
    {
        using var photo = SolidImage(400, 300, Color.CornflowerBlue);
        var palette = new ColorPalette([new ColorSwatch("#6495ED", (100, 149, 237), 1f)]);
        string plainPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.png");
        string qrPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.png");

        try
        {
            PaletteImageRenderer.Render(photo, palette, plainPath, showSwatches: false);
            PaletteImageRenderer.Render(photo, palette, qrPath, showSwatches: false,
                qr: new QrRenderOptions(QrPlacement.Below, TestUrl));

            using var plain = Image.Load(plainPath);
            using var withQr = Image.Load(qrPath);
            Assert.True(withQr.Height > plain.Height);
            Assert.Equal(plain.Width, withQr.Width);
        }
        finally
        {
            if (File.Exists(plainPath)) File.Delete(plainPath);
            if (File.Exists(qrPath)) File.Delete(qrPath);
        }
    }
}
