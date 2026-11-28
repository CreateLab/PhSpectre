using PhSpectre.Camera;
using PhSpectre.Recipes;
using Xunit;

namespace PhSpecre.Tests;

public class BundledRecipesTests
{
    private static IReadOnlyList<RecipeLibraryEntry> Load()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PhSpectre.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var path = Path.Combine(dir!.FullName, "PhSpectre.Avalonia", "Assets", "Recipes", "library.json");
        return RecipeLibrary.ParseEntries(File.ReadAllText(path));
    }

    [Fact]
    public void Bundled_HasRecipes_WithUniqueIdsAndNames()
    {
        var entries = Load();

        Assert.NotEmpty(entries);
        Assert.Equal(entries.Count, entries.Select(e => e.Id).Distinct().Count());
        Assert.Equal(entries.Count, entries.Select(e => e.Name.ToLowerInvariant()).Distinct().Count());
        Assert.All(entries, e => Assert.Equal(RecipeOrigin.Bundled, e.Origin));
    }

    [Fact]
    public void Bundled_EveryRecipeKeepsCredit()
    {
        Assert.All(Load(), e =>
        {
            Assert.False(string.IsNullOrWhiteSpace(e.Author), $"{e.Name} has no author");
            Assert.StartsWith("http", e.SourceUrl);
        });
    }

    [Fact]
    public void Bundled_EveryRecipeFitsInACameraSlot()
    {
        // Every value must have a verified encoding, or "Send to camera" would drop settings. The monochrome
        // warm/cool shift is the known exception.
        Assert.All(Load(), e =>
        {
            Assert.InRange(e.Name.Length, 1, 25);
            var encoding = FujiSlotEncoder.Encode(e.Recipe);
            var unexpected = encoding.Skipped.Where(x => !x.StartsWith("BwAdjustment")).ToList();
            Assert.True(unexpected.Count == 0, $"{e.Name}: {string.Join("; ", unexpected)}");
        });
    }
}
