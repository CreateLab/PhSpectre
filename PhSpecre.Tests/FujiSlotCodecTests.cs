using PhSpectre.Camera;
using PhSpectre.Models;
using Xunit;

namespace PhSpecre.Tests;

// Ground truth: raw bank dumps from a real X-T5 (fw 4.31) read over USB, paired with the recipe
// FujiRecipeExtractor reads from a JPEG shot from that same slot. Each case asserts the slot decodes
// to the JPEG's recipe, which is what validates the PTP wire encodings.
public class FujiSlotCodecTests
{
    private static readonly Dictionary<string, string> Banks = new()
    {
        ["C1"] = "D18D=114D006F006F006400790020004D006F006E006F006300680072006F006D0065000000;D190=FFFF;D191=0000;D192=0D00;D193=0000;D194=0000;D195=0500;D196=0100;D197=0100;D198=0100;D199=0380;D19A=FCFF;D19B=F7FF;D19C=1027;D19D=F6FF;D19E=2800;D19F=0000;D1A0=ECFF;D1A1=0080;D1A2=E2FF",
        ["C2"] = "D18D=1052006500670067006900650027007300200050006F0072007400720061000000;D190=FFFF;D191=0000;D192=0B00;D193=0000;D194=0000;D195=0200;D196=0300;D197=0200;D198=0100;D199=0200;D19A=0200;D19B=FCFF;D19C=B414;D19D=F6FF;D19E=F6FF;D19F=1400;D1A0=ECFF;D1A1=0080;D1A2=0000",
        ["C3"] = "D18D=1152006500670067006900650027007300200053007500700065007200690061000000;D190=FFFF;D191=0000;D192=1100;D193=0000;D194=0000;D195=0500;D196=0300;D197=0300;D198=0100;D199=0200;D19A=0100;D19B=FDFF;D19C=A816;D19D=ECFF;D19E=F6FF;D19F=0A00;D1A0=ECFF;D1A1=0080;D1A2=0000",
        ["C4"] = "D18D=0F4B006F00640061006B0020004E0065006700610074006900760065000000;D190=9001;D191=0000;D192=1300;D193=0000;D194=0000;D195=0400;D196=0100;D197=0100;D198=0100;D199=0200;D19A=0100;D19B=FCFF;D19C=8813;D19D=FBFF;D19E=1900;D19F=2800;D1A0=F6FF;D1A1=0080;D1A2=E2FF",
        ["C5"] = "D18D=0542004900520044000000;D190=9001;D191=0000;D192=0100;D193=0000;D194=0000;D195=0600;D196=0100;D197=0200;D198=0100;D199=0200;D19A=0000;D19B=0000;D19C=8813;D19D=F6FF;D19E=F6FF;D19F=0A00;D1A0=1400;D1A1=0040;D1A2=0000",
        ["C6"] = "D18D=0D540068006500200052006F0063006B00770065006C006C000000;D190=FFFF;D191=0000;D192=0200;D193=0000;D194=0000;D195=0200;D196=0300;D197=0300;D198=0100;D199=0200;D19A=0100;D19B=FFFF;D19C=A816;D19D=F6FF;D19E=F6FF;D19F=2800;D1A0=2800;D1A1=0080;D1A2=3200",

    };

    private static FujiSlotDecode DecodeBank(string bank)
    {
        var slot = Banks[bank].Split(';')
            .Select(kv => kv.Split('='))
            .ToDictionary(p => Convert.ToUInt16(p[0], 16), p => Convert.FromHexString(p[1]));
        return FujiSlotCodec.Decode(slot, "X-T5");
    }

    private static void AssertMatchesJpeg(FilmRecipe actual, FilmRecipe jpeg)
    {
        Assert.Equal(jpeg.FilmSimulation, actual.FilmSimulation);
        Assert.Equal(jpeg.WhiteBalance, actual.WhiteBalance);
        Assert.Equal(jpeg.WhiteBalanceShift, actual.WhiteBalanceShift);
        Assert.Equal(jpeg.DynamicRange, actual.DynamicRange);
        Assert.Equal(jpeg.HighlightTone, actual.HighlightTone);
        Assert.Equal(jpeg.ShadowTone, actual.ShadowTone);
        Assert.Equal(jpeg.Color, actual.Color);
        Assert.Equal(jpeg.Sharpness, actual.Sharpness);
        Assert.Equal(jpeg.NoiseReduction, actual.NoiseReduction);
        Assert.Equal(jpeg.Clarity, actual.Clarity);
        Assert.Equal(jpeg.GrainEffect, actual.GrainEffect);
        Assert.Equal(jpeg.ColorChromeEffect, actual.ColorChromeEffect);
        Assert.Equal(jpeg.ColorChromeFxBlue, actual.ColorChromeFxBlue);
        Assert.Equal(jpeg.BwAdjustment, actual.BwAdjustment);
    }

