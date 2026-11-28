using PhSpectre.Localization;
using System.Buffers.Binary;
using System.Text;
using PhSpectre.Models;

namespace PhSpectre.Camera;

public sealed record PropertyWrite(ushort Property, byte[] Value, string Description);

/// <summary>Writes planned for one slot, in the order they must be sent, plus recipe fields that cannot be written.</summary>
public sealed record SlotEncoding(IReadOnlyList<PropertyWrite> Writes, IReadOnlyList<string> Skipped);

/// <summary>
/// Inverse of <see cref="FujiSlotCodec.Decode"/>. Null fields are left alone; values without a verified encoding
/// go to <see cref="SlotEncoding.Skipped"/> instead of being approximated.
/// </summary>
public static class FujiSlotEncoder
{
    /// <summary>Settings the body lacks are reported as skipped.</summary>
    public static SlotEncoding Encode(FilmRecipe recipe, FujiCameraProfile? profile)
    {
        var encoding = Encode(recipe);
        if (profile?.AbsentProperties is not { Count: > 0 }) return encoding;

        var skipped = encoding.Skipped.ToList();
        var writes = new List<PropertyWrite>();
        foreach (var write in encoding.Writes)
        {
            if (profile.IsAbsent(write.Property)) skipped.Add(Loc.F("{0}: {1} does not store this in a slot", write.Description, profile.Model));
            else writes.Add(write);
        }
        return new SlotEncoding(writes, skipped);
    }

    // Order matters: DR Priority off first, film simulation before Color, WB mode, Kelvin, then shifts (a late
    // Kelvin write zeroed them).
    public static SlotEncoding Encode(FilmRecipe recipe)
    {
        var writes = new List<PropertyWrite>();
        var skipped = new List<string>();

        if (recipe.RecipeName != null)
            writes.Add(new(FujiSlotCodec.PropName, EncodeString(recipe.RecipeName), Loc.F("name '{0}'", recipe.RecipeName)));

        ushort? filmCode = null;
        if (recipe.FilmSimulation != null)
        {
            if (TryReverse(FujiSlotCodec.FilmSimulations, recipe.FilmSimulation, out var code))
            {
                filmCode = code;
                writes.Add(U16(FujiSlotCodec.PropFilmSimulation, code, Loc.F("film simulation {0}", recipe.FilmSimulation)));
            }
            else skipped.Add(Loc.F("FilmSimulation '{0}' has no known camera code", recipe.FilmSimulation));
        }
        var isMono = filmCode is { } fc && FujiSlotCodec.MonochromeFamily.Contains(fc);

        if (recipe.DynamicRange != null)
        {
            ushort? dr = recipe.DynamicRange switch
            {
                "Auto" => 0xFFFF,
                "DR100" => 100,
                "DR200" => 200,
                "DR400" => 400,
                _ => null,
            };
            if (dr is { } value)
            {
                // A stored DR is only honoured with D-Range Priority off.
                writes.Add(U16(FujiSlotCodec.PropDRangePriority, 0, Loc.T("D-Range Priority off")));
                writes.Add(U16(FujiSlotCodec.PropDynamicRange, value, Loc.F("dynamic range {0}", recipe.DynamicRange)));
            }
            else skipped.Add(Loc.F("DynamicRange '{0}' cannot be stored in a slot", recipe.DynamicRange));
        }

        if (recipe.GrainEffect != null)
            EncodeGrain(recipe, writes, skipped);

        AddThreeLevel(FujiSlotCodec.PropColorChrome, "color chrome effect", recipe.ColorChromeEffect, writes, skipped);
        AddThreeLevel(FujiSlotCodec.PropColorChromeBlue, "color chrome FX blue", recipe.ColorChromeFxBlue, writes, skipped);
        AddThreeLevel(FujiSlotCodec.PropSmoothSkin, "smooth skin effect", recipe.SmoothSkinEffect, writes, skipped);

        if (recipe.WhiteBalance != null)
        {
            if (TryReverse(FujiSlotCodec.WhiteBalances, recipe.WhiteBalance, out var wb))
                writes.Add(U16(FujiSlotCodec.PropWhiteBalance, wb, Loc.F("white balance {0}", recipe.WhiteBalance)));
            else skipped.Add(Loc.F("WhiteBalance '{0}' has no known camera code", recipe.WhiteBalance));
        }

        if (recipe.ColorTemperature is { } kelvin)
        {
            if (recipe.WhiteBalance != "Kelvin")
                skipped.Add(Loc.T("ColorTemperature ignored: white balance is not Kelvin"));
            else if (kelvin is < 2500 or > 10000 || kelvin % 10 != 0)
                skipped.Add(Loc.F("ColorTemperature {0} is outside 2500-10000 in steps of 10", kelvin));
            else
                writes.Add(U16(FujiSlotCodec.PropColorTemperature, (ushort)kelvin, Loc.F("color temperature {0}K", kelvin)));
        }

        if (recipe.WhiteBalanceShift is { } shift)
        {
            if (shift.Red is < -9 or > 9 || shift.Blue is < -9 or > 9)
                skipped.Add(Loc.F("WhiteBalanceShift ({0},{1}) is outside -9..+9", shift.Red, shift.Blue));
            else
            {
                writes.Add(S16(FujiSlotCodec.PropWbShiftRed, shift.Red, Loc.F("WB shift red {0}", shift.Red)));
                writes.Add(S16(FujiSlotCodec.PropWbShiftBlue, shift.Blue, Loc.F("WB shift blue {0}", shift.Blue)));
            }
        }

        AddTenths(FujiSlotCodec.PropHighlightTone, "highlight tone", "HighlightTone", recipe.HighlightTone, 5, writes, skipped);
        AddTenths(FujiSlotCodec.PropShadowTone, "shadow tone", "ShadowTone", recipe.ShadowTone, 5, writes, skipped);

        if (recipe.Color is { } color)
        {
            if (isMono) skipped.Add(Loc.T("Color ignored: the camera refuses Color under a monochrome simulation"));
            else writes.Add(S16(FujiSlotCodec.PropColor, color * 10, Loc.F("color {0}", color)));
        }
        if (recipe.Sharpness is { } sharpness)
            writes.Add(S16(FujiSlotCodec.PropSharpness, sharpness * 10, Loc.F("sharpness {0}", sharpness)));

        if (recipe.NoiseReduction is { } nr)
        {
            if (TryReverse(FujiSlotCodec.NoiseReduction, nr, out var nrCode))
                writes.Add(U16(FujiSlotCodec.PropNoiseReduction, nrCode, Loc.F("noise reduction {0}", nr)));
            else skipped.Add(Loc.F("NoiseReduction {0} is outside -4..+4", nr));
        }

        AddTenths(FujiSlotCodec.PropClarity, "clarity", "Clarity", recipe.Clarity, 10, writes, skipped);

        // Only zero is verified, so it is not written.
        if (recipe.BwAdjustment is { } bw && bw != 0)
            skipped.Add(Loc.T("BwAdjustment is not written: its encoding is unverified"));

        return new SlotEncoding(writes, skipped);
    }

