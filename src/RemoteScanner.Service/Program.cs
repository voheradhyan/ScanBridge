using System.Runtime.Versioning;
using System.ServiceProcess;
using RemoteScanner.Common;
using RemoteScanner.Rdp;
using CommonLog = RemoteScanner.Common.Log;

namespace RemoteScanner.Service;

/// <summary>
/// The server-side control service.
///
/// It does one job: watch Terminal Services session events and keep exactly one session
/// agent alive inside each connected RDP session. It never touches scanner data, opens no
/// network listener, and holds no user documents — everything that handles a scan runs in
/// the user's own session under the user's own token.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ScannerService : ServiceBase
{
    public const string ServiceName_ = "RemoteScanner";

    private SessionLauncher? _launcher;

    public ScannerService()
    {
        ServiceName = ServiceName_;
        CanHandleSessionChangeEvent = true;
        CanShutdown = true;
        CanStop = true;
        AutoLog = false;
    }

    protected override void OnStart(string[] args)
    {
        CommonLog.Initialize("service", useEventLog: true);
        AppPaths.EnsureDirectories();

        string agentPath = Path.Combine(AppContext.BaseDirectory, "RemoteScanner.SessionAgent.exe");
        if (!File.Exists(agentPath))
        {
            CommonLog.Logger.Fatal("Session agent not found at {Path}. The installation is incomplete.", agentPath);
            throw new FileNotFoundException("RemoteScanner.SessionAgent.exe is missing.", agentPath);
        }

        _launcher = new SessionLauncher(agentPath);
        CommonLog.Logger.Information("RemoteScanner service started.");

        // Sessions that were already connected before the service started still need agents.
        foreach (RdpSession session in RdpSessionInfo.Enumerate().Where(s => s.IsUsable))
        {
            CommonLog.Logger.Information("Attaching to existing session {Session} ({Client}).",
                                         session.SessionId, session.ClientName);
            _launcher.Start(session.SessionId);
        }
    }

    protected override void OnSessionChange(SessionChangeDescription change)
    {
        uint sessionId = (uint)change.SessionId;

        switch (change.Reason)
        {
            case SessionChangeReason.RemoteConnect:
            case SessionChangeReason.SessionLogon:
            case SessionChangeReason.SessionUnlock:
                // Only remote sessions get an agent; a console logon has no channel to open.
                if (RdpSessionInfo.IsRemoteSession(sessionId))
                {
                    CommonLog.Logger.Information("Session {Session}: {Reason}.", sessionId, change.Reason);
                    _launcher?.Start(sessionId);
                }
                break;

            case SessionChangeReason.RemoteDisconnect:
            case SessionChangeReason.SessionLogoff:
                CommonLog.Logger.Information("Session {Session}: {Reason}.", sessionId, change.Reason);
                _launcher?.Stop(sessionId);
                break;

            default:
                break;
        }
    }

    protected override void OnStop()
    {
        CommonLog.Logger.Information("RemoteScanner service stopping.");
        _launcher?.StopAll();
        _launcher?.Dispose();
        _launcher = null;
        CommonLog.Shutdown();
    }

    protected override void OnShutdown() => OnStop();

    public static void Main(string[] args)
    {
        // Running interactively is how the service is debugged on a real server: it does the
        // same work in the foreground so its log output can be watched live.
        if (args.Contains("--console"))
        {
            CommonLog.Initialize("service");
            AppPaths.EnsureDirectories();

            var service = new ScannerService();
            service.OnStart(args);

            Console.WriteLine("RemoteScanner service running in console mode. Press Ctrl+C to stop.");
            using var stop = new ManualResetEventSlim(false);
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Set(); };
            stop.Wait();

            service.OnStop();
            return;
        }

        ServiceBase.Run(new ScannerService());
    }
}
