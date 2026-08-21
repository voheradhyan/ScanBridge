using System.Security.AccessControl;
using System.Security.Principal;
using ScanBridge.Common;
using Xunit;

namespace ScanBridge.Tests.Unit;

/// <summary>
/// The permissions the server installer puts on the directory it installs into.
///
/// This exists because the installer had none. It called Directory.CreateDirectory and let the
/// folder inherit whatever its parent granted, which under %ProgramFiles% is fine and elsewhere
/// is not: a second volume's root commonly grants Authenticated Users Modify, and on a Session
/// Host that is every user of this product. The directory holds a service that runs as
/// LocalSystem and restarts itself on failure, so being able to replace a file in it is being
/// able to run code as SYSTEM.
///
/// Asserted against the policy rather than against a real directory on purpose. Applying it
/// requires administrator rights - setting an owner does - and a security rule that can only be
/// verified by an elevated test is one that will not be verified.
/// </summary>
public sealed class SecureDirectoryTests
{
    private static readonly SecurityIdentifier Administrators =
        new(WellKnownSidType.BuiltinAdministratorsSid, null);

    private static readonly SecurityIdentifier System =
        new(WellKnownSidType.LocalSystemSid, null);

    private static readonly SecurityIdentifier Users =
        new(WellKnownSidType.BuiltinUsersSid, null);

    private static List<FileSystemAccessRule> RulesFor(SecurityIdentifier who) =>
        SecureDirectory.ServicePolicy()
            .GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .Where(rule => rule.IdentityReference.Equals(who))
            .ToList();

    [Fact]
    public void OrdinaryUsersCannotWriteIntoIt()
    {
        // The whole point. Users must be able to run what is in here - the session agent is this
        // executable started in each user's own session - and must not be able to change it.
        List<FileSystemAccessRule> rules = RulesFor(Users);

        Assert.NotEmpty(rules);

        foreach (FileSystemAccessRule rule in rules)
        {
            Assert.Equal(AccessControlType.Allow, rule.AccessControlType);

            FileSystemRights granted = rule.FileSystemRights;

            Assert.True(granted.HasFlag(FileSystemRights.ReadAndExecute),
                "Users must be able to execute the service binary in their own session.");

            foreach (FileSystemRights forbidden in new[]
                     {
                         FileSystemRights.Write,
                         FileSystemRights.WriteData,
                         FileSystemRights.CreateFiles,
                         FileSystemRights.AppendData,
                         FileSystemRights.Delete,
                         FileSystemRights.ChangePermissions,
                         FileSystemRights.TakeOwnership,
                         FileSystemRights.FullControl,
                     })
            {
                Assert.False(granted.HasFlag(forbidden),
                    $"Users must not be granted {forbidden} on a directory SYSTEM executes from.");
            }
        }
    }

    [Fact]
    public void InheritanceIsBrokenRatherThanAddedTo()
    {
        // Without this the rules above are added on top of whatever the parent already granted,
        // which is precisely the permission being removed. It is the single line that makes the
        // rest of the policy mean anything.
        Assert.True(SecureDirectory.ServicePolicy()
                        .AreAccessRulesProtected,
                    "Access rules must be protected, or the parent's grants survive.");
    }

    [Fact]
    public void AdministratorsAndSystemKeepFullControl()
    {
        foreach (SecurityIdentifier who in new[] { Administrators, System })
        {
            Assert.Contains(RulesFor(who), rule =>
                rule.AccessControlType == AccessControlType.Allow &&
                rule.FileSystemRights.HasFlag(FileSystemRights.FullControl));
        }
    }

    [Fact]
    public void AdministratorsOwnIt()
    {
        // An owner can rewrite permissions whatever the rules say, so leaving the installing
        // account as owner would hand back exactly what was just taken away.
        Assert.Equal(Administrators,
                     SecureDirectory.ServicePolicy().GetOwner(typeof(SecurityIdentifier)));
    }

    [Fact]
    public void RulesApplyToWhatIsCreatedInsideItLater()
    {
        // The service binary is written after the directory is hardened, so a policy that did
        // not inherit downward would protect an empty folder and nothing in it.
        foreach (FileSystemAccessRule rule in RulesFor(Users))
        {
            Assert.True(rule.InheritanceFlags.HasFlag(InheritanceFlags.ObjectInherit),
                        "Files created later must inherit these rules.");
        }
    }

    [Theory]
    [InlineData(@"C:\Program Files\ScanBridge", true)]
    [InlineData(@"C:\Program Files (x86)\ScanBridge", true)]
    [InlineData(@"D:\Apps\ScanBridge", false)]
    [InlineData(@"C:\ScanBridge", false)]
    [InlineData(@"C:\Program FilesEvil\ScanBridge", false)]
    public void RecognisesWhetherWindowsIsAlreadyProtectingThePath(string path, bool expected)
    {
        // The last case is the one worth having: a prefix match without the separator would call
        // "C:\Program FilesEvil" a Program Files directory and skip the warning.
        Assert.Equal(expected, SecureDirectory.IsUnderProgramFiles(path));
    }
}
