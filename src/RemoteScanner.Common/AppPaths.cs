using System.Runtime.Versioning;

namespace RemoteScanner.Common;

/// <summary>
/// Every filesystem location the product uses. Centralised so the installer, the service and
/// the diagnostics report cannot disagree about where anything lives.
/// </summary>
[SupportedOSPlatform("windows")]
public static class AppPaths
{
    /// <summary>%ProgramData%\RemoteScanner — shared by the service and all session agents.</summary>
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RemoteScanner");

    public static string LogDirectory { get; } = Path.Combine(Root, "logs");

    public static string ConfigFile { get; } = Path.Combine(Root, "config.json");

    /// <summary>
    /// Where the 64-bit payload is installed.
    ///
    /// Resolved through ProgramW6432 first, because SpecialFolder.ProgramFiles answers
    /// according to the *calling process's* bitness: a 32-bit component asking the same
    /// question is told "Program Files (x86)" and looks for the install in a folder that does
    /// not contain it. Both bitnesses of this product must agree on one answer, and the 64-bit
    /// one is where the installer puts things.
    /// </summary>
    public static string InstallDirectory { get; } = Path.Combine(
        Environment.GetEnvironmentVariable("ProgramW6432")
            ?? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        "RemoteScanner");

    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(LogDirectory);
    }
}
