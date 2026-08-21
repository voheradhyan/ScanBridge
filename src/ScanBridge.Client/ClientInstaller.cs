using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32;
using ScanBridge.Common;

namespace ScanBridge.Client;

/// <summary>
/// Installs and removes the client half, from inside the executable being installed.
///
/// Per user, and never elevated. That is not a convenience: the add-in registration lives in
/// HKCU, the shared key is protected with the user's own DPAPI key, and the tray application
/// has to run as the person whose scanner it is. An elevated install writes all of that into
/// the administrator's account, where the user it was meant for cannot see any of it — and it
/// fails in the most confusing way possible, by appearing to succeed.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ClientInstaller
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "ScanBridge";

    /// <summary>Per user by default: %LocalAppData%\Programs\ScanBridge.</summary>
    public static string DefaultDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs", "ScanBridge");

    /// <summary>Where the Start Menu and Desktop shortcuts are called.</summary>
    private const string ShortcutName = "ScanBridge";

    /// <summary>The subkey under HKCU ...\Uninstall that Apps &amp; Features reads.</summary>
    private const string UninstallKey = "ScanBridge";

    /// <summary>Where Windows says this is installed, or null if it is not.</summary>
    public static string? InstalledDirectory() =>
        AddRemovePrograms.InstalledLocation(UninstallKey, machineWide: false);

    /// <summary>
    /// What the setup window shows and calls back into.
    ///
    /// The window collects choices; every rule about how the client installs stays here, where
    /// it is already written down. Note that RequiresElevation is false and stays false: this
    /// half refuses to run elevated at all, so there is nothing for the window to escalate to.
    /// </summary>
    public static Setup.SetupPlan SetupPlan() => new()
    {
        ProductName = "ScanBridge",
        Summary = "Makes the scanner attached to this PC usable by applications running "
                + "inside your Remote Desktop session.",
        DefaultDirectory = DefaultDirectory,
        OffersStartWithWindows = true,
        RequiresElevation = false,
        ExistingInstall = InstalledDirectory,
        ExistingVersion = () => AddRemovePrograms.InstalledVersion(UninstallKey, machineWide: false),

        Install = (choices, writer) => Setup.SetupHost.Capturing(writer, () =>
            Install(choices.Directory, startNow: true,
                    desktopShortcut: choices.DesktopShortcut,
                    startWithWindows: choices.StartWithWindows)),

        Uninstall = writer => Setup.SetupHost.Capturing(writer, Uninstall),
    };

    public static int Install(string? installDirectory, bool startNow = true,
                              bool desktopShortcut = true, bool startWithWindows = true)
    {
        if (IsElevated())
        {
            Console.Error.WriteLine("""
                Do not run this as an administrator.

                Everything it installs belongs to one user: the Remote Desktop add-in is
                registered under HKCU, the key it authenticates with is protected by that
                user's own DPAPI key, and the tray application must run as the person whose
                scanner it is. Installed from an elevated prompt, all of that lands in the
                administrator's account and the intended user gets nothing — while the install
                appears to have worked.

                Run it again normally, as the account you use Remote Desktop with.
                """);
            return 5;
        }

        string target = installDirectory ?? DefaultDirectory;

        try
        {
            Console.WriteLine($"Installing ScanBridge to {target}");

            // Before anything is written. The RDP add-in is loaded inside mstsc.exe whenever a
            // Remote Desktop session is open, and Windows will not replace a loaded image — so
            // an upgrade attempted mid-session used to copy the new program into place and then
            // fail on the add-in, leaving a new executable beside old plugins. That combination
            // is worse than not upgrading at all, and it looked like a successful install right
            // up until scanning quietly stopped working.
            if (BlockedPayload(target) is { } lockedFile)
            {
                Console.Error.WriteLine();
                Console.Error.WriteLine(lockedFile);
                Console.Error.WriteLine();
                Console.Error.WriteLine("Nothing was changed.");
                return 6;
            }

            StopRunningCopy();

            Directory.CreateDirectory(target);
            string installedExe = Path.Combine(target, "ScanBridge.Client.exe");
            CopySelf(installedExe);

            // The RDP add-in, both bitnesses: mstsc.exe decides which it loads, not us.
            foreach (string architecture in new[] { "x64", "x86" })
            {
                string resource = $"{architecture}/ScanBridge.DvcPlugin.dll";
                if (!EmbeddedPayload.Contains(resource)) continue;

                string file = Path.Combine(target, architecture, "ScanBridge.DvcPlugin.dll");
                string hash = EmbeddedPayload.Extract(resource, file);
                Console.WriteLine($"  rdp add-in   {file}  [{hash}]");
            }

            // The 32-bit scan host, which cannot be a role of this 64-bit application: a
            // scanner driver's bitness decides the bitness of the process that loads it.
            const string hostResource = "x86/ScanBridge.ScanHost.exe";
            if (EmbeddedPayload.Contains(hostResource))
            {
                string file = Path.Combine(target, "x86", "ScanBridge.ScanHost.exe");
                string hash = EmbeddedPayload.Extract(hostResource, file);
                Console.WriteLine($"  32-bit host  {file}  [{hash}]");
            }
            else
            {
                Console.WriteLine("  32-bit host  not carried by this build; 32-bit-only scanner");
                Console.WriteLine("               drivers will not be reachable.");
            }

            RegisterAddIn(target);

            if (startWithWindows) RegisterAutoStart(installedExe);
            else RemoveAutoStart(quiet: true);

            CreateShortcuts(installedExe, desktopShortcut);

            AddRemovePrograms.Register(UninstallKey, "ScanBridge", installedExe, target,
                                       machineWide: false);
            Console.WriteLine($"  listed in    Apps & Features as ScanBridge {AddRemovePrograms.Version}");

            if (PluginRegistration.PolicyBlockReason() is { } blocked)
            {
                Console.WriteLine();
                Console.WriteLine($"  Warning: {blocked}");
            }

            Console.WriteLine();
            Console.WriteLine("Installed. Reconnect your Remote Desktop session — mstsc.exe loads add-ins");
            Console.WriteLine("when it connects, so a session that is already open will not see the scanner.");

            if (startNow) Process.Start(new ProcessStartInfo(installedExe) { UseShellExecute = true });
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine($"Install failed: {ex.Message}");
            return 1;
        }
    }

    public static int Uninstall()
    {
        int problems = 0;

        StopRunningCopy();
        PluginRegistration.Unregister();

        if (!RemoveAutoStart(quiet: false)) problems++;

        foreach (string link in new[] { Shortcuts.UserStartMenu(ShortcutName),
                                        Shortcuts.UserDesktop(ShortcutName) })
        {
            if (Shortcuts.Remove(link)) Console.WriteLine($"  removed {link}");
        }

        AddRemovePrograms.Unregister(UninstallKey, machineWide: false);
        Console.WriteLine("  removed the Apps & Features entry");

        // The directory this process is running from cannot delete itself; say so rather than
        // reporting a success that leaves files behind.
        string here = Path.GetDirectoryName(Environment.ProcessPath ?? string.Empty) ?? string.Empty;
        string installed = DefaultDirectory;

        if (Directory.Exists(installed) &&
            !string.Equals(Path.GetFullPath(here), Path.GetFullPath(installed),
                           StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                Directory.Delete(installed, recursive: true);
                Console.WriteLine($"  removed {installed}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"  {installed} could not be removed: {ex.Message}");
                problems++;
            }
        }
        else if (Directory.Exists(installed))
        {
            Console.WriteLine($"  {installed} is where this copy is running from; delete it once this exits.");
        }

        Console.WriteLine(problems == 0 ? "\nRemoved." : "\nRemoved, with the problems noted above.");
        return problems == 0 ? 0 : 1;
    }

    // ------------------------------------------------------------------ pieces

    private static bool IsElevated()
        => new WindowsPrincipal(WindowsIdentity.GetCurrent())
            .IsInRole(WindowsBuiltInRole.Administrator);

    /// <summary>
    /// Is any file this install would have to replace currently locked by another process?
    /// Returns a message naming it, or null when the install can proceed.
    ///
    /// A file whose contents already match is not a problem: it will be skipped, not rewritten,
    /// so being loaded somewhere is irrelevant. Only a file that genuinely has to change and
    /// cannot counts.
    /// </summary>
    private static string? BlockedPayload(string target)
    {
        var payload = new (string Resource, string Path)[]
        {
            ("x64/ScanBridge.DvcPlugin.dll", Path.Combine(target, "x64", "ScanBridge.DvcPlugin.dll")),
            ("x86/ScanBridge.DvcPlugin.dll", Path.Combine(target, "x86", "ScanBridge.DvcPlugin.dll")),
            ("x86/ScanBridge.ScanHost.exe",  Path.Combine(target, "x86", "ScanBridge.ScanHost.exe")),
        };

        foreach ((string resource, string path) in payload)
        {
            if (!EmbeddedPayload.Contains(resource) || !File.Exists(path)) continue;
            if (EmbeddedPayload.HashOf(resource) == EmbeddedPayload.HashOfFile(path)) continue;

            try
            {
                // Opened the way the replacement would open it. If this succeeds the file is
                // free and the install can go ahead.
                using var probe = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                string holders = FileLock.DescribeHolders(path);
                return $"{Path.GetFileName(path)} has changed in this version and is in use{holders}.";
            }
        }

        return null;
    }

    private static void StopRunningCopy()
    {
        foreach (Process process in Process.GetProcessesByName("ScanBridge.Client"))
        {
            try
            {
                if (process.Id == Environment.ProcessId) continue;
                process.Kill();
                process.WaitForExit(5000);
                Console.WriteLine("  stopped the copy that was already running");
            }
            catch (Exception)
            {
                // Another user's copy on a shared PC; not ours to stop, and its files are
                // somewhere else.
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    private static void CopySelf(string destination)
    {
        string source = Environment.ProcessPath
            ?? throw new InvalidOperationException("Cannot determine this executable's path.");

        if (string.Equals(Path.GetFullPath(source), Path.GetFullPath(destination),
                          StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("  already running from the install directory; not copying over itself");
            return;
        }

        File.Copy(source, destination, overwrite: true);
        Console.WriteLine($"  program      {destination}");
    }

    /// <summary>
    /// Registers the add-in mstsc.exe loads. Which bitness that is depends on the Remote
    /// Desktop client, not on us, so the path registered is the one matching this OS — a
    /// 64-bit Windows runs the 64-bit mstsc.exe.
    /// </summary>
    private static void RegisterAddIn(string installDirectory)
    {
        string architecture = Environment.Is64BitOperatingSystem ? "x64" : "x86";
        string plugin = Path.Combine(installDirectory, architecture, "ScanBridge.DvcPlugin.dll");

        if (!File.Exists(plugin))
        {
            Console.Error.WriteLine($"  the add-in was not laid down at {plugin}; not registering it");
            return;
        }

        PluginRegistration.Register(plugin);
        Console.WriteLine($"  registered   HKCU\\{PluginRegistration.KeyPath}");
    }

    private static void RegisterAutoStart(string executable)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException("Cannot open the Run key.");

        key.SetValue(RunValueName, $"\"{executable}\"", RegistryValueKind.String);
        Console.WriteLine("  starts with Windows");
    }

    /// <summary>
    /// Removes the auto-start entry. Also called during an install where the user cleared that
    /// option, so that re-running the installer can turn it off as well as on — an installer
    /// whose checkboxes only work in one direction is worse than one with no checkboxes.
    /// </summary>
    private static bool RemoveAutoStart(bool quiet)
    {
        try
        {
            using RegistryKey? run = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            run?.DeleteValue(RunValueName, throwOnMissingValue: false);
            return true;
        }
        catch (Exception ex)
        {
            if (!quiet) Console.Error.WriteLine($"  could not remove the auto-start entry: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// A Start Menu entry always, a Desktop one only if asked.
    ///
    /// The Start Menu is not optional because it is how somebody finds a program they installed
    /// weeks ago; the desktop is a matter of taste. Neither failing is worth aborting an install
    /// that has otherwise succeeded — the product works without them, it is just harder to
    /// launch — so both are reported rather than thrown.
    /// </summary>
    private static void CreateShortcuts(string installedExe, bool desktopShortcut)
    {
        const string description = "Makes this PC's scanner usable inside a Remote Desktop session";

        try
        {
            string startMenu = Shortcuts.UserStartMenu(ShortcutName);
            Shortcuts.Create(startMenu, installedExe, description);
            Console.WriteLine($"  start menu   {startMenu}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"  could not create the Start Menu shortcut: {ex.Message}");
        }

        string desktop = Shortcuts.UserDesktop(ShortcutName);

        if (!desktopShortcut)
        {
            // Cleared on a re-install: take the old one away rather than leaving a shortcut the
            // user has just asked not to have.
            if (Shortcuts.Remove(desktop)) Console.WriteLine("  removed the desktop shortcut");
            return;
        }

        try
        {
            Shortcuts.Create(desktop, installedExe, description);
            Console.WriteLine($"  desktop      {desktop}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"  could not create the desktop shortcut: {ex.Message}");
        }
    }
}
