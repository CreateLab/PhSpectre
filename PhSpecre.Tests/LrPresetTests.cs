using System;
using PhSpectre.ColorGrading;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace PhSpecre.Tests;

public class LrPresetTests
{
    private static string Xmp(string attrs = "", string body = "") =>
        "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\" xmlns:crs=\"http://ns.adobe.com/camera-raw-settings/1.0/\" " +
        "xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\"><rdf:RDF>" +
        $"<rdf:Description rdf:about=\"\" crs:Name=\"T\" {attrs}>{body}</rdf:Description></rdf:RDF></x:xmpmeta>";

    private static Rgb24 Grade(string xmp, Rgb24 px, float strength = 1f)
    {
        using var img = new Image<Rgb24>(1, 1);
        img[0, 0] = px;
        LrPresetApplier.Apply(img, LrPreset.Parse(xmp), strength);
        return img[0, 0];
    }

    [Fact]
    public void Parse_ReadsValuesNameAndSignedNumbers()
    {
        var p = LrPreset.Parse(Xmp("crs:Contrast2012=\"+18\" crs:Shadows2012=\"-25\" crs:Exposure2012=\"0.50\""));
        Assert.Equal("T", p.Name);
        Assert.Equal(18f, p.Get("Contrast2012"));
        Assert.Equal(-25f, p.Get("Shadows2012"));
        Assert.Equal(0.5f, p.Get("Exposure2012"));
    }

    [Fact]
    public void Parse_ReadsToneCurvePoints()
    {
        var body = "<crs:ToneCurvePV2012><rdf:Seq><rdf:li>0, 10</rdf:li><rdf:li>255, 245</rdf:li></rdf:Seq></crs:ToneCurvePV2012>";
        var p = LrPreset.Parse(Xmp(body: body));
        Assert.Equal(new[] { (0f, 10f), (255f, 245f) }, p.Curves["Master"]);
    }

    [Fact]
    public void Parse_ListsUnsupportedNonZeroKeys()
    {
        var p = LrPreset.Parse(Xmp("crs:Clarity2012=\"20\" crs:Texture=\"0\" crs:Dehaze=\"5\""));
        Assert.Contains("Clarity2012", p.Skipped);
        Assert.DoesNotContain("Temperature", p.Skipped);
        Assert.Contains("Dehaze", p.Skipped);
        Assert.DoesNotContain("Texture", p.Skipped);
    }

    [Fact]
    public void Parse_Throws_OnMalformedXml() => Assert.Throws<FormatException>(() => LrPreset.Parse("<a>"));

    [Fact]
    public void Parse_Throws_WhenNoCrsSettings() =>
        Assert.Throws<FormatException>(() => LrPreset.Parse("<rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\"><rdf:Description/></rdf:RDF>"));

    [Fact]
    public void Apply_NeutralPreset_IsIdentity()
    {
        var o = Grade(Xmp("crs:Contrast2012=\"0\" crs:Exposure2012=\"0\""), new Rgb24(40, 130, 220));
        Assert.True(Math.Abs(o.R - 40) <= 1 && Math.Abs(o.G - 130) <= 1 && Math.Abs(o.B - 220) <= 1, o.ToString());
    }

    [Fact]
    public void Apply_PositiveExposure_Brightens_NegativeDarkens()
    {
        var up = Grade(Xmp("crs:Exposure2012=\"1\""), new Rgb24(60, 60, 60));
        var down = Grade(Xmp("crs:Exposure2012=\"-1\""), new Rgb24(60, 60, 60));
        Assert.True(up.R > 60 && down.R < 60);
    }

    [Fact]
    public void Apply_MinusHundredSaturation_GivesGray()
    {
        var o = Grade(Xmp("crs:Saturation=\"-100\""), new Rgb24(200, 60, 30));
        Assert.True(Math.Abs(o.R - o.G) <= 1 && Math.Abs(o.G - o.B) <= 1, o.ToString());
    }

    [Fact]
    public void Apply_MonochromeTreatment_GivesGray()
    {
        var o = Grade(Xmp("crs:Treatment=\"Monochrome\""), new Rgb24(200, 60, 30));
        Assert.True(Math.Abs(o.R - o.G) <= 1 && Math.Abs(o.G - o.B) <= 1, o.ToString());
    }

    [Fact]
    public void Apply_IdentityCurve_IsNoOp()
    {
        var body = "<crs:ToneCurvePV2012><rdf:Seq><rdf:li>0, 0</rdf:li><rdf:li>255, 255</rdf:li></rdf:Seq></crs:ToneCurvePV2012>";
        var o = Grade(Xmp(body: body), new Rgb24(90, 150, 30));
        Assert.True(Math.Abs(o.R - 90) <= 1 && Math.Abs(o.G - 150) <= 1 && Math.Abs(o.B - 30) <= 1, o.ToString());
    }

    [Fact]
    public void Apply_LiftingCurve_BrightensMidtones()
    {
        var body = "<crs:ToneCurvePV2012><rdf:Seq><rdf:li>0, 0</rdf:li><rdf:li>128, 160</rdf:li><rdf:li>255, 255</rdf:li></rdf:Seq></crs:ToneCurvePV2012>";
        var o = Grade(Xmp(body: body), new Rgb24(128, 128, 128));
        Assert.InRange(o.R, 158, 162);
    }

    [Fact]
    public void Apply_ZeroStrength_IsNoOp()
    {
        var o = Grade(Xmp("crs:Exposure2012=\"2\""), new Rgb24(50, 60, 70), 0f);
        Assert.Equal(new Rgb24(50, 60, 70), o);
    }

    [Fact]
    public void Apply_HalfStrength_LiesBetweenOriginalAndFull()
    {
        var full = Grade(Xmp("crs:Exposure2012=\"1\""), new Rgb24(60, 60, 60));
        var half = Grade(Xmp("crs:Exposure2012=\"1\""), new Rgb24(60, 60, 60), 0.5f);
        Assert.InRange(half.R, 61, full.R - 1);
    }

    [Fact]
    public void Apply_WarmerTemperature_RaisesRedLowersBlue()
    {
        var o = Grade(Xmp("crs:Temperature=\"+40\""), new Rgb24(128, 128, 128));
        Assert.True(o.R > o.B, o.ToString());
        var k = Grade(Xmp("crs:Temperature=\"7500\""), new Rgb24(128, 128, 128));
        Assert.True(k.R > k.B, k.ToString());
        var cool = Grade(Xmp("crs:Temperature=\"4500\""), new Rgb24(128, 128, 128));
        Assert.True(cool.B > cool.R, cool.ToString());
    }

    [Fact]
    public void Apply_PositiveTint_ShiftsTowardMagenta()
    {
        var o = Grade(Xmp("crs:Tint=\"+30\""), new Rgb24(128, 128, 128));
        Assert.True(o.G < o.R && o.G < o.B, o.ToString());
    }

    [Fact]
    public void Bake_ProducesUsable3DLut()
    {
        var lut = LrPresetApplier.Bake(LrPreset.Parse(Xmp("crs:Exposure2012=\"1\"")));
        Assert.True(lut.Is3D);
        Assert.True(lut.Evaluate(0.3f, 0.3f, 0.3f).X > 0.3f);
    }
}
