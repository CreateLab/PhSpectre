using System.Net;
using System.Text.RegularExpressions;
using PhSpectre.Localization;
using PhSpectre.Models;
using PhSpectre.Recipes;
using PhSpectre.Rendering;
using Xunit;

namespace PhSpecre.Tests;

public class LocalizationTests
{
    private const string Literal = "\"((?:[^\"\\\\]|\\\\.)*)\"";
    private static readonly string[] Translated = ["ru", "es"];

    [Theory]
    [InlineData("ru")]
    [InlineData("es")]
    public void Every_key_used_in_the_sources_is_translated(string code)
    {
        var table = Loc.Load(code);
        var missing = SourceKeys().Concat(DataKeys()).Distinct().Where(k => !table.ContainsKey(k)).ToList();
        Assert.True(missing.Count == 0, $"{code}.json lacks:\n{string.Join("\n", missing)}");
    }

    [Fact]
    public void Translations_cover_the_same_keys_and_keep_the_placeholders()
    {
        var ru = Loc.Load("ru");
        var es = Loc.Load("es");
        Assert.Equal(ru.Keys.Order(), es.Keys.Order());
        foreach (var code in Translated)
            foreach (var (key, value) in Loc.Load(code))
                foreach (var form in key.Contains('|') ? [value] : value.Split('|'))
                {
                    // "{1} of {0}" may reorder placeholders but not drop or invent one.
                    Assert.True(Placeholders(key).SetEquals(Placeholders(form)), $"{code}: {key} -> {form}");
                    Assert.False(string.IsNullOrWhiteSpace(form), $"{code}: {key} is empty");
                }
    }

    [Theory]
    [InlineData("ru")]
    [InlineData("es")]
    public void Segmented_captions_keep_their_count(string code)
    {
        foreach (var (key, value) in Loc.Load(code).Where(e => e.Key.Contains('|')))
            Assert.Equal(key.Split('|').Length, value.Split('|').Length);
    }

    private static HashSet<string> Placeholders(string text) =>
        Regex.Matches(text, @"\{\d+(:[^{}]*)?\}").Select(m => m.Value).ToHashSet();

    private static IEnumerable<string> SourceKeys()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "PhSpectre.sln"))) root = root.Parent;
        Assert.NotNull(root);

        var files = root.GetDirectories("PhSpectre*")
            .SelectMany(d => d.EnumerateFiles("*.*", SearchOption.AllDirectories))
            .Where(f => f.Extension is ".cs" or ".axaml")
            .Where(f => !Regex.IsMatch(f.FullName, @"[\\/](bin|obj)[\\/]"));

        // Tables whose labels go through Loc.T later: film edge choices, recipe diff fields, encoder labels.
        string[] tables =
        [
            @"\(" + Literal + @",\s*\[(?:\]|FilmToken)",
            @"\(" + Literal + @",\s*r => ",
            @"Add(?:ThreeLevel|Tenths)\(FujiSlotCodec\.\w+, " + Literal,
        ];

        foreach (var file in files)
        {
            var text = File.ReadAllText(file.FullName);
            if (file.Extension == ".axaml")
            {
                foreach (Match m in Regex.Matches(text, @"\{l:T '((?:[^'\\]|\\.)*)'\}"))
                    yield return Regex.Replace(WebUtility.HtmlDecode(m.Groups[1].Value), @"\\(.)", "$1");
                continue;
            }
            if (file.Name is "ProcessViewModel.Film.cs" or "RecipeDiff.cs" or "FujiSlotEncoder.cs")
                foreach (Match m in tables.SelectMany(pattern => Regex.Matches(text, pattern)))
                    yield return Regex.Unescape(m.Groups[1].Value);
            foreach (Match m in Regex.Matches(text, @"Loc\.[TF]\(\s*(?:[\w.]+ \? )?" + Literal + @"(?:\s*:\s*" + Literal + ")?"))
                foreach (var group in m.Groups.Cast<Group>().Skip(1).Where(g => g.Success))
                    yield return Regex.Unescape(group.Value);
            foreach (Match m in Regex.Matches(text, @"Loc\.N\([^""]*" + Literal + @",\s*" + Literal))
                yield return Regex.Unescape(m.Groups[2].Value);
        }
    }

    // Keys that reach Loc.T through a variable.
    private static IEnumerable<string> DataKeys()
    {
        var recipe = new FilmRecipe(RecipeSource.Manual, null, "", "", null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null)
        {
            WhiteBalance = "Auto", WhiteBalanceShift = new(1, 1), DynamicRange = "DR100", HighlightTone = 1,
            ShadowTone = 1, Color = 1, Sharpness = 1, NoiseReduction = 1, Clarity = 1, GrainEffect = "Weak",
            ColorChromeEffect = "Weak", ColorChromeFxBlue = "Weak", BwAdjustment = 1,
        };
        return RecipeFields.BuildAll(recipe).SelectMany(f => new[] { f.Label, f.FullLabel })
            .Concat(PhotoAdjust.CropChoices.Select(c => c.Label))
            .Concat(PhotoFrame.AspectChoices.Select(c => c.Label))
            .Where(label => label.Any(char.IsLetter));
    }
}
