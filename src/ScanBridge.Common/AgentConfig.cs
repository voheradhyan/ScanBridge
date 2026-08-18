using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;
using ScanBridge.Protocol;

namespace ScanBridge.Common;

/// <summary>
/// User-visible settings, persisted as JSON under %LOCALAPPDATA%\ScanBridge\config.json.
///
/// These are defaults the agent applies when a remote application does not ask for something
/// specific. Anything the application *does* negotiate through TWAIN wins — a scanning app
/// that requests 600 dpi gets 600 dpi regardless of what is set here.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AgentConfig
{
    // ---- General
    public bool StartWithWindows { get; set; } = true;
    public bool MinimizeToTray { get; set; } = true;
    public bool AutoConnectScanners { get; set; } = true;
    public bool AutoReconnect { get; set; } = true;

    // ---- Scanner
    /// <summary>Scanner offered to remote applications first. Empty means "the first found".</summary>
    public string DefaultScannerId { get; set; } = string.Empty;

    /// <summary>Which stack to prefer when a device exposes both a TWAIN and a WIA driver.</summary>
    public ScannerInterface PreferredInterface { get; set; } = ScannerInterface.Twain;

    public int DefaultResolution { get; set; } = 300;
    public PixelType DefaultPixelType { get; set; } = PixelType.Rgb;
    public PaperSize DefaultPaperSize { get; set; } = PaperSize.Auto;

    // ---- Connection
    public int JpegQuality { get; set; } = 85;
    public int CreditWindowFrames { get; set; } = Wire.DefaultCreditWindow;
    public int ConnectTimeoutSeconds { get; set; } = 10;
    public int RetryCount { get; set; } = 5;

    // ---- Diagnostics
    public string LogLevel { get; set; } = "Information";
    public bool DiagnosticMode { get; set; }

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static AgentConfig Load()
    {
        try
        {
            if (!File.Exists(AppPaths.ConfigFile))
            {
                // Settings moved out of ProgramData and into the user's profile on 18 August
                // 2026. An installation from before that has its file in the old place; read it
                // once and write it back to the new one, so nobody silently loses the scanner
                // they had chosen. The old file is left alone rather than deleted - it may
                // belong to another user on a Session Host, and this process may not be able to
                // remove it anyway.
                if (!File.Exists(AppPaths.LegacyConfigFile)) return new AgentConfig();

                var migrated = JsonSerializer.Deserialize<AgentConfig>(
                    File.ReadAllText(AppPaths.LegacyConfigFile), Options) ?? new AgentConfig();
                migrated.Save();
                return migrated;
            }

            string json = File.ReadAllText(AppPaths.ConfigFile);
            return JsonSerializer.Deserialize<AgentConfig>(json, Options) ?? new AgentConfig();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A corrupt or unreadable config must not stop scanner redirection working.
            Log.Logger.Warning(ex, "Could not read {Path}; using defaults.", AppPaths.ConfigFile);
            return new AgentConfig();
        }
    }

    public void Save()
    {
        AppPaths.EnsureDirectories();

        // Written via a temporary file so a crash mid-write cannot leave a truncated config.
        string temporary = AppPaths.ConfigFile + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this, Options));
        File.Move(temporary, AppPaths.ConfigFile, overwrite: true);
    }

    /// <summary>Turns the stored defaults into a settings block for a scan.</summary>
    public ScanSettings ToScanSettings() => ScanSettings.Default with
    {
        Resolution = DefaultResolution,
        PixelType = DefaultPixelType,
        PaperSize = DefaultPaperSize,
        JpegQuality = JpegQuality,
    };
}
