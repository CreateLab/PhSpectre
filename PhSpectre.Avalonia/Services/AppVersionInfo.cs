using System;
using System.Reflection;

namespace PhSpectre.Avalonia.Services;

// CI stamps InformationalVersion as "1.2.0+<sha>" via -p:InformationalVersion at publish
// time (see .github/workflows/release.yml). Local/dev builds never set that property, so
// Directory.Build.props defaults it to "0.0.0-dev" — anything that isn't a clean X.Y.Z is
// treated as a dev build: shown as "dev", update checks silently skipped.
public static class AppVersionInfo
{
    public static string DisplayVersion { get; }
    public static string? BuildSha { get; }
    public static Version? Version { get; }
    public static bool IsDev => Version is null;

    static AppVersionInfo()
    {
        var raw = typeof(AppVersionInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        var plusIndex = raw?.IndexOf('+') ?? -1;
        var versionPart = plusIndex >= 0 ? raw![..plusIndex] : raw;
        BuildSha = plusIndex >= 0 ? raw![(plusIndex + 1)..] : null;

        if (versionPart != null && System.Version.TryParse(versionPart, out var parsed))
        {
            Version = parsed;
            DisplayVersion = versionPart;
        }
        else
        {
            Version = null;
            DisplayVersion = "dev";
        }
    }
}
