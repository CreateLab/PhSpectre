using SixLabors.ImageSharp;

namespace PhSpectre.Rendering;

// Accepts only #RRGGBB or RRGGBB: Color.TryParseHex takes other forms, and failures fell back silently.
public static class HexColorInput
{
    public static bool TryParse(string? text, out Color color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var s = text.Trim();
        if (s.StartsWith('#')) s = s[1..];
        if (s.Length != 6) return false;

        foreach (var ch in s)
            if (!Uri.IsHexDigit(ch)) return false;

        return Color.TryParseHex(s, out color);
    }
}
