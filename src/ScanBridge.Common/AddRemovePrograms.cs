using System.Reflection;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace ScanBridge.Common;

/// <summary>
/// The entry in Settings → Apps → Installed apps, and in the old Programs and Features.
///
/// Without one, a product is not uninstallable by any means a normal person knows about. The
/// switch existed from the start, but a command-line switch is not an uninstaller: somebody who
/// installed this months ago and now wants it gone looks in one place, does not find it, and
/// concludes it cannot be removed. That is also where an administrator looks to answer "what is
/// this and who put it here".
///
/// Registered in HKCU for the client and HKLM for the server, matching where each half actually
/// installs. Windows reads both.
/// </summary>
[SupportedOSPlatform("windows")]
public static class AddRemovePrograms
{
    private const string UninstallPath =
        @"Software\Microsoft\Windows\CurrentVersion\Uninstall";

    /// <summary>Version as shipped, without the "+&lt;commit&gt;" the SDK appends.</summary>
    public static string Version
    {
        get
        {
            string full = Assembly.GetEntryAssembly()
                ?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? "1.0.0";

            int plus = full.IndexOf('+');
            return plus < 0 ? full : full[..plus];
        }
    }

    /// <param name="machineWide">
    /// True for the server, which installs once for the machine under HKLM and needs
    /// administrator rights to do it. False for the client, which is per user.
    /// </param>
    public static void Register(string key, string displayName, string installedExe,
                                string installLocation, bool machineWide)
    {
        RegistryKey root = machineWide ? Registry.LocalMachine : Registry.CurrentUser;

        using RegistryKey entry = root.CreateSubKey($@"{UninstallPath}\{key}", writable: true)
            ?? throw new InvalidOperationException($"Cannot create the uninstall entry for {key}.");

        entry.SetValue("DisplayName", displayName, RegistryValueKind.String);
        entry.SetValue("DisplayVersion", Version, RegistryValueKind.String);
        entry.SetValue("Publisher", "Dhyan Vohera", RegistryValueKind.String);
        entry.SetValue("DisplayIcon", installedExe, RegistryValueKind.String);
        entry.SetValue("InstallLocation", installLocation, RegistryValueKind.String);
        entry.SetValue("URLInfoAbout", "https://github.com/voheradhyan/ScanBridge", RegistryValueKind.String);

        // Quoted: the path contains spaces on any normal machine, and Windows hands this
        // string to the shell exactly as written.
        entry.SetValue("UninstallString", $"\"{installedExe}\" --uninstall", RegistryValueKind.String);
        entry.SetValue("QuietUninstallString", $"\"{installedExe}\" --uninstall --quiet", RegistryValueKind.String);

        // There is nothing to modify or repair, and offering buttons that do nothing is worse
        // than not offering them.
        entry.SetValue("NoModify", 1, RegistryValueKind.DWord);
        entry.SetValue("NoRepair", 1, RegistryValueKind.DWord);

        entry.SetValue("EstimatedSize", EstimatedSizeKb(installLocation), RegistryValueKind.DWord);
    }

    public static void Unregister(string key, bool machineWide)
    {
        RegistryKey root = machineWide ? Registry.LocalMachine : Registry.CurrentUser;

        try
        {
            root.DeleteSubKeyTree($@"{UninstallPath}\{key}", throwOnMissingSubKey: false);
        }
        catch (Exception ex)
        {
            Log.Logger.Warning(ex, "Could not remove the uninstall entry {Key}.", key);
        }
    }

    /// <summary>Is this half already installed, according to Windows?</summary>
    public static string? InstalledLocation(string key, bool machineWide)
    {
        RegistryKey root = machineWide ? Registry.LocalMachine : Registry.CurrentUser;
        using RegistryKey? entry = root.OpenSubKey($@"{UninstallPath}\{key}");
        return entry?.GetValue("InstallLocation") as string;
    }

    public static string? InstalledVersion(string key, bool machineWide)
    {
        RegistryKey root = machineWide ? Registry.LocalMachine : Registry.CurrentUser;
        using RegistryKey? entry = root.OpenSubKey($@"{UninstallPath}\{key}");
        return entry?.GetValue("DisplayVersion") as string;
    }

    /// <summary>
    /// Size in KB, which is what Windows displays. Measured rather than hardcoded: the client
    /// carries a payload it lays down beside itself, so the number changes with the build, and
    /// a figure that disagrees with the folder is the kind of small wrongness people notice.
    /// </summary>
    private static int EstimatedSizeKb(string installLocation)
    {
        try
        {
            if (!Directory.Exists(installLocation)) return 0;

            long bytes = new DirectoryInfo(installLocation)
                .EnumerateFiles("*", SearchOption.AllDirectories)
                .Sum(f => f.Length);

            return (int)Math.Min(bytes / 1024, int.MaxValue);
        }
        catch (Exception)
        {
            // A size Windows shows as blank is better than a failed install.
            return 0;
        }
    }
}
