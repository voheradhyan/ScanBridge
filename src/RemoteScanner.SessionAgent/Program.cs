using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using RemoteScanner.Common;
using RemoteScanner.Rdp;

namespace RemoteScanner.SessionAgent;

/// <summary>
/// One instance runs per RDP session, as the logged-on user, launched by
/// RemoteScanner.Service when the session connects.
///
/// It must run in the user's session rather than in session 0: WTSVirtualChannelOpenEx is
/// scoped to the calling process's session, and the pipe it serves has to be reachable by
/// data sources loaded into that user's applications.
/// </summary>
[SupportedOSPlatform("windows")]
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Log.Initialize("sessionagent");
        var log = Log.Logger;

        try
        {
            // Pairing runs and exits; it is a setup step, not a mode of the agent.
            string? pairArgument = args.FirstOrDefault(a => a.StartsWith("--pair", StringComparison.OrdinalIgnoreCase));
            if (pairArgument is not null)
                return Pair(args, log);

            uint sessionId = RdpSessionInfo.CurrentSessionId;

            if (sessionId == 0)
            {
                log.Error("Refusing to run in session 0. This agent must run inside the user's session.");
                return 2;
            }

            RdpSession? session = RdpSessionInfo.Describe(sessionId);
            if (session is null)
            {
                log.Error("Cannot describe session {Session}.", sessionId);
                return 3;
            }

            // Loopback replaces the RDP hop with a direct connection to the tray agent on this
            // same PC, so the data source, the protocol, the relay, the tray agent, ScanHost
            // and the physical scanner can all be tested together without a server.
            bool loopback = args.Contains("--loopback");

            // Direct connection only, skipping the virtual channel. "--lan" alone dials this
            // machine, which is how the transport is tested without a second PC; "--lan=<host>"
            // dials a named one. Diagnostic: production picks its own transport.
            string? lanTarget = args
                .FirstOrDefault(a => a.Equals("--lan", StringComparison.OrdinalIgnoreCase) ||
                                     a.StartsWith("--lan=", StringComparison.OrdinalIgnoreCase));

            string lanAddress = lanTarget is null ? string.Empty
                : lanTarget.Contains('=') ? lanTarget[(lanTarget.IndexOf('=') + 1)..]
                : "127.0.0.1";

            if (loopback && lanTarget is not null)
            {
                log.Error("--loopback and --lan are different transports; pass one or the other.");
                return 6;
            }

            if (loopback && session.IsRemote)
            {
                // In a real remote session this would quietly scan the server's own hardware
                // instead of the user's, which is the opposite of the product's purpose and
                // would look like success.
                log.Error("--loopback cannot be used inside a remote session: it would bypass " +
                          "the redirection it exists to test.");
                return 5;
            }

            if (!session.IsRemote && !loopback && lanTarget is null && !args.Contains("--allow-local"))
            {
                // Running on the console is legitimate for diagnostics but pointless in
                // production: there is no RDP client to reach, so say so and exit.
                log.Warning("Session {Session} is not a remote session; nothing to redirect. " +
                            "Pass --allow-local to run anyway, or --loopback to self-test.", sessionId);
                return 4;
            }

            StopOrphanedAgents(sessionId);

            AppPaths.EnsureDirectories(sessionId);

            // Publish the client machine name so the data source can title itself
            // "Remote Scanner (DESKTOP-XXXX)" before it has connected to anything.
            if (!string.IsNullOrEmpty(session.ClientName))
                SecretStore.SetSessionClientName(sessionId, session.ClientName);

            byte[] secret = SecretStore.GetOrCreateSessionSecret(sessionId);

            log.Information(
                "Session agent starting: session {Session}, user {User}, client {Client} ({Address}).",
                sessionId, session.UserName, session.ClientName, session.ClientAddress);

            using var shutdown = new CancellationTokenSource();

            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                shutdown.Cancel();
            };

            // The service signals a clean stop by closing this event rather than killing us,
            // so spooled pages get purged and the channel is closed politely.
            AppDomain.CurrentDomain.ProcessExit += (_, _) => shutdown.Cancel();

            RelayTransport transport =
                loopback ? RelayTransport.Loopback :
                lanTarget is not null ? RelayTransport.Direct :
                RelayTransport.Auto;

            string clientAddress = lanTarget is not null ? lanAddress : session.ClientAddress;

            await using var relay = new Relay(
                sessionId, secret, SecurePipe.CurrentUserSid(), clientAddress, transport);
            await relay.RunAsync(shutdown.Token).ConfigureAwait(false);

            AppPaths.PurgeSpool(sessionId);
            SecretStore.ClearSession(sessionId);

            log.Information("Session agent for session {Session} stopped.", sessionId);
            return 0;
        }
        catch (Exception ex)
        {
            log.Fatal(ex, "Session agent terminated unexpectedly.");
            return 1;
        }
        finally
        {
            Log.Shutdown();
        }
    }

    /// <summary>
    /// Stores the pairing code for this user, on this server.
    ///
    /// Per user and per server rather than machine-wide, deliberately: on a session host there
    /// are other people's sessions, and a key kept where they could read it would let any of
    /// them reach this user's scanner and the documents on it.
    /// </summary>
    private static int Pair(string[] args, Serilog.ILogger log)
    {
        string code = string.Join(
            string.Empty,
            args.Where(a => !a.StartsWith("--", StringComparison.OrdinalIgnoreCase)));

        string? inline = args.FirstOrDefault(a => a.StartsWith("--pair=", StringComparison.OrdinalIgnoreCase));
        if (inline is not null) code = inline["--pair=".Length..];

        if (string.IsNullOrWhiteSpace(code))
        {
            Console.Write("Pairing code from the PC with the scanner: ");
            code = Console.ReadLine() ?? string.Empty;
        }

        try
        {
            byte[] key = PairingCode.ToKey(code);
            SecretStore.SetPairingKey(key);

            Console.WriteLine();
            Console.WriteLine($"  Paired. This session will use key {SecretStore.Fingerprint(key)}.");
            Console.WriteLine("  Scanning will now work even if the Remote Desktop channel cannot carry data.");
            Console.WriteLine();

            log.Information("Paired with a client PC; pairing key {Key}.", SecretStore.Fingerprint(key));
            return 0;
        }
        catch (FormatException ex)
        {
            Console.WriteLine();
            Console.WriteLine($"  That code was not accepted: {ex.Message}");
            Console.WriteLine();
            return 7;
        }
    }

    /// <summary>
    /// Ends any earlier session agent still running in this session.
    ///
    /// Exactly one agent may serve a session, and the pipe it listens on enforces that with
    /// FILE_FLAG_FIRST_PIPE_INSTANCE — but only against a second agent starting while the first
    /// still holds it. The damaging case is an agent that outlives the thing that started it:
    /// reinstalling or restarting the service loses the handles it tracked, leaving the old
    /// process running with a virtual channel that is no longer connected to anything.
    ///
    /// That orphan keeps the pipe. A data source then connects to it, authenticates against it
    /// perfectly — the shared secret is the user's, not the process's — sends its request into
    /// a dead channel, and waits until it times out. Every application in the session reports
    /// the scanner as offline while a healthy new agent sits alongside, unable to take the pipe.
    ///
    /// Killing is appropriate rather than harsh: an agent with no live channel can do nothing
    /// but absorb requests, and the one starting now is the one the service expects to serve
    /// this session.
    /// </summary>
    private static void StopOrphanedAgents(uint sessionId)
    {
        var log = Log.Logger;
        int self = Environment.ProcessId;

        foreach (Process other in Process.GetProcessesByName("RemoteScanner.SessionAgent"))
        {
            using (other)
            {
                try
                {
                    if (other.Id == self || (uint)other.SessionId != sessionId) continue;

                    // Not {Pid}: every log line is already enriched with the writing process's
                    // id under that name, and reusing it here overwrites the enriched value, so
                    // the line appears to come from the process being killed rather than the one
                    // killing it.
                    log.Warning("Stopping an earlier session agent (pid {OrphanPid}) left running " +
                                "in session {Session}; it would hold the pipe this one needs.",
                                other.Id, sessionId);

                    other.Kill(entireProcessTree: true);
                    other.WaitForExit(5000);
                }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
                {
                    // Exited between enumeration and the kill, or belongs to another user and
                    // cannot be touched. Neither is worth failing startup over: if it really is
                    // still holding the pipe, the pipe creation below reports that clearly.
                    log.Debug(ex, "Could not stop a previous session agent.");
                }
            }
        }
    }
}