    private static FilmRecipe Jpeg(string fs, string wb, (int, int) shift, string dr, decimal hl, decimal sh, int? color,
        int sharp, int nr, decimal clarity, string grain, string cce, string ccfb, int? bw = null) =>
        new(RecipeSource.AutoDetected, null, "FUJIFILM", "X-T5", fs, wb, shift, dr, hl, sh, color, sharp, nr, clarity,
            grain, cce, ccfb, bw);

    [Fact]
    public void C1_AcrosYe_MatchesJpeg()
    {
        var result = DecodeBank("C1");
        Assert.Equal("Moody Monochrome", result.Recipe.RecipeName);
        AssertMatchesJpeg(result.Recipe,
            Jpeg("Acros + Ye Filter", "Fluorescent (Cool White)", (-4, -9), "Auto", -1, 4, null, -2, -4, -3, "Strong", "Off", "Off", bw: 0));
        Assert.Empty(result.Unmapped);
    }

    [Fact]
    public void C2_ClassicChrome_MatchesJpeg()
    {
        var result = DecodeBank("C2");
        AssertMatchesJpeg(result.Recipe,
            Jpeg("Classic Chrome", "Auto", (2, -4), "Auto", -1, -1, 2, -2, -4, 0, "Weak", "Strong", "Weak"));
        Assert.Empty(result.Unmapped);
    }

    [Fact]
    public void C3_ClassicNegative_MatchesJpeg()
    {
        var result = DecodeBank("C3");
        AssertMatchesJpeg(result.Recipe,
            Jpeg("Classic Negative", "Auto", (1, -3), "Auto", -2, -1, 1, -2, -4, 0, "Strong", "Strong", "Strong"));
        Assert.Empty(result.Unmapped);
    }

    [Fact]
    public void C4_NostalgicNeg_HalfStepTonesAndDr400_MatchesJpeg()
    {
        var result = DecodeBank("C4");
        AssertMatchesJpeg(result.Recipe,
            Jpeg("Nostalgic Neg", "Auto", (1, -4), "DR400", -0.5m, 2.5m, 4, -1, -4, -3, "Weak", "Off", "Off"));
        Assert.Empty(result.Unmapped);
    }

    [Fact]
    public void C5_ProviaWithNoiseReductionMinus2_MatchesJpeg()
    {
        var result = DecodeBank("C5");
        AssertMatchesJpeg(result.Recipe,
            Jpeg("Provia/Standard", "Auto", (0, 0), "DR400", -1, -1, 1, 2, -2, 0, "Off", "Off", "Weak"));
        Assert.Empty(result.Unmapped);
    }

    [Fact]
    public void C6_Velvia_MatchesJpeg()
    {
        var result = DecodeBank("C6");
        AssertMatchesJpeg(result.Recipe,
            Jpeg("Velvia", "Auto", (1, -1), "Auto", -1, -1, 4, 4, -4, 5, "Weak", "Strong", "Strong"));
        Assert.Empty(result.Unmapped);
    }

    [Fact]
    public void UnknownFilmSimulation_IsReportedNotGuessed()
    {
        var slot = new Dictionary<ushort, byte[]> { [FujiSlotCodec.PropFilmSimulation] = [0x63, 0x00] };

        var result = FujiSlotCodec.Decode(slot, "X-T5");

        Assert.Null(result.Recipe.FilmSimulation);
        Assert.Contains("0xD192=6300", result.Unmapped);
    }

    [Fact]
    public void EmptyName_BecomesNull()
    {
        var slot = new Dictionary<ushort, byte[]> { [FujiSlotCodec.PropName] = [0x01, 0x00, 0x00] };

        Assert.Null(FujiSlotCodec.DecodeName(slot));
    }

    [Fact]
    public void ActiveDRangePriority_DropsDynamicRangeAndReportsIt()
    {
        var slot = new Dictionary<ushort, byte[]>
        {
            [FujiSlotCodec.PropDRangePriority] = [0x01, 0x00],
            [FujiSlotCodec.PropDynamicRange] = [0x64, 0x00],
        };

        var result = FujiSlotCodec.Decode(slot, "X-T5");

        Assert.Null(result.Recipe.DynamicRange);
        Assert.Contains("0xD191=0100", result.Unmapped);
    }
}
