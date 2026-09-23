using System.Text.Json.Serialization;

namespace PhSpectre.Models;

public sealed record ColorSwatch(
    string Hex,
    [property: JsonConverter(typeof(RgbJsonConverter))] (byte R, byte G, byte B) Rgb,
    float Percentage);
