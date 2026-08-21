using System.Diagnostics;
using System.Runtime.Versioning;

namespace ScanBridge.Common;

/// <summary>
/// Which process is holding a file open.
///
/// Exists because "Access to the path is denied" is a true statement that helps nobody. Two of
/// the files this product installs are loaded into processes it does not own — the RDP add-in
/// lives inside mstsc.exe, the data source inside whatever application is scanning — and Windows
/// will not replace a loaded image. The install then fails halfway with a message that names
/// neither the file nor the reason, and the obvious next move (run it as administrator) does not
/// help, because rights were never the problem.
/// </summary>
[SupportedOSPlatform("windows")]
public static class FileLock
{
    /// <summary>
    /// A fragment to append to an error message: " (mstsc.exe has it open)", or empty when
    /// nothing can be identified.
    ///
    /// Best effort by design. Reading another process's module list can fail for reasons that
    /// are not interesting here — a process exiting mid-enumeration, a service in another
    /// session, a bitness mismatch — and a diagnostic that throws while explaining a failure is
    /// worse than one that says nothing.
    /// </summary>
    public static string DescribeHolders(string path)
    {
        List<string> names = Holders(path);
        if (names.Count == 0) return string.Empty;

        string list = string.Join(", ", names);
        string advice = names.Any(n => n.StartsWith("mstsc", StringComparison.OrdinalIgnoreCase))
            ? " — sign out of your Remote Desktop session and close it, then install again"
            : " — close it and install again";

        return $" ({list} has it open{advice})";
    }

    /// <summary>Distinct process names with this exact file loaded as a module.</summary>
    public static List<string> Holders(string path)
    {
        var found = new List<string>();

        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception) { return found; }

        foreach (Process process in Process.GetProcesses())
        {
            try
            {
                foreach (ProcessModule module in process.Modules)
                {
                    if (!string.Equals(module.FileName, full, StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (!found.Contains(process.ProcessName, StringComparer.OrdinalIgnoreCase))
                        found.Add(process.ProcessName + ".exe");
                    break;
                }
            }
            catch (Exception)
            {
                // Not ours to inspect, or it exited while we looked. Either way, not a holder we
                // can report, and not a reason to stop looking at the rest.
            }
            finally
            {
                process.Dispose();
            }
        }

        return found;
    }
}
