using System.Text.Json;
using PhSpectre.ColorGrading;
using Xunit;

namespace PhSpecre.Tests;

// Every look listed in looks.json must ship and load.
public class BundledLooksTests
{
    private sealed record Item(string Id, string Name, string Kind, string Category, string File);

    private static string LooksDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PhSpectre.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "PhSpectre.Avalonia", "Assets", "Looks");
    }

    [Fact]
    public void EveryListedLook_Loads_WithUniqueIdAndName()
    {
        var root = LooksDir();
        var items = JsonSerializer.Deserialize<List<Item>>(File.ReadAllText(Path.Combine(root, "looks.json")),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

        Assert.Equal(items.Count, items.Select(i => i.Id).Distinct().Count());
        Assert.Equal(items.Count, items.Select(i => (i.Kind, i.Name)).Distinct().Count());
        foreach (var i in items)
        {
            var text = File.ReadAllText(Path.Combine(root, i.File));
            if (i.Kind == "lut") Assert.True(CubeLut.Parse(text) != null, i.Name);
            else Assert.True(LrPreset.Parse(text) != null, i.Name);
        }
    }
}
