using System.Buffers.Binary;
using System.Text;
using PhSpectre.Models;

namespace PhSpectre.Camera;

/// <summary>A decoded custom-setting slot plus the properties whose raw value has no known meaning.</summary>
public sealed record FujiSlotDecode(FilmRecipe Recipe, IReadOnlyList<string> Unmapped);

/// <summary>
/// Maps the per-bank PTP properties (0xD18Dâ€“0xD1A4) of a Fujifilm body to <see cref="FilmRecipe"/>.
/// These wire values are not the MakerNote values <see cref="Recipes.FujiRecipeExtractor"/> reads: tones,
/// Color, Sharpness and Clarity are signed Ã—10, Film Simulation and Grain use their own code lists, and
/// High ISO NR is a non-linear lookup. Tables follow fujifilm-ptp-reference (docs/03-encodings.md) and were
/// checked against JPEGs shot from X-T5 slots C1â€“C6 (see FujiSlotCodecTests).
/// </summary>
public static class FujiSlotCodec
{
    public const ushort PropName = 0xD18D;
    public const ushort PropDynamicRange = 0xD190;
    public const ushort PropDRangePriority = 0xD191;
    public const ushort PropFilmSimulation = 0xD192;
    public const ushort PropMonoWarmCool = 0xD193;
    public const ushort PropGrain = 0xD195;
    public const ushort PropColorChrome = 0xD196;
    public const ushort PropColorChromeBlue = 0xD197;
    public const ushort PropSmoothSkin = 0xD198;
    public const ushort PropWhiteBalance = 0xD199;
    public const ushort PropColorTemperature = 0xD19C;
    public const ushort WbKelvin = 0x8007;
    public const ushort PropWbShiftRed = 0xD19A;
    public const ushort PropWbShiftBlue = 0xD19B;
    public const ushort PropHighlightTone = 0xD19D;
    public const ushort PropShadowTone = 0xD19E;
    public const ushort PropColor = 0xD19F;
    public const ushort PropSharpness = 0xD1A0;
    public const ushort PropNoiseReduction = 0xD1A1;
    public const ushort PropClarity = 0xD1A2;

    // Names match RecipeFieldOptions / FujiRecipeExtractor so a slot and a JPEG of the same recipe compare equal.
    internal static readonly IReadOnlyDictionary<ushort, string> FilmSimulations = new Dictionary<ushort, string>
    {
        [0x01] = "Provia/Standard", [0x02] = "Velvia", [0x03] = "Astia (Soft)",
        [0x04] = "Pro Neg. Hi", [0x05] = "Pro Neg. Std",
        [0x06] = "Monochrome", [0x07] = "Monochrome + Ye Filter", [0x08] = "Monochrome + R Filter", [0x09] = "Monochrome + G Filter",
        [0x0A] = "Sepia", [0x0B] = "Classic Chrome",
        [0x0C] = "Acros", [0x0D] = "Acros + Ye Filter", [0x0E] = "Acros + R Filter", [0x0F] = "Acros + G Filter",
        [0x10] = "Eterna", [0x11] = "Classic Negative", [0x12] = "Eterna Bleach Bypass",
        [0x13] = "Nostalgic Neg", [0x14] = "Reala ACE",
    };

    /// <summary>Film simulation names the camera can store, for the manual recipe form.</summary>
    public static IReadOnlyList<string> FilmSimulationNames { get; } = FilmSimulations.Values.ToList();

    internal static readonly HashSet<ushort> MonochromeFamily = [0x06, 0x07, 0x08, 0x09, 0x0A, 0x0C, 0x0D, 0x0E, 0x0F];

    // 0x8006 is Fuji's "Shade"; the extractor/exiftool name for that slot of the scale is "Cloudy".
    internal static readonly IReadOnlyDictionary<ushort, string> WhiteBalances = new Dictionary<ushort, string>
    {
        [0x0002] = "Auto", [0x0004] = "Daylight", [0x0006] = "Incandescent", [0x0008] = "Underwater",
        [0x8001] = "Fluorescent (Daylight)", [0x8002] = "Fluorescent (Warm White)", [0x8003] = "Fluorescent (Cool White)",
        [0x8006] = "Cloudy", [0x8007] = "Kelvin",
        [0x8008] = "Custom", [0x8009] = "Custom 2", [0x800A] = "Custom 3",
        [0x8020] = "Auto (white priority)", [0x8021] = "Auto (ambiance priority)",
    };

    internal static readonly IReadOnlyDictionary<ushort, int> NoiseReduction = new Dictionary<ushort, int>
    {
        [0x8000] = -4, [0x7000] = -3, [0x4000] = -2, [0x3000] = -1, [0x2000] = 0,
        [0x1000] = 1, [0x0000] = 2, [0x6000] = 3, [0x5000] = 4,
    };

    // 1 = Off, 2/3 = Weak/Strong small, 4/5 = Weak/Strong large; 6/7 = Off with the size remembered.
    private static readonly IReadOnlyDictionary<ushort, string> Grain = new Dictionary<ushort, string>
    {
        [1] = "Off", [6] = "Off", [7] = "Off",
        [2] = "Weak", [4] = "Weak", [3] = "Strong", [5] = "Strong",
    };

    internal static readonly IReadOnlyDictionary<ushort, string> ThreeLevel = new Dictionary<ushort, string>
    {
        [1] = "Off", [2] = "Weak", [3] = "Strong",
    };

