using PhSpectre.Models;
using PhSpectre.Recipes;
using Xunit;

namespace PhSpecre.Tests;

public class RecipeLibraryTests
{
    private static FilmRecipe Recipe(string? name = "A", string film = "Classic Chrome", string model = "X-T5") =>
        new(RecipeSource.AutoDetected, name, "FUJIFILM", model, film, "Auto", (2, -4), "DR200", -0.5m, 1.5m, 3, -1, -2, 2m,
            "Strong", "Strong", "Weak", null);

    private static string TempPath() => Path.Combine(Path.GetTempPath(), $"phspectre-lib-{Guid.NewGuid():N}", "recipes.json");

    [Fact]
    public void Add_StoresAndPersists_AcrossReload()
    {
        var path = TempPath();
        var library = new RecipeLibrary(path);

        var result = library.Add(Recipe(), "My look", RecipeOrigin.FromImage);

        Assert.Equal(AddOutcome.Added, result.Outcome);
        var reloaded = RecipeLibrary.Load(path);
        var entry = Assert.Single(reloaded.Entries);
        Assert.Equal("My look", entry.Name);
        Assert.Equal("My look", entry.Recipe.RecipeName);
        Assert.Equal((2, -4), entry.Recipe.WhiteBalanceShift);
        Assert.Equal(RecipeOrigin.FromImage, entry.Origin);
    }

    [Fact]
    public void Add_SameSettingsFromAnotherCameraOrName_IsADuplicate()
    {
        var library = new RecipeLibrary();
        var first = library.Add(Recipe("One"), null, RecipeOrigin.FromImage);

        var second = library.Add(Recipe("Two", model: "X-M5"), null, RecipeOrigin.FromCamera);

        Assert.Equal(AddOutcome.Duplicate, second.Outcome);
        Assert.Equal(first.Entry.Id, second.Entry.Id);
        Assert.Single(library.Entries);
    }

    [Fact]
    public void Add_SameNameDifferentSettings_GetsUniqueName()
    {
        var library = new RecipeLibrary();
        library.Add(Recipe("Look"), null, RecipeOrigin.FromImage);

        var added = library.Add(Recipe("Look", film: "Velvia"), null, RecipeOrigin.FromImage);

        Assert.Equal("Look (2)", added.Entry.Name);
    }

    [Fact]
    public void Add_WithoutAnyName_FallsBackToFilmSimulation()
    {
        var library = new RecipeLibrary();
        var added = library.Add(Recipe(name: null), null, RecipeOrigin.FromImage);
        Assert.Equal("Classic Chrome recipe", added.Entry.Name);
    }

    [Fact]
    public void RemoveAndRename_Persist()
    {
        var path = TempPath();
        var library = new RecipeLibrary(path);
        var a = library.Add(Recipe("A"), null, RecipeOrigin.FromImage).Entry;
        var b = library.Add(Recipe("B", film: "Velvia"), null, RecipeOrigin.FromImage).Entry;

        Assert.True(library.Rename(a.Id, "Renamed"));
        Assert.True(library.Remove(b.Id));

        var reloaded = RecipeLibrary.Load(path);
        var entry = Assert.Single(reloaded.Entries);
        Assert.Equal("Renamed", entry.Name);
    }

    [Fact]
    public void Load_DamagedFile_GivesEmptyLibrary()
    {
        var path = TempPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ not json");

        Assert.Empty(RecipeLibrary.Load(path).Entries);
    }
}
