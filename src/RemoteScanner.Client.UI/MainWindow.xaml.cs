using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;
using System.Text;
using System.Windows;
using RemoteScanner.Agent;
using RemoteScanner.Common;
using RemoteScanner.Protocol;
using RemoteScanner.Rdp;
using CommonLog = RemoteScanner.Common.Log;
using MessageBox = System.Windows.MessageBox;

namespace RemoteScanner.Client.UI;

/// <summary>
/// The tray application's window.
///
/// It is a view over the agent, not a participant: redirection keeps working whether this
/// window is open, minimised or closed, because the agent runs regardless. Closing the
/// window hides it to the tray rather than exiting, since exiting would take every remote
/// session's scanner away.
/// </summary>
[SupportedOSPlatform("windows")]
public partial class MainWindow : Window
{
    private readonly AgentHost _host;
    private readonly ScanHostRunner _runner;
    private readonly ObservableCollection<RemoteLink> _links = new();
    private readonly ObservableCollection<ScannerRow> _scanners = new();

    private bool _reallyExit;

    /// <summary>Last hardware enumeration, so the rows can be refreshed without re-scanning.</summary>
    private IReadOnlyList<ScannerInfo> _lastFound = Array.Empty<ScannerInfo>();

    /// <summary>
    /// Repaints the live columns. Enumerating scanners takes seconds and spins up two ScanHost
    /// processes, so it is not repeated on a timer - but "in use now" and how long a link has
    /// been up are only true for a moment, and a window left open would quietly show stale
    /// information. This redraws from data already held.
    /// </summary>
    private readonly System.Windows.Threading.DispatcherTimer _tick = new()
    {
        Interval = TimeSpan.FromSeconds(5),
    };

    public MainWindow(AgentHost host, ScanHostRunner runner)
    {
        InitializeComponent();

        _host = host;
        _runner = runner;

        // Build date in the title. "Am I looking at the new version or an old one still running
        // in the tray?" is otherwise unanswerable from the screen, and answering it wrongly
        // sends people debugging a bug that was already fixed.
        var built = File.GetLastWriteTime(typeof(MainWindow).Assembly.Location);
        Title = $"Remote Scanner  —  build {built:yyyy-MM-dd HH:mm}";

        SessionsGrid.ItemsSource = _links;
        ScannersGrid.ItemsSource = _scanners;

        _host.StateChanged += OnAgentStateChanged;

        _tick.Tick += (_, _) => RebuildRows();
        _tick.Start();

        Loaded += async (_, _) => await RefreshAsync().ConfigureAwait(true);
    }

