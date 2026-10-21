using PhSpectre.Models;
using PhSpectre.Recipes;
using PhSpectre.Rendering;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Xunit;

namespace PhSpecre.Tests;

public class RecipeFieldsTests
{
    private static FilmRecipe Full(int? color = 2, string sim = "Provia") => new(
        RecipeSource.AutoDetected, null, "FUJIFILM", "X-T5", sim, "Auto", (1, -2), "DR200",
        1m, -1m, color, 1, -2, 0m, "Weak", "Strong", "Weak", 0);

    [Fact]
    public void BuildAll_ReturnsAllThirteenFields_InOrder()
    {
        var all = RecipeFields.BuildAll(Full());
        Assert.Equal(13, all.Count);
        Assert.Equal(RecipeFields.WhiteBalance, all[0].Key);
    }

    [Fact]
    public void OutOfRangeColor_ShowsDash()
    {
        var color = RecipeFields.BuildAll(Full(color: 1282)).Single(f => f.Key == RecipeFields.Color);
        Assert.Equal("—", color.Value);
    }

    [Fact]
    public void BuildVisible_SkipsHiddenKeys()
    {
        var hidden = RecipeFields.ParseKeys("color,cc");
        var visible = RecipeFields.BuildVisible(Full(), hidden);
        Assert.Equal(11, visible.Count);
        Assert.DoesNotContain(visible, f => f.Key == RecipeFields.Color);
    }

    [Theory]
    [InlineData("Acros", true)]
    [InlineData("Monochrome + Yellow", true)]
    [InlineData("Provia", false)]
    [InlineData(null, false)]
    public void IsMonochrome(string? sim, bool expected) =>
        Assert.Equal(expected, RecipeFields.IsMonochrome(sim));

    [Fact]
    public void Minimal_KeepsFourFields()
    {
        var all = RecipeFields.BuildAll(Full());
        var hidden = RecipeFields.MinimalHidden(all);
        Assert.Equal(4, RecipeFields.BuildVisible(Full(), hidden).Count);
    }

    [Fact]
    public void Card_ShrinksWhenFieldsHidden_AndSurvivesNoVisibleFields()
    {
        using var photo = new Image<Rgb24>(600, 400);
        photo.Mutate(c => c.BackgroundColor(Color.Gray));
        var recipe = Full();

        using var all = RecipeCardRenderer.RenderToImage(photo, recipe);
        using var few = RecipeCardRenderer.RenderToImage(photo, recipe,
            hiddenRecipeFields: RecipeFields.JoinKeys(RecipeFields.MinimalHidden(RecipeFields.BuildAll(recipe))));
        using var none = RecipeCardRenderer.RenderToImage(photo, recipe,
            hiddenRecipeFields: RecipeFields.JoinKeys(RecipeFields.BuildAll(recipe).Select(f => f.Key)));

        Assert.True(few.Height < all.Height);
        Assert.True(none.Height < few.Height);
    }
}
