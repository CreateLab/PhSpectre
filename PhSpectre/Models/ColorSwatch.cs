namespace PhSpectre.Models;

public sealed record ColorSwatch(string Hex, (byte R, byte G, byte B) Rgb, float Percentage);
