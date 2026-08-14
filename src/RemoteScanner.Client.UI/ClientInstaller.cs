using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32;
using RemoteScanner.Common;

namespace RemoteScanner.Client.UI;

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
    private const string RunValueName = "RemoteScanner";

    /// <summary>Per user by default: %LocalAppData%\Programs\RemoteScanner.</summary>
    public static string DefaultDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs", "RemoteScanner");

    public static int Install(string? installDirectory, bool startNow = true)
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
            Console.WriteLine($"Installing Remote Scanner to {target}");

            StopRunningCopy();

            Directory.CreateDirectory(target);
            string installedExe = Path.Combine(target, "RemoteScanner.Client.exe");
            CopySelf(installedExe);

            // The RDP add-in, both bitnesses: mstsc.exe decides which it loads, not us.
            foreach (string architecture in new[] { "x64", "x86" })
            {
                string resource = $"{architecture}/RemoteScanner.DvcPlugin.dll";
                if (!EmbeddedPayload.Contains(resource)) continue;

                string file = Path.Combine(target, architecture, "RemoteScanner.DvcPlugin.dll");
                string hash = EmbeddedPayload.Extract(resource, file);
                Console.WriteLine($"  rdp add-in   {file}  [{hash}]");
            }

            // The 32-bit scan host, which cannot be a role of this 64-bit application: a
            // scanner driver's bitness decides the bitness of the process that loads it.
            const string hostResource = "x86/RemoteScanner.ScanHost.exe";
            if (EmbeddedPayload.Contains(hostResource))
            {
                string file = Path.Combine(target, "x86", "RemoteScanner.ScanHost.exe");
                string hash = EmbeddedPayload.Extract(hostResource, file);
                Console.WriteLine($"  32-bit host  {file}  [{hash}]");
            }
            else
            {
                Console.WriteLine("  32-bit host  not carried by this build; 32-bit-only scanner");
                Console.WriteLine("               drivers will not be reachable.");
            }

            RegisterAddIn(target);
            RegisterAutoStart(installedExe);

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

        try
        {
            using RegistryKey? run = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            run?.DeleteValue(RunValueName, throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"  could not remove the auto-start entry: {ex.Message}");
            problems++;
        }

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

    private static void StopRunningCopy()
    {
        foreach (Process process in Process.GetProcessesByName("RemoteScanner.Client"))
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
        string plugin = Path.Combine(installDirectory, architecture, "RemoteScanner.DvcPlugin.dll");

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
}
