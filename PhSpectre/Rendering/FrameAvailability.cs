namespace PhSpectre.Rendering;

public enum FrameType { None, Border, Card, RecipeCard }

public readonly record struct FrameAvailabilityResult(bool Available, string? Reason);

/// <summary>Which frame types can be used for a photo. Recipe card needs an EXIF recipe and no Look.</summary>
public static class FrameAvailability
{
    public static FrameAvailabilityResult Check(FrameType type, bool hasExifRecipe, bool lookApplied)
    {
        if (type != FrameType.RecipeCard) return new(true, null);
        if (lookApplied) return new(false, "a Look is applied. Remove the Look to use Recipe card.");
        if (!hasExifRecipe) return new(false, "no film recipe found in this photo (not a Fujifilm JPEG, or EXIF was removed).");
        return new(true, null);
    }

    /// <summary>The type to fall back to when the chosen one is unavailable.</summary>
    public static FrameType Resolve(FrameType type, bool hasExifRecipe, bool lookApplied) =>
        Check(type, hasExifRecipe, lookApplied).Available ? type : FrameType.Card;
}
