using System.Text.Json;
using System.Text.Json.Serialization;

namespace PhSpectre.Models;

// Same gap as WhiteBalanceShiftJsonConverter (see its comment) — ColorSwatch.Rgb is also a
// ValueTuple and would otherwise serialize as {} (or {"Item1":..,"Item2":..,"Item3":..} with
// IncludeFields). Nothing serialized ColorSwatch to JSON before the API's json=true response
// mode, so this never surfaced until then.
public sealed class RgbJsonConverter : JsonConverter<(byte R, byte G, byte B)>
{
    public override (byte R, byte G, byte B) Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        byte r = 0, g = 0, b = 0;
        using var doc = JsonDocument.ParseValue(ref reader);
        if (doc.RootElement.TryGetProperty("R", out var rv)) r = (byte)rv.GetInt32();
        if (doc.RootElement.TryGetProperty("G", out var gv)) g = (byte)gv.GetInt32();
        if (doc.RootElement.TryGetProperty("B", out var bv)) b = (byte)bv.GetInt32();
        return (r, g, b);
    }

    public override void Write(Utf8JsonWriter writer, (byte R, byte G, byte B) value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteNumber("R", value.R);
        writer.WriteNumber("G", value.G);
        writer.WriteNumber("B", value.B);
        writer.WriteEndObject();
    }
}
