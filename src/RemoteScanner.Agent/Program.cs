using System.Runtime.Versioning;
using RemoteScanner.Common;
using RemoteScanner.Rdp;

namespace RemoteScanner.Agent;

/// <summary>
/// Headless entry point for the local tray agent.
///
/// Runs on the user's own PC, owns the physical scanners, and serves the pipe that the DVC
/// plugin inside mstsc.exe connects to. The WPF tray UI hosts this same class; this entry
/// point exists so the agent can be run from a console for diagnostics and integration tests.
/// </summary>
[SupportedOSPlatform("windows")]
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Log.Initialize("agent");
        var log = Log.Logger;

        try
        {
            AppPaths.EnsureDirectories();

            if (args.Contains("--pairing-code"))
            {
                // Before anything is checked: reading the code must work on a machine where
                // scanning does not, because that is exactly when somebody needs it.
                Console.WriteLine(PairingCode.Format(SecretStore.GetOrCreatePairingSeed()));
                return 0;
            }

            var runner = new ScanHostRunner(ResolveInstallDirectory());

            if (!runner.IsAvailable(true) && !runner.IsAvailable(false))
            {
                log.Fatal("Neither ScanHost build was found under {Directory}. " +
                          "The agent cannot reach any scanner.", ResolveInstallDirectory());
                return 2;
            }

            if (args.Contains("--pairing-code"))
            {
                // Same value the tray window shows. Here as well so it can be read without a
                // desktop — over a support call, or from a script.
                Console.WriteLine(PairingCode.Format(SecretStore.GetOrCreatePairingSeed()));
                return 0;
            }

            byte[] secret = SecretStore.GetOrCreateLocalSecret();

            await using var host = new AgentHost(runner, secret);

            using var shutdown = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                shutdown.Cancel();
            };

            foreach (RdpSession session in RdpSessionInfo.Enumerate().Where(s => s.IsUsable))
                log.Information("Existing RDP session {Session} to {Client}.", session.SessionId, session.ClientName);

            log.Information("Agent listening on pipe '{Pipe}'.", Protocol.Wire.AgentPipeName);

            if (args.Contains("--enumerate-once"))
            {
                // Diagnostic mode: prove the scanner stack works without any RDP involved.
                await ReportScannersAsync(runner, log, shutdown.Token).ConfigureAwait(false);
                return 0;
            }

            // Direct connections from Remote Desktop servers, for when the virtual channel
            // cannot carry data. Runs alongside the pipe; whichever a server reaches us on,
            // everything above the transport is the same.
            var lan = new LanListener(
                host, () => PairingCode.KeyFor(SecretStore.GetOrCreatePairingSeed()));
            Task listening = lan.RunAsync(shutdown.Token);

            await host.RunAsync(shutdown.Token).ConfigureAwait(false);
            await listening.ConfigureAwait(false);
            return 0;
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (Exception ex)
        {
            log.Fatal(ex, "Agent terminated unexpectedly.");
            return 1;
        }
        finally
        {
            Log.Shutdown();
        }
    }

    /// <summary>
    /// Lists what the scanner stack can see, with no RDP in the picture. This is the first
    /// thing to run when a user reports "no scanners in the remote session" — it separates a
    /// driver problem from a redirection problem.
    /// </summary>
    private static async Task ReportScannersAsync(ScanHostRunner runner, Serilog.ILogger log,
                                                  CancellationToken cancellationToken)
    {
        foreach (bool use64Bit in new[] { true, false })
        {
            if (!runner.IsAvailable(use64Bit))
            {
                Console.WriteLine($"{(use64Bit ? "x64" : "x86")} host: not installed");
                continue;
            }

            try
            {
                await foreach (Protocol.Frame frame in runner.ExecuteAsync(
                    use64Bit, Protocol.MessageType.ScannerEnumRequest,
                    new Protocol.ScannerEnumRequestMessage(), cancellationToken).ConfigureAwait(false))
                {
                    if (frame.Type != Protocol.MessageType.ScannerEnumResponse) continue;

                    var response = Decode.EnumResponse(frame);
                    Console.WriteLine($"{(use64Bit ? "x64" : "x86")} host: {response.Scanners.Count} scanner(s)");

                    foreach (var scanner in response.Scanners)
                    {
                        Console.WriteLine($"    {scanner.Name}  [{scanner.Interface}]  {scanner.Vendor}");
                        await ReportCapabilitiesAsync(runner, use64Bit, scanner.Id, cancellationToken)
                            .ConfigureAwait(false);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"{(use64Bit ? "x64" : "x86")} host: failed - {ex.Message}");
                log.Warning(ex, "Diagnostic enumeration failed on the {Bitness} host.", use64Bit ? "x64" : "x86");
            }
        }
    }

    /// <summary>
    /// Queries and prints what a device can actually do. This exercises the same capability
    /// path the virtual data source uses to build its TWAIN capability set, so if the remote
    /// application shows the wrong resolutions this is where to look first.
    /// </summary>
    private static async Task ReportCapabilitiesAsync(ScanHostRunner runner, bool use64Bit,
                                                      string scannerId, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (Protocol.Frame frame in runner.ExecuteAsync(
                use64Bit, Protocol.MessageType.ScannerCapsRequest,
                new Protocol.ScannerCapsRequestMessage(scannerId), cancellationToken).ConfigureAwait(false))
            {
                if (frame.Type != Protocol.MessageType.ScannerCapsResponse) continue;

                Protocol.ScannerCapsResponseMessage caps = DecodeCaps(frame);
                if (!caps.Found)
                {
                    Console.WriteLine("        capabilities: unavailable");
                    continue;
                }

                Console.WriteLine($"        dpi        : {string.Join(", ", caps.Resolutions)}");
                Console.WriteLine($"        colour     : {string.Join(", ", caps.PixelTypes)}");
                Console.WriteLine($"        paper      : {string.Join(", ", caps.PaperSizes)}");
                Console.WriteLine($"        features   : {caps.Features}");
                Console.WriteLine($"        bed (in)   : {caps.MaxWidthThousandthsInch / 1000.0:0.##} x " +
                                  $"{caps.MaxHeightThousandthsInch / 1000.0:0.##}");
                if (caps.BrightnessMax > caps.BrightnessMin)
                    Console.WriteLine($"        brightness : {caps.BrightnessMin} .. {caps.BrightnessMax}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"        capabilities: failed - {ex.Message}");
        }
    }

    private static Protocol.ScannerCapsResponseMessage DecodeCaps(Protocol.Frame frame)
    {
        var reader = frame.Reader();
        return Protocol.ScannerCapsResponseMessage.Read(ref reader);
    }

    /// <summary>
    /// ScanHost lives in x64\ and x86\ beside the agent once installed. During development
    /// the build output is laid out differently, so a couple of fallbacks are tried.
    /// </summary>
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
