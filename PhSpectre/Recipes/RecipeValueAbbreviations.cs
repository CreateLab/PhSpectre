namespace PhSpectre.Recipes;

// Short forms for values too wide for a plate; others wrap or shrink.
public static class RecipeValueAbbreviations
{
    private static readonly IReadOnlyDictionary<string, string> Map = new Dictionary<string, string>
    {
        // "Auto (white/ambiance priority)" is left unshortened: "Auto W" is unreadable.
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
