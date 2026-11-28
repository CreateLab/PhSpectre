using System;
using System.Reflection;

namespace PhSpectre.Avalonia.Services;

// CI stamps "1.2.0+<sha>" (release.yml). Anything that is not X.Y.Z, such as the local default "0.0.0-dev", counts
// as a dev build.
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
