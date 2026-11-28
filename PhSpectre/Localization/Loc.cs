using System.Globalization;
using System.Text.Json;

namespace PhSpectre.Localization;

public enum AppLanguage { System, English, Russian, Spanish }

/// <summary>
/// Interface translations. The English text is the key; ru.json and es.json hold the rest.
/// A "context::" prefix tells apart two uses of one English word and is never shown.
/// Plural entries are keyed by the English plural and hold the forms separated by '|'.
/// English until <see cref="Use"/> is called, so the CLI, the API and tests are unaffected.
/// </summary>
public static class Loc
{
    private static IReadOnlyDictionary<string, string> _table = new Dictionary<string, string>();

    public static string Code { get; private set; } = "en";

    /// <summary>For dates and other text that must match the interface language.</summary>
    public static CultureInfo Culture { get; private set; } = CultureInfo.GetCultureInfo("en-US");

    public static void Use(AppLanguage language) => Use(language switch
    {
        AppLanguage.English => "en",
        AppLanguage.Russian => "ru",
        AppLanguage.Spanish => "es",
        _ => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName,
    });

    public static void Use(string code)
    {
        Code = code is "ru" or "es" ? code : "en";
        Culture = CultureInfo.GetCultureInfo(Code switch { "ru" => "ru-RU", "es" => "es-ES", _ => "en-US" });
        _table = Code == "en" ? new Dictionary<string, string>() : Load(Code);
    }

    public static IReadOnlyDictionary<string, string> Load(string code)
    {
        var assembly = typeof(Loc).Assembly;
        using var stream = assembly.GetManifestResourceStream($"PhSpectre.Localization.{code}.json")
            ?? throw new InvalidOperationException($"No translations for '{code}'.");
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!;
    }

    public static string T(string key) => _table.TryGetValue(key, out var text) ? text : English(key);

    public static string F(string key, params object?[] args) => string.Format(T(key), args);

    /// <summary>Text that depends on a count. {0} is the count, {1} and on are <paramref name="args"/>.</summary>
    public static string N(int count, string one, string other, params object?[] args)
    {
        string format = count == 1 ? one : other;
        if (_table.TryGetValue(other, out var entry))
        {
            var forms = entry.Split('|');
            format = forms[Math.Min(PluralForm(Code, count), forms.Length - 1)];
        }
        return string.Format(format, [count, .. args]);
    }

    // Russian has three forms (1, 2-4, 5+); English and Spanish two.
    private static int PluralForm(string code, int count)
    {
        if (code != "ru") return count == 1 ? 0 : 1;
        int n = Math.Abs(count), tens = n % 100, units = n % 10;
        if (units == 1 && tens != 11) return 0;
        return units is >= 2 and <= 4 && tens is < 12 or > 14 ? 1 : 2;
    }

    private static string English(string key)
    {
        int at = key.IndexOf("::", StringComparison.Ordinal);
        return at < 0 ? key : key[(at + 2)..];
    }
}
