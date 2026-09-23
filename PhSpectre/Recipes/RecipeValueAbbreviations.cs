namespace PhSpectre.Recipes;

// Shortened display forms for recipe values whose full text is too wide to sit comfortably
// in a fixed-width plate cell (both the exported/previewed RecipeCardRenderer grid and the
// in-app Settings panel's plate chips). Anything not in this table is left as-is and relies
// on the caller's own wrap/shrink handling for the rare longer value.
public static class RecipeValueAbbreviations
{
    private static readonly IReadOnlyDictionary<string, string> Map = new Dictionary<string, string>
    {
        ["Auto (white priority)"] = "Auto W",
        ["Auto (ambiance priority)"] = "Auto A",
        ["Fluorescent (Daylight)"] = "Fluor. Daylight",
        ["Fluorescent (Warm White)"] = "Fluor. Warm",
        ["Fluorescent (Cool White)"] = "Fluor. Cool",
        ["Studio Portrait Enhanced Saturation"] = "Studio Port. +Sat",
        ["Studio Portrait Increased Sharpness"] = "Studio Port. +Sharp",
        ["Eterna Bleach Bypass"] = "Eterna Bleach",
        ["Monochrome + R Filter"] = "Mono +R",
        ["Monochrome + Ye Filter"] = "Mono +Ye",
        ["Monochrome + G Filter"] = "Mono +G",
        ["Acros + R Filter"] = "Acros +R",
        ["Acros + Ye Filter"] = "Acros +Ye",
        ["Acros + G Filter"] = "Acros +G",
        ["Wide1 (230%)"] = "Wide1",
        ["Wide2 (400%)"] = "Wide2",
        ["Standard (100%)"] = "Standard",
        ["Film Simulation"] = "Film Sim.",
    };

    public static string Shorten(string value) => Map.TryGetValue(value, out var shortForm) ? shortForm : value;
}
