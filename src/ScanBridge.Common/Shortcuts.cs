using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.Versioning;

namespace ScanBridge.Common;

/// <summary>
/// Start Menu and Desktop shortcuts.
///
/// Until 21 August 2026 the installer created none, which meant that once the tray application
/// was closed there was no way to start it again short of finding the executable inside
/// %LocalAppData%\Programs. Nothing in the product told you where that was. An application with
/// no Start Menu entry is one most people conclude is not installed.
///
/// Written through IShellLink rather than by emitting .lnk bytes: the format is undocumented,
/// and the shell is the only thing that agrees with itself about it. There is no managed API
/// for this in .NET, so the two COM interfaces are declared here. That is the whole reason this
/// file exists.
/// </summary>
[SupportedOSPlatform("windows")]
public static class Shortcuts
{
    /// <summary>Per-user Start Menu, for the client, which is installed per user.</summary>
    public static string UserStartMenu(string name) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Programs), name + ".lnk");

    /// <summary>Per-user Desktop.</summary>
    public static string UserDesktop(string name) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), name + ".lnk");

    /// <summary>
    /// All-users Start Menu, for the server, which is installed once by an administrator and
    /// belongs to the machine rather than to whoever happened to run the installer. Writing it
    /// into that administrator's own profile would hide it from everybody else.
    /// </summary>
    public static string CommonStartMenu(string name) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), name + ".lnk");

    public static string CommonDesktop(string name) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), name + ".lnk");

    /// <summary>
    /// Creates or replaces a shortcut. The icon comes from the target executable itself, which
    /// is the only arrangement that cannot break: an icon file beside the program is a file that
    /// can go missing, and a single-file executable has nothing beside it anyway.
    /// </summary>
    public static void Create(string linkPath, string targetExe, string description, string? arguments = null)
    {
        if (!File.Exists(targetExe))
            throw new FileNotFoundException("Cannot create a shortcut to a file that is not there.", targetExe);

        Directory.CreateDirectory(Path.GetDirectoryName(linkPath)!);

        var link = (IShellLinkW)new ShellLink();
        link.SetPath(targetExe);
        link.SetWorkingDirectory(Path.GetDirectoryName(targetExe)!);
        link.SetIconLocation(targetExe, 0);

        // Truncated rather than rejected: this is the tooltip, and a description slightly too
        // long is not a reason to fail an install.
        link.SetDescription(description.Length > 259 ? description[..259] : description);

        if (!string.IsNullOrEmpty(arguments)) link.SetArguments(arguments);

        ((IPersistFile)link).Save(linkPath, fRemember: true);
        Marshal.FinalReleaseComObject(link);
    }

    /// <summary>Deletes a shortcut if it is there. Never throws for one that is not.</summary>
    public static bool Remove(string linkPath)
    {
        try
        {
            if (!File.Exists(linkPath)) return false;
            File.Delete(linkPath);
            return true;
        }
        catch (Exception)
        {
            // A shortcut that will not delete is not worth failing an uninstall over; the
            // caller reports what was left behind.
            return false;
        }
    }

    // ---------------------------------------------------------------- COM

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink { }

    [ComImport,
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown),
     Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder file,
                     int maxPath, nint findData, int flags);
        void GetIDList(out nint idList);
        void SetIDList(nint idList);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder name, int maxName);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder dir, int maxPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder args, int maxArgs);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int showCmd);
        void SetShowCmd(int showCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder iconPath,
                             int iconPathLength, out int iconIndex);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int iconIndex);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string relative, int reserved);
        void Resolve(nint hwnd, int flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }
}
