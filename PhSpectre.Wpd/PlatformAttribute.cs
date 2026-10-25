using System.Runtime.Versioning;

// Windows-only at runtime; callers must guard with OperatingSystem.IsWindows() (the project itself builds on any OS so
// the cross-platform Desktop host can reference it).
[assembly: SupportedOSPlatform("windows")]
