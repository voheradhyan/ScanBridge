using System.Runtime.Versioning;
using Microsoft.Win32;

namespace ScanBridge.Common;

/// <summary>
/// Registers the DVC plugin with the Remote Desktop client.
///
/// mstsc.exe discovers add-ins at connect time from
/// HKCU\Software\Microsoft\Terminal Server Client\Default\AddIns\&lt;name&gt;, where the "Name"
/// value is the full path to a DLL exporting VirtualChannelGetInstance. This lives under
/// HKCU, so registration needs no elevation and is per-user — which is correct, since the
/// agent and its scanners are per-user too.
/// </summary>
[SupportedOSPlatform("windows")]
public static class PluginRegistration
{
    private const string AddInsPath = @"Software\Microsoft\Terminal Server Client\Default\AddIns";
    private const string AddInName = "ScanBridge";
    private const string PolicyPath = @"Software\Policies\Microsoft\Windows NT\Terminal Services\Client";

    public static string KeyPath => $@"{AddInsPath}\{AddInName}";

    public static void Register(string pluginDllPath)
    {
        if (!File.Exists(pluginDllPath))
            throw new FileNotFoundException("The DVC plugin DLL was not found.", pluginDllPath);

        using RegistryKey key = Registry.CurrentUser.CreateSubKey(KeyPath, writable: true)
            ?? throw new InvalidOperationException($"Cannot create HKCU\\{KeyPath}.");

        key.SetValue("Name", pluginDllPath, RegistryValueKind.String);
        Log.Logger.Information("Registered the RDP add-in: {Path}", pluginDllPath);
    }

    public static void Unregister()
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(KeyPath, throwOnMissingSubKey: false);
        }
        catch (UnauthorizedAccessException ex)
        {
            Log.Logger.Warning(ex, "Could not remove the RDP add-in registration.");
        }
    }

    public static bool IsRegistered() => RegisteredPath() is { } path && File.Exists(path);

    public static string? RegisteredPath()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(KeyPath);
        return key?.GetValue("Name") as string;
    }

    /// <summary>
    /// Checks the group policy that can stop mstsc.exe loading add-ins at all.
    ///
    /// This is the single most common cause of "everything is installed and nothing happens":
    /// the plugin never gets loaded, so the server sees no channel and reports no scanner.
    /// Returns null when policy permits us, or a description of what is blocking.
    /// </summary>
    public static string? PolicyBlockReason()
    {
        using RegistryKey? machine = Registry.LocalMachine.OpenSubKey(PolicyPath);
        using RegistryKey? user = Registry.CurrentUser.OpenSubKey(PolicyPath);

        foreach (RegistryKey? key in new[] { machine, user })
        {
            if (key is null) continue;

            if (key.GetValue("DisableAddIns") is int disabled && disabled != 0)
                return @"Group policy 'DisableAddIns' is set under " + PolicyPath +
                       ". The Remote Desktop client will not load any add-in until it is cleared.";

            if (key.GetValue("AllowedAddIns") is string allowed && allowed.Length > 0
                && !allowed.Contains(AddInName, StringComparison.OrdinalIgnoreCase))
            {
                return $"Group policy 'AllowedAddIns' is an allow-list that does not include " +
                       $"'{AddInName}'. Add it, or the add-in will not load.";
            }
        }

        return null;
    }
}