    public static FujiSlotDecode Decode(IReadOnlyDictionary<ushort, byte[]> slot, string cameraModel)
    {
        var unmapped = new List<string>();

        ushort? filmCode = TryU16(slot, PropFilmSimulation);
        var film = filmCode is { } fc && FilmSimulations.TryGetValue(fc, out var fn) ? fn : null;
        if (film == null) Note(unmapped, PropFilmSimulation, slot);
        var isMono = filmCode is { } mc && MonochromeFamily.Contains(mc);

        var recipe = new FilmRecipe(
            Source: RecipeSource.Manual,
            RecipeName: DecodeName(slot),
            CameraMake: "FUJIFILM",
            CameraModel: cameraModel,
            FilmSimulation: film,
            WhiteBalance: Lookup(slot, PropWhiteBalance, WhiteBalances, unmapped),
            WhiteBalanceShift: DecodeShift(slot),
            DynamicRange: DecodeDynamicRange(slot, unmapped),
            HighlightTone: Tenths(slot, PropHighlightTone),
            ShadowTone: Tenths(slot, PropShadowTone),
            // The camera greys out Color for monochrome simulations and the extractor reports null there.
            Color: isMono ? null : WholeLevel(slot, PropColor, unmapped),
            Sharpness: WholeLevel(slot, PropSharpness, unmapped),
            NoiseReduction: Lookup(slot, PropNoiseReduction, NoiseReduction, unmapped),
            Clarity: Tenths(slot, PropClarity),
            GrainEffect: Lookup(slot, PropGrain, Grain, unmapped),
            ColorChromeEffect: Lookup(slot, PropColorChrome, ThreeLevel, unmapped),
            ColorChromeFxBlue: Lookup(slot, PropColorChromeBlue, ThreeLevel, unmapped),
            // Warm/cool axis of monochrome toning; only the zero case has been checked against a real JPEG.
            BwAdjustment: isMono && TryS16(slot, PropMonoWarmCool) is { } wc && wc % 10 == 0 ? wc / 10 : null,
            GrainSize: DecodeGrainSize(slot),
            SmoothSkinEffect: Lookup(slot, PropSmoothSkin, ThreeLevel, unmapped),
            // The bank keeps its last Kelvin value under other WB modes, so it only means something under Kelvin.
            ColorTemperature: TryU16(slot, PropWhiteBalance) == WbKelvin ? TryU16(slot, PropColorTemperature) : null);

        return new FujiSlotDecode(recipe, unmapped);
    }

    private static string? DecodeGrainSize(IReadOnlyDictionary<ushort, byte[]> slot) => TryU16(slot, PropGrain) switch
    {
        2 or 3 or 6 => "Small",
        4 or 5 or 7 => "Large",
        _ => null, // plain Off (1) remembers nothing
    };

    /// <summary>Decodes a PTP string property (u8 char count incl. NUL, then UCS-2LE); empty names become null.</summary>
    public static string? DecodeName(IReadOnlyDictionary<ushort, byte[]> slot)
    {
        if (!slot.TryGetValue(PropName, out var bytes) || bytes.Length == 0) return null;
        var chars = bytes[0] - 1;
        if (chars <= 0 || bytes.Length < 1 + chars * 2) return null;
        var name = Encoding.Unicode.GetString(bytes, 1, chars * 2).TrimEnd('\0');
        return name.Length == 0 ? null : name;
    }

    private static string? DecodeDynamicRange(IReadOnlyDictionary<ushort, byte[]> slot, List<string> unmapped)
    {
        // An active D-Range Priority overrides the stored DR, and the model has no field for it.
        if (TryU16(slot, PropDRangePriority) is { } drp && drp != 0)
        {
            Note(unmapped, PropDRangePriority, slot);
            return null;
        }

        switch (TryU16(slot, PropDynamicRange))
        {
            case 0xFFFF: return "Auto";
            case 100: return "DR100";
            case 200: return "DR200";
            case 400: return "DR400";
            default:
                Note(unmapped, PropDynamicRange, slot);
                return null;
        }
    }

    private static (int Red, int Blue)? DecodeShift(IReadOnlyDictionary<ushort, byte[]> slot) =>
        TryS16(slot, PropWbShiftRed) is { } r && TryS16(slot, PropWbShiftBlue) is { } b ? (r, b) : null;

    private static decimal? Tenths(IReadOnlyDictionary<ushort, byte[]> slot, ushort prop) =>
        TryS16(slot, prop) is { } v ? v / 10m : null;

    private static int? WholeLevel(IReadOnlyDictionary<ushort, byte[]> slot, ushort prop, List<string> unmapped)
    {
        if (TryS16(slot, prop) is not { } v) return null;
        if (v % 10 == 0) return v / 10;
        // FilmRecipe keeps this field as a whole number; a half step would be silently rounded.
        Note(unmapped, prop, slot);
        return null;
    }

    private static T? Lookup<T>(IReadOnlyDictionary<ushort, byte[]> slot, ushort prop,
        IReadOnlyDictionary<ushort, T> table, List<string> unmapped)
    {
        if (TryU16(slot, prop) is { } v && table.TryGetValue(v, out var result)) return result;
        Note(unmapped, prop, slot);
        return default;
    }

    private static void Note(List<string> unmapped, ushort prop, IReadOnlyDictionary<ushort, byte[]> slot)
    {
        var raw = slot.TryGetValue(prop, out var bytes) ? Convert.ToHexString(bytes) : "missing";
        unmapped.Add($"0x{prop:X4}={raw}");
    }

    private static ushort? TryU16(IReadOnlyDictionary<ushort, byte[]> slot, ushort prop) =>
        slot.TryGetValue(prop, out var b) && b.Length >= 2 ? BinaryPrimitives.ReadUInt16LittleEndian(b) : null;

    private static short? TryS16(IReadOnlyDictionary<ushort, byte[]> slot, ushort prop) =>
        slot.TryGetValue(prop, out var b) && b.Length >= 2 ? BinaryPrimitives.ReadInt16LittleEndian(b) : null;
}

