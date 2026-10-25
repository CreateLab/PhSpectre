using System.Buffers.Binary;
using System.Text;
using PhSpectre.Models;

namespace PhSpectre.Camera;

public sealed record PropertyWrite(ushort Property, byte[] Value, string Description);

/// <summary>Writes planned for one slot, in the order they must be sent, plus recipe fields that cannot be written.</summary>
public sealed record SlotEncoding(IReadOnlyList<PropertyWrite> Writes, IReadOnlyList<string> Skipped);

/// <summary>
/// Inverse of <see cref="FujiSlotCodec.Decode"/>. Null recipe fields are left untouched in the slot.
/// Values that have no verified encoding or that the camera cannot hold are reported in
/// <see cref="SlotEncoding.Skipped"/> rather than approximated, so a write never silently changes a recipe.
/// </summary>
public static class FujiSlotEncoder
{
    /// <summary>Encodes for a specific body: settings the body does not have are reported as skipped instead of written.</summary>
    public static SlotEncoding Encode(FilmRecipe recipe, FujiCameraProfile? profile)
    {
        var encoding = Encode(recipe);
        if (profile?.AbsentProperties is not { Count: > 0 }) return encoding;

        var skipped = encoding.Skipped.ToList();
        var writes = new List<PropertyWrite>();
        foreach (var write in encoding.Writes)
        {
            if (profile.IsAbsent(write.Property)) skipped.Add($"{write.Description}: {profile.Model} does not store this in a slot");
            else writes.Add(write);
        }
        return new SlotEncoding(writes, skipped);
    }

    // Order follows the dependency rules in fujifilm-ptp-reference: DR Priority off before tones/DR, the film
    // simulation before Color (refused under monochrome), WB mode before Kelvin before the shifts (a Kelvin write
    // after the shifts zeroed them on one body).
    public static SlotEncoding Encode(FilmRecipe recipe)
    {
        var writes = new List<PropertyWrite>();
        var skipped = new List<string>();

        if (recipe.RecipeName != null)
            writes.Add(new(FujiSlotCodec.PropName, EncodeString(recipe.RecipeName), $"name '{recipe.RecipeName}'"));

        ushort? filmCode = null;
        if (recipe.FilmSimulation != null)
        {
            if (TryReverse(FujiSlotCodec.FilmSimulations, recipe.FilmSimulation, out var code))
            {
                filmCode = code;
                writes.Add(U16(FujiSlotCodec.PropFilmSimulation, code, $"film simulation {recipe.FilmSimulation}"));
            }
            else skipped.Add($"FilmSimulation '{recipe.FilmSimulation}' has no known camera code");
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
                writes.Add(U16(FujiSlotCodec.PropDRangePriority, 0, "D-Range Priority off"));
                writes.Add(U16(FujiSlotCodec.PropDynamicRange, value, $"dynamic range {recipe.DynamicRange}"));
            }
            else skipped.Add($"DynamicRange '{recipe.DynamicRange}' cannot be stored in a slot");
        }

        if (recipe.GrainEffect != null)
            EncodeGrain(recipe, writes, skipped);

        AddThreeLevel(FujiSlotCodec.PropColorChrome, "color chrome effect", recipe.ColorChromeEffect, writes, skipped);
        AddThreeLevel(FujiSlotCodec.PropColorChromeBlue, "color chrome FX blue", recipe.ColorChromeFxBlue, writes, skipped);
        AddThreeLevel(FujiSlotCodec.PropSmoothSkin, "smooth skin effect", recipe.SmoothSkinEffect, writes, skipped);

        if (recipe.WhiteBalance != null)
        {
            if (TryReverse(FujiSlotCodec.WhiteBalances, recipe.WhiteBalance, out var wb))
                writes.Add(U16(FujiSlotCodec.PropWhiteBalance, wb, $"white balance {recipe.WhiteBalance}"));
            else skipped.Add($"WhiteBalance '{recipe.WhiteBalance}' has no known camera code");
        }

        if (recipe.ColorTemperature is { } kelvin)
        {
            if (recipe.WhiteBalance != "Kelvin")
                skipped.Add("ColorTemperature ignored: white balance is not Kelvin");
            else if (kelvin is < 2500 or > 10000 || kelvin % 10 != 0)
                skipped.Add($"ColorTemperature {kelvin} is outside 2500-10000 in steps of 10");
            else
                writes.Add(U16(FujiSlotCodec.PropColorTemperature, (ushort)kelvin, $"color temperature {kelvin}K"));
        }

