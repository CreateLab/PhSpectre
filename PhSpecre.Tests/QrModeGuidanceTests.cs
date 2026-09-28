using System;
using PhSpectre.Models;
using PhSpectre.Qr;
using Xunit;

namespace PhSpecre.Tests;

// Covers the Export-Mode-aware preset guidance added in the v2 bugfix pass §2: Recipe mode
// already prints the whole recipe as text and InfoOnly/CollageInfoOnly already print camera/
// lens/EXIF as text, so the recommended default and the "duplicates" hint must line up with
// what each mode actually renders as text.
public class QrModeGuidanceTests
{
    [Theory]
    [InlineData(ExportMode.Card, QrContentPreset.CameraLensPhotoInfoAndRecipe)]
    [InlineData(ExportMode.Collage, QrContentPreset.CameraLensPhotoInfoAndRecipe)]
    [InlineData(ExportMode.Recipe, QrContentPreset.CameraLensAndPhotoInfo)]
    [InlineData(ExportMode.InfoOnly, QrContentPreset.RecipeOnly)]
    [InlineData(ExportMode.CollageInfoOnly, QrContentPreset.RecipeOnly)]
    public void RecommendedPreset_MatchesSpecTable(ExportMode mode, QrContentPreset expected)
        => Assert.Equal(expected, QrModeGuidance.RecommendedPreset(mode));

    [Fact]
    public void DuplicatesCardText_RecipeMode_FlagsAnyPresetThatIncludesRecipe()
    {
        Assert.True(QrModeGuidance.DuplicatesCardText(ExportMode.Recipe, QrContentPreset.RecipeOnly));
        Assert.True(QrModeGuidance.DuplicatesCardText(ExportMode.Recipe, QrContentPreset.CameraAndRecipe));
        Assert.True(QrModeGuidance.DuplicatesCardText(ExportMode.Recipe, QrContentPreset.CameraPhotoInfoAndRecipe));
        Assert.True(QrModeGuidance.DuplicatesCardText(ExportMode.Recipe, QrContentPreset.CameraLensPhotoInfoAndRecipe));
        Assert.False(QrModeGuidance.DuplicatesCardText(ExportMode.Recipe, QrContentPreset.CameraLensAndPhotoInfo));
    }

    [Fact]
    public void DuplicatesCardText_InfoOnlyModes_FlagOnlyTheNoRecipePreset()
    {
        Assert.True(QrModeGuidance.DuplicatesCardText(ExportMode.InfoOnly, QrContentPreset.CameraLensAndPhotoInfo));
        Assert.True(QrModeGuidance.DuplicatesCardText(ExportMode.CollageInfoOnly, QrContentPreset.CameraLensAndPhotoInfo));
        Assert.False(QrModeGuidance.DuplicatesCardText(ExportMode.InfoOnly, QrContentPreset.RecipeOnly));
        Assert.False(QrModeGuidance.DuplicatesCardText(ExportMode.CollageInfoOnly, QrContentPreset.CameraAndRecipe));
    }

    [Fact]
    public void DuplicatesCardText_CardOrCollage_NeverFlagsAnyPreset()
    {
        foreach (QrContentPreset preset in Enum.GetValues<QrContentPreset>())
        {
            Assert.False(QrModeGuidance.DuplicatesCardText(ExportMode.Card, preset));
            Assert.False(QrModeGuidance.DuplicatesCardText(ExportMode.Collage, preset));
        }
    }
}
