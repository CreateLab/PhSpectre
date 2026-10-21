using System;
using System.IO;
using System.Linq;
using PhSpectre.Models;
using PhSpectre.Rendering;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Xunit;

namespace PhSpecre.Tests;

// The card layout is derived from the card width, so the same photo at two resolutions must
// produce the same proportions (the old fixed pixel clamps made small photos' labels tiny
// and large photos' labels huge).
public class CardGridTests
{
    private static ColorPalette Palette(int n) => new(Enumerable.Range(0, n)
        .Select(i => new ColorSwatch($"#{i * 30:X2}{i * 20:X2}{i * 10:X2}", ((byte)(i * 30), (byte)(i * 20), (byte)(i * 10)), 1f / n))
        .ToList());

    private static (int W, int H) RenderSize(int w, int h, int colors)
    {
        using var photo = new Image<Rgb24>(w, h);
        photo.Mutate(c => c.BackgroundColor(Color.Gray));
        var path = Path.Combine(Path.GetTempPath(), $"cardgrid_{Guid.NewGuid():N}.png");
        try
        {
            PaletteImageRenderer.Render(photo, Palette(colors), path, metaVerbosity: MetaVerbosity.Off);
            var info = Image.Identify(path);
            return (info.Width, info.Height);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(4)]
    [InlineData(6)]
    public void PaletteBand_IsProportionalToCardWidth(int colors)
    {
        var small = RenderSize(600, 400, colors);
        var large = RenderSize(6000, 4000, colors);

        double smallBand = (small.H - 400) / 600.0;
        double largeBand = (large.H - 4000) / 6000.0;
        Assert.InRange(smallBand / largeBand, 0.9, 1.1);
    }

    [Fact]
    public void CardPadding_IsTwoAndAHalfPercent_WithTwelvePixelFloor()
    {
        Assert.Equal(12, PaletteImageRenderer.CardPadding(300));
        Assert.Equal(15, PaletteImageRenderer.CardPadding(600));
        Assert.Equal(150, PaletteImageRenderer.CardPadding(6000));
    }
}
