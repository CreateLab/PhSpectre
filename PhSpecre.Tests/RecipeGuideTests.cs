using PhSpectre.Models;
using PhSpectre.Rendering;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Xunit;

namespace PhSpecre.Tests;

public class RecipeGuideTests
{
    private static FilmRecipe Recipe() => new(
        Source: RecipeSource.AutoDetected, RecipeName: null,
        CameraMake: "FUJIFILM", CameraModel: "X-T5",
        FilmSimulation: "Provia", WhiteBalance: null, WhiteBalanceShift: null,
        DynamicRange: null, HighlightTone: null, ShadowTone: null, Color: null,
        Sharpness: null, NoiseReduction: null, Clarity: null, GrainEffect: null,
        ColorChromeEffect: null, ColorChromeFxBlue: null, BwAdjustment: null);

    [Fact]
    public void RecipeCard_DrawsGuideLines_WhenRequested()
    {
        using var photo = new Image<Rgb24>(600, 400);
        photo.Mutate(c => c.BackgroundColor(Color.Black));

        using var plain = RecipeCardRenderer.RenderToImage(photo, Recipe());
        using var guided = RecipeCardRenderer.RenderToImage(photo, Recipe(),
            compositionGuide: CompositionGuide.RuleOfThirds);

        Assert.Equal(plain.Height, guided.Height);
        bool differs = false;
        for (int y = 0; y < 400 && !differs; y++)
            for (int x = 0; x < 1200 && !differs; x++)
                differs = plain[x, y] != guided[x, y];
        Assert.True(differs);
    }

    [Fact]
    public void RecipeCard_OverlayStyle_DrawsCameraTextOnThePhoto_NotInAStrip()
    {
        using var photo = new Image<Rgb24>(600, 400);
        photo.Mutate(c => c.BackgroundColor(Color.Black));
        var metadata = new PaletteImageRenderer.PhotoMetadata(
            "FUJIFILM X-T5", "XF35mmF1.4 R", "35mm", "52mm eq", "f/1.4", "1/500s", "ISO 400",
            "2026-01-01", "+0.3 EV", "WB: Auto", "Manual", "S/N: 12345");

        using var strip = RecipeCardRenderer.RenderToImage(photo, Recipe(),
            metaVerbosity: MetaVerbosity.Default, metadataOverride: metadata, metaStyle: MetaStyle.FilmStrip);
        using var overlay = RecipeCardRenderer.RenderToImage(photo, Recipe(),
            metaVerbosity: MetaVerbosity.Default, metadataOverride: metadata, metaStyle: MetaStyle.Overlay);

        Assert.True(overlay.Height < strip.Height);
        bool textOnPhoto = false;
        for (int y = 0; y < 150 && !textOnPhoto; y++)
            for (int x = 0; x < 600 && !textOnPhoto; x++)
                textOnPhoto = overlay[x, y] != new Rgb24(0, 0, 0);
        Assert.True(textOnPhoto);
    }
}

public class BlurredBackdropRobustnessTests
{
    [Theory]
    [InlineData(60, 40)]
    [InlineData(300, 20)]
    [InlineData(1600, 900)]
    public void RecipeCard_WithBlurredBackdrop_DoesNotThrow_OnAnySize(int w, int h)
    {
        using var photo = new Image<Rgb24>(w, h);
        photo.Mutate(c => c.BackgroundColor(Color.SteelBlue));
        var recipe = new FilmRecipe(
            RecipeSource.AutoDetected, null, null, null, null, null, null, null, null, null, null,
            null, null, null, null, null, null, null);

        using var card = RecipeCardRenderer.RenderToImage(photo, recipe, useBlurredBackground: true);
        Assert.True(card.Width > 0 && card.Height > 0);
    }
}
