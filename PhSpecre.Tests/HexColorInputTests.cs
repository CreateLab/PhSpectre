using PhSpectre.Rendering;
using Xunit;

namespace PhSpecre.Tests;

public class HexColorInputTests
{
    [Theory]
    [InlineData("#FFFFFF")]
    [InlineData("ffffff")]
    [InlineData("  #1a2B3c ")]
    public void Accepts_SixDigitHex_WithOrWithoutHash(string text)
        => Assert.True(HexColorInput.TryParse(text, out _));

    [Theory]
    [InlineData("zzz")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("#abc")]
    [InlineData("#AARRGGBB")]
    [InlineData("#80FFFFFF")]
    [InlineData("#12345G")]
    public void Rejects_Everything_Else(string? text)
        => Assert.False(HexColorInput.TryParse(text, out _));
}
