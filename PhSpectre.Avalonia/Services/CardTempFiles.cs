using System;
using System.IO;
using System.Linq;

namespace PhSpectre.Avalonia.Services;

// Rendered cards wait in their own temporary folder until saved.
internal static class CardTempFiles
{
    private static string Folder => Path.Combine(Path.GetTempPath(), "phspectre-cards");

    // A path for a new render. Older ones go, those of earlier runs included; the files in `keep` and anything just
    // written may still be shown or saved.
    public static string New(string extension, params string?[] keep)
    {
        Directory.CreateDirectory(Folder);
        var old = DateTime.UtcNow.AddMinutes(-2);
        foreach (var file in Directory.GetFiles(Folder))
        {
            if (keep.Contains(file) || File.GetLastWriteTimeUtc(file) > old) continue;
            try { File.Delete(file); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* next time */ }
        }
        return Path.Combine(Folder, $"{Guid.NewGuid()}{extension}");
    }
}
