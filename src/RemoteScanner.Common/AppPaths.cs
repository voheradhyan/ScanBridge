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

    /// <summary>
    /// Where in-flight page data is spooled. Per-session so one user's scan is never visible
    /// in another's directory listing.
    /// </summary>
    public static string SpoolDirectory(uint sessionId) => Path.Combine(Root, "spool", sessionId.ToString());

    public static string ConfigFile { get; } = Path.Combine(Root, "config.json");

    public static string InstallDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "RemoteScanner");

    public static void EnsureDirectories(uint? sessionId = null)
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(LogDirectory);
        if (sessionId is { } id) Directory.CreateDirectory(SpoolDirectory(id));
    }

    /// <summary>
    /// Deletes spooled pages for a session. Scanned documents are business records; they do
    /// not outlive the job that produced them unless the user explicitly asked to keep one.
    /// </summary>
    public static void PurgeSpool(uint sessionId)
    {
        string directory = SpoolDirectory(sessionId);
        if (!Directory.Exists(directory)) return;

        foreach (string file in Directory.EnumerateFiles(directory))
        {
            try { File.Delete(file); }
            catch (IOException) { /* still mapped by a transfer in progress; it is delete-on-close anyway */ }
            catch (UnauthorizedAccessException) { }
        }
    }
}
