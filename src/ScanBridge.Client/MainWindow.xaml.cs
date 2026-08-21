using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;
using System.Text;
using System.Windows;
using System.Windows.Media;
using ScanBridge.Agent;
using ScanBridge.Common;
using ScanBridge.Protocol;
using ScanBridge.Rdp;
using CommonLog = ScanBridge.Common.Log;
using Button = System.Windows.Controls.Button;
using MessageBox = System.Windows.MessageBox;

namespace ScanBridge.Client;

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

        // The version, beside the name in the header. Same question the build date in the title
        // answers — "is this the build I just installed?" — but in the form a person would
        // quote in a bug report.
        // HeaderVersion is a Run now, so it lines up on the same baseline as the name
        // beside it instead of being a second TextBlock guessing at the alignment.
        HeaderVersion.Text = AddRemovePrograms.Version;

        HeaderLogo.Source = LoadMark();
        RemoveOldSavedScans();

        // The build stamp still exists, because "am I looking at the new version or an old one
        // still running in the tray?" is otherwise unanswerable and answering it wrongly sends
        // people debugging a bug that was already fixed. It has moved out of the title bar and
        // into the Troubleshooting menu: it is diagnostic information, and a title bar reading
        // "ScanBridge — build 2026-08-21 15:04" spends the most prominent text on the screen on
        // something nobody needs until something is wrong.
        //
        // Taken from the executable on disk, not from Assembly.Location: packed as a single
        // file the assembly has no location and that returns an empty string, which would have
        // put a 1601 date on every shipped build.
        string self = Environment.ProcessPath ?? AppContext.BaseDirectory;
        var built = File.Exists(self) ? File.GetLastWriteTime(self) : DateTime.Now;
        AboutItem.Header = $"Version {AddRemovePrograms.Version}  ·  build {built:d MMM yyyy HH:mm}";

        SessionsGrid.ItemsSource = _links;
        ScannersGrid.ItemsSource = _scanners;

        _host.StateChanged += OnAgentStateChanged;

        _tick.Tick += (_, _) => RebuildRows();
        _tick.Start();

        Loaded += async (_, _) => await RefreshAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// Deletes a test scan left behind by an earlier build.
    ///
    /// Between 21 August 2026 and the same afternoon, a test scan was written to
    /// %LocalAppData%\ScanBridge as last-test-scan.jpg so it could be opened with the shell.
    /// That was the wrong trade - it is somebody's document, and a diagnostic has no business
    /// leaving one on disk - and the preview now holds the page in memory instead. Anyone who
    /// ran that build still has the file, and would have no reason to know it was there.
    /// </summary>
    private static void RemoveOldSavedScans()
    {
        try
        {
            if (!Directory.Exists(AppPaths.UserDataDirectory)) return;

            foreach (string stale in Directory.EnumerateFiles(
                         AppPaths.UserDataDirectory, "last-test-scan.*"))
            {
                File.Delete(stale);
                CommonLog.Logger.Information(
                    "Removed a test scan left on disk by an earlier build: {Path}", stale);
            }
        }
        catch (Exception ex)
        {
            CommonLog.Logger.Warning(ex, "Could not remove an old saved test scan.");
        }
    }

    /// <summary>
    /// The mark, for the header, out of the icon carried inside this executable.
    ///
    /// The same resource the tray icon uses. Not a file beside the program: there is nothing
    /// beside the program once it is packed as a single file, and an icon that can go missing
    /// from an install is one that eventually will.
    /// </summary>
    private static System.Windows.Media.ImageSource? LoadMark()
    {
        try
        {
            using Stream? stream = typeof(MainWindow).Assembly
                .GetManifestResourceStream("ScanBridge.Icon.ico");
            if (stream is null) return null;

            var decoder = new System.Windows.Media.Imaging.IconBitmapDecoder(
                stream,
                System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat,
                System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);

            // The largest frame, then let WPF scale it down. Picking the 16px frame and drawing
            // it at 18 would show the deliberately-simplified small artwork, slightly blurred.
            System.Windows.Media.Imaging.BitmapFrame best = decoder.Frames[0];
            foreach (System.Windows.Media.Imaging.BitmapFrame frame in decoder.Frames)
                if (frame.PixelWidth > best.PixelWidth) best = frame;

            return best;
        }
        catch (Exception)
        {
            // A header without a logo is a cosmetic loss; failing to open the window is not.
            return null;
        }
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

            // Deliberately not a summary of the state: the headline above already says that,
            // and saying it twice in two registers was one of the things wrong with the old
            // window. This line is for what just happened, and for detail the headline cannot
            // hold.
            SetStatus(_scanners.Count == 0
                ? string.Empty
                : $"Applications in your Remote Desktop session are given \"{found[0].Name}\".");
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

        NoScannersHint.Visibility = _scanners.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateHero();
    }

    /// <summary>
    /// The sentence at the top, and the bridge beside it.
    ///
    /// This is the only part of the window most people will read. It answers the question they
    /// opened it for - is my scanner working in my remote session - rather than leaving them to
    /// infer it from two lists and the absence of rows in them.
    /// </summary>
    private void UpdateHero()
    {
        ScannerRow? live = _scanners.FirstOrDefault(row => row.IsLive);
        ScannerRow? target = _scanners.FirstOrDefault(row => row.IsDefault);
        int links = _host.ConnectedLinks;

        BridgeScannerName.Text = target?.Name ?? string.Empty;
        BridgeRemoteName.Text = _links.Count > 0 ? _links[0].PeerName : string.Empty;

        bool bridged = links > 0;
        BridgeLine.Stroke = bridged ? Brush("#2FD3C7") : Brush("#2E5488");
        BridgeLine.StrokeDashArray = bridged ? null : new DoubleCollection(new double[] { 3, 3 });
        BridgePulse.Visibility = live is not null ? Visibility.Visible : Visibility.Collapsed;
        BridgeRemoteBox.Background = bridged ? Brush("#1B3A66") : Brush("#152F52");
        BridgeRemoteText.Foreground = bridged ? Brush("#BFD3EE") : Brush("#6E86B2");

        // The far machine lights up with its box. Dim while nothing is connected, so the gap
        // in the middle is not the only thing carrying that fact.
        BridgeRemoteScreen.Stroke = bridged ? Brush("#BFD3EE") : Brush("#6E86B2");
        BridgeRemoteStand.Fill = bridged ? Brush("#BFD3EE") : Brush("#6E86B2");

        if (_scanners.Count == 0)
        {
            HeroDot.Fill = Brush("#E8A33D");
            HeroHeadline.Text = "No scanner found";
            return;
        }

        if (live is not null)
        {
            HeroDot.Fill = Brush("#2FD3C7");
            HeroHeadline.Text = $"In use — {live.Name}";
            return;
        }

        if (bridged)
        {
            HeroDot.Fill = Brush("#2FD3C7");
            HeroHeadline.Text = links == 1 ? "Remote session connected"
                                           : $"{links} remote sessions connected";
            return;
        }

        HeroDot.Fill = Brush("#7E97C4");
        HeroHeadline.Text = "Ready";
    }

    // Fully qualified: System.Drawing is in scope here for the tray icon, and both namespaces
    // define Color and ColorConverter.
    private static SolidColorBrush Brush(string hex) =>
        new((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex));

    /// <summary>Opens the troubleshooting menu under its button.</summary>
    private void OnMore(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.ContextMenu is null) return;

        button.ContextMenu.PlacementTarget = button;
        button.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
        button.ContextMenu.IsOpen = true;
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
    /// Scans one page locally and keeps it. This is the "is the scanner itself working" check —
    /// it deliberately involves no RDP, so a failure here points at the driver and a success
    /// points at the redirection path.
    ///
    /// The page used to be counted and thrown away, which answered "did bytes arrive" but not
    /// "is the image right". Those are different questions, and the second one is the one that
    /// mattered when a scan came back a plausible size and entirely the wrong colour. It is now
    /// shown, from memory - never written to disk, because it is somebody's document.
    /// </summary>
    private async void OnTestScan(object sender, RoutedEventArgs e)
    {
        if (ScannersGrid.SelectedItem is not ScannerRow row)
        {
            MessageBox.Show(this, "Select a scanner first.", "ScanBridge",
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

            var page = new MemoryStream();
            PageEncoding encoding = PageEncoding.Jpeg;

            await foreach (Frame frame in _runner.ExecuteAsync(
                !scanner.Is32BitOnly, MessageType.ScanRequest,
                new ScanRequestMessage(scanner.Id, settings), CancellationToken.None).ConfigureAwait(true))
            {
                switch (frame.Type)
                {
                    case MessageType.ScanPageBegin:
                        encoding = DecodeEncoding(frame);
                        break;

                    case MessageType.ScanPageEnd:
                        pages++;
                        break;

                    case MessageType.ScanPageData:
                    {
                        // The blob, not the whole frame: the frame also carries the job id, page
                        // number and offset, and writing those into the file would corrupt it by
                        // twenty bytes a chunk — enough to break the image and not obviously
                        // enough to be noticed as the cause.
                        byte[] data = DecodePageData(frame);
                        bytes += data.Length;

                        // Only the first page is kept: PageLimit is 1, and a sheet-feeder that
                        // ignores it should not fill the disk.
                        if (pages == 0) page.Write(data, 0, data.Length);
                        break;
                    }

                    case MessageType.ScanError:
                        error = DecodeError(frame).Message;
                        break;

                    default:
                        break;
                }
            }

            SetStatus(error is null
                ? $"Test scan succeeded: {pages} page, {bytes / 1024:N0} KB."
                : $"Test scan failed: {error}");

            if (error is not null)
            {
                MessageBox.Show(this, error, "Scan failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            else if (page.Length > 0)
            {
                // Shown straight away rather than parked behind a button. The only reason to run
                // a test scan is to look at the result, so making that a second decision added a
                // step and no information. The bytes live in that window and go when it closes.
                new ScanPreviewWindow(page.ToArray(), encoding.ToString(), (int)page.Length)
                {
                    Owner = this,
                }.ShowDialog();
            }
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

    // PayloadReader is a ref struct, so it cannot live in an async method. These exist for the
    // same reason DecodeError below does: read the frame here, hand back something ordinary.
    private static PageEncoding DecodeEncoding(Frame frame)
    {
        var reader = frame.Reader();
        return ScanPageBeginMessage.Read(ref reader).Encoding;
    }

    private static byte[] DecodePageData(Frame frame)
    {
        var reader = frame.Reader();
        return ScanPageDataMessage.Read(ref reader).Data;
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
    /// <summary>
    /// The radio on a scanner card. Choosing is the action.
    ///
    /// This replaced a select-the-row-then-press-a-button pair, which left a gap where the user
    /// had done half of something and the screen looked identical either way.
    /// </summary>
    private async void OnRedirectChosen(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ScannerRow row }) return;

        // Already the one? Then the click was on the radio that is already filled in, and
        // rewriting the config and showing a dialog would be noise.
        if (row.IsDefault) return;

        await UseForRemoteAsync(row).ConfigureAwait(true);
    }

    private async Task UseForRemoteAsync(ScannerRow row)
    {
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
                "ScanBridge", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            CommonLog.Logger.Error(ex, "Could not save the default scanner.");
            MessageBox.Show(this, $"Could not save the setting:\n\n{ex.Message}",
                            "ScanBridge", MessageBoxButton.OK, MessageBoxImage.Error);
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
            report.AppendLine(@"     HKCU\Software\Microsoft\Terminal Server Client\Default\AddIns\ScanBridge");
            report.AppendLine("  3. The ScanBridge service is running on the server.");
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
                "ScanBridge", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not produce a pairing code:\n\n{ex.Message}",
                            "ScanBridge", MessageBoxButton.OK, MessageBoxImage.Error);
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
        ScannersGrid.IsEnabled = !busy;
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
    /// The quiet line under the scanner's name.
    ///
    /// Three facts that were three columns. As columns they were given the same weight as the
    /// name, which is the only thing anybody reads, and two of them - "WIA", "Ready" - are
    /// details you want available rather than announced.
    /// </summary>
    public string Detail
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(Vendor)) parts.Add(Vendor);
            parts.Add(Interface.Equals("Wia", StringComparison.OrdinalIgnoreCase) ? "WIA" : Interface);
            parts.Add(Status);
            if (IsDefault) parts.Add(IsChosen ? "your choice" : "chosen automatically");
            return string.Join("  ·  ", parts);
        }
    }

    /// <summary>
    /// Evidence rather than intent: when a remote session last actually reached this scanner.
    /// Nothing here while the radio beside it is filled in is the clearest sign that redirection
    /// is set up and not working.
    /// </summary>
    public string ActivityBadge
    {
        get
        {
            if (LastUsed is not { } when) return string.Empty;

            TimeSpan ago = DateTime.Now - when;
            if (ago.TotalSeconds < 90) return "In use now";
            if (ago.TotalMinutes < 60) return $"Used {(int)ago.TotalMinutes} min ago";
            if (when.Date == DateTime.Now.Date) return $"Used at {when:HH:mm}";
            return $"Used {when:d MMM}";
        }
    }

    public Visibility ActivityVisibility =>
        LastUsed is null ? Visibility.Collapsed : Visibility.Visible;

    // Brushes rather than colour strings. A string bound to a Brush property leans on WPF's
    // default value converter finding BrushConverter, which it usually does and silently does
    // not when the property is templated - producing an invisible badge and no error anywhere.
    public System.Windows.Media.Brush ActivityBackground =>
        IsLive ? Swatch(0xE6, 0xFA, 0xF8) : Swatch(0xF1, 0xF5, 0xF9);

    public System.Windows.Media.Brush ActivityForeground =>
        IsLive ? Swatch(0x0E, 0x7C, 0x74) : Swatch(0x5A, 0x6B, 0x82);

    private static System.Windows.Media.Brush Swatch(byte r, byte g, byte b)
    {
        var brush = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(r, g, b));
        brush.Freeze();     // shared across rows and rebuilt on a timer
        return brush;
    }

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
