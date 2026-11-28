using PhSpectre.Localization;
using System;
using System.IO;
using System.Linq;
using PhSpectre.Models;

namespace PhSpectre.Avalonia.Services;

// Default save name <stem>_<mode>.<ext>. Picker names (Android content URIs) can be unusable, so the stem is
// sanitized, falling back to "photo".
public static class SuggestedFileName
{
    public static string ModeSuffix(ExportMode mode) => mode switch
    {
        ExportMode.Recipe => "_recipe",
        _ => "_palette"
    };

    public static string Build(string? sourceFileName, ExportMode mode, string extension)
    {
        var stem = Sanitize(Path.GetFileNameWithoutExtension(sourceFileName ?? ""));
        return stem + ModeSuffix(mode) + extension;
    }

    // Saved cards go to Pictures/PhSpectre, away from the sources, so Open folder does not pick them up.
    public static string DefaultOutputFolder()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "PhSpectre");
        try { Directory.CreateDirectory(dir); } catch { /* picker just falls back to its own default */ }
        return dir;
    }

    // Files this app produced; Open folder and Save all skip them.
    public static bool IsGeneratedCard(string path)
    {
        var stem = Path.GetFileNameWithoutExtension(path);
        return stem.EndsWith("_palette", StringComparison.OrdinalIgnoreCase)
            || stem.EndsWith("_recipe",  StringComparison.OrdinalIgnoreCase);
    }

    public static string BuildCollage(string extension) => "collage_palette" + extension;

    public static string DialogTitle(string suggestedName)
    {
        var stem = Path.GetFileNameWithoutExtension(suggestedName);
        if (stem.StartsWith("collage", StringComparison.OrdinalIgnoreCase)) return Loc.T("Save collage");
        if (stem.EndsWith("_recipe", StringComparison.OrdinalIgnoreCase)) return Loc.T("Save recipe card");
        return Loc.T("Save palette card");
    }

    internal static string Sanitize(string stem)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(stem.Where(c => !invalid.Contains(c)).ToArray()).Trim();
        return cleaned.Any(char.IsLetterOrDigit) ? cleaned : "photo";
    }
}
