using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;

namespace PhSpectre.Rendering;

// The number on a film edge: from the file name, else the EXIF image number, else none.
public static class FilmFrameNumber
{
    // The last run of digits in the file name, without leading zeros: "DSCF5206" -> "5206", "IMG_0012 (2)" -> "2".
    public static string FromFileName(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        var name = Path.GetFileNameWithoutExtension(path);
        int end = name.Length - 1;
        while (end >= 0 && !char.IsDigit(name[end])) end--;
        if (end < 0) return "";
        int start = end;
        while (start > 0 && char.IsDigit(name[start - 1])) start--;
        return name.Substring(start, end - start + 1).TrimStart('0');
    }

    // EXIF "image number" (a frame counter some cameras write); "" when absent or zero.
    public static string FromExif(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        try
        {
            var info = Image.Identify(path);
            var exif = info?.Metadata.ExifProfile;
            if (exif != null && exif.TryGetValue(ExifTag.ImageNumber, out var n) && n.Value > 0) return n.Value.ToString();
        }
        catch { /* unreadable file: no number */ }
        return "";
    }

    public static string Resolve(string? path)
    {
        var fromName = FromFileName(path);
        return fromName.Length > 0 ? fromName : FromExif(path);
    }
}