    /// <summary>Set by the tray icon's Exit command; otherwise closing only hides the window.</summary>
    public void AllowExit() => _reallyExit = true;

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_reallyExit)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        _tick.Stop();
        _host.StateChanged -= OnAgentStateChanged;
        base.OnClosing(e);
    }

    private void OnAgentStateChanged()
        => _ = Dispatcher.BeginInvoke(() =>
        {
            // A remote session reaching a scanner raises this, and that is exactly the moment
            // the grid should light up rather than waiting for the next tick.
            RebuildRows();
            SetStatus($"{_host.ConnectedLinks} RDP link(s) connected, " +
                      $"{_scanners.Count} scanner(s) available.");
        });

    private async Task RefreshAsync()
    {
        SetBusy(true);
        try
        {
            // The agent puts the scanner remote sessions get at the front, so the first row is
            // the redirected one. Marked rather than left to be inferred from position.
            _lastFound = await EnumerateScannersAsync().ConfigureAwait(true);
            IReadOnlyList<ScannerInfo> found = _lastFound;
            RebuildRows();

            SetStatus(_scanners.Count == 0
                ? "No scanners detected. Check the scanner is switched on and its driver is installed."
                : $"{_scanners.Count} scanner(s) ready. Remote sessions get \"{found[0].Name}\". " +
                  $"{_host.ConnectedLinks} RDP link(s) connected.");
        }
        catch (Exception ex)
        {
            CommonLog.Logger.Warning(ex, "Refresh failed.");
            SetStatus($"Refresh failed: {ex.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    /// <summary>Redraws the two grids from data already in hand. Cheap; safe to call often.</summary>
    private void RebuildRows()
    {
        _links.Clear();
        foreach (RemoteLink link in _host.ActiveLinks) _links.Add(link);
        NoLinksHint.Visibility = _links.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        // Remembered across the rebuild. This runs on a timer, and a list that silently
        // deselects itself every few seconds is one the user cannot click a button against —
        // they would reach for "Use for Remote" and be told to select a scanner first.
        string? selectedId = (ScannersGrid.SelectedItem as ScannerRow)?.Scanner.Id;

        string chosenId = AgentConfig.Load().DefaultScannerId;

        _scanners.Clear();
        for (int i = 0; i < _lastFound.Count; i++)
        {
            bool chosen = !string.IsNullOrEmpty(chosenId) &&
                          string.Equals(_lastFound[i].Id, chosenId, StringComparison.OrdinalIgnoreCase);

            _scanners.Add(new ScannerRow(_lastFound[i], IsDefault: i == 0, IsChosen: chosen,
                                         LastUsed: _host.LastRemoteUse(_lastFound[i].Id)));
        }

        if (selectedId is not null)
        {
            ScannersGrid.SelectedItem = _scanners.FirstOrDefault(
                row => string.Equals(row.Scanner.Id, selectedId, StringComparison.OrdinalIgnoreCase));
        }
    }

    private async Task<IReadOnlyList<ScannerInfo>> EnumerateScannersAsync()
    {
        var scanners = new List<ScannerInfo>();

        foreach (bool use64Bit in new[] { true, false })
        {
            if (!_runner.IsAvailable(use64Bit)) continue;

            try
            {
                await foreach (Frame frame in _runner.ExecuteAsync(
                    use64Bit, MessageType.ScannerEnumRequest, new ScannerEnumRequestMessage(),
                    CancellationToken.None).ConfigureAwait(true))
                {
                    if (frame.Type != MessageType.ScannerEnumResponse) continue;

                    foreach (ScannerInfo scanner in DecodeEnum(frame).Scanners)
                    {
                        if (scanners.All(existing => existing.Id != scanner.Id)) scanners.Add(scanner);
                    }
                }
            }
            catch (Exception ex)
            {
                CommonLog.Logger.Warning(ex, "Enumeration failed on the {Bitness} host.",
                                         use64Bit ? "x64" : "x86");
            }
        }

        // Exactly what the agent does before answering a remote session. This window has to
        // show the same list, in the same order: if it showed the raw enumeration the user
        // would pick a scanner here and a different one would be redirected.
        return ScannerList.Arrange(scanners, AgentConfig.Load().DefaultScannerId);
    }

    private static ScannerEnumResponseMessage DecodeEnum(Frame frame)
    {
        var reader = frame.Reader();
        return ScannerEnumResponseMessage.Read(ref reader);
    }

    private async void OnRefresh(object sender, RoutedEventArgs e)
        => await RefreshAsync().ConfigureAwait(true);

    /// <summary>
    /// Scans one page locally and discards it. This is the "is the scanner itself working"
    /// check — it deliberately involves no RDP, so a failure here points at the driver and a
    /// success points at the redirection path.
    /// </summary>
    private async void OnTestScan(object sender, RoutedEventArgs e)
    {
        if (ScannersGrid.SelectedItem is not ScannerRow row)
        {
            MessageBox.Show(this, "Select a scanner first.", "Remote Scanner",
                            MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        ScannerInfo scanner = row.Scanner;

        SetBusy(true);
        SetStatus($"Test scanning one page on {scanner.Name}...");

        try
        {
            AgentConfig config = AgentConfig.Load();
            ScanSettings settings = config.ToScanSettings() with { PageLimit = 1 };

            int pages = 0;
            long bytes = 0;
            string? error = null;

            await foreach (Frame frame in _runner.ExecuteAsync(
                !scanner.Is32BitOnly, MessageType.ScanRequest,
                new ScanRequestMessage(scanner.Id, settings), CancellationToken.None).ConfigureAwait(true))
            {
                switch (frame.Type)
                {
                    case MessageType.ScanPageEnd: pages++; break;
                    case MessageType.ScanPageData: bytes += frame.Payload.Length; break;
                    case MessageType.ScanError: error = DecodeError(frame).Message; break;
                    default: break;
                }
            }

            SetStatus(error is null
                ? $"Test scan succeeded: {pages} page, {bytes / 1024} KB."
                : $"Test scan failed: {error}");

            if (error is not null)
                MessageBox.Show(this, error, "Scan failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            CommonLog.Logger.Error(ex, "Test scan failed.");
            SetStatus($"Test scan failed: {ex.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private static ScanErrorMessage DecodeError(Frame frame)
    {
        var reader = frame.Reader();
        return ScanErrorMessage.Read(ref reader);
    }

    /// <summary>
    /// Chooses which scanner remote applications get.
    ///
    /// A remote session sees one scanner, not a list — the data source on the server represents
    /// a single device, because that is what a TWAIN application expects to pick from its own
    /// device list. So when a PC has several scanners, this is where the user says which.
    /// </summary>
    private async void OnUseForRemote(object sender, RoutedEventArgs e)
    {
        if (ScannersGrid.SelectedItem is not ScannerRow row)
        {
            MessageBox.Show(this, "Select a scanner first, then choose 'Use for Remote'.",
                            "Remote Scanner", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            AgentConfig config = AgentConfig.Load();
            config.DefaultScannerId = row.Scanner.Id;
            config.Save();

            CommonLog.Logger.Information("Default scanner for remote sessions set to {Scanner} ({Id}).",
                                         row.Scanner.Name, row.Scanner.Id);

            await RefreshAsync().ConfigureAwait(true);

            // Two things worth saying here, both learned the hard way.
            //
            // The restart note, because the change does not reach an application that is
            // already running: it asked for the scanner list once, when it started.
            //
            // The Test Scan suggestion, because "Ready" in the Status column cannot be relied
            // on. WIA reports a device as ready whenever it is registered with Windows, which
            // includes a wireless scanner that is currently switched off — the failure only
            // appears when something actually tries to scan. Choosing such a device here would
            // leave every remote session with a scanner that lists fine and never works.
            MessageBox.Show(this,
                $"Remote sessions will now use:\n\n    {row.Scanner.Name}\n\n" +
                "Scanning applications already open on the server must be restarted before " +
                "they see the change.\n\n" +
                "Tip: click \"Test Scan\" on this scanner first. The Status column says " +
                "\"Ready\" for any scanner Windows knows about, including a wireless one that " +
                "is switched off, so a test scan is the only way to be sure.",
                "Remote Scanner", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            CommonLog.Logger.Error(ex, "Could not save the default scanner.");
            MessageBox.Show(this, $"Could not save the setting:\n\n{ex.Message}",
                            "Remote Scanner", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnTestConnection(object sender, RoutedEventArgs e)
    {
        var report = new StringBuilder();
        report.AppendLine($"Agent pipe        : \\\\.\\pipe\\{Wire.AgentPipeName}");
        report.AppendLine($"Active RDP links  : {_host.ConnectedLinks}");
        report.AppendLine();

        if (_host.ConnectedLinks == 0)
        {
            report.AppendLine("No RDP session is currently redirecting a scanner.");
            report.AppendLine();
            report.AppendLine("Check, in order:");
            report.AppendLine("  1. You are connected with mstsc.exe. The Microsoft Store");
            report.AppendLine("     'Windows App' cannot load RDP add-ins and will never work.");
            report.AppendLine("  2. The client plugin is registered under");
            report.AppendLine(@"     HKCU\Software\Microsoft\Terminal Server Client\Default\AddIns\RemoteScanner");
            report.AppendLine("  3. The RemoteScanner service is running on the server.");
        }
        else
        {
            report.AppendLine("Scanner redirection is active.");
        }

        MessageBox.Show(this, report.ToString(), "Connection test",
                        MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnDiagnostics(object sender, RoutedEventArgs e)
    {
        string report = BuildDiagnosticReport();
        string path = Path.Combine(AppPaths.LogDirectory,
                                   $"diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        File.WriteAllText(path, report);

        MessageBox.Show(this, report + $"\nSaved to:\n{path}", "Diagnostics",
                        MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private string BuildDiagnosticReport()
    {
        var report = new StringBuilder();
        report.AppendLine("=================================");
        report.AppendLine("REMOTE SCANNER DIAGNOSTICS");
        report.AppendLine("=================================");
        report.AppendLine();
        report.AppendLine($"Local Computer : {Environment.MachineName}");
        report.AppendLine($"User           : {Environment.UserName}");
        report.AppendLine($"OS             : {Environment.OSVersion.VersionString}");
        report.AppendLine($"Agent          : {typeof(MainWindow).Assembly.GetName().Version}");
        report.AppendLine();

        report.AppendLine($"ScanHost x64   : {(_runner.IsAvailable(true) ? "installed" : "MISSING")}");
        report.AppendLine($"ScanHost x86   : {(_runner.IsAvailable(false) ? "installed" : "MISSING")}");
        report.AppendLine($"Plugin AddIn   : {(PluginRegistration.IsRegistered() ? "registered" : "NOT REGISTERED")}");
        report.AppendLine();

        report.AppendLine($"Scanners       : {_scanners.Count}");
        foreach (ScannerRow row in _scanners)
        {
            // Marking the redirected one matters in a diagnostic report: "the wrong scanner is
            // being used" is otherwise indistinguishable from "the scanner is broken".
            report.AppendLine($"  {(row.IsDefault ? "->" : "  ")} {row.Name} " +
                              $"[{row.Interface}] {row.Status}  id={row.Scanner.Id}");
        }
        report.AppendLine();

        report.AppendLine($"Remote links   : {_links.Count}");
        foreach (RemoteLink link in _links)
            report.AppendLine($"  {link.Id}  from {link.PeerName}  up {link.Age}");
        report.AppendLine();

        report.AppendLine($"Active links   : {_host.ConnectedLinks}");
        report.AppendLine($"Logs           : {AppPaths.LogDirectory}");
        report.AppendLine("=================================");
        return report.ToString();
    }

    /// <summary>
    /// Shows the code that lets a Remote Desktop server reach this PC directly.
    ///
    /// Only needed when the Remote Desktop virtual channel will not carry data — which is
    /// supposed to be never, and has been the entire problem. The code is copied to the
    /// clipboard because the next thing the user does with it is paste it into a session on
    /// another machine.
    /// </summary>
    private void OnPairingCode(object sender, RoutedEventArgs e)
    {
        try
        {
            string code = PairingCode.Format(SecretStore.GetOrCreatePairingSeed());
            System.Windows.Clipboard.SetText(code);

            MessageBox.Show(
                $"Pairing code for this PC:\n\n    {code}\n\n" +
                "It has been copied to the clipboard.\n\n" +
                "You only need this if scanning through Remote Desktop does not work on its " +
                "own. In your Remote Desktop session, run PAIR-WITH-MY-PC.bat and paste it in.\n\n" +
                "Treat it like a password: anyone who has it can use this scanner.",
                "Remote Scanner", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not produce a pairing code:\n\n{ex.Message}",
                            "Remote Scanner", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnSettings(object sender, RoutedEventArgs e)
    {
        // The configuration file is the settings surface for now; a dedicated dialog is the
        // next piece of UI work. Opening it in the default editor is honest and usable.
        AgentConfig config = AgentConfig.Load();
        config.Save();       // materialise the file if it does not exist yet

        Process.Start(new ProcessStartInfo(AppPaths.ConfigFile) { UseShellExecute = true });
    }

    private void OnOpenLogs(object sender, RoutedEventArgs e)
    {
        AppPaths.EnsureDirectories();
        Process.Start(new ProcessStartInfo(AppPaths.LogDirectory) { UseShellExecute = true });
    }

    private void SetStatus(string text) => StatusText.Text = text;

    private void SetBusy(bool busy)
    {
        ScanButton.IsEnabled = !busy;
        RefreshButton.IsEnabled = !busy;
        TestButton.IsEnabled = !busy;
        UseForRemoteButton.IsEnabled = !busy;
        Cursor = busy ? System.Windows.Input.Cursors.Wait : null;
    }
}

/// <summary>
/// One row of the scanner grid.
///
/// <see cref="ScannerInfo"/> is a wire type shared with the server and has no business
/// carrying a display flag, so the "this is the one remote sessions get" marker lives here.
/// </summary>
/// <param name="Scanner">The scanner this row stands for.</param>
/// <param name="IsDefault">Whether remote sessions are currently given this scanner.</param>
/// <param name="IsChosen">Whether the user picked it, rather than it merely being found first.</param>
/// <param name="LastUsed">When a remote session last actually reached it, if ever.</param>
public sealed record ScannerRow(ScannerInfo Scanner, bool IsDefault, bool IsChosen, DateTime? LastUsed)
{
    public string Name => Scanner.Name;
    public string Vendor => Scanner.Vendor;
    public string Interface => Scanner.Interface.ToString();
    public string Status => Scanner.Status.ToString();

    /// <summary>
    /// Which scanner the remote session is offered, and whether that was a decision.
    ///
    /// "Yes" alone was ambiguous: it appeared on the first scanner found whether or not anyone
    /// had chosen anything, so a user with several scanners could not tell their choice from
    /// the default and had no way to know whether clicking the button had done anything.
    /// </summary>
    public string RemoteUse => (IsDefault, IsChosen) switch
    {
        (true, true) => "Yes — your choice",
        (true, false) => "Yes — first found",
        _ => string.Empty,
    };

    /// <summary>
    /// Evidence, as opposed to intent: when the remote session last actually reached this
    /// scanner. Blank here while "Yes" sits in the column beside it is the clearest sign that
    /// redirection is set up but not working.
    /// </summary>
    /// <summary>True while a remote session is actively using this scanner.</summary>
    public bool IsLive => LastUsed is { } when && (DateTime.Now - when).TotalSeconds < 90;

    public string RemoteActivity
    {
        get
        {
            if (LastUsed is not { } when) return IsDefault ? "never" : string.Empty;

            TimeSpan ago = DateTime.Now - when;
            if (ago.TotalSeconds < 90) return "in use now";
            if (ago.TotalMinutes < 60) return $"{(int)ago.TotalMinutes} min ago";
            if (when.Date == DateTime.Now.Date) return when.ToString("HH:mm");
            return when.ToString("d MMM HH:mm");
        }
    }
}
