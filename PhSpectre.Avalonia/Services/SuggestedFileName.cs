using System;
using System.IO;
using System.Linq;
using PhSpectre.Models;

namespace PhSpectre.Avalonia.Services;

// Default save-dialog file name: <source stem>_<mode>.<ext>, plus the matching dialog title.
// Picker-provided names (notably Android content-URI display names) can come back mangled —
// only punctuation, or with characters the destination file system rejects — so the stem is
// sanitized and falls back to "photo" when nothing readable is left.
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

    // Default destination for saved cards: Pictures/PhSpectre, not the folder the sources
    // live in (saved cards used to land next to the originals and get re-imported by Open folder).
    public static string DefaultOutputFolder()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "PhSpectre");
        try { Directory.CreateDirectory(dir); } catch { /* picker just falls back to its own default */ }
        return dir;
    }

    // True for files this app produced (<stem>_palette / _recipe, collage_palette) so Open
    // folder can keep them out of the photo list and out of "Save all".
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
        if (stem.StartsWith("collage", StringComparison.OrdinalIgnoreCase)) return "Save collage";
        if (stem.EndsWith("_recipe", StringComparison.OrdinalIgnoreCase)) return "Save recipe card";
        return "Save palette card";
    }

    internal static string Sanitize(string stem)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(stem.Where(c => !invalid.Contains(c)).ToArray()).Trim();
        return cleaned.Any(char.IsLetterOrDigit) ? cleaned : "photo";
    }
}