    public static byte[] EncodeString(string value)
    {
        var bytes = new byte[1 + (value.Length + 1) * 2];
        bytes[0] = (byte)(value.Length + 1);
        Encoding.Unicode.GetBytes(value, 0, value.Length, bytes, 1);
        return bytes;
    }

    private static void EncodeGrain(FilmRecipe recipe, List<PropertyWrite> writes, List<string> skipped)
    {
        // A strength needs a size; Small by default.
        var large = recipe.GrainSize == "Large";
        if (recipe.GrainSize is not (null or "Small" or "Large"))
        {
            skipped.Add(Loc.F("GrainSize '{0}' must be Small or Large", recipe.GrainSize));
            return;
        }

        switch (recipe.GrainEffect)
        {
            case "Off": writes.Add(U16(FujiSlotCodec.PropGrain, 1, Loc.T("grain off"))); break;
            case "Weak": writes.Add(U16(FujiSlotCodec.PropGrain, (ushort)(large ? 4 : 2), Loc.T(large ? "grain weak large" : "grain weak small"))); break;
            case "Strong": writes.Add(U16(FujiSlotCodec.PropGrain, (ushort)(large ? 5 : 3), Loc.T(large ? "grain strong large" : "grain strong small"))); break;
            default: skipped.Add(Loc.F("GrainEffect '{0}' must be Off, Weak or Strong", recipe.GrainEffect)); break;
        }
    }

    private static void AddThreeLevel(ushort prop, string label, string? value, List<PropertyWrite> writes, List<string> skipped)
    {
        if (value == null) return;
        if (TryReverse(FujiSlotCodec.ThreeLevel, value, out var code))
            writes.Add(U16(prop, code, $"{Loc.T(label)} {value}"));
        else skipped.Add(Loc.F("{0} '{1}' must be Off, Weak or Strong", Loc.T(label), value));
    }

    private static void AddTenths(ushort prop, string label, string field, decimal? value, int stepTenths, List<PropertyWrite> writes, List<string> skipped)
    {
        if (value is not { } v) return;
        var scaled = v * 10;
        if (scaled % stepTenths != 0 || scaled is < -50 or > 50)
            skipped.Add(Loc.F("{0} {1} is not a multiple of {2} within -5..+5", field, v, stepTenths / 10m));
        else
            writes.Add(S16(prop, (int)scaled, $"{Loc.T(label)} {v}"));
    }

    private static bool TryReverse<TValue>(IReadOnlyDictionary<ushort, TValue> table, TValue value, out ushort code)
    {
        foreach (var (key, candidate) in table)
        {
            if (!EqualityComparer<TValue>.Default.Equals(candidate, value)) continue;
            // The first of equivalent wire values is written.
            code = key;
            return true;
        }
        code = 0;
        return false;
    }

    private static PropertyWrite U16(ushort prop, ushort value, string description)
    {
        var bytes = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, value);
        return new PropertyWrite(prop, bytes, description);
    }

    private static PropertyWrite S16(ushort prop, int value, string description)
    {
        var bytes = new byte[2];
        BinaryPrimitives.WriteInt16LittleEndian(bytes, (short)value);
        return new PropertyWrite(prop, bytes, description);
    }
}

