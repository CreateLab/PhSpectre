using System;
using System.Collections.Generic;
using System.IO;
using PhSpectre.Recipes;
using global::Avalonia.Platform;

namespace PhSpectre.Avalonia.Services;

/// <summary>The saved-recipes file and the recipes that ship with the app.</summary>
internal static class RecipeLibraryService
{
    private static readonly Lazy<RecipeLibrary> UserLibrary = new(() => RecipeLibrary.Load(FilePath));
    private static readonly Lazy<IReadOnlyList<RecipeLibraryEntry>> Bundled = new(LoadBundled);

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PhSpectre", "recipes.json");

    public static RecipeLibrary User => UserLibrary.Value;

    public static IReadOnlyList<RecipeLibraryEntry> Popular => Bundled.Value;

    private static IReadOnlyList<RecipeLibraryEntry> LoadBundled()
    {
        try
        {
            using var stream = AssetLoader.Open(new Uri("avares://PhSpectre.Avalonia/Assets/Recipes/library.json"));
            using var reader = new StreamReader(stream);
            return RecipeLibrary.ParseEntries(reader.ReadToEnd());
        }
        catch (Exception e) when (e is IOException or FileNotFoundException)
        {
            AppLogger.LogError("Failed to read the bundled recipes", e);
            return [];
        }
    }
}
