using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.Versioning;
using System.Windows;
using Forms = System.Windows.Forms;
using RemoteScanner.Agent;
using RemoteScanner.Common;
using CommonLog = RemoteScanner.Common.Log;
using MessageBox = System.Windows.MessageBox;

namespace RemoteScanner.Client.UI;

/// <summary>
/// Tray application host.
///
/// The agent starts with the application and keeps running while the window is hidden —
/// closing the window must not take scanner redirection away from a live RDP session, so
/// ShutdownMode is OnExplicitShutdown and only the tray's Exit command really quits.
/// </summary>
[SupportedOSPlatform("windows")]
// Fully qualified: enabling WinForms for NotifyIcon brings a second Application type into
// scope through implicit usings.
public partial class App : System.Windows.Application
{
    private Forms.NotifyIcon? _trayIcon;
    private AgentHost? _host;
    private CancellationTokenSource? _shutdown;
    private MainWindow? _window;
    private Task? _agentTask;

    private LanListener? _lanListener;
    private Task? _lanTask;
    private Mutex? _singleInstance;

    /// <summary>
    /// Per-session, per-user. "Local\" scopes it to the logon session, which is right: on a
    /// machine with fast user switching each user gets their own agent and their own pipe.
    /// </summary>
    private const string SingleInstanceName = @"Local\RemoteScanner.Agent.SingleInstance";

    /// <summary>
    /// Set by a second copy of the application to ask the running one to show its window.
    /// Same "Local\" scoping as the mutex, for the same reason.
    /// </summary>
    private const string ShowWindowEventName = @"Local\RemoteScanner.Agent.ShowWindow";

    private EventWaitHandle? _showWindowSignal;
    private CancellationTokenSource? _showWindowListener;

    /// <summary>
    /// Waits for a later copy of the application to ask for the window, and shows it.
    ///
    /// A background thread rather than a timer: it costs nothing while idle and responds
    /// immediately, and the alternative — polling — would mean the shortcut feels dead for up
    /// to the poll interval.
    /// </summary>
    private void StartShowWindowListener()
    {
        _showWindowSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowWindowEventName);
        _showWindowListener = new CancellationTokenSource();

