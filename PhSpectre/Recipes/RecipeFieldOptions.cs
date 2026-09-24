namespace PhSpectre.Recipes;

// Display-string option lists for manual recipe entry (UI dropdowns). Kept separate from
// FujiRecipeExtractor's internal raw-byte-code maps — those are keyed by Fuji's hardware
// values and not meant as a public surface; these are just the same value domain expressed
// as plain strings for a picker.
public static class RecipeFieldOptions
{
    public static readonly IReadOnlyList<string> WhiteBalance =
    [
        "Auto", "Auto (white priority)", "Auto (ambiance priority)",
        "Daylight", "Cloudy",
        "Fluorescent (Daylight)", "Fluorescent (Warm White)", "Fluorescent (Cool White)",
        "Incandescent", "Flash", "Underwater",
        "Custom", "Custom 2", "Custom 3", "Custom 4", "Kelvin",
    ];

    // "Manual" alone (a raw MakerNote flag meaning "not Auto", see FujiRecipeExtractor.
    // DecodeDynamicRange) isn't a value anyone actually picks on the camera dial, so it isn't
    // offered here — a manually-entered recipe should use one of the real percentages instead.
    // "Off" covers bodies/shots where DR bracketing wasn't used at all.
    public static readonly IReadOnlyList<string> DynamicRange =
    [
        "Off", "Auto", "DR100%", "DR200%", "DR400%", "Film Simulation",
    ];

    // Shared by Grain Effect, Color Chrome Effect, Color Chrome FX Blue.
    public static readonly IReadOnlyList<string> ThreeLevel = ["Off", "Weak", "Strong"];

    public static readonly IReadOnlyList<string> FilmSimulation =
    [
        "Provia/Standard", "Studio Portrait", "Studio Portrait Enhanced Saturation",
        "Astia (Soft)", "Studio Portrait Increased Sharpness", "Velvia", "Studio Portrait Ex",
        "Velvia (old)", "Pro Neg. Std", "Pro Neg. Hi", "Classic Chrome", "Eterna",
        "Classic Negative", "Eterna Bleach Bypass", "Nostalgic Neg", "Reala ACE",
        "Monochrome", "Monochrome + R Filter", "Monochrome + Ye Filter", "Monochrome + G Filter",
        "Sepia", "Acros", "Acros + R Filter", "Acros + Ye Filter", "Acros + G Filter",
    ];
}
