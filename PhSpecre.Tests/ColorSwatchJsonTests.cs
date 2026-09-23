using System.Text.Json;
using PhSpectre.Models;
using Xunit;

namespace PhSpecre.Tests;

public class ColorSwatchJsonTests
{
    [Fact]
    public void Serialize_Rgb_UsesRGBNamesNotItem123()
    {
        var swatch = new ColorSwatch("#FF8040", (255, 128, 64), 0.5f);
        var json = JsonSerializer.Serialize(swatch);

        Assert.Contains("\"Rgb\":{\"R\":255,\"G\":128,\"B\":64}", json);
        Assert.DoesNotContain("Item1", json);
    }

    [Fact]
    public void RoundTrip_Rgb_PreservesValues()
    {
        var swatch = new ColorSwatch("#010203", (1, 2, 3), 0.1f);
        var json = JsonSerializer.Serialize(swatch);
        var back = JsonSerializer.Deserialize<ColorSwatch>(json);

        Assert.NotNull(back);
        Assert.Equal((1, 2, 3), back!.Rgb);
    }
}
