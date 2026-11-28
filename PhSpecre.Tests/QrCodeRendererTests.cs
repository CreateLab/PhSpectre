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

// Smoke tests for the QR code inside both card renderers.
public class QrCodeRendererTests
{

    [Fact]
    public void PaletteImageRenderer_NoPaletteAndNoCameraInfo_RendersPlainCard()
    {
        using var photo = new Image<Rgb24>(400, 300);
        var palette = new ColorPalette([new ColorSwatch("#000000", (0, 0, 0), 1f)]);
        string path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.png");
        try
        {
            PaletteImageRenderer.Render(photo, palette, path, showSwatches: false, metaVerbosity: MetaVerbosity.Off);
            using var result = Image.Load(path);
            Assert.True(result.Width >= 400 && result.Height >= 300);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
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

    // "Below" goes right after the photo and swatches, before any text plate. The dark theme makes the white QR
    // chip unambiguous.
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

            // 27 px into the quiet zone clears the chip's rounded corner (up to 24 px).
            Assert.True(HasNearWhiteInColumn(result, result.Width - margin - 27, qrBlockY, result.Height),
                "expected the QR quiet zone at the bottom-right, below the photo/swatches");
        }
        finally
        {
            if (File.Exists(outputPath)) File.Delete(outputPath);
        }
    }

    private static bool HasNearWhiteInColumn(Image<Rgb24> img, int x, int yFrom, int yTo)
    {
        for (int y = yFrom; y < yTo; y++)
        {
            var px = img[x, y];
            if (px.R > 200 && px.G > 200 && px.B > 200) return true;
        }
        return false;
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
            int margin = Math.Max(12, (int)Math.Round(cardWidth * 0.025)); // mirrors PaletteImageRenderer.CardPadding

            Assert.True(HasNearWhiteInColumn(result, result.Width - margin - 27, photoH, result.Height),
                "expected the QR quiet zone at the bottom-right of the card");
        }
        finally
        {
            if (File.Exists(outputPath)) File.Delete(outputPath);
        }
    }

    // A long payload with high error correction must not shrink modules below the legible size.
    [Fact]
    public void ComputeBelowBlockHeight_LongUrl_KeepsModulesAtLeastMinPixelFloor()
    {
        string longUrl = "https://createlab.github.io/phspectre-view/#b." + new string('A', 150);
        var qr = new QrRenderOptions(QrPlacement.Below, longUrl);
        int boxSize = QrCodeRenderer.SuggestBoxSize(400);
        const int margin = 10;

        int belowH = QrCodeRenderer.ComputeBelowBlockHeight(qr, boxSize, margin);
        int imgSize = belowH - 2 * margin;

        var encoded = QrCode.EncodeText(longUrl, QrCode.Ecc.Low);
        int modules = encoded.Size + 8; // 4-module quiet zone on each side
        double pxPerModule = (double)imgSize / modules;

        Assert.True(pxPerModule >= 4, $"expected >=4px/module, got {pxPerModule}");
    }

    // An export downscale must not shrink the QR below the legible size.
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
