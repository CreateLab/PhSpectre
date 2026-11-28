using PhSpectre.Models;

namespace PhSpectre.Recipes;

// A displayable parameter: Key (stored for visibility), short and full label, value.
public sealed record RecipeField(string Key, string Label, string FullLabel, string Value);

// Parameters in display order, shared by the card and the settings panel.
public static class RecipeFields
{
    public const string WhiteBalance = "wb";
    public const string WhiteBalanceShift = "wbshift";
    public const string DynamicRange = "dr";
    public const string Highlight = "highlight";
    public const string Shadow = "shadow";
    public const string Color = "color";
    public const string Sharpness = "sharpness";
    public const string NoiseReduction = "nr";
    public const string Clarity = "clarity";
    public const string Grain = "grain";
    public const string ColorChrome = "cc";
    public const string ColorChromeBlue = "ccblue";
    public const string BwAdjustment = "bw";

    // "B&W" drops the colour fields; "Minimal" keeps the simulation and four parameters.
    public static readonly IReadOnlySet<string> BlackAndWhiteHidden = new HashSet<string>
    {
        Color, WhiteBalance, WhiteBalanceShift, ColorChrome, ColorChromeBlue,
    };

    private static readonly string[] MinimalVisible = [WhiteBalance, DynamicRange, Highlight, Shadow];

    public static IReadOnlySet<string> MinimalHidden(IEnumerable<RecipeField> all) =>
        all.Select(f => f.Key).Where(k => !MinimalVisible.Contains(k)).ToHashSet();

    // Acros, Monochrome, Sepia (and the decoder's "+ filter" variants) — anything B&W-family.
    public static bool IsMonochrome(string? filmSimulation) =>
        !string.IsNullOrEmpty(filmSimulation) &&
        (filmSimulation.Contains("acros", StringComparison.OrdinalIgnoreCase)
         || filmSimulation.Contains("monochrome", StringComparison.OrdinalIgnoreCase)
         || filmSimulation.Contains("sepia", StringComparison.OrdinalIgnoreCase)
         || filmSimulation.Contains("b&w", StringComparison.OrdinalIgnoreCase));

    // Stored as one comma-separated string.
    public static IReadOnlySet<string> ParseKeys(string? csv) =>
        string.IsNullOrWhiteSpace(csv)
            ? new HashSet<string>()
            : csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet();

    public static string JoinKeys(IEnumerable<string> keys) => string.Join(",", keys.OrderBy(k => k, StringComparer.Ordinal));

    // Every parameter the recipe actually carries, in display order.
    public static List<RecipeField> BuildAll(FilmRecipe r)
    {
        var rows = new List<RecipeField>();
        void Add(string key, string shortLabel, string fullLabel, string? value)
        {
            if (!string.IsNullOrEmpty(value))
                rows.Add(new RecipeField(key, shortLabel, fullLabel, RecipeValueAbbreviations.Shorten(value)));
        }

        Add(WhiteBalance, "White Balance", "White Balance", r.WhiteBalance);
        if (r.WhiteBalanceShift is { } shift)
            Add(WhiteBalanceShift, "WB Shift", "WB Shift", $"R{shift.Red:+0;-0;0} B{shift.Blue:+0;-0;0}");
        Add(DynamicRange, "Dynamic Range", "Dynamic Range", r.DynamicRange);
        if (r.HighlightTone is { } hi) Add(Highlight, "Highlight", "Highlight", hi.ToString("+0.#;-0.#;0"));
        if (r.ShadowTone is { } sh) Add(Shadow, "Shadow", "Shadow", sh.ToString("+0.#;-0.#;0"));
        // Color is -4..+4; anything else shows as a dash.
        if (r.Color is { } col) Add(Color, "Color", "Color", col is >= -4 and <= 4 ? col.ToString("+0;-0;0") : "—");
        if (r.Sharpness is { } sharp) Add(Sharpness, "Sharpness", "Sharpness", sharp.ToString("+0;-0;0"));
        if (r.NoiseReduction is { } nr) Add(NoiseReduction, "Noise Red.", "Noise Reduction", nr.ToString("+0;-0;0"));
        if (r.Clarity is { } clarity) Add(Clarity, "Clarity", "Clarity", clarity.ToString("+0.#;-0.#;0"));
        Add(Grain, "Grain", "Grain Effect", r.GrainEffect);
        Add(ColorChrome, "Color Chrome", "Color Chrome", r.ColorChromeEffect);
        Add(ColorChromeBlue, "CC Blue", "Color Chrome Blue", r.ColorChromeFxBlue);
        if (r.BwAdjustment is { } bw) Add(BwAdjustment, "B&W Adj.", "B&W Adjustment", bw.ToString("+0;-0;0"));
        return rows;
    }

    public static List<RecipeField> BuildVisible(FilmRecipe r, IReadOnlySet<string>? hidden) =>
        BuildAll(r).Where(f => hidden == null || !hidden.Contains(f.Key)).ToList();
}
