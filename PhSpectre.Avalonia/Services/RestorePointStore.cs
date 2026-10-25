using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PhSpectre.Camera;

namespace PhSpectre.Avalonia.Services;

internal sealed record RestorePointEntry(string Path, FujiSlotBackup Backup);

/// <summary>
/// Restore points: the raw contents of a camera slot saved to disk before anything is written to it,
/// so a failed or unwanted write can be put back, even after the app was closed.
/// </summary>
internal static class RestorePointStore
{
    // Pictures/PhSpectre/restore where the app can reach Pictures; Android keeps them private to the app.
    public static string Folder => Path.Combine(
        OperatingSystem.IsAndroid()
            ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
            : Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
        "PhSpectre", "restore");

    public static string DisplayFolder => OperatingSystem.IsAndroid() ? "App storage/restore" : "Pictures/PhSpectre/restore";

    /// <summary>Saves a restore point and trims the oldest ones beyond <paramref name="keepLast"/> (0 keeps all).</summary>
    public static string Save(FujiSlotBackup backup, int keepLast)
    {
        Directory.CreateDirectory(Folder);
        var name = $"{backup.Model}-C{backup.Slot}-{backup.Created:yyyyMMdd-HHmmss}.json";
        var path = Path.Combine(Folder, name);
        File.WriteAllText(path, backup.ToJson());
        Trim(keepLast);
        return path;
    }

    public static FujiSlotBackup Load(string path) => FujiSlotBackup.FromJson(File.ReadAllText(path));

    /// <summary>Saved restore points, newest first. Files that cannot be read are skipped.</summary>
    public static IReadOnlyList<RestorePointEntry> List()
    {
        var result = new List<RestorePointEntry>();
        if (!Directory.Exists(Folder)) return result;
        foreach (var file in new DirectoryInfo(Folder).GetFiles("*.json").OrderByDescending(f => f.LastWriteTimeUtc))
        {
            try { result.Add(new RestorePointEntry(file.FullName, Load(file.FullName))); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException or FormatException)
            {
                AppLogger.LogError($"Skipping unreadable restore point {file.Name}", e);
            }
        }
        return result;
    }

    private static void Trim(int keepLast)
    {
        if (keepLast <= 0) return;
        try
        {
            var stale = new DirectoryInfo(Folder).GetFiles("*.json")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Skip(keepLast);
            foreach (var file in stale) file.Delete();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            AppLogger.LogError("Failed to trim restore points", e);
        }
    }
}
