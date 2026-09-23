using System.Text.Json;
using PhSpectre.Models;
using Xunit;

namespace PhSpecre.Tests;

// System.Text.Json ignores ValueTuple's public fields by default, and even with
// IncludeFields it serializes as {"Item1":..,"Item2":..} rather than the tuple's compile-time
// element names — WhiteBalanceShiftJsonConverter fixes this. Regression coverage for that.
public class FilmRecipeJsonTests
{
    private static FilmRecipe Minimal((int, int)? shift) => new(
        Source: RecipeSource.AutoDetected, RecipeName: null,
        CameraMake: "FUJIFILM", CameraModel: "X-T5",
        FilmSimulation: null, WhiteBalance: null, WhiteBalanceShift: shift,
        DynamicRange: null, HighlightTone: null, ShadowTone: null, Color: null,
        Sharpness: null, NoiseReduction: null, Clarity: null, GrainEffect: null,
        ColorChromeEffect: null, ColorChromeFxBlue: null, BwAdjustment: null);

    [Fact]
    public void Serialize_WhiteBalanceShift_UsesRedBlueNames()
    {
        var json = JsonSerializer.Serialize(Minimal((40, -60)));

        Assert.Contains("\"WhiteBalanceShift\":{\"Red\":40,\"Blue\":-60}", json);
        Assert.DoesNotContain("Item1", json);
        Assert.DoesNotContain("Item2", json);
    }

    [Fact]
    public void Serialize_NullWhiteBalanceShift_WritesJsonNull()
    {
        var json = JsonSerializer.Serialize(Minimal(null));
        Assert.Contains("\"WhiteBalanceShift\":null", json);
    }

    [Fact]
    public void RoundTrip_WhiteBalanceShift_PreservesValues()
    {
        var json = JsonSerializer.Serialize(Minimal((3, -7)));
        var back = JsonSerializer.Deserialize<FilmRecipe>(json);

        Assert.NotNull(back);
        Assert.Equal((3, -7), back!.WhiteBalanceShift);
    }
}
