using System;
using System.IO;

namespace PhSpectre.Avalonia.Services;

internal static class AppLogger
{
    private const long MaxFileSizeBytes = 5 * 1024 * 1024;

    private static readonly object Lock = new();

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PhSpectre", "logs", "app.log");

    public static void LogInfo(string message) => Write("INFO", message, null);

    public static void LogError(string message, Exception? ex = null) => Write("ERROR", message, ex);

    private static void Write(string level, string message, Exception? ex)
    {
        try
        {
            var path = FilePath;
            var dir = Path.GetDirectoryName(path)!;

            lock (Lock)
            {
                Directory.CreateDirectory(dir);

                if (File.Exists(path) && new FileInfo(path).Length > MaxFileSizeBytes)
                    File.Delete(path);

                var line = $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
                if (ex != null) line += Environment.NewLine + ex;

                File.AppendAllText(path, line + Environment.NewLine);
            }
        }
        catch
        {
        }
    }
}
