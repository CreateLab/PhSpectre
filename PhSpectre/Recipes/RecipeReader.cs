using PhSpectre.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;

namespace PhSpectre.Recipes;

// Mirrors PaletteImageRenderer.ReadMetadata's dual-overload shape so callers already
// familiar with that convention can read a recipe the same way. Best-effort: never throws,
// returns null for a non-matching camera, unreadable file, or extraction failure.
public static class RecipeReader
{
    private static readonly IReadOnlyList<IRecipeExtractor> Extractors = new IRecipeExtractor[]
    {
        new FujiRecipeExtractor(),
    };

    public static FilmRecipe? Read(string path)
    {
        try
        {
            var info = Image.Identify(path);
            return info == null ? null : Read(info.Metadata.ExifProfile);
        }
        catch { return null; }
    }

    public static FilmRecipe? Read(Image image) => Read(image.Metadata.ExifProfile);

    private static FilmRecipe? Read(ExifProfile? profile)
    {
        if (profile == null) return null;
        try
        {
            foreach (var extractor in Extractors)
                if (extractor.CanHandle(profile))
                    return extractor.Extract(profile);
            return null;
        }
        catch { return null; }
    }
}
