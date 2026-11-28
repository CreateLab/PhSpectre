using System.Buffers.Binary;
using PhSpectre.Models;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;

namespace PhSpectre.Recipes;

// Parses the Fujifilm MakerNote IFD (0x927C). Checked against real JPEGs and ExifTool. Long values are addressed
// from the MakerNote start.
public sealed class FujiRecipeExtractor : IRecipeExtractor
{
    public bool CanHandle(ExifProfile exif) =>
        exif.TryGetValue(ExifTag.Make, out var make) &&
        string.Equals(make.Value?.Trim(), "FUJIFILM", StringComparison.OrdinalIgnoreCase);

    public FilmRecipe? Extract(ExifProfile exif)
    {
        try
        {
            if (!exif.TryGetValue(ExifTag.Make, out var makeVal)) return null;
            string make = makeVal.Value?.Trim() ?? "";
            if (!string.Equals(make, "FUJIFILM", StringComparison.OrdinalIgnoreCase)) return null;

            exif.TryGetValue(ExifTag.Model, out var modelVal);
            string model = modelVal?.Value?.Trim() ?? "";

            if (!exif.TryGetValue(ExifTag.MakerNote, out var mnVal) || mnVal.Value is not { Length: > 16 } bytes)
                return null;

            var entries = ParseFujiIfd(bytes);
            if (entries == null) return null;

            return new FilmRecipe(
                Source: RecipeSource.AutoDetected,
                RecipeName: null, // Fuji never stores a user-given preset name in MakerNotes — see FilmRecipe doc.
                CameraMake: make,
                CameraModel: model,
                FilmSimulation: DecodeFilmSimulation(entries),
                WhiteBalance: DecodeEnumOrNull(entries, 0x1002, WhiteBalanceMap),
                WhiteBalanceShift: DecodeShift(entries, 0x100a),
                DynamicRange: DecodeDynamicRange(entries),
                HighlightTone: DecodeTone(entries, 0x1041),
                ShadowTone: DecodeTone(entries, 0x1040),
                Color: DecodeColorLevel(entries),
                Sharpness: DecodeLevel(entries, 0x1001, SharpnessMap),
                NoiseReduction: DecodeLevel(entries, 0x100e, NoiseReductionMap),
                Clarity: DecodeClarity(entries, 0x100f),
                GrainEffect: DecodeEnumOrNull(entries, 0x1047, ThreeLevelMap),
                ColorChromeEffect: DecodeEnumOrNull(entries, 0x1048, ThreeLevelMap),
                ColorChromeFxBlue: DecodeEnumOrNull(entries, 0x104e, ThreeLevelMap),
                BwAdjustment: DecodeSByte(entries, 0x1049)
            );
        }
        catch
        {
            return null;
        }
    }

    // Binary IFD parsing (internal for tests)

    // Bounds-checked; a malformed entry only loses its own field.
    internal static Dictionary<ushort, byte[]>? ParseFujiIfd(byte[] mn)
    {
        if (mn.Length < 12 || !mn.AsSpan(0, 8).SequenceEqual("FUJIFILM"u8))
            return null;

        if (!TryReadU32LE(mn, 8, out uint ifdOffset) || ifdOffset >= mn.Length)
            return null;
        if (!TryReadU16LE(mn, (int)ifdOffset, out ushort count))
            return null;

        var result = new Dictionary<ushort, byte[]>();
        int entriesStart = (int)ifdOffset + 2;
        for (int i = 0; i < count; i++)
        {
            int entryOffset = entriesStart + i * 12;
            if (entryOffset + 12 > mn.Length) break; // truncated entry table — stop, keep what we have

            if (!TryReadU16LE(mn, entryOffset, out ushort tag)) continue;
            if (!TryReadU16LE(mn, entryOffset + 2, out ushort type)) continue;
            if (!TryReadU32LE(mn, entryOffset + 4, out uint elemCount)) continue;

            long totalSize = (long)TypeSize(type) * elemCount;
            if (totalSize is < 0 or > int.MaxValue) continue;

            byte[] value;
            if (totalSize <= 4)
            {
                value = new byte[totalSize];
                Array.Copy(mn, entryOffset + 8, value, 0, (int)totalSize);
            }
            else
            {
                if (!TryReadU32LE(mn, entryOffset + 8, out uint valueOffset)) continue;
                long start = valueOffset;
                long end = start + totalSize;
                if (start < 0 || end > mn.Length) continue; // offset/length out of range — skip this field only
                value = new byte[totalSize];
                Array.Copy(mn, (int)start, value, 0, (int)totalSize);
            }
            result[tag] = value;
        }
        return result;
    }

    // TIFF type to element size; unknown types count as 1.
    private static int TypeSize(ushort type) => type switch
    {
        3 or 8 => 2,
        4 or 9 or 11 => 4,
        5 or 10 or 12 => 8,
        _ => 1
    };

