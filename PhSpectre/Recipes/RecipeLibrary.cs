using System.Text.Json;
using PhSpectre.Models;

namespace PhSpectre.Recipes;

public enum RecipeOrigin { Bundled, FromCamera, FromImage }

/// <summary>A recipe kept in the app: either shipped with it or saved by the user from a photo or a camera slot.</summary>
public sealed record RecipeLibraryEntry(
    string Id,
    string Name,
    RecipeOrigin Origin,
    FilmRecipe Recipe,
    string? Author = null,
    string? SourceUrl = null,
    DateTimeOffset? Saved = null);

public enum AddOutcome { Added, Duplicate }

public sealed record AddResult(AddOutcome Outcome, RecipeLibraryEntry Entry);

/// <summary>Saved recipes in a JSON file. A recipe with identical settings is not stored twice.</summary>
public sealed class RecipeLibrary
{
    private const int FormatVersion = 1;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private sealed record FileDto(int Version, List<RecipeLibraryEntry> Entries);

    private readonly List<RecipeLibraryEntry> _entries = [];
    private readonly string? _path;

    public RecipeLibrary(string? path = null)
    {
        _path = path;
    }

    public IReadOnlyList<RecipeLibraryEntry> Entries => _entries;

    /// <summary>A missing or unreadable file gives an empty library.</summary>
    public static RecipeLibrary Load(string path)
    {
        var library = new RecipeLibrary(path);
        try
        {
            if (!File.Exists(path)) return library;
            var dto = JsonSerializer.Deserialize<FileDto>(File.ReadAllText(path), Json);
            if (dto?.Entries != null) library._entries.AddRange(dto.Entries);
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            // A damaged file must not stop the app.
            PhSpectre.Services.SafeFiles.KeepUnreadable(path);
        }
        return library;
    }

    /// <summary>Parses a bundled recipe list; a damaged one gives an empty list.</summary>
    public static IReadOnlyList<RecipeLibraryEntry> ParseEntries(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<RecipeLibraryEntry>>(json, Json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>The settings without labels, for comparison.</summary>
    public static FilmRecipe ContentKey(FilmRecipe recipe) =>
        recipe with { Source = RecipeSource.Manual, RecipeName = null, CameraMake = "", CameraModel = "" };

    public AddResult Add(FilmRecipe recipe, string? name, RecipeOrigin origin, string? author = null, string? sourceUrl = null)
    {
        var key = ContentKey(recipe);
        var existing = _entries.FirstOrDefault(e => ContentKey(e.Recipe).Equals(key));
        if (existing != null) return new AddResult(AddOutcome.Duplicate, existing);

        var title = string.IsNullOrWhiteSpace(name) ? DefaultName(recipe) : name.Trim();
        var entry = new RecipeLibraryEntry(Guid.NewGuid().ToString("N"), UniqueName(title), origin,
            recipe with { RecipeName = title }, author, sourceUrl, DateTimeOffset.Now);
        _entries.Add(entry);
        Save();
        return new AddResult(AddOutcome.Added, entry);
    }

    public bool Remove(string id)
    {
        var removed = _entries.RemoveAll(e => e.Id == id) > 0;
        if (removed) Save();
        return removed;
    }

    public bool Rename(string id, string name)
    {
        var index = _entries.FindIndex(e => e.Id == id);
        if (index < 0 || string.IsNullOrWhiteSpace(name)) return false;
        var clean = name.Trim();
        var entry = _entries[index];
        _entries[index] = entry with { Name = UniqueName(clean, id), Recipe = entry.Recipe with { RecipeName = clean } };
        Save();
        return true;
    }

    public static string DefaultName(FilmRecipe recipe) =>
        !string.IsNullOrWhiteSpace(recipe.RecipeName) ? recipe.RecipeName!
        : recipe.FilmSimulation != null ? $"{recipe.FilmSimulation} recipe"
        : "Recipe";

    private string UniqueName(string name, string? exceptId = null)
    {
        bool Taken(string n) => _entries.Any(e => e.Id != exceptId && string.Equals(e.Name, n, StringComparison.OrdinalIgnoreCase));
        if (!Taken(name)) return name;
        for (var i = 2; ; i++)
            if (!Taken($"{name} ({i})")) return $"{name} ({i})";
    }

    private void Save()
    {
        if (_path == null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            // Temp file first, so a crash cannot leave half a file.
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(new FileDto(FormatVersion, _entries), Json));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Keep working in memory.
        }
    }
}
