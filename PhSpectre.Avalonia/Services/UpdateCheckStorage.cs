using System;
using System.IO;
using System.Text.Json;

namespace PhSpectre.Avalonia.Services;

internal sealed class UpdateCheckState
{
    public DateTime? LastCheckedUtc { get; set; }
    public string? LastKnownVersion { get; set; }
    public string? LastKnownUrl { get; set; }
    public string? DismissedVersion { get; set; }
}

// Best-effort JSON file: if reading or writing fails, the check simply runs again next launch.
internal static class UpdateCheckStorage
{
    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PhSpectre", "update-check.json");

    public static UpdateCheckState Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new UpdateCheckState();
            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<UpdateCheckState>(json) ?? new UpdateCheckState();
        }
        catch
        {
            return new UpdateCheckState();
        }
    }

    public static void Save(UpdateCheckState state)
    {
        try
        {
            var dir = Path.GetDirectoryName(FilePath)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(state));
        }
        catch
        {
        }
    }
}
