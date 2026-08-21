using System.IO;
using System.Runtime.Versioning;

namespace ScanBridge.Setup;

/// <summary>
/// Everything the setup window needs to know about the half being installed, and the two things
/// it needs to be able to do.
///
/// The window itself knows nothing about services, registry keys or TWAIN folders. It collects
/// choices and calls back. That keeps the client's rules (never elevated, per user) and the
/// server's (must be elevated, machine-wide) where they already live and are already explained,
/// instead of splitting them across a dialog.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SetupPlan
{
    public required string ProductName { get; init; }

    /// <summary>One sentence under the title. What this half does, in the user's terms.</summary>
    public required string Summary { get; init; }

    public required string DefaultDirectory { get; init; }

    /// <summary>Offered only by the client: the server half runs as a service instead.</summary>
    public bool OffersStartWithWindows { get; init; }

    /// <summary>
    /// True for the server. When the process is not already elevated the window does not attempt
    /// the install itself — it re-launches with the chosen options and lets Windows raise the
    /// consent prompt, because an installer that fails on the last step with "access denied"
    /// after asking you three questions is worse than one that asks for rights up front.
    /// </summary>
    public bool RequiresElevation { get; init; }

    /// <summary>Already installed? Returns the location, or null.</summary>
    public required Func<string?> ExistingInstall { get; init; }

    public required Func<string?> ExistingVersion { get; init; }

    /// <summary>
    /// Performs the install. Runs off the UI thread. Anything written to the returned writer
    /// appears in the window, so this is the same text the command-line install prints.
    /// </summary>
    public required Func<SetupChoices, TextWriter, int> Install { get; init; }

    public required Func<TextWriter, int> Uninstall { get; init; }

    /// <summary>
    /// Re-launches this executable elevated with the choices as switches. Only used when
    /// RequiresElevation is set and the process is not elevated.
    /// </summary>
    public Func<SetupChoices, bool>? RelaunchElevated { get; init; }
}

/// <summary>What the person chose.</summary>
public sealed record SetupChoices(string Directory, bool DesktopShortcut, bool StartWithWindows);
