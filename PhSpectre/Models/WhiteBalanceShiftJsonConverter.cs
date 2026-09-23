using System.Text.Json;
using System.Text.Json.Serialization;

namespace PhSpectre.Models;

// System.Text.Json ignores ValueTuple's public fields by default (it only serializes
// properties unless IncludeFields is set globally), and even with IncludeFields on, a tuple
// serializes as {"Item1":..,"Item2":..} — its "Red"/"Blue" element names are compile-time
// only (TupleElementNamesAttribute), invisible to reflection-based serialization. This
// converter gives FilmRecipe.WhiteBalanceShift a proper {"Red":..,"Blue":..} JSON shape.
public sealed class WhiteBalanceShiftJsonConverter : JsonConverter<(int Red, int Blue)?>
{
    public override (int Red, int Blue)? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;

        int red = 0, blue = 0;
        using var doc = JsonDocument.ParseValue(ref reader);
        if (doc.RootElement.TryGetProperty("Red", out var r)) red = r.GetInt32();
        if (doc.RootElement.TryGetProperty("Blue", out var b)) blue = b.GetInt32();
        return (red, blue);
    }

    public override void Write(Utf8JsonWriter writer, (int Red, int Blue)? value, JsonSerializerOptions options)
    {
        if (value is not { } v)
        {
            writer.WriteNullValue();
            return;
        }
        writer.WriteStartObject();
        writer.WriteNumber("Red", v.Red);
        writer.WriteNumber("Blue", v.Blue);
        writer.WriteEndObject();
    }
}
