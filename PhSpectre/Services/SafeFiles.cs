namespace PhSpectre.Services;

public static class SafeFiles
{
    // A path for `name` in `folder` that neither exists nor was handed out before: "name (2).jpg" and so on.
    public static string Unused(string folder, string name, ISet<string>? taken = null, bool avoidExisting = true)
    {
        var stem = Path.GetFileNameWithoutExtension(name);
        var extension = Path.GetExtension(name);
        var path = Path.Combine(folder, name);
        for (int n = 2; (avoidExisting && File.Exists(path)) || taken?.Contains(path.ToUpperInvariant()) == true; n++)
            path = Path.Combine(folder, $"{stem} ({n}){extension}");
        taken?.Add(path.ToUpperInvariant());
        return path;
    }

    // Keeps a copy of a file that could not be read, so the next save does not destroy what is in it.
    public static void KeepUnreadable(string path)
    {
        try
        {
            if (File.Exists(path)) File.Copy(path, path + ".bad", overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Nothing more can be done; the app must still start.
        }
    }
}
