using PhSpectre.Models;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;

namespace PhSpectre.Recipes;

// Extraction point for manufacturer-specific MakerNote parsing. Only FujiRecipeExtractor
// exists today; other manufacturers plug in here later without touching RecipeReader's
// dispatch logic.
public interface IRecipeExtractor
{
    bool CanHandle(ExifProfile exif);
    FilmRecipe? Extract(ExifProfile exif);
}
