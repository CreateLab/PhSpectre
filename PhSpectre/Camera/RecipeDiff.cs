using PhSpectre.Models;

namespace PhSpectre.Camera;

public sealed record RecipeChange(string Field, string Was, string Will);

/// <summary>What a write would change in a slot, as the slot itself would read back afterwards.</summary>
public sealed record RecipeDiffResult(IReadOnlyList<RecipeChange> Changes, int Unchanged);

public static class RecipeDiff
{
    private const string None = "—";

    private static readonly (string Label, Func<FilmRecipe, string?> Get)[] Fields =
    [
        ("Name", r => r.RecipeName),
        ("Film Simulation", r => r.FilmSimulation),
        ("White Balance", r => r.WhiteBalance),
        ("Color Temperature", r => r.ColorTemperature is { } k ? $"{k}K" : null),
        ("WB Shift", r => r.WhiteBalanceShift is { } s ? $"R{Signed(s.Red)} B{Signed(s.Blue)}" : null),
        ("Dynamic Range", r => r.DynamicRange),
        ("Highlight", r => r.HighlightTone is { } v ? Signed(v) : null),
        ("Shadow", r => r.ShadowTone is { } v ? Signed(v) : null),
        ("Color", r => r.Color is { } v ? Signed(v) : null),
        ("Sharpness", r => r.Sharpness is { } v ? Signed(v) : null),
        ("Noise Reduction", r => r.NoiseReduction is { } v ? Signed(v) : null),
        ("Clarity", r => r.Clarity is { } v ? Signed(v) : null),
        ("Grain Effect", r => r.GrainEffect),
        ("Grain Size", r => r.GrainSize),
        ("Color Chrome", r => r.ColorChromeEffect),
        ("Color Chrome Blue", r => r.ColorChromeFxBlue),
        ("Smooth Skin", r => r.SmoothSkinEffect),
        ("B&W Adjustment", r => r.BwAdjustment is { } v ? Signed(v) : null),
    ];

    /// <summary>
    /// Applies the encoded writes to a copy of the slot and compares the two decoded results, so skipped
    /// fields and fields the recipe leaves null correctly show as unchanged.
    /// </summary>
    public static RecipeDiffResult Compute(IReadOnlyDictionary<ushort, byte[]> slot, SlotEncoding encoding, string model)
    {
        var after = slot.ToDictionary(kv => kv.Key, kv => kv.Value);
        foreach (var write in encoding.Writes) after[write.Property] = write.Value;

        var before = FujiSlotCodec.Decode(slot, model).Recipe;
        var next = FujiSlotCodec.Decode(after, model).Recipe;

        var changes = new List<RecipeChange>();
        var unchanged = 0;
        foreach (var (label, get) in Fields)
        {
            var was = get(before);
            var will = get(next);
            if (was == will)
            {
                if (was != null) unchanged++;
                continue;
            }
            changes.Add(new RecipeChange(label, was ?? None, will ?? None));
        }
        return new RecipeDiffResult(changes, unchanged);
    }

    private static string Signed(decimal v) => v > 0 ? $"+{v}" : v.ToString();
}
