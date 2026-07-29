using PhSpectre.Models;
using PhSpectre.Rendering;
using Xunit;

namespace PhSpecre.Tests;

public class PaletteSortingTests
{
    private static readonly ColorSwatch Red   = new("#FF0000", (255, 0, 0), 0.10f);
    private static readonly ColorSwatch Green = new("#00FF00", (0, 255, 0), 0.50f);
    private static readonly ColorSwatch Blue  = new("#0000FF", (0, 0, 255), 0.40f);
    private static readonly ColorSwatch Gray  = new("#808080", (128, 128, 128), 0.30f);

    [Theory]
    [InlineData(255, 0, 0, 0f)]
    [InlineData(0, 255, 0, 120f)]
    [InlineData(0, 0, 255, 240f)]
    [InlineData(128, 128, 128, 0f)]
    public void RgbToHue_ReturnsExpectedDegrees(byte r, byte g, byte b, float expected)
    {
        var hue = PaletteImageRenderer.RgbToHue(r, g, b);
        Assert.Equal(expected, hue, 0.01f);
    }

    [Fact]
    public void SortSwatches_None_PreservesOriginalOrder()
    {
        var swatches = new[] { Blue, Red, Green };
        var sorted = PaletteImageRenderer.SortSwatches(swatches, SortOrder.None);
        Assert.Equal(swatches, sorted);
    }

    [Fact]
    public void SortSwatches_Hue_OrdersByAscendingHue()
    {
        var swatches = new[] { Blue, Red, Green };
        var sorted = PaletteImageRenderer.SortSwatches(swatches, SortOrder.Hue);
        Assert.Equal([Red, Green, Blue], sorted);
    }

    [Fact]
    public void SortSwatches_Luminance_OrdersAscendingByBrightness()
    {
        var swatches = new[] { Green, Red, Blue };
        var sorted = PaletteImageRenderer.SortSwatches(swatches, SortOrder.Luminance);
        // Blue is darkest, Red mid, Green brightest per WCAG relative luminance weights.
        Assert.Equal([Blue, Red, Green], sorted);
    }

    [Fact]
    public void SortSwatches_Percent_OrdersDescendingByCoverage()
    {
        var swatches = new[] { Red, Green, Blue, Gray };
        var sorted = PaletteImageRenderer.SortSwatches(swatches, SortOrder.Percent);
        Assert.Equal([Green, Blue, Gray, Red], sorted);
    }
}
