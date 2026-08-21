using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace ScanBridge.Common;

/// <summary>
/// Locks down a directory that a privileged service will later execute out of.
///
/// The server installs a Windows service that runs as LocalSystem, is configured to restart
/// itself on failure, and is also launched as every signed-in user by the session launcher. The
/// installer creates its directory with Directory.CreateDirectory and wrote nothing else: the
/// folder simply inherited whatever permissions its parent had.
///
/// Under %ProgramFiles% that is fine. But the setup window offers a folder browser, and a Remote
/// Desktop Session Host very often has a second volume for user data - whose root, on a freshly
/// formatted disk, grants Authenticated Users Modify. Install there and any user who can sign in
/// to that host, which is every user of this product by definition, can overwrite
/// ScanBridge.Server.exe. The next restart runs their file as SYSTEM.
///
/// So the directory is hardened explicitly rather than trusted to be somewhere safe. Then where
/// it is stops mattering, which is the property worth having: a rule that only holds in the
/// default location is one that fails exactly when somebody does something slightly unusual.
/// </summary>
[SupportedOSPlatform("windows")]
public static class SecureDirectory
{
    /// <summary>
    /// Administrators and SYSTEM may write; Users may read and execute; nobody inherits anything
    /// from the parent.
    ///
    /// Users need read and execute, not nothing: the session agent is this same executable
    /// started in each user's own session, so they have to be able to run it. What they must not
    /// be able to do is replace it.
    /// </summary>
    public static void HardenForService(string path)
    {
        var directory = new DirectoryInfo(path);
        if (!directory.Exists) throw new DirectoryNotFoundException(path);

        directory.SetAccessControl(ServicePolicy());
    }

    /// <summary>
    /// The rules, without applying them.
    ///
    /// Separated so the policy can be asserted in a test. Applying it needs administrator rights
    /// — setting an owner does — and a security rule that can only be checked by an elevated
    /// test is one that in practice never gets checked at all.
    /// </summary>
    public static DirectorySecurity ServicePolicy()
    {
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);

        var security = new DirectorySecurity();

        // The important line. Without protection this only adds rules on top of whatever the
        // parent already granted, which on a data volume is the very thing being removed.
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;

        security.AddAccessRule(new FileSystemAccessRule(
            administrators, FileSystemRights.FullControl, inherit, PropagationFlags.None,
            AccessControlType.Allow));

        security.AddAccessRule(new FileSystemAccessRule(
            system, FileSystemRights.FullControl, inherit, PropagationFlags.None,
            AccessControlType.Allow));

        security.AddAccessRule(new FileSystemAccessRule(
            users, FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None,
            AccessControlType.Allow));

        // Owner too: whoever owns a directory can rewrite its permissions regardless of the
        // rules above, so leaving a creating user as owner would hand back what was just taken.
        security.SetOwner(administrators);

        return security;
    }

    /// <summary>
    /// Is this path inside one of the Program Files trees, where Windows already protects it?
    ///
    /// Used only to decide whether to say something, not whether to proceed. The hardening above
    /// runs either way.
    /// </summary>
    public static bool IsUnderProgramFiles(string path)
    {
        string full = Path.GetFullPath(path).TrimEnd('\\');

        foreach (string? root in new[]
                 {
                     Environment.GetEnvironmentVariable("ProgramW6432"),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                 })
        {
            if (string.IsNullOrEmpty(root)) continue;

            string prefix = Path.GetFullPath(root).TrimEnd('\\') + "\\";
            if (full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }
}
