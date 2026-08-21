using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.ServiceProcess;
using Microsoft.Win32;
using ScanBridge.Common;

namespace ScanBridge.Server;

/// <summary>
/// Installs and removes the server half, from inside the executable being installed.
///
/// This used to be a PowerShell script sitting beside a folder of two hundred files. One
/// executable that installs itself is not merely tidier: there is no script that can be run
/// against the wrong payload, no folder that can be half-copied, and no execution policy to
/// argue with. It also means the thing doing the installing is the same build as the thing
/// being installed, which a separate script can never guarantee.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ServerInstaller
{
    public const string ServiceName = "ScanBridge";
    private const string DisplayName = "ScanBridge Redirection";
    private const string Description =
        "Makes scanners attached to the client PC available to applications in an RDP session.";

    /// <summary>
    /// Where the data source must go for each bitness. TWAIN managers look in these two places
    /// and nowhere else, and both are installed on purpose: a 32-bit application can only load
    /// a 32-bit data source, and most line-of-business software is still 32-bit.
    /// </summary>
    private static (string Directory, string Resource)[] TwainTargets() => new[]
    {
        (Path.Combine(Environment.SystemDirectory, "..", "twain_64", "ScanBridge"), "x64/ScanBridge.ds"),
        (Path.Combine(Environment.SystemDirectory, "..", "twain_32", "ScanBridge"), "x86/ScanBridge.ds"),
    };

    /// <summary>What the Start Menu entry is called.</summary>
    private const string ShortcutName = "ScanBridge Server";

    /// <summary>The subkey under HKLM ...\Uninstall that Apps &amp; Features reads.</summary>
    private const string UninstallKey = "ScanBridge Server";

    /// <summary>
    /// Shortcuts for the machine, not for whoever ran the installer.
    ///
    /// The server half is installed once, elevated, and belongs to the host. Putting its Start
    /// Menu entry in the installing administrator's own profile would hide it from every other
    /// administrator who later wonders what is running — which is exactly the person who needs
    /// to find it. Neither shortcut failing is worth aborting an install that has otherwise put
    /// a working service on the machine.
    ///
    /// The shortcut points at the installed executable, which with no arguments shows the
    /// manage-and-remove window rather than trying to run as a service.
    /// </summary>
    private static void CreateShortcuts(string installedExe, bool desktopShortcut)
    {
        const string description = "Manage or remove ScanBridge scanner redirection on this server";

        try
        {
            string startMenu = Shortcuts.CommonStartMenu(ShortcutName);
            Shortcuts.Create(startMenu, installedExe, description);
            Console.WriteLine($"  start menu   {startMenu}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"  could not create the Start Menu shortcut: {ex.Message}");
        }

        string desktop = Shortcuts.CommonDesktop(ShortcutName);

        if (!desktopShortcut)
        {
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

    /// <summary>Where Windows says this is installed, or null if it is not.</summary>
    public static string? InstalledDirectory() =>
        AddRemovePrograms.InstalledLocation(UninstallKey, machineWide: true);

    /// <summary>
    /// What the setup window shows and calls back into.
    ///
    /// Unlike the client, this half must be elevated, so the window is allowed to re-launch the
    /// executable and let Windows raise its own consent prompt. Asking three questions and then
    /// failing on "access denied" is the behaviour that arrangement exists to avoid.
    /// </summary>
    public static Setup.SetupPlan SetupPlan() => new()
    {
        ProductName = "ScanBridge Server",
        Summary = "Installs the service and both TWAIN data sources, so applications in a "
                + "Remote Desktop session can use a scanner attached to the user's own PC.",
        DefaultDirectory = AppPaths.InstallDirectory,
        OffersStartWithWindows = false,   // it is a service; it starts with the machine
        RequiresElevation = true,
        ExistingInstall = InstalledDirectory,
        ExistingVersion = () => AddRemovePrograms.InstalledVersion(UninstallKey, machineWide: true),

        Install = (choices, writer) => Setup.SetupHost.Capturing(writer, () =>
            Install(choices.Directory, choices.DesktopShortcut)),

        Uninstall = writer => Setup.SetupHost.Capturing(writer, Uninstall),

        RelaunchElevated = choices => RelaunchElevated(choices.Directory, choices.DesktopShortcut),
    };

    /// <summary>
    /// Starts this executable again, elevated, carrying the choices as switches.
    ///
    /// ShellExecute with the "runas" verb is what raises the consent prompt; there is no way to
    /// gain rights inside the running process. A user who declines produces ERROR_CANCELLED,
    /// which is a refusal rather than a failure and is reported as such.
    /// </summary>
    private static bool RelaunchElevated(string directory, bool desktopShortcut)
    {
        string? self = Environment.ProcessPath;
        if (self is null) return false;

        string arguments = $"--install --to \"{directory.TrimEnd('\\')}\"";
        if (desktopShortcut) arguments += " --desktop-shortcut";

        try
        {
            var started = Process.Start(new ProcessStartInfo(self, arguments)
            {
                UseShellExecute = true,
                Verb = "runas",
            });

            // Waited for rather than fired and forgotten, so the window that launched it does
            // not disappear before the install it asked for has actually happened.
            started?.WaitForExit();
            return started is not null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // ERROR_CANCELLED: the consent prompt was declined.
            return false;
        }
    }

    public static int Install(string? installDirectory, bool desktopShortcut = false)
    {
        if (!IsElevated())
        {
            Console.Error.WriteLine(
                "This has to run as an administrator: it installs a Windows service and writes\n" +
                "into the system TWAIN folders. Right-click it and choose 'Run as administrator'.");
            return 5;
        }

        string target = installDirectory ?? AppPaths.InstallDirectory;

        try
        {
            Console.WriteLine($"Installing ScanBridge to {target}");

            StopServiceAndAgents();

            Directory.CreateDirectory(target);
            string installedExe = Path.Combine(target, "ScanBridge.Server.exe");
            CopySelf(installedExe);

            foreach ((string directory, string resource) in TwainTargets())
            {
                string full = Path.GetFullPath(directory);
                string file = Path.Combine(full, "ScanBridge.ds");
                string hash = EmbeddedPayload.Extract(resource, file);
                Console.WriteLine($"  data source  {file}  [{hash}]");

                RemoveStaleTopLevelCopy(full);
            }

            RegisterService(installedExe);
            RegisterEventLogSource();

            using (RegistryKey key = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\ScanBridge"))
                key.SetValue("LogLevel", "Information", RegistryValueKind.String);

            // Only the service writes here; it runs as LocalSystem, so the default permissions
            // on ProgramData are correct and nothing needs widening.
            Directory.CreateDirectory(AppPaths.MachineLogDirectory);

            StartService();

            CreateShortcuts(installedExe, desktopShortcut);

            AddRemovePrograms.Register(UninstallKey, "ScanBridge Server", installedExe, target,
                                       machineWide: true);
            Console.WriteLine($"  listed in    Apps & Features as ScanBridge Server {AddRemovePrograms.Version}");

            Console.WriteLine();
            Console.WriteLine("Installed. Applications in a Remote Desktop session will list a scanner");
            Console.WriteLine("named 'ScanBridge (<the client PC>)' once a client connects with the");
            Console.WriteLine("add-in registered.");
            Console.WriteLine();
            Console.WriteLine("Sign out and back in before testing: the service starts a session agent at");
            Console.WriteLine("logon, and without one the driver has nothing to connect to.");
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
        if (!IsElevated())
        {
            Console.Error.WriteLine("This has to run as an administrator. Right-click it and choose " +
                                    "'Run as administrator'.");
            return 5;
        }

        int problems = 0;

        try
        {
            StopServiceAndAgents();
            Run("sc.exe", $"delete {ServiceName}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"  could not remove the service: {ex.Message}");
            problems++;
        }

        foreach ((string directory, _) in TwainTargets())
        {
            string full = Path.GetFullPath(directory);
            try
            {
                if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
                Console.WriteLine($"  removed {full}");
            }
            catch (Exception ex)
            {
                // Almost always a scanning application still holding the data source mapped.
                Console.Error.WriteLine(
                    $"  {full} is in use and was left in place ({ex.GetType().Name}). Close every " +
                    "scanning application and run this again.");
                problems++;
            }
        }

        try
        {
            Registry.LocalMachine.DeleteSubKeyTree(@"SOFTWARE\ScanBridge", throwOnMissingSubKey: false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"  could not remove the registry key: {ex.Message}");
            problems++;
        }

        foreach (string link in new[] { Shortcuts.CommonStartMenu(ShortcutName),
                                        Shortcuts.CommonDesktop(ShortcutName) })
        {
            if (Shortcuts.Remove(link)) Console.WriteLine($"  removed {link}");
        }

        AddRemovePrograms.Unregister(UninstallKey, machineWide: true);
        Console.WriteLine("  removed the Apps & Features entry");

        Console.WriteLine(problems == 0
            ? "\nRemoved. Users must sign out and back in for applications to stop listing the scanner."
            : "\nRemoved, with the problems noted above.");

        return problems == 0 ? 0 : 1;
    }

    // ------------------------------------------------------------------ pieces

    private static bool IsElevated()
        => new WindowsPrincipal(WindowsIdentity.GetCurrent())
            .IsInRole(WindowsBuiltInRole.Administrator);

    private static void StopServiceAndAgents()
    {
        using (var existing = ServiceController.GetServices()
                   .FirstOrDefault(s => s.ServiceName == ServiceName))
        {
            if (existing is not null)
            {
                try
                {
                    if (existing.Status != ServiceControllerStatus.Stopped)
                    {
                        existing.Stop();
                        existing.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"  could not stop the running service: {ex.Message}");
                }

                // sc delete is asynchronous; the files stay locked until every handle closes.
                Run("sc.exe", $"delete {ServiceName}");
                Thread.Sleep(2000);
            }
        }

        // Stopping the service does not stop the agents it started: they run in users' sessions
        // and outlive it. One left behind holds the pipe that session's data sources connect
        // to, while its channel back to the user's PC is dead — applications connect, are
        // authenticated, then wait for a reply that can never come. It looks exactly like the
        // scanner being switched off.
        foreach (Process agent in FindSessionAgents())
        {
            try
            {
                agent.Kill();
                agent.WaitForExit(5000);
                Console.WriteLine($"  stopped a leftover session agent (pid {agent.Id})");
            }
            catch (Exception)
            {
                // Another user's session. The service replaces it when that user reconnects.
            }
            finally
            {
                agent.Dispose();
            }
        }
    }

    /// <summary>
    /// Session agents by command line, not by image name: since the two roles share an
    /// executable, matching on the name would also kill this very process.
    /// </summary>
    private static IEnumerable<Process> FindSessionAgents()
    {
        foreach (Process process in Process.GetProcessesByName("ScanBridge.Server"))
        {
            if (process.Id == Environment.ProcessId) { process.Dispose(); continue; }
            yield return process;
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
    /// An installer before this one also dropped a copy directly in twain_32\ and twain_64\,
    /// one level above the per-vendor folder. A TWAIN 2.x manager scans both levels and lists
    /// the scanner once per copy, so the stray one is removed rather than refreshed — which
    /// also repairs a machine that had the earlier version.
    /// </summary>
    private static void RemoveStaleTopLevelCopy(string vendorDirectory)
    {
        string stale = Path.Combine(Path.GetDirectoryName(vendorDirectory)!, "ScanBridge.ds");
        if (!File.Exists(stale)) return;

        try
        {
            File.Delete(stale);
            Console.WriteLine($"  removed a duplicate at {stale}");
        }
        catch (Exception)
        {
            Console.Error.WriteLine(
                $"  {stale} is in use and could not be removed; close every scanning application " +
                "and run this again, or the scanner will be listed twice.");
        }
    }

    private static void RegisterService(string executable)
    {
        // LocalSystem is required: WTSQueryUserToken and CreateProcessAsUser need SeTcbPrivilege
        // to launch the per-session agent inside a user's session.
        Run("sc.exe", $"create {ServiceName} binPath= \"\\\"{executable}\\\"\" start= auto " +
                      $"obj= LocalSystem DisplayName= \"{DisplayName}\"");
        Run("sc.exe", $"description {ServiceName} \"{Description}\"");

        // Restart on failure rather than leaving sessions without a scanner until somebody
        // notices and reports it.
        Run("sc.exe", $"failure {ServiceName} reset= 86400 actions= restart/5000/restart/10000/restart/30000");
        Console.WriteLine($"  service      {ServiceName}");
    }

    private static void RegisterEventLogSource()
    {
        try
        {
            if (!EventLog.SourceExists(Log.EventLogSource))
                EventLog.CreateEventSource(Log.EventLogSource, "Application");
        }
        catch (Exception ex)
        {
            // Not fatal: the file log is the one that gets read in practice.
            Console.Error.WriteLine($"  event log source not registered: {ex.Message}");
        }
    }

    private static void StartService()
    {
        using var service = new ServiceController(ServiceName);
        service.Start();
        service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
        Console.WriteLine("  service started");
    }

    private static void Run(string program, string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo(program, arguments)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        }) ?? throw new InvalidOperationException($"Could not run {program}.");

        process.WaitForExit();
    }
}
