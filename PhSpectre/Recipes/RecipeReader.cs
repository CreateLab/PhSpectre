using PhSpectre.Models;
using PhSpectre.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;

namespace PhSpectre.Recipes;

// Best effort: null for another camera, an unreadable file or a failed extraction.
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
            // HEIC goes through HeifContainerReader: not every platform registers a HEIF decoder.
            if (HeifContainerReader.IsHeifFile(path))
                return Read(HeifContainerReader.TryReadExifProfile(path));

            var info = Image.Identify(path);
            return info == null ? null : Read(info.Metadata.ExifProfile);
        }
        catch { return null; }
    }

    public static FilmRecipe? Read(Image image) => Read(image.Metadata.ExifProfile);

    public static FilmRecipe? Read(ExifProfile? profile)
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
