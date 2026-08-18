using System.Runtime.Versioning;

namespace ScanBridge.Common;

/// <summary>
/// Every filesystem location the product uses. Centralised so the installer, the service and
/// the diagnostics report cannot disagree about where anything lives.
/// </summary>
[SupportedOSPlatform("windows")]
public static class AppPaths
{
    /// <summary>%ProgramData%\ScanBridge — shared by the service and all session agents.</summary>
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ScanBridge");

    /// <summary>
    /// Everything a component running as a signed-in user writes: their own profile.
    ///
    /// Not ProgramData. On a multi-user Session Host a shared directory means every user can
    /// read every other user's logs — machine names, session ids, scanner models, link timings
    /// — and, worse, they would share one settings file, so one person choosing a scanner
    /// would change it for everyone. Page content is never logged at any level, but that list
    /// is still more than a colleague needs to know. Matches what the native logger in
    /// rs_log.h does.
    /// </summary>
    public static string UserDataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScanBridge");

    public static string LogDirectory { get; } = Path.Combine(UserDataDirectory, "logs");

    /// <summary>
    /// Where the machine-wide service logs. It runs as LocalSystem, belongs to no user, and is
    /// the only thing that writes here — so ProgramData keeps its default permissions and the
    /// installer no longer has to widen them.
    /// </summary>
    public static string MachineLogDirectory { get; } = Path.Combine(Root, "logs");

    /// <summary>
    /// Settings, in the user's own profile — for the same reasons as the log, plus one that
    /// made it fail outright.
    ///
    /// This used to live under Root. Every component that reads or writes it runs as a signed-in
    /// user and is never elevated (the tray client refuses to be), while EnsureDirectories only
    /// creates Root for the service. So on any machine where the server installer had never run
    /// — which is every PC that only has the client — choosing a scanner threw
    /// "Could not find a part of the path 'C:\ProgramData\ScanBridge\config.json.tmp'". Saving a
    /// setting had never once worked there.
    ///
    /// Creating the folder from the client would have silenced that and left the real problem:
    /// on a Session Host the first user to save would own the file and everyone else would be
    /// writing over their choices, or be unable to write at all.
    /// </summary>
    public static string ConfigFile { get; } = Path.Combine(UserDataDirectory, "config.json");

    /// <summary>
    /// Where settings were kept before 18 August 2026. Read once, if the current file does not
    /// exist yet, so an existing installation keeps the scanner it had chosen.
    /// </summary>
    public static string LegacyConfigFile { get; } = Path.Combine(Root, "config.json");

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
        "ScanBridge");

    /// <param name="machineWide">
    /// True only for the service. Everything else runs as a user and must not depend on being
    /// able to write to ProgramData — on a locked-down host it cannot.
    /// </param>
    public static void EnsureDirectories(bool machineWide = false)
    {
        // UserDataDirectory explicitly, not just LogDirectory, because the config file sits
        // beside the log folder rather than inside it. Creating only the deeper path happened
        // to work while both were under one root and would break silently the moment they
        // were not.
        Directory.CreateDirectory(UserDataDirectory);
        Directory.CreateDirectory(LogDirectory);

        if (!machineWide) return;
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(MachineLogDirectory);
    }
}
