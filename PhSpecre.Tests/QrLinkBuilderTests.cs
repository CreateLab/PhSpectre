using PhSpectre.Models;
using PhSpectre.Qr;
using PhSpectre.Rendering;
using Xunit;

namespace PhSpecre.Tests;

// Covers the preset → field-selection mapping and the placeholder payload encoder — the part
// of the QR feature that isn't exercised by QrCodeRendererTests' raw-URL smoke tests.
public class QrLinkBuilderTests
{
    private static readonly PaletteImageRenderer.PhotoMetadata Metadata = new(
        Camera: "FUJIFILM X-T5", Lens: "XF35mmF1.4 R",
        Focal: "35mm", FocalEq: "52mm eq", Aperture: "f/1.4", Shutter: "1/500s", Iso: "ISO 400",
        Date: "2026-01-01", ExposureBias: "+0.3 EV", WhiteBalance: "WB: Auto",
        ExpProgram: "Manual", Serial: "S/N: 12345");

    private static FilmRecipe MinimalRecipe() => new(
        Source: RecipeSource.AutoDetected, RecipeName: null,
        CameraMake: "FUJIFILM", CameraModel: "X-T5",
        FilmSimulation: "Provia", WhiteBalance: null, WhiteBalanceShift: null,
        DynamicRange: null, HighlightTone: null, ShadowTone: null, Color: null,
        Sharpness: null, NoiseReduction: null, Clarity: null, GrainEffect: null,
        ColorChromeEffect: null, ColorChromeFxBlue: null, BwAdjustment: null);

    [Theory]
    [InlineData(QrContentPreset.CameraAndRecipe,              false, false, true)]
    [InlineData(QrContentPreset.CameraLensPhotoInfoAndRecipe, true,  true,  true)]
    [InlineData(QrContentPreset.CameraPhotoInfoAndRecipe,     false, true,  true)]
    [InlineData(QrContentPreset.CameraLensAndPhotoInfo,       true,  true,  false)]
    [InlineData(QrContentPreset.RecipeOnly,                   false, false, true)]
    public void BuildFields_MatchesPresetTable(
        QrContentPreset preset, bool expectLens, bool expectPhotoInfo, bool expectRecipe)
    {
        var fields = QrLinkBuilder.BuildFields(preset, Metadata, MinimalRecipe(), note: null);

        bool expectCamera = preset != QrContentPreset.RecipeOnly;
        Assert.Equal(expectCamera, fields.Camera != null);
        Assert.Equal(expectLens, fields.Lens != null);
        Assert.Equal(expectPhotoInfo, fields.Focal != null);
        Assert.Equal(expectPhotoInfo, fields.Aperture != null);
        Assert.Equal(expectPhotoInfo, fields.Shutter != null);
        Assert.Equal(expectPhotoInfo, fields.Iso != null);
        Assert.Equal(expectRecipe, fields.Recipe != null);
    }

    [Fact]
    public void BuildFields_BlankNote_BecomesNull()
    {
        var fields = QrLinkBuilder.BuildFields(QrContentPreset.RecipeOnly, Metadata, MinimalRecipe(), note: "  ");
        Assert.Null(fields.Note);
    }

    [Fact]
    public void BuildGeneratedUrl_ProducesViewerUrlWithFragmentPayload()
    {
        string url = QrLinkBuilder.BuildGeneratedUrl(
            QrContentPreset.CameraLensPhotoInfoAndRecipe, Metadata, MinimalRecipe(), note: "hello");

        Assert.StartsWith("https://createlab.github.io/phspectre-view/#b.", url);
        Assert.True(url.Length > "https://createlab.github.io/phspectre-view/#b.".Length);
    }

    [Fact]
    public void BuildGeneratedUrl_NullRecipe_DoesNotThrow()
    {
        string url = QrLinkBuilder.BuildGeneratedUrl(
            QrContentPreset.CameraLensAndPhotoInfo, Metadata, recipe: null, note: null);

        Assert.StartsWith("https://createlab.github.io/phspectre-view/#b.", url);
    }
}