    private static bool TryReadU16LE(byte[] data, int offset, out ushort value)
    {
        value = 0;
        if (offset < 0 || offset + 2 > data.Length) return false;
        value = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset, 2));
        return true;
    }

    private static bool TryReadU32LE(byte[] data, int offset, out uint value)
    {
        value = 0;
        if (offset < 0 || offset + 4 > data.Length) return false;
        value = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));
        return true;
    }

    private static int? ReadRawNumber(Dictionary<ushort, byte[]> entries, ushort tag)
    {
        if (!entries.TryGetValue(tag, out var b) || b.Length == 0) return null;
        return b.Length >= 4 ? BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(0, 4))
             : b.Length >= 2 ? BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(0, 2))
             : b[0];
    }

    private static string? DecodeEnumOrNull(Dictionary<ushort, byte[]> entries, ushort tag, IReadOnlyDictionary<int, string> map)
    {
        var raw = ReadRawNumber(entries, tag);
        if (raw is not int r) return null;
        return map.TryGetValue(r, out var name) ? name : $"Unknown (0x{r:X})";
    }

    // An unrecognised raw value passes through unchanged.
    private static int? DecodeColorLevel(Dictionary<ushort, byte[]> entries) =>
        DecodeLevel(entries, 0x1003, SaturationLevelMap) is >= -4 and <= 4 and var c ? c : null;

    private static int? DecodeLevel(Dictionary<ushort, byte[]> entries, ushort tag, IReadOnlyDictionary<int, int> map)
    {
        var raw = ReadRawNumber(entries, tag);
        if (raw is not int r) return null;
        return map.TryGetValue(r, out var v) ? v : r;
    }

    // Tone raw = -dial * 16 (ExifTool). Reala ACE firmware reports half steps (24 = -1.5), hence decimal.
    private const int ToneScale = 16;
    private const int ToneGranularity = 8; // finest step width confirmed in real MakerNote data
    private const decimal ToneMin = -10m; // generous bound around the documented -2..+4 dial range
    private const decimal ToneMax = 10m;

    private static decimal? DecodeTone(Dictionary<ushort, byte[]> entries, ushort tag)
    {
        var raw = ReadRawNumber(entries, tag);
        if (raw is not int r) return null;

        if (r % ToneGranularity != 0)
        {
            System.Diagnostics.Trace.TraceWarning(
                $"[PhSpectre.Recipes] Tone raw value {r} (tag 0x{tag:X4}) is not divisible by {ToneGranularity} — showing raw value as-is.");
            return r;
        }

        decimal dial = -(r / (decimal)ToneScale);
        if (dial < ToneMin || dial > ToneMax)
        {
            System.Diagnostics.Trace.TraceWarning(
                $"[PhSpectre.Recipes] Tone raw value {r} (tag 0x{tag:X4}) -> dial {dial} is outside the expected [{ToneMin},{ToneMax}] range — showing raw value as-is.");
            return r;
        }

        return dial;
    }

    // Shift raw = dial x 20. Odd or out-of-range values are logged and shown as they are.
    private const int WhiteBalanceShiftScale = 20;
    private const int WhiteBalanceShiftMin = -9;
    private const int WhiteBalanceShiftMax = 9;

    private static (int Red, int Blue)? DecodeShift(Dictionary<ushort, byte[]> entries, ushort tag)
    {
        if (!entries.TryGetValue(tag, out var b) || b.Length < 8) return null;
        int rawRed = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(0, 4));
        int rawBlue = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(4, 4));
        return (ScaleShiftAxis(rawRed), ScaleShiftAxis(rawBlue));
    }

    private static int ScaleShiftAxis(int raw)
    {
        if (raw % WhiteBalanceShiftScale != 0)
        {
            System.Diagnostics.Trace.TraceWarning(
                $"[PhSpectre.Recipes] WhiteBalanceShift raw value {raw} is not divisible by {WhiteBalanceShiftScale} — showing raw value as-is.");
            return raw;
        }

        int value = raw / WhiteBalanceShiftScale;
        if (value < WhiteBalanceShiftMin || value > WhiteBalanceShiftMax)
        {
            System.Diagnostics.Trace.TraceWarning(
                $"[PhSpectre.Recipes] WhiteBalanceShift {raw}/{WhiteBalanceShiftScale}={value} is outside the expected [{WhiteBalanceShiftMin},{WhiteBalanceShiftMax}] range — showing raw value as-is.");
            return raw;
        }

        return value;
    }

    private static decimal? DecodeClarity(Dictionary<ushort, byte[]> entries, ushort tag)
    {
        var raw = ReadRawNumber(entries, tag);
        return raw is int r ? r / 1000m : null;
    }

    private static int? DecodeSByte(Dictionary<ushort, byte[]> entries, ushort tag)
    {
        if (!entries.TryGetValue(tag, out var b) || b.Length < 1) return null;
        return unchecked((sbyte)b[0]);
    }

    // 0x1402 wins over 0x1400. Under "Manual" the percentage is in 0x1403.
    private static string? DecodeDynamicRange(Dictionary<ushort, byte[]> entries)
    {
        var fine = DecodeEnumOrNull(entries, 0x1402, DynamicRangeSettingMap);
        if (fine == "Manual")
        {
            var dev = ReadRawNumber(entries, 0x1403);
            if (dev is int d) return $"DR{d}";
        }
        return fine ?? DecodeEnumOrNull(entries, 0x1400, DynamicRangeMap);
    }

    // Monochrome simulations are reported through Saturation (0x1003), which wins over FilmMode (0x1401).
    private static string? DecodeFilmSimulation(Dictionary<ushort, byte[]> entries)
    {
        var satRaw = ReadRawNumber(entries, 0x1003);
        if (satRaw is int s && MonochromeFamilyMap.TryGetValue(s, out var monoName))
            return monoName;
        return DecodeEnumOrNull(entries, 0x1401, FilmSimulationMap);
    }

    // Tag values from exiftool.org, verified against real files

    private static readonly IReadOnlyDictionary<int, string> WhiteBalanceMap = new Dictionary<int, string>
    {
        [0x0] = "Auto", [0x1] = "Auto (white priority)", [0x2] = "Auto (ambiance priority)",
        [0x100] = "Daylight", [0x200] = "Cloudy",
        [0x300] = "Fluorescent (Daylight)", [0x301] = "Fluorescent (Warm White)", [0x302] = "Fluorescent (Cool White)",
        [0x400] = "Incandescent", [0x500] = "Flash", [0x600] = "Underwater",
        [0xf00] = "Custom", [0xf01] = "Custom 2", [0xf02] = "Custom 3", [0xf03] = "Custom 4",
        [0xff0] = "Kelvin",
    };

    // Colour simulations only.
    private static readonly IReadOnlyDictionary<int, string> FilmSimulationMap = new Dictionary<int, string>
    {
        [0x0] = "Provia/Standard",
        [0x100] = "Studio Portrait", [0x110] = "Studio Portrait Enhanced Saturation",
        [0x120] = "Astia (Soft)", [0x130] = "Studio Portrait Increased Sharpness",
        [0x200] = "Velvia", [0x300] = "Studio Portrait Ex", [0x400] = "Velvia (old)",
        [0x500] = "Pro Neg. Std", [0x501] = "Pro Neg. Hi",
        [0x600] = "Classic Chrome", [0x700] = "Eterna", [0x800] = "Classic Negative",
        [0x900] = "Eterna Bleach Bypass", [0xa00] = "Nostalgic Neg", [0xb00] = "Reala ACE",
    };

    // Saturation values that mean a monochrome simulation.
    private static readonly IReadOnlyDictionary<int, string> MonochromeFamilyMap = new Dictionary<int, string>
    {
        [0x300] = "Monochrome", [0x301] = "Monochrome + R Filter",
        [0x302] = "Monochrome + Ye Filter", [0x303] = "Monochrome + G Filter",
        [0x310] = "Sepia",
        [0x500] = "Acros", [0x501] = "Acros + R Filter",
        [0x502] = "Acros + Ye Filter", [0x503] = "Acros + G Filter",
    };

    // The -4..+4 Color level.
    private static readonly IReadOnlyDictionary<int, int> SaturationLevelMap = new Dictionary<int, int>
    {
        [0x0] = 0, [0x80] = 1, [0x100] = 2, [0xc0] = 3, [0xe0] = 4,
        [0x180] = -1, [0x400] = -2, [0x4c0] = -3, [0x4e0] = -4,
    };

    private static readonly IReadOnlyDictionary<int, string> DynamicRangeMap = new Dictionary<int, string>
    {
        [1] = "Standard", [3] = "Wide",
    };

    private static readonly IReadOnlyDictionary<int, string> DynamicRangeSettingMap = new Dictionary<int, string>
    {
        [0x0] = "Auto", [0x1] = "Manual", // "Manual" alone is resolved further in DecodeDynamicRange
        [0x100] = "DR100", [0x200] = "DR200", [0x201] = "DR400",
        [0x8000] = "Film Simulation",
    };

    // The Off / Weak / Strong scale.
    private static readonly IReadOnlyDictionary<int, string> ThreeLevelMap = new Dictionary<int, string>
    {
        [0] = "Off", [32] = "Weak", [64] = "Strong",
    };

    private static readonly IReadOnlyDictionary<int, int> SharpnessMap = new Dictionary<int, int>
    {
        [0x0] = -4, [0x1] = -3, [0x2] = -2, [0x3] = 0, [0x4] = 2, [0x5] = 3, [0x6] = 4,
        [0x82] = -1, [0x84] = 1,
    };

    private static readonly IReadOnlyDictionary<int, int> NoiseReductionMap = new Dictionary<int, int>
    {
        [0x0] = 0, [0x100] = 2, [0x180] = 1, [0x1c0] = 3, [0x1e0] = 4,
        [0x200] = -2, [0x280] = -1, [0x2c0] = -3, [0x2e0] = -4,
    };
}
