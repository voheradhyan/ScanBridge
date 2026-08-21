using System.IO;
using System.Runtime.Versioning;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using MessageBox = System.Windows.MessageBox;
using ScanBridge.Common;

namespace ScanBridge.Setup;

/// <summary>
/// The window somebody sees when they double-click either executable.
///
/// It does none of the work. It collects three choices, calls back into the installer that
/// already existed for the command line, and shows exactly the text that install would have
/// printed to a console. That is deliberate: two code paths that install slightly differently
/// is how a product ends up with a bug that only reproduces "when you use the wizard".
/// </summary>
[SupportedOSPlatform("windows")]
public partial class SetupWindow : Window
{
    private readonly SetupPlan _plan;
    private bool _busy;
    private bool _finished;

    public SetupWindow(SetupPlan plan)
    {
        _plan = plan;
        InitializeComponent();

        Title = $"{plan.ProductName} Setup";
        TitleText.Text = plan.ProductName;
        VersionText.Text = AddRemovePrograms.Version;
        SummaryText.Text = plan.Summary;

        FolderBox.Text = plan.ExistingInstall() ?? plan.DefaultDirectory;

        StartupCheck.Visibility = plan.OffersStartWithWindows ? Visibility.Visible : Visibility.Collapsed;
        ElevationNote.Visibility = plan.RequiresElevation && !IsElevated
            ? Visibility.Visible : Visibility.Collapsed;

        if (plan.ExistingInstall() is { } existing)
        {
            string version = plan.ExistingVersion() ?? "an earlier version";
            ExistingText.Text =
                $"{plan.ProductName} {version} is already installed in {existing}. " +
                "Installing again replaces it in place and keeps your settings.";
            ExistingBanner.Visibility = Visibility.Visible;
            UninstallButton.Visibility = Visibility.Visible;
            InstallButton.Content = "Reinstall";
        }
    }

    private static bool IsElevated =>
        new System.Security.Principal.WindowsPrincipal(
            System.Security.Principal.WindowsIdentity.GetCurrent())
        .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);

    private SetupChoices Choices => new(
        FolderBox.Text.Trim(),
        DesktopCheck.IsChecked == true,
        _plan.OffersStartWithWindows && StartupCheck.IsChecked == true);

    // ------------------------------------------------------------------ actions

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = $"Where should {_plan.ProductName} be installed?",
            UseDescriptionForTitle = true,
            SelectedPath = Directory.Exists(FolderBox.Text) ? FolderBox.Text : _plan.DefaultDirectory,
            ShowNewFolderButton = true,
        };

        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            // Appended so that picking "D:\Programs" installs into "D:\Programs\ScanBridge"
            // rather than scattering the payload across whatever folder was chosen. Not
            // appended twice if the chosen folder already ends with the product directory.
            string chosen = dialog.SelectedPath;
            string leaf = Path.GetFileName(_plan.DefaultDirectory);

            FolderBox.Text = string.Equals(Path.GetFileName(chosen), leaf, StringComparison.OrdinalIgnoreCase)
                ? chosen
                : Path.Combine(chosen, leaf);
        }
    }

    private async void OnInstall(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        if (string.IsNullOrWhiteSpace(FolderBox.Text))
        {
            MessageBox.Show(this, "Choose a folder to install into.", Title,
                            MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // The server half cannot install from an unelevated process. Relaunching with the
        // choices carried on the command line means Windows raises its own consent prompt,
        // which is the only prompt anyone should trust for this.
        if (_plan.RequiresElevation && !IsElevated)
        {
            if (_plan.RelaunchElevated?.Invoke(Choices) == true) { Close(); return; }

            MessageBox.Show(this,
                "Windows did not grant administrator rights, so nothing was installed.",
                Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // Read on this thread, deliberately, and not inside the lambda below.
        //
        // Choices reads FolderBox.Text and the two checkboxes. RunAsync invokes its callback
        // inside Task.Run, so evaluating Choices there touches WPF controls from a thread that
        // does not own them, and every install died with "The calling thread cannot access this
        // object because a different thread owns it" before it had laid down a single file.
        SetupChoices choices = Choices;

        await RunAsync("Installing", writer => _plan.Install(choices, writer));
    }

    private async void OnUninstall(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        if (MessageBox.Show(this, $"Remove {_plan.ProductName} from this computer?", Title,
                            MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        if (_plan.RequiresElevation && !IsElevated)
        {
            MessageBox.Show(this,
                "Removing this needs administrator rights. Start it again with 'Run as administrator'.",
                Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        await RunAsync("Removing", writer => _plan.Uninstall(writer));
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    // ------------------------------------------------------------------ running

    /// <summary>
    /// Runs the work off the UI thread and streams its output into the window as it happens.
    ///
    /// Streamed rather than shown at the end because an installer that freezes for eight seconds
    /// with no output is one people kill, and this one lays down a hundred megabytes.
    /// </summary>
    private async Task RunAsync(string verb, Func<TextWriter, int> work)
    {
        _busy = true;
        InstallButton.IsEnabled = false;
        UninstallButton.IsEnabled = false;
        BrowseButton.IsEnabled = false;
        FolderBox.IsEnabled = false;
        DesktopCheck.IsEnabled = false;
        StartupCheck.IsEnabled = false;

        LogText.Text = string.Empty;
        LogPanel.Visibility = Visibility.Visible;
        StatusText.Text = verb + "…";

        var writer = new DispatchingWriter(Dispatcher, line =>
        {
            LogText.Text += line;
            LogScroller.ScrollToEnd();
        });

        int code;
        try
        {
            code = await Task.Run(() => work(writer));
        }
        catch (Exception ex)
        {
            writer.WriteLine();
            writer.WriteLine(ex.Message);
            code = 1;
        }

        _busy = false;
        _finished = true;

        StatusText.Text = code == 0 ? $"{verb} finished." : $"{verb} failed (code {code}).";
        InstallButton.Visibility = Visibility.Collapsed;
        UninstallButton.Visibility = Visibility.Collapsed;
        CloseButton.Content = "Close";
        CloseButton.IsDefault = true;
        CloseButton.Focus();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // Closing the window halfway through laying down files would leave a half-installed
        // product with no record of itself.
        if (_busy && !_finished)
        {
            e.Cancel = true;
            return;
        }

        base.OnClosing(e);
    }

    /// <summary>
    /// A TextWriter the installer can write to from a background thread, which lands on the UI
    /// thread. The installers were written to print to the console; this is what lets them keep
    /// doing exactly that.
    /// </summary>
    private sealed class DispatchingWriter(Dispatcher dispatcher, Action<string> append) : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value) => Send(value.ToString());
        public override void Write(string? value) { if (value is not null) Send(value); }
        public override void WriteLine(string? value) => Send((value ?? string.Empty) + Environment.NewLine);
        public override void WriteLine() => Send(Environment.NewLine);

        private void Send(string text) =>
            dispatcher.BeginInvoke(DispatcherPriority.Background, () => append(text));
    }
}