        CancellationToken token = _showWindowListener.Token;
        var thread = new Thread(() =>
        {
            WaitHandle[] handles = { _showWindowSignal, token.WaitHandle };
            while (!token.IsCancellationRequested)
            {
                if (WaitHandle.WaitAny(handles) != 0) return;
                _ = Dispatcher.BeginInvoke(ShowWindow);
            }
        })
        {
            IsBackground = true,
            Name = "RemoteScanner.ShowWindow",
        };
        thread.Start();
    }


    /// <summary>
    /// Switches that ask for a one-shot answer on the command line instead of the tray
    /// application: the diagnostics that have to work on a PC where scanning does not.
    /// </summary>
    private static readonly string[] ConsoleRoles =
    {
        "--enumerate-once", "--pairing-code", "--headless", "--help", "-?", "/?",
    };

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // The headless agent used to be a second executable. It is a role of this one now, so
        // there is one file on the user's PC rather than two that could be different builds.
        //
        // A WinExe has no console of its own, so it borrows the one it was launched from —
        // without this, output from these switches goes nowhere and the command looks broken.
        if (e.Args.Any(a => ConsoleRoles.Contains(a, StringComparer.OrdinalIgnoreCase)))
        {
            AttachConsole(-1);   // ATTACH_PARENT_PROCESS
            int code = RemoteScanner.Agent.Program.RunAsync(e.Args).GetAwaiter().GetResult();
            Console.Out.Flush();

            // Environment.Exit, not Shutdown: Shutdown() during OnStartup leaves the process
            // exit code at -1 regardless of what is passed to it, and CHECK-MY-SCANNER.bat and
            // anything else scripted around these switches reads errorlevel to decide whether
            // the machine is healthy.
            Environment.Exit(code);
            return;
        }

        // Only one agent may run per session: the second one would fail to create the agent
        // pipe (by design - FILE_FLAG_FIRST_PIPE_INSTANCE is what stops another process
        // hijacking the name) and die with an error the user cannot act on. Catching it here
        // turns that into "it is already running", which is what actually happened.
        _singleInstance = new Mutex(initiallyOwned: true, SingleInstanceName, out bool createdNew);
        if (!createdNew)
        {
            // Hand over to the copy that is already running and leave quietly.
            //
            // This used to show "Remote Scanner is already running" and exit, which is true but
            // useless: clicking the shortcut appeared to do nothing at all, and after an upgrade
            // it looked exactly like the shortcut was still opening the old version — the new
            // process really did start, saw the old one, and vanished without showing anything.
            // Signalling the running instance to show its window is what the user wanted from
            // the click in the first place.
            try
            {
                using var show = EventWaitHandle.OpenExisting(ShowWindowEventName);
                show.Set();
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                // An older build is running and has no such event. Say so, rather than exiting
                // silently and looking broken.
                MessageBox.Show(
                    "Remote Scanner is already running, but it is an older version that cannot " +
                    "be brought to the front.\n\n" +
                    "Right-click its icon near the clock (you may need the small arrow to see " +
                    "hidden icons) and choose Exit, then open Remote Scanner again.",
                    "Remote Scanner", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (UnauthorizedAccessException)
            {
                MessageBox.Show(
                    "Remote Scanner is already running under a different account.",
                    "Remote Scanner", MessageBoxButton.OK, MessageBoxImage.Information);
            }

            _singleInstance.Dispose();
            _singleInstance = null;
            Shutdown(0);
            return;
        }

        StartShowWindowListener();

        CommonLog.Initialize("agent");
        AppPaths.EnsureDirectories();

        try
        {
            var runner = new ScanHostRunner(ResolveInstallDirectory());

            if (!runner.IsAvailable(true) && !runner.IsAvailable(false))
            {
                MessageBox.Show(
                    "RemoteScanner.ScanHost.exe was not found.\n\n" +
                    "The installation is incomplete; no scanner can be reached.",
                    "Remote Scanner", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(2);
                return;
            }

            byte[] secret = SecretStore.GetOrCreateLocalSecret();
            Log.Logger.Information("Shared secret loaded: key {Key}.", SecretStore.Fingerprint(secret));

            _host = new AgentHost(runner, secret);
            _shutdown = new CancellationTokenSource();

            // The agent runs for the lifetime of the process; a failure here is fatal to
            // redirection, so it is surfaced rather than swallowed.
            _agentTask = Task.Run(async () =>
            {
                try
                {
                    await _host.RunAsync(_shutdown.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    CommonLog.Logger.Fatal(ex, "The agent stopped.");
                    _ = Dispatcher.BeginInvoke(() => MessageBox.Show(
                        $"Scanner redirection stopped:\n\n{ex.Message}",
                        "Remote Scanner", MessageBoxButton.OK, MessageBoxImage.Error));
                }
            });

            // Direct connections from Remote Desktop servers, for when the virtual channel
            // cannot carry data. The secret is read per connection, not captured here, so a key
            // rewritten while this process runs does not disable the fallback.
            _lanListener = new LanListener(
                _host, () => PairingCode.KeyFor(SecretStore.GetOrCreatePairingSeed()));
            _lanTask = Task.Run(async () =>
            {
                try
                {
                    await _lanListener.RunAsync(_shutdown.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    // The primary transport is unaffected, so this is not fatal and must not
                    // put a dialog in front of somebody who is scanning perfectly happily.
                    CommonLog.Logger.Warning(ex, "The direct-connection listener stopped.");
                }
            });

            _window = new MainWindow(_host, runner);
            CreateTrayIcon();

            WarnIfPolicyBlocksTheAddIn();

            AgentConfig config = AgentConfig.Load();
            if (!config.MinimizeToTray) _window.Show();
        }
        catch (Exception ex)
        {
            CommonLog.Logger.Fatal(ex, "Startup failed.");
            MessageBox.Show($"Remote Scanner could not start:\n\n{ex.Message}",
                            "Remote Scanner", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    /// <summary>
    /// Group policy silently preventing mstsc.exe from loading add-ins is the most common
    /// cause of a perfectly installed system doing nothing, so it is checked at startup
    /// rather than left for the user to discover.
    /// </summary>
    private void WarnIfPolicyBlocksTheAddIn()
    {
        string? reason = PluginRegistration.PolicyBlockReason();
        if (reason is null) return;

        CommonLog.Logger.Warning("RDP add-in policy problem: {Reason}", reason);
        _trayIcon?.ShowBalloonTip(10_000, "Remote Scanner", reason, Forms.ToolTipIcon.Warning);
    }

    private void CreateTrayIcon()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open", null, (_, _) => ShowWindow());
        menu.Items.Add("Logs", null, (_, _) =>
            Process.Start(new ProcessStartInfo(AppPaths.LogDirectory) { UseShellExecute = true }));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitApplication());

        _trayIcon = new Forms.NotifyIcon
        {
            Icon = LoadTrayIcon(),
            Visible = true,
            Text = "Remote Scanner",
            ContextMenuStrip = menu,
        };

        _trayIcon.DoubleClick += (_, _) => ShowWindow();
    }

    private static Icon LoadTrayIcon()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "RemoteScanner.ico");
        if (File.Exists(path))
        {
            try { return new Icon(path); }
            catch (ArgumentException) { /* fall through to the stock icon */ }
        }

        // No branded icon shipped yet; the stock application icon is better than crashing.
        return SystemIcons.Application;
    }

    private void ShowWindow()
    {
        if (_window is null) return;

        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();

        // Windows refuses focus to a process the user did not just interact with, and when the
        // request came from a second copy of the application the click landed on *that* process,
        // not this one. Without the nudge the window is restored behind everything and the
        // shortcut still looks dead. Topmost is set and immediately cleared so the window comes
        // forward once rather than staying pinned above other applications.
        _window.Topmost = true;
        _window.Topmost = false;
        _window.Focus();
    }

    private void ExitApplication()
    {
        _window?.AllowExit();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _shutdown?.Cancel();

        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
        }

        try
        {
            _agentTask?.Wait(TimeSpan.FromSeconds(3));
            _lanTask?.Wait(TimeSpan.FromSeconds(3));
        }
        catch (AggregateException)
        {
            // Already logged by the agent task.
        }

        _host?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3));
        _shutdown?.Dispose();

        _showWindowListener?.Cancel();
        _showWindowListener?.Dispose();
        _showWindowSignal?.Dispose();

        if (_singleInstance is not null)
        {
            try { _singleInstance.ReleaseMutex(); } catch (ApplicationException) { /* not owned */ }
            _singleInstance.Dispose();
            _singleInstance = null;
        }

        CommonLog.Logger.Information("Agent stopped.");
        CommonLog.Shutdown();

        base.OnExit(e);
    }

    private static string ResolveInstallDirectory()
    {
        string baseDirectory = AppContext.BaseDirectory;

        if (Directory.Exists(Path.Combine(baseDirectory, "x64")) ||
            Directory.Exists(Path.Combine(baseDirectory, "x86")))
        {
            return baseDirectory;
        }

        return Directory.Exists(AppPaths.InstallDirectory) ? AppPaths.InstallDirectory : baseDirectory;
    }
}
