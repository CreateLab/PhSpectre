using System;
using System.IO;
using PhSpectre.Models;
using PhSpectre.Rendering;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Xunit;

namespace PhSpecre.Tests;

// Structural smoke tests for the useBlurredBackground option added to both card renderers —
// this suite has no pixel-diffing tests anywhere else, so these only assert "doesn't throw,
// produces a decodable image of the expected size" rather than checking exact pixels.
public class BlurredBackgroundTests
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

    [Theory]
    [InlineData(Theme.Dark)]
    [InlineData(Theme.Light)]
    public void PaletteImageRenderer_BlurredBackground_RendersWithoutError(Theme theme)
    {
        using var photo = SolidImage(400, 300, Color.CornflowerBlue);
        var palette = new ColorPalette([new ColorSwatch("#6495ED", (100, 149, 237), 1f)]);
        string outputPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.png");

        try
        {
            PaletteImageRenderer.Render(photo, palette, outputPath, theme: theme, useBlurredBackground: true);

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
    [InlineData(Theme.Dark)]
    [InlineData(Theme.Light)]
    public void RecipeCardRenderer_BlurredBackground_RendersWithoutError(Theme theme)
    {
        using var photo = SolidImage(400, 300, Color.CornflowerBlue);
        string outputPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.png");

        try
        {
            RecipeCardRenderer.Render(photo, MinimalRecipe(), outputPath, theme: theme, useBlurredBackground: true);

            using var result = Image.Load(outputPath);
            Assert.True(result.Width > 0);
            Assert.True(result.Height > 0);
        }
        finally
        {
            if (File.Exists(outputPath)) File.Delete(outputPath);
        }
    }
}