        if (recipe.WhiteBalanceShift is { } shift)
        {
            if (shift.Red is < -9 or > 9 || shift.Blue is < -9 or > 9)
                skipped.Add($"WhiteBalanceShift ({shift.Red},{shift.Blue}) is outside -9..+9");
            else
            {
                writes.Add(S16(FujiSlotCodec.PropWbShiftRed, shift.Red, $"WB shift red {shift.Red}"));
                writes.Add(S16(FujiSlotCodec.PropWbShiftBlue, shift.Blue, $"WB shift blue {shift.Blue}"));
            }
        }

        AddTenths(FujiSlotCodec.PropHighlightTone, "highlight tone", "HighlightTone", recipe.HighlightTone, 5, writes, skipped);
        AddTenths(FujiSlotCodec.PropShadowTone, "shadow tone", "ShadowTone", recipe.ShadowTone, 5, writes, skipped);

        if (recipe.Color is { } color)
        {
            if (isMono) skipped.Add("Color ignored: the camera refuses Color under a monochrome simulation");
            else writes.Add(S16(FujiSlotCodec.PropColor, color * 10, $"color {color}"));
        }
        if (recipe.Sharpness is { } sharpness)
            writes.Add(S16(FujiSlotCodec.PropSharpness, sharpness * 10, $"sharpness {sharpness}"));

        if (recipe.NoiseReduction is { } nr)
        {
            if (TryReverse(FujiSlotCodec.NoiseReduction, nr, out var nrCode))
                writes.Add(U16(FujiSlotCodec.PropNoiseReduction, nrCode, $"noise reduction {nr}"));
            else skipped.Add($"NoiseReduction {nr} is outside -4..+4");
        }

        AddTenths(FujiSlotCodec.PropClarity, "clarity", "Clarity", recipe.Clarity, 10, writes, skipped);

        // Monochrome warm/cool toning only has its zero case checked against real files, so it is not written.
        if (recipe.BwAdjustment is { } bw && bw != 0)
            skipped.Add("BwAdjustment is not written: its encoding is unverified");

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
        // Off alone is code 1; a strength needs a size, defaulting to Small when the recipe has none.
        var large = recipe.GrainSize == "Large";
        if (recipe.GrainSize is not (null or "Small" or "Large"))
        {
            skipped.Add($"GrainSize '{recipe.GrainSize}' must be Small or Large");
            return;
        }

        switch (recipe.GrainEffect)
        {
            case "Off": writes.Add(U16(FujiSlotCodec.PropGrain, 1, "grain off")); break;
            case "Weak": writes.Add(U16(FujiSlotCodec.PropGrain, (ushort)(large ? 4 : 2), $"grain weak {(large ? "large" : "small")}")); break;
            case "Strong": writes.Add(U16(FujiSlotCodec.PropGrain, (ushort)(large ? 5 : 3), $"grain strong {(large ? "large" : "small")}")); break;
            default: skipped.Add($"GrainEffect '{recipe.GrainEffect}' must be Off, Weak or Strong"); break;
        }
    }

    private static void AddThreeLevel(ushort prop, string label, string? value, List<PropertyWrite> writes, List<string> skipped)
    {
        if (value == null) return;
        if (TryReverse(FujiSlotCodec.ThreeLevel, value, out var code))
            writes.Add(U16(prop, code, $"{label} {value}"));
        else skipped.Add($"{label} '{value}' must be Off, Weak or Strong");
    }

    private static void AddTenths(ushort prop, string label, string field, decimal? value, int stepTenths, List<PropertyWrite> writes, List<string> skipped)
    {
        if (value is not { } v) return;
        var scaled = v * 10;
        if (scaled % stepTenths != 0 || scaled is < -50 or > 50)
            skipped.Add($"{field} {v} is not a multiple of {stepTenths / 10m} within -5..+5");
        else
            writes.Add(S16(prop, (int)scaled, $"{label} {v}"));
    }

    private static bool TryReverse<TValue>(IReadOnlyDictionary<ushort, TValue> table, TValue value, out ushort code)
    {
        foreach (var (key, candidate) in table)
        {
            if (!EqualityComparer<TValue>.Default.Equals(candidate, value)) continue;
            // Several wire values can mean the same thing (Grain Off); the first listed is the one to write.
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

