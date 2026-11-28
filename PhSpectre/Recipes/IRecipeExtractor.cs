using PhSpectre.Models;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;

namespace PhSpectre.Recipes;

// Manufacturer-specific MakerNote parsing.
public interface IRecipeExtractor
{
    bool CanHandle(ExifProfile exif);
    FilmRecipe? Extract(ExifProfile exif);
}
