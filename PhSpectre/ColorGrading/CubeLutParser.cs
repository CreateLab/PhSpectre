using System.Globalization;
using System.Numerics;

namespace PhSpectre.ColorGrading;

// Parses the text ".cube" format. Tolerant of what real exporters emit (comments, blank lines, any
// keyword order, TITLE with or without quotes, whitespace-separated floats) and strict about what
// would silently corrupt an image: wrong row count, missing size, unparsable numbers.
// Every failure is a FormatException carrying the 1-based line number where one applies.
public static class CubeLutParser
{
    private const int MaxSize3D = 256;
    private const int MaxSize1D = 65536;

    public static CubeLut Parse(TextReader reader)
    {
        string? title = null;
        int size1D = 0, size3D = 0;
        var domainMin = Vector3.Zero;
        var domainMax = Vector3.One;
        var rows = new List<float>();
        int expectedRows = -1;
        int lineNo = 0;

        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            lineNo++;
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed[0] == '#') continue;

            // Data rows start with a number; everything else is a keyword line.
            char c = trimmed[0];
            if (char.IsDigit(c) || c == '-' || c == '+' || c == '.')
            {
                if (expectedRows < 0)
                    throw Error(lineNo, "data row before LUT_1D_SIZE / LUT_3D_SIZE");
                var parts = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 3)
                    throw Error(lineNo, $"expected 3 values per row, found {parts.Length}");
                foreach (var p in parts)
                    rows.Add(ParseFloat(p, lineNo));
                continue;
            }

            var tokens = trimmed.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
            string key = tokens[0];
            string rest = tokens.Length > 1 ? tokens[1].Trim() : string.Empty;

            switch (key)
            {
                case "TITLE":
                    title = rest.Trim('"');
                    break;
                case "LUT_1D_SIZE":
                    size1D = ParseSize(rest, lineNo, 2, MaxSize1D);
                    expectedRows = size1D;
                    break;
                case "LUT_3D_SIZE":
                    size3D = ParseSize(rest, lineNo, 2, MaxSize3D);
                    expectedRows = size3D * size3D * size3D;
                    break;
                case "DOMAIN_MIN":
                    domainMin = ParseVector(rest, lineNo);
                    break;
                case "DOMAIN_MAX":
                    domainMax = ParseVector(rest, lineNo);
                    break;
                case "LUT_1D_INPUT_RANGE":
                case "LUT_3D_INPUT_RANGE":
                {
                    // Adobe's scalar variant of DOMAIN_MIN/MAX.
                    var v = rest.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                    if (v.Length != 2) throw Error(lineNo, $"{key} needs two values");
                    domainMin = new Vector3(ParseFloat(v[0], lineNo));
                    domainMax = new Vector3(ParseFloat(v[1], lineNo));
                    break;
                }
                default:
                    // Unknown keywords (LUT_IN_VIDEO_RANGE, vendor extensions) carry nothing we need.
                    break;
            }
        }

        if (size1D > 0 && size3D > 0)
            throw new FormatException("A .cube file cannot declare both LUT_1D_SIZE and LUT_3D_SIZE.");
        if (expectedRows < 0)
            throw new FormatException("Missing LUT_1D_SIZE / LUT_3D_SIZE.");
        if (rows.Count != expectedRows * 3)
            throw new FormatException($"Expected {expectedRows} data rows, found {rows.Count / 3}.");
        if (domainMax.X <= domainMin.X || domainMax.Y <= domainMin.Y || domainMax.Z <= domainMin.Z)
            throw new FormatException("DOMAIN_MAX must be greater than DOMAIN_MIN on every channel.");

        bool is3D = size3D > 0;
        return new CubeLut(title, is3D, is3D ? size3D : size1D, domainMin, domainMax, rows.ToArray());
    }

    private static int ParseSize(string text, int lineNo, int min, int max)
    {
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int n) || n < min || n > max)
            throw Error(lineNo, $"size must be an integer between {min} and {max}");
        return n;
    }

    private static Vector3 ParseVector(string text, int lineNo)
    {
        var v = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (v.Length != 3) throw Error(lineNo, "expected three values");
        return new Vector3(ParseFloat(v[0], lineNo), ParseFloat(v[1], lineNo), ParseFloat(v[2], lineNo));
    }

    private static float ParseFloat(string s, int lineNo)
    {
        // Invariant culture always: a ".cube" written on one machine must read the same on another.
        if (!float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float f) || !float.IsFinite(f))
            throw Error(lineNo, $"cannot parse number '{s}'");
        return f;
    }

    private static FormatException Error(int lineNo, string message) =>
        new($"Line {lineNo}: {message}.");
}
