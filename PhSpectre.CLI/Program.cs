using PhSpectre;
using PhSpectre.Rendering;

if (args.Length == 0)
{
    Console.Error.WriteLine("Usage: phspectre <image.jpg> [--colors <n>] [--no-hex] [--hex-below]");
    Console.Error.WriteLine("       [--mode vivid|standard|contrast]");
    Console.Error.WriteLine("       [--no-meta] [--meta-short] [--meta-detail] [--meta-full] [--meta-overlay]");
    Console.Error.WriteLine("       [--theme dark|light]");
    Console.Error.WriteLine("       [--sort none|hue|luminance|percent] [--shape rect|rounded|circle] [--show-percent]");
    Console.Error.WriteLine("       [--guide none|thirds|golden|diagonal|cross]");
    return 1;
}

string imagePath = args[0];
int? colorCount = null;
bool showHex      = true;
bool hexBelow     = false;
var samplingMode  = SamplingMode.Vivid;
var metaVerbosity = MetaVerbosity.Default;
var metaStyle     = MetaStyle.FilmStrip;
var theme         = Theme.Dark;
var sortOrder     = SortOrder.None;
var swatchShape   = SwatchShape.Rectangle;
bool showPercent  = false;
var compositionGuide = CompositionGuide.None;

for (int i = 1; i < args.Length; i++)
{
    if (args[i] == "--colors" && i + 1 < args.Length)
    {
        if (!int.TryParse(args[i + 1], out int n) || n < 1 || n > 32)
        {
            Console.Error.WriteLine("--colors must be an integer between 1 and 32.");
            return 1;
        }
        colorCount = n;
        i++;
    }
    else if (args[i] == "--no-hex")      { showHex = false; }
    else if (args[i] == "--hex-below")   { hexBelow = true; }
    else if (args[i] == "--mode" && i + 1 < args.Length)
    {
        samplingMode = args[i + 1].ToLowerInvariant() switch
        {
            "standard" => SamplingMode.Standard,
            "contrast" => SamplingMode.Contrast,
            "vivid"    => SamplingMode.Vivid,
            _ => throw new Exception($"Unknown mode '{args[i + 1]}'. Use vivid, standard or contrast.")
        };
        i++;
    }
    else if (args[i] == "--no-meta")     { metaVerbosity = MetaVerbosity.Off; }
    else if (args[i] == "--meta-short")  { metaVerbosity = MetaVerbosity.Short; }
    else if (args[i] == "--meta-detail") { metaVerbosity = MetaVerbosity.Detail; }
    else if (args[i] == "--meta-full")   { metaVerbosity = MetaVerbosity.Full; }
    else if (args[i] == "--meta-overlay"){ metaStyle = MetaStyle.Overlay; }
    else if (args[i] == "--theme" && i + 1 < args.Length)
    {
        if (args[i + 1].ToLowerInvariant() == "light")       theme = Theme.Light;
        else if (args[i + 1].ToLowerInvariant() == "dark")   theme = Theme.Dark;
        else { Console.Error.WriteLine($"Unknown theme '{args[i + 1]}'. Use 'dark' or 'light'."); return 1; }
        i++;
    }
    else if (args[i] == "--sort" && i + 1 < args.Length)
    {
        sortOrder = args[i + 1].ToLowerInvariant() switch
        {
            "none"      => SortOrder.None,
            "hue"       => SortOrder.Hue,
            "luminance" => SortOrder.Luminance,
            "percent"   => SortOrder.Percent,
            _ => throw new Exception($"Unknown sort '{args[i + 1]}'. Use none, hue, luminance or percent.")
        };
        i++;
    }
    else if (args[i] == "--shape" && i + 1 < args.Length)
    {
        swatchShape = args[i + 1].ToLowerInvariant() switch
        {
            "rect"    => SwatchShape.Rectangle,
            "rounded" => SwatchShape.Rounded,
            "circle"  => SwatchShape.Circle,
            _ => throw new Exception($"Unknown shape '{args[i + 1]}'. Use rect, rounded or circle.")
        };
        i++;
    }
    else if (args[i] == "--show-percent") { showPercent = true; }
    else if (args[i] == "--guide" && i + 1 < args.Length)
    {
        compositionGuide = args[i + 1].ToLowerInvariant() switch
        {
            "none"     => CompositionGuide.None,
            "thirds"   => CompositionGuide.RuleOfThirds,
            "golden"   => CompositionGuide.GoldenRatio,
            "diagonal" => CompositionGuide.Diagonal,
            "cross"    => CompositionGuide.CenterCross,
            _ => throw new Exception($"Unknown guide '{args[i + 1]}'. Use none, thirds, golden, diagonal or cross.")
        };
        i++;
    }
    else
    {
        Console.Error.WriteLine($"Unknown argument: {args[i]}");
        return 1;
    }
}

if (!File.Exists(imagePath))
{
    Console.Error.WriteLine($"File not found: {imagePath}");
    return 1;
}

string ext = Path.GetExtension(imagePath).ToLowerInvariant();
if (ext != ".jpg" && ext != ".jpeg")
{
    Console.Error.WriteLine("Only JPEG files (.jpg, .jpeg) are supported.");
    return 1;
}

PhSpectre.Models.ColorPalette palette;
try
{
    await using var stream = File.OpenRead(imagePath);
    var extractor = new PaletteExtractor();
    palette = await extractor.ExtractAsync(stream, colorCount, samplingMode);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Failed to extract palette: {ex.Message}");
    return 1;
}

string outputPath = Path.Combine(
    Path.GetDirectoryName(Path.GetFullPath(imagePath))!,
    Path.GetFileNameWithoutExtension(imagePath) + "_palette.png");

PaletteImageRenderer.Render(imagePath, palette, outputPath, showHex, metaVerbosity, metaStyle, theme, hexBelow,
    sortOrder: sortOrder, swatchShape: swatchShape, showPercent: showPercent, compositionGuide: compositionGuide);

Console.WriteLine($"Saved: {outputPath}");
foreach (var swatch in palette.Swatches)
    Console.WriteLine($"  {swatch.Hex}  {swatch.Percentage:P1}");

return 0;
