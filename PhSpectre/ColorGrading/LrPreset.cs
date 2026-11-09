using System.Globalization;
using System.Xml.Linq;

namespace PhSpectre.ColorGrading;

// A Lightroom / Camera Raw preset (.xmp with crs: settings). Holds the numeric develop settings and
// tone curves; anything we parse but cannot reproduce is listed in Skipped so callers can tell the user.
public sealed class LrPreset
{
    private static readonly XNamespace Crs = "http://ns.adobe.com/camera-raw-settings/1.0/";
    private static readonly XNamespace Rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";

    // Keys we read but do not implement (spatial filters, embedded profiles). Reported only when non-zero.
    private static readonly string[] UnsupportedKeys =
    [
        "Texture", "Clarity2012", "Dehaze", "Sharpness", "LuminanceSmoothing", "ColorNoiseReduction",
        "RGBTable"
    ];

    public string? Name { get; }
    public bool Monochrome { get; }
    public IReadOnlyDictionary<string, float> Values { get; }
    // Curve name ("Master", "Red", "Green", "Blue") -> control points in 0..255 space.
    public IReadOnlyDictionary<string, (float X, float Y)[]> Curves { get; }
    public IReadOnlyList<string> Skipped { get; }

    private LrPreset(string? name, bool mono, Dictionary<string, float> values,
        Dictionary<string, (float, float)[]> curves, List<string> skipped)
    {
        Name = name; Monochrome = mono; Values = values; Curves = curves; Skipped = skipped;
    }

    public float Get(string key) => Values.TryGetValue(key, out var v) ? v : 0f;

    public static LrPreset Load(string path) => Parse(File.ReadAllText(path));

    public static LrPreset Parse(string xml)
    {
        XDocument doc;
        try { doc = XDocument.Parse(xml); }
        catch (System.Xml.XmlException ex) { throw new FormatException($"Invalid XMP: {ex.Message}", ex); }

        var desc = doc.Descendants(Rdf + "Description").FirstOrDefault(d => d.Attributes().Any(a => a.Name.Namespace == Crs))
                   ?? throw new FormatException("No crs: settings found; not a Lightroom preset.");

        var values = new Dictionary<string, float>();
        string? name = null, treatment = null;
        bool grayscale = false;
        foreach (var a in desc.Attributes().Where(a => a.Name.Namespace == Crs))
        {
            string key = a.Name.LocalName;
            if (key == "Name") name = a.Value;
            else if (key == "Treatment") treatment = a.Value;
            else if (key == "ConvertToGrayscale") grayscale = a.Value.Equals("True", StringComparison.OrdinalIgnoreCase);
            else if (float.TryParse(a.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float f) && float.IsFinite(f))
                values[key] = f;
        }

        var curves = new Dictionary<string, (float, float)[]>();
        foreach (var (elem, curve) in new[]
                 { ("ToneCurvePV2012", "Master"), ("ToneCurvePV2012Red", "Red"),
                   ("ToneCurvePV2012Green", "Green"), ("ToneCurvePV2012Blue", "Blue") })
        {
            var pts = new List<(float, float)>();
            foreach (var li in desc.Elements(Crs + elem).Descendants(Rdf + "li"))
            {
                var p = li.Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                if (p.Length == 2
                    && float.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x)
                    && float.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float y))
                    pts.Add((x, y));
            }
            if (pts.Count >= 2) curves[curve] = pts.OrderBy(t => t.Item1).ToArray();
        }

        var skipped = new List<string>();
        foreach (var k in UnsupportedKeys)
            if (values.TryGetValue(k, out var v) && v != 0f) skipped.Add(k);
        if (desc.Descendants().Any(e => e.Name.Namespace == Crs && e.Name.LocalName == "RGBTable")
            || desc.Attribute(Crs + "RGBTable") != null)
            skipped.Add("RGBTable");

        bool mono = grayscale || string.Equals(treatment, "Monochrome", StringComparison.OrdinalIgnoreCase);
        return new LrPreset(name, mono, values, curves, skipped.Distinct().ToList());
    }
}
