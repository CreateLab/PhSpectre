using PhSpectre.Models;

namespace PhSpectre.Recipes;

// One displayable recipe parameter: a stable Key (what visibility is stored against), a short
// Label for tight tiles, the FullLabel for the card and tooltips, and the shortened Value.
public sealed record RecipeField(string Key, string Label, string FullLabel, string Value);

// The recipe parameters in display order, plus the hide/show helpers shared by the card renderer
// and the settings panel so the preview and the export always use the same visible list.
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

    // "Color" preset hides nothing; "B&W" drops everything about colour; "Minimal" keeps the
    // film simulation (the card title) and four main parameters.
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

    // Hidden keys are persisted/compared as one comma-separated string (record-friendly equality).
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
        // Fujifilm Color is -4..+4; anything else is a mis-decoded tag, shown as a dash, not noise.
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
