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

    /// <summary>
    /// Where a component running as a signed-in user writes its log: their own profile.
    ///
    /// Not ProgramData. On a multi-user Session Host a shared log directory means every user
    /// can read every other user's: machine names, session ids, scanner models, link timings.
    /// Page content is never logged at any level, but that list is still more than a colleague
    /// needs to know. Matches what the native logger in rs_log.h does.
    /// </summary>
    public static string LogDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RemoteScanner", "logs");

    /// <summary>
    /// Where the machine-wide service logs. It runs as LocalSystem, belongs to no user, and is
    /// the only thing that writes here — so ProgramData keeps its default permissions and the
    /// installer no longer has to widen them.
    /// </summary>
    public static string MachineLogDirectory { get; } = Path.Combine(Root, "logs");

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

    /// <param name="machineWide">
    /// True only for the service. Everything else runs as a user and must not depend on being
    /// able to write to ProgramData — on a locked-down host it cannot.
    /// </param>
    public static void EnsureDirectories(bool machineWide = false)
    {
        Directory.CreateDirectory(LogDirectory);

        if (!machineWide) return;
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(MachineLogDirectory);
    }
}
