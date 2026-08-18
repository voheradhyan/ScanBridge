using ScanBridge.Common;
using Xunit;

namespace ScanBridge.Tests.Unit;

/// <summary>
/// Where the product puts things.
///
/// These exist because of a fault that no test could have caught by exercising the code that
/// broke. Settings were kept under %ProgramData%\ScanBridge, and AgentConfig.Save called
/// AppPaths.EnsureDirectories() before writing — which looked exhaustive and was not: since
/// logs moved to the user's profile, that overload deliberately creates only the per-user
/// directories and leaves ProgramData to the service installer.
///
/// So on any machine where the server installer had never run — every PC that has only the
/// client — selecting a scanner threw "Could not find a part of the path
/// 'C:\ProgramData\ScanBridge\config.json.tmp'". Saving a setting had never once worked there,
/// through a green build, a tagged release, and an upload.
///
/// The invariants below are what actually went wrong, stated so they cannot go wrong quietly
/// again: the settings file belongs to the user, and the call Save makes must create the
/// directory Save writes into. Each fails against the code as it was.
/// </summary>
public sealed class AppPathsTests
{
    [Fact]
    public void SettingsBelongToTheUserNotTheMachine()
    {
        string perUser = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string machineWide = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

        Assert.StartsWith(perUser, AppPaths.ConfigFile, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(machineWide, AppPaths.ConfigFile, StringComparison.OrdinalIgnoreCase);

        // Not a stylistic point. Everything that reads or writes this file runs as a signed-in
        // user, and on a Session Host several of them run at once: a shared file means the first
        // person to choose a scanner chooses it for everybody, and owns the file so that nobody
        // else can change it back.
        Assert.StartsWith(perUser, AppPaths.LogDirectory, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheDirectoryTheConfigIsWrittenIntoIsOneEnsureDirectoriesCreates()
    {
        // The precise shape of the bug: Save() calls this overload and then writes beside
        // ConfigFile. If the two ever disagree again, saving fails at the moment a user changes
        // a setting, which is the worst place to find out.
        AppPaths.EnsureDirectories();

        string configDirectory = Path.GetDirectoryName(AppPaths.ConfigFile)!;
        Assert.True(Directory.Exists(configDirectory),
            $"EnsureDirectories() did not create {configDirectory}, which is where Save() writes.");
    }

    [Fact]
    public void SavingASettingWorksOnAMachineThatHasOnlyEverRunTheClient()
    {
        // The end-to-end version of the same thing, and the one that reproduces the report: a
        // round trip through the real file, on the real path, with no server installer having
        // run. This threw DirectoryNotFoundException before the fix.
        AgentConfig original = AgentConfig.Load();
        string previous = original.DefaultScannerId;

        try
        {
            original.DefaultScannerId = "wia:test-scanner-" + Guid.NewGuid().ToString("N")[..8];
            original.Save();

            Assert.Equal(original.DefaultScannerId, AgentConfig.Load().DefaultScannerId);
        }
        finally
        {
            original.DefaultScannerId = previous;
            original.Save();
        }
    }

    [Fact]
    public void TheMachineWideRootIsStillThereForTheService()
    {
        // Moving settings out of ProgramData must not move the service's own log with them: it
        // runs as LocalSystem, belongs to no user, and has no profile to write into.
        string machineWide = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

        Assert.StartsWith(machineWide, AppPaths.MachineLogDirectory, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(machineWide, AppPaths.LegacyConfigFile, StringComparison.OrdinalIgnoreCase);
    }
}
