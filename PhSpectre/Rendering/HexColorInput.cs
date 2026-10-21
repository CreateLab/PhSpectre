using SixLabors.ImageSharp;

namespace PhSpectre.Rendering;

// Strict parser for the user-typed custom background color. Accepts only #RRGGBB / RRGGBB
// (Color.TryParseHex alone also takes #RGB and #AARRGGBB, and anything else silently became
// "no custom background" — the card quietly fell back to the theme with no feedback).
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
