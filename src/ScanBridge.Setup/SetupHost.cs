using System.IO;
using System.Runtime.Versioning;
using System.Windows;

// WPF and WinForms are both referenced here - WinForms only for its folder picker - and
// each defines an Application and a MessageBox. Aliased rather than fully qualified at every
// use, so that a later edit cannot quietly pick the wrong one.
using Application = System.Windows.Application;

namespace ScanBridge.Setup;

/// <summary>
/// Shows the setup window from either half.
///
/// The client is already a WPF application and has an Application object by the time it decides
/// to show this. The server is a console-subsystem executable that spends most of its life as a
/// service and has neither an Application nor an STA thread. Both are handled here so neither
/// installer has to know which case it is.
/// </summary>
[SupportedOSPlatform("windows")]
public static class SetupHost
{
    /// <summary>
    /// Runs the window to completion and returns a process exit code.
    ///
    /// Creates an Application only if the process does not already have one. Creating a second
    /// throws, and the client would hit that every time.
    /// </summary>
    public static int Run(SetupPlan plan)
    {
        if (Application.Current is not null)
        {
            new SetupWindow(plan).ShowDialog();
            return 0;
        }

        int code = 0;
        Exception? failure = null;

        // WPF requires single-threaded apartment. A console application's main thread is MTA
        // unless it says otherwise, and by the time this is called it is too late to change it —
        // so the window gets a thread of its own.
        var thread = new Thread(() =>
        {
            try
            {
                var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                var window = new SetupWindow(plan);
                window.Closed += (_, _) => application.Shutdown();
                application.Run(window);
            }
            catch (Exception ex)
            {
                failure = ex;
                code = 1;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
        {
            Console.Error.WriteLine($"Could not show the setup window: {failure.Message}");
            Console.Error.WriteLine("Use --install from a command prompt instead.");
        }

        return code;
    }

    /// <summary>
    /// Runs an installer that prints to the console, sending its output somewhere else instead.
    ///
    /// Both installers were written to print progress to a console, and that text is good: it
    /// names every file it lays down with a hash. Rather than rewrite them to take a writer —
    /// which would leave two ways of saying the same thing, and eventually two that disagree —
    /// the console is pointed at the window for the duration of the call.
    /// </summary>
    public static int Capturing(TextWriter writer, Func<int> work)
    {
        TextWriter previousOut = Console.Out;
        TextWriter previousError = Console.Error;

        Console.SetOut(writer);
        Console.SetError(writer);

        try
        {
            return work();
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
        }
    }
}
