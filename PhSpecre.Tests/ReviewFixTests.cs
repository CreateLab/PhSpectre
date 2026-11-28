using System.Globalization;
using PhSpectre.ColorGrading;
using PhSpectre.Models;
using PhSpectre.Recipes;
using PhSpectre.Rendering;
using PhSpectre.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace PhSpecre.Tests;

public class ReviewFixTests
{
    private static string TempFolder() => Directory.CreateTempSubdirectory("phspectre-test-").FullName;

    private static PaletteImageRenderer.PhotoMetadata Metadata(Action<ExifProfile> set)
    {
        using var image = new Image<Rgb24>(4, 4);
        var exif = new ExifProfile();
        set(exif);
        image.Metadata.ExifProfile = exif;
        return PaletteImageRenderer.ReadMetadata(image)!;
    }

    [Theory]
    [InlineData(1u, 125u, "1/125s")]
    [InlineData(10u, 1250u, "1/125s")]
    [InlineData(1u, 4u, "1/4s")]
    [InlineData(4u, 10u, "0.4s")]
    [InlineData(15u, 10u, "1.5s")]
    [InlineData(25u, 10u, "2.5s")]
    [InlineData(30u, 1u, "30s")]
    public void ShutterSpeed_IsPrintedAsItWas(uint numerator, uint denominator, string expected) =>
        Assert.Equal(expected,
            Metadata(e => e.SetValue(ExifTag.ExposureTime, new Rational(numerator, denominator, false))).Shutter);

    [Fact]
    public void Aperture_UsesADot_OnAnySystemLanguage()
    {
        var before = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ru-RU");
            Assert.Equal("f/1.4",
                Metadata(e => e.SetValue(ExifTag.FNumber, new Rational(14, 10, false))).Aperture);
        }
        finally { CultureInfo.CurrentCulture = before; }
    }

    private static LrPreset Preset(string attributes) => LrPreset.Parse(
        "<x:xmpmeta xmlns:x='adobe:ns:meta/'><rdf:RDF xmlns:rdf='http://www.w3.org/1999/02/22-rdf-syntax-ns#'>" +
        $"<rdf:Description xmlns:crs='http://ns.adobe.com/camera-raw-settings/1.0/' {attributes}/></rdf:RDF></x:xmpmeta>");

    [Theory]
    [InlineData("Shadow", 0.2f)]
    [InlineData("Highlight", 0.8f)]
    public void SplitToningNames_TintLikeTheColorGradeNames(string region, float level)
    {
        var file = LrPresetApplier.Bake(Preset(
            $"crs:SplitToning{region}Hue='220' crs:SplitToning{region}Saturation='60'"));
        var own = LrPresetApplier.Bake(Preset($"crs:ColorGrade{region}Hue='220' crs:ColorGrade{region}Sat='60'"));

        var tinted = file.Evaluate(level, level, level);

        Assert.Equal(own.Evaluate(level, level, level), tinted);
        Assert.True(tinted.Z - tinted.X > 0.05f, "a blue tint was expected");
    }

    [Theory]
    [InlineData(17)]
    [InlineData(32)]
    [InlineData(60)]
    public void Collage_PlacesManyPhotos(int count)
    {
        var rows = CollageComposer.PlanRows(Enumerable.Repeat(1.5, count).ToList(), targetAspect: 1.0);

        Assert.Equal(count, rows.Sum(r => r.Count));
        Assert.Equal(1.0, rows.Sum(r => r.HeightFraction), 6);
        Assert.All(rows, r => Assert.True(r.Count > 0));
        Assert.Equal(Enumerable.Range(0, count),
            rows.SelectMany(r => Enumerable.Range(r.StartIndex, r.Count)));
        // 1.5 wide photos in a square: the number of rows is close to the root of 1.5 per photo.
        Assert.InRange(rows.Count, Math.Sqrt(count * 1.5) - 1.5, Math.Sqrt(count * 1.5) + 1.5);
    }

    [Fact]
    public void UnusedPath_NeverRepeatsOrOverwrites()
    {
        var folder = TempFolder();
        File.WriteAllText(Path.Combine(folder, "a_graded.jpg"), "kept");
        var taken = new HashSet<string>();

        var first = SafeFiles.Unused(folder, "a_graded.jpg", taken);
        var second = SafeFiles.Unused(folder, "a_graded.jpg", taken);
        var replacing = SafeFiles.Unused(folder, "a_graded.jpg", new HashSet<string>(), avoidExisting: false);

        Assert.Equal("a_graded (2).jpg", Path.GetFileName(first));
        Assert.Equal("a_graded (3).jpg", Path.GetFileName(second));
        Assert.Equal("a_graded.jpg", Path.GetFileName(replacing));
    }

    [Fact]
    public void ADamagedLibrary_IsKeptAside()
    {
        var folder = TempFolder();
        var recipes = Path.Combine(folder, "recipes.json");
        File.WriteAllText(recipes, "{ broken");
        File.WriteAllText(Path.Combine(folder, "frames.json"), "[ broken");
        File.WriteAllText(Path.Combine(folder, "film-styles.json"), "[ broken");
        File.WriteAllText(Path.Combine(folder, "index.json"), "[ broken");

        RecipeLibrary.Load(recipes);
        _ = new PhotoFrameStore(Path.Combine(folder, "frames.json"));
        _ = new FilmStyleStore(Path.Combine(folder, "film-styles.json"));
        _ = new GradeLibrary(folder);

        Assert.Equal("{ broken", File.ReadAllText(recipes + ".bad"));
        foreach (var name in new[] { "frames.json", "film-styles.json", "index.json" })
            Assert.Equal("[ broken", File.ReadAllText(Path.Combine(folder, name + ".bad")));
    }

    [Fact]
    public void ImportErrors_AreInTheInterfaceLanguage()
    {
        var library = new GradeLibrary(TempFolder());

        var result = library.Import(Path.Combine(TempFolder(), "missing.cube"));

        Assert.Equal("File not found.", result.Error);
    }

    [Fact]
    public void AStackRender_UsesTheSkinMaskItIsGiven()
    {
        using var photo = new Image<Rgb24>(320, 240);
        var random = new Random(3);
        for (int y = 0; y < photo.Height; y++)
            for (int x = 0; x < photo.Width; x++)
            {
                int n = random.Next(-9, 10);
                photo[x, y] = new Rgb24((byte)(200 + n), (byte)(150 + n), (byte)(130 + n));
            }
        var stack = new GradeStack { Preset = Preset("crs:SkinSmoothing='100'") };
        var skin = new SkinMask(Enumerable.Repeat(1f, 64).ToArray(), 8, 8);
        var nothing = new SkinMask(new float[64], 8, 8);

        using var smoothed = stack.Render(photo, skin);
        using var left = stack.Render(photo, nothing);

        Assert.True(Difference(photo, smoothed) > Difference(photo, left) + 1);
    }

    private static double Difference(Image<Rgb24> a, Image<Rgb24> b)
    {
        double sum = 0;
        for (int y = 0; y < a.Height; y++)
            for (int x = 0; x < a.Width; x++)
                sum += Math.Abs(a[x, y].G - b[x, y].G);
        return sum / (a.Width * a.Height);
    }
}
