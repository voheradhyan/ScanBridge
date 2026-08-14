using System.Runtime.Versioning;
using Microsoft.Win32;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace RemoteScanner.Common;

/// <summary>
/// Structured logging for the managed components.
///
/// The level is read from HKLM\SOFTWARE\RemoteScanner\LogLevel, the same value the native
/// logger in rs_log.h reads, so one setting controls the whole stack and the diagnostics
/// report can merge both sets of files.
///
/// Nothing here ever accepts page pixel data. Sizes, page counts and hashes are logged;
/// document content is not, at any level.
/// </summary>
[SupportedOSPlatform("windows")]
public static class Log
{
    private static readonly LoggingLevelSwitch LevelSwitch = new(LogEventLevel.Information);
    private static ILogger _logger = Serilog.Core.Logger.None;
    private static bool _initialised;

    public const string EventLogSource = "RemoteScanner";

    /// <param name="component">Short tag naming the log file, e.g. "agent" or "sessionagent".</param>
    /// <param name="useEventLog">
    /// Services log errors to the Windows Event Log as well; interactive components do not,
    /// because writing to it from a non-elevated process fails when the source is missing.
    /// </param>
    public static void Initialize(string component, bool useEventLog = false)
    {
        if (_initialised) return;
        _initialised = true;

        AppPaths.EnsureDirectories();
        LevelSwitch.MinimumLevel = ReadConfiguredLevel();

        var configuration = new LoggerConfiguration()
            .MinimumLevel.ControlledBy(LevelSwitch)
            .Enrich.WithProperty("Component", component)
            .Enrich.WithProperty("Pid", Environment.ProcessId)
            .WriteTo.File(
                Path.Combine(AppPaths.LogDirectory, $"{component}-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                fileSizeLimitBytes: 32L * 1024 * 1024,
                rollOnFileSizeLimit: true,
                shared: true,
                outputTemplate:
                    "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] [{Pid}] {Message:lj}{NewLine}{Exception}");

        if (useEventLog && OperatingSystem.IsWindows())
        {
            try
            {
                configuration = configuration.WriteTo.EventLog(
                    source: EventLogSource, manageEventSource: false,
                    restrictedToMinimumLevel: LogEventLevel.Error);
            }
            catch (Exception)
            {
                // The installer registers the event source; if it is absent we still have
                // the file sink, and failing to start over a logging sink would be absurd.
            }
        }

        _logger = configuration.CreateLogger();
        _logger.Information("{Component} starting (level {Level})", component, LevelSwitch.MinimumLevel);
    }

    public static ILogger Logger => _logger;

    public static void SetLevel(LogEventLevel level) => LevelSwitch.MinimumLevel = level;

    public static void Shutdown()
    {
        (_logger as IDisposable)?.Dispose();
        _logger = Serilog.Core.Logger.None;
        _initialised = false;
    }

    private static LogEventLevel ReadConfiguredLevel()
    {
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\RemoteScanner");
            return (key?.GetValue("LogLevel") as string) switch
            {
                "Trace" => LogEventLevel.Verbose,
                "Debug" => LogEventLevel.Debug,
                "Information" => LogEventLevel.Information,
                "Warning" => LogEventLevel.Warning,
                "Error" => LogEventLevel.Error,
                "Off" => LogEventLevel.Fatal,
                _ => LogEventLevel.Information,
            };
        }
        catch (Exception)
        {
            return LogEventLevel.Information;
        }
    }
}
