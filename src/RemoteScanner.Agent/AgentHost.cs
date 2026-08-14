using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Principal;
using RemoteScanner.Common;
using RemoteScanner.Protocol;
using Serilog;
using CommonLog = RemoteScanner.Common.Log;

namespace RemoteScanner.Agent;

/// <summary>
/// The tray agent's engine: serves the pipe that the DVC plugin inside mstsc.exe connects
/// to, and is the far end of the protocol the virtual data source on the server speaks.
///
/// The session agent on the server relays; this is where enumeration and scan requests are
/// actually answered, because this is the machine the scanner is plugged into.
///
/// One connection per RDP session, since each mstsc.exe loads its own plugin instance.
/// Within a connection, frames are tagged with the stream id of the data source that sent
/// them, so several applications on the server can scan concurrently.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AgentHost : IAsyncDisposable
{
    private readonly ScanHostRunner _runner;
    private readonly ILogger _log = CommonLog.Logger;

    /// <summary>
    /// The key this agent authenticates links with. Not readonly: it is replaced when the
    /// registry turns out to hold the key a peer is actually using — see
    /// <see cref="VerifyAgainstTheStoredSecret"/>.
    /// </summary>
    private byte[] _secret;

    /// <summary>Which bitness of ScanHost enumerated each scanner. TWAIN is bitness-split.</summary>
    private readonly ConcurrentDictionary<string, bool> _scannerBitness = new();

    private readonly ConcurrentDictionary<uint, CancellationTokenSource> _jobs = new();
    private readonly ConcurrentDictionary<uint, FlowController> _flow = new();

    private uint _nextJobId;

    public AgentHost(ScanHostRunner runner, byte[] secret)
    {
        _runner = runner;
        _secret = secret;
    }

    /// <summary>Raised when the connected-session count or scanner list changes, for the UI.</summary>
    public event Action? StateChanged;

    public int ConnectedLinks { get; private set; }

    public IReadOnlyList<ScannerInfo> LastKnownScanners { get; private set; } = Array.Empty<ScannerInfo>();

    /// <summary>
    /// The Remote Desktop connections currently able to use this PC's scanner.
    ///
    /// Exposed because the tray window used to list RDP sessions *hosted by this machine*,
    /// which on a user's own PC is always none — it was showing server-side information in the
    /// client's window and looked permanently broken. What this machine actually knows is which
    /// links reached it, and when.
    /// </summary>
    public IReadOnlyList<RemoteLink> ActiveLinks
    {
        get { lock (_links) return _links.Values.OrderBy(l => l.Id).ToArray(); }
    }

    private readonly Dictionary<int, RemoteLink> _links = new();
    private int _nextLinkId;

    /// <summary>
    /// When a remote session last actually reached each scanner, by scanner id.
    ///
    /// "Which scanner will the remote session get" and "which scanner has the remote session
    /// touched" are different questions, and only the second is evidence. The first is a
    /// setting; it looks identical whether redirection is working perfectly or not working at
    /// all. This records the moment a remote application asked a specific scanner for its
    /// capabilities or told it to scan, which is the first thing that cannot happen unless the
    /// whole chain is alive.
    /// </summary>
    private readonly ConcurrentDictionary<string, DateTime> _remoteUse = new();

    /// <summary>Last time a remote session used this scanner, or null if it never has.</summary>
    public DateTime? LastRemoteUse(string scannerId)
        => _remoteUse.TryGetValue(scannerId, out DateTime when) ? when : null;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        bool first = true;

        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeServerStream server;
            try
            {
                server = SecurePipe.Create(Wire.AgentPipeName, SecurePipe.CurrentUserSid(),
                                           maxInstances: 16, firstInstance: first);
                first = false;
            }
            catch (Exception ex)
            {
                // FILE_FLAG_FIRST_PIPE_INSTANCE failing means another agent already owns the
                // name. Two agents would fight over the scanner, so this one stands down.
                _log.Fatal(ex, "Cannot create the agent pipe. Is another RemoteScanner agent running?");
                throw;
            }

            try
            {
                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception)
            {
                await server.DisposeAsync().ConfigureAwait(false);
                throw;
            }

            _ = Task.Run(() => ServeAsync(server, null, cancellationToken, server), CancellationToken.None);
        }
    }

    /// <summary>
    /// Serves a link whose identity has already been proven — used by the direct network
    /// transport, which must authenticate before it can encrypt and so cannot leave the check
    /// until later.
    /// </summary>
    public Task ServeAuthenticatedAsync(Stream link, string peerName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(link);
        ArgumentException.ThrowIfNullOrEmpty(peerName);

        return ServeAsync(link, peerName, cancellationToken);
    }

    /// <param name="authenticatedPeer">
    /// Null when this connection still has to prove itself — the pipe hop, where the handshake
    /// runs over the same stream it protects. Non-null when it already has.
    /// </param>
    private async Task ServeAsync(Stream server, string? authenticatedPeer,
                                  CancellationToken cancellationToken,
                                  NamedPipeServerStream? pipe = null)
    {
        var channel = new FrameChannel(server, ownsStream: true);

        // Whether this connection was ever counted. Without it, a connection rejected during
        // authentication still ran the decrement in the finally block, so every failed attempt
        // silently subtracted one from a count it had never added to and the tray reported
        // fewer connected sessions than there really were.
        bool counted = false;
        int linkId = 0;

        try
        {
            channel.Start();

            string? peerName = authenticatedPeer
                ?? await AuthenticateAsync(channel, pipe, cancellationToken).ConfigureAwait(false);

            if (peerName is null)
            {
                _log.Warning("Rejected an RDP link: authentication failed.");
                return;
            }

            counted = true;
            linkId = Interlocked.Increment(ref _nextLinkId);
            lock (_links) _links[linkId] = new RemoteLink(linkId, peerName, DateTime.Now);

            ConnectedLinks++;
            StateChanged?.Invoke();
            _log.Information("RDP link established. Active links: {Count}.", ConnectedLinks);

            await ProcessAsync(channel, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "RDP link ended unexpectedly.");
        }
        finally
        {
            await channel.DisposeAsync().ConfigureAwait(false);

            if (counted)
            {
                lock (_links) _links.Remove(linkId);
                if (ConnectedLinks > 0) ConnectedLinks--;
                StateChanged?.Invoke();
                _log.Information("RDP link closed. Active links: {Count}.", ConnectedLinks);
            }
        }
    }

    /// <summary>
    /// Second chance for a failed MAC: check it against the key the registry holds *now*.
    ///
    /// This agent loads the key once, when it starts, and every peer reads it fresh. If the two
    /// ever diverge the agent refuses every link forever, with a message that says only that
    /// they disagree — not which one is stale. That state cost days: the Remote Desktop plugin
    /// was turned away on every attempt, so the channel had no listener, so the server could not
    /// open it at all, and the symptom looked like a broken virtual channel rather than a
    /// rejected handshake.
    ///
    /// Rather than diagnose every cause of divergence, this removes the consequence. The
    /// registry is the authority — it is what every peer reads — so if the peer's MAC verifies
    /// against the stored key, the stored key is the right one and this process adopts it.
    ///
    /// Not a weakening of the check: the peer must still produce a MAC over both nonces with a
    /// key it cannot read without this user's DPAPI key and access to their registry hive. The
    /// only thing that changes is which copy of that key is compared against.
    /// </summary>
    private bool VerifyAgainstTheStoredSecret(byte[] peerNonce, byte[] ourNonce, byte[] presented)
    {
        byte[] stored;
        try
        {
            stored = SecretStore.GetOrCreateLocalSecret();
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Could not re-read the shared secret while checking a link.");
            return false;
        }

        // Same key: the MAC is genuinely wrong and re-checking proves nothing.
        if (CryptographicOperations.FixedTimeEquals(stored, _secret)) return false;

        byte[] expected = ChannelAuth.ComputeMac(stored, ChannelAuth.InitiatorLabel, peerNonce, ourNonce, 0);
        if (!ChannelAuth.Verify(expected, presented)) return false;

        _log.Warning(
            "The shared secret in the registry ({Stored}) is not the one this agent loaded at " +
            "startup ({Loaded}), and the peer is using the stored one. Adopting it; links will " +
            "keep working without a restart.",
            SecretStore.Fingerprint(stored), SecretStore.Fingerprint(_secret));

        _secret = stored;
        return true;
    }

    /// <summary>
    /// Verifies the DVC plugin holds this user's shared secret. The plugin runs inside
    /// mstsc.exe as the same user; anything else on the PC that opens the pipe cannot
    /// produce the MAC and is turned away.
    ///
    /// Returns the peer's machine name, or null when the link was refused.
    /// </summary>
    /// <summary>
    /// Confirms the caller is this same Windows user, by asking the pipe rather than the peer.
    ///
    /// This is the check that actually protects the local hop, and it always was. The pipe is
    /// created with an explicit DACL — <c>D:P(A;;GA;;;SY)(A;;GA;;;&lt;user SID&gt;)</c> — so the
    /// kernel has already refused everyone else before a byte is read. Impersonating the client
    /// and comparing SIDs states that guarantee out loud instead of inferring it.
    ///
    /// It exists because the shared secret proved unreliable in a way I could not explain. On
    /// this machine, components running as the same user, reading the same registry value, at
    /// the same moment, intermittently got two different keys — the tray agent read one, the
    /// Remote Desktop plugin read another, and the registry held a third. Every layer then
    /// behaved perfectly correctly and refused the link, and scanning was dead for days with no
    /// error pointing anywhere near the cause.
    ///
    /// So the secret is no longer the thing standing between a caller and this scanner on this
    /// hop; the pipe's ACL is, checked directly. That is not a weakening — nothing that fails
    /// this check could have opened the pipe in the first place. The network hop is unaffected
    /// and still requires the pairing key, because there is no ACL out there to lean on.
    /// </summary>
    private bool CallerIsThisUser(NamedPipeServerStream pipe)
    {
        try
        {
            string? ours = WindowsIdentity.GetCurrent().User?.Value;
            string? theirs = null;

            pipe.RunAsClient(() => theirs = WindowsIdentity.GetCurrent().User?.Value);

            return ours is not null && theirs is not null &&
                   string.Equals(ours, theirs, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Could not establish who opened the pipe; refusing the link.");
            return false;
        }
    }

    private async Task<string?> AuthenticateAsync(FrameChannel channel, NamedPipeServerStream? pipe,
                                                  CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));

        Frame? helloFrame = await channel.ReadAsync(timeout.Token).ConfigureAwait(false);
        if (helloFrame is not { Type: MessageType.Hello } hello) return null;

        HelloMessage peer = Decode.Hello(hello);
        if (peer.ProtocolVersion != Wire.Version)
        {
            await channel.SendAsync(
                new AuthResultMessage(AuthStatus.VersionMismatch,
                    $"This agent speaks protocol v{Wire.Version}."),
                hello.StreamId, timeout.Token).ConfigureAwait(false);
            return null;
        }

        byte[] nonce = ChannelAuth.NewNonce();
        await channel.SendAsync(
            new HelloAckMessage(Wire.Version, PeerRole.LocalAgent, Environment.MachineName, nonce,
                                PeerCapabilities.JpegEncoding | PeerCapabilities.PngEncoding
                                | PeerCapabilities.FlowControl | PeerCapabilities.Cancellation
                                | PeerCapabilities.Duplex | PeerCapabilities.Adf),
            hello.StreamId, timeout.Token).ConfigureAwait(false);

        Frame? authFrame = await channel.ReadAsync(timeout.Token).ConfigureAwait(false);
        if (authFrame is not { Type: MessageType.Authenticate } auth) return null;

        // Session id is 0 on this hop: it is scoped by the user's DPAPI key and the pipe
        // ACL, not by a terminal services session.
        byte[] presented = Decode.Authenticate(auth).Mac;

        byte[] expected = ChannelAuth.ComputeMac(_secret, ChannelAuth.InitiatorLabel, peer.Nonce, nonce, 0);
        bool ok = ChannelAuth.Verify(expected, presented);

        if (!ok) ok = VerifyAgainstTheStoredSecret(peer.Nonce, nonce, presented);

        if (!ok && pipe is not null && CallerIsThisUser(pipe))
        {
            // The key disagrees but the caller is provably this Windows user, which is the only
            // thing the key was ever standing in for on this hop. Accept, and say plainly that
            // the keys have diverged so it is visible rather than silent.
            _log.Warning(
                "The shared secret does not match — this agent holds key {Key} — but the caller " +
                "is this same Windows user and the pipe only admits this user, so the link is " +
                "accepted. Something is making components read different keys from the same " +
                "registry value; scanning is unaffected.",
                SecretStore.Fingerprint(_secret));

            ok = true;
        }

        if (!ok)
        {
            // Naming the key this process is holding turns an unattributable rejection into a
            // one-line comparison against the other end's startup line.
            _log.Warning(
                "Rejecting a link: the shared secret does not match, the key in the registry " +
                "does not match, and the caller is not this Windows user. This agent is using " +
                "key {Key}.", SecretStore.Fingerprint(_secret));
        }

        await channel.SendAsync(
            new AuthResultMessage(ok ? AuthStatus.Ok : AuthStatus.BadCredentials,
                                  ok ? string.Empty : "Shared secret mismatch."),
            hello.StreamId, timeout.Token).ConfigureAwait(false);

        return ok ? peer.MachineName : null;
    }

    private async Task ProcessAsync(FrameChannel channel, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            Frame? next = await channel.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (next is null) break;

            Frame frame = next.Value;

            switch (frame.Type)
            {
                case MessageType.ScannerEnumRequest:
                    await HandleEnumerateAsync(channel, frame.StreamId, cancellationToken).ConfigureAwait(false);
                    break;

                case MessageType.ScannerCapsRequest:
                    await HandleCapabilitiesAsync(channel, frame, cancellationToken).ConfigureAwait(false);
                    break;

                case MessageType.ScanRequest:
                    // Each scan runs independently so one application's job does not block
                    // another's, or the heartbeat.
                    _ = Task.Run(() => HandleScanAsync(channel, frame, cancellationToken),
                                 CancellationToken.None);
                    break;

                case MessageType.ScanCancel:
                    CancelJob(Decode.ScanCancel(frame).JobId);
                    break;

                case MessageType.FlowCredit:
                {
                    FlowCreditMessage credit = Decode.FlowCredit(frame);
                    if (_flow.TryGetValue(credit.JobId, out FlowController? controller))
                        controller.Grant(credit.Frames);
                    break;
                }

                case MessageType.Heartbeat:
                    await channel.SendAsync(new HeartbeatMessage(DateTime.UtcNow.Ticks),
                                            frame.StreamId, cancellationToken).ConfigureAwait(false);
                    break;

                case MessageType.Disconnect:
                    return;

                default:
                    _log.Debug("Ignoring unexpected {Type} on stream {Stream}.", frame.Type, frame.StreamId);
                    break;
            }
        }
    }

    private async Task HandleEnumerateAsync(FrameChannel channel, uint streamId,
                                            CancellationToken cancellationToken)
    {
        var scanners = new List<ScannerInfo>();

        // Both bitnesses are asked: a 32-bit-only vendor TWAIN driver is invisible to the
        // x64 host and vice versa, and users have no idea which their scanner ships.
        foreach (bool use64Bit in new[] { true, false })
        {
            if (!_runner.IsAvailable(use64Bit)) continue;

            try
            {
                await foreach (Frame frame in _runner.ExecuteAsync(
                    use64Bit, MessageType.ScannerEnumRequest, new ScannerEnumRequestMessage(),
                    cancellationToken).ConfigureAwait(false))
                {
                    if (frame.Type != MessageType.ScannerEnumResponse) continue;

                    foreach (ScannerInfo scanner in Decode.EnumResponse(frame).Scanners)
                    {
                        if (scanners.Any(existing => existing.Id == scanner.Id)) continue;
                        scanners.Add(scanner);
                        _scannerBitness[scanner.Id] = use64Bit;
                    }
                }
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "Enumeration via the {Bitness} host failed.", use64Bit ? "x64" : "x86");
            }
        }

        // The raw list contains each WIA device twice — once natively, once as the TWAIN source
        // Windows' own shim synthesises from it — and is in whatever order the drivers happened
        // to enumerate. The data source on the server binds to index 0, so this ordering is how
        // the user's chosen scanner is selected.
        IReadOnlyList<ScannerInfo> offered =
            ScannerList.Arrange(scanners, AgentConfig.Load().DefaultScannerId);

        LastKnownScanners = offered;
        StateChanged?.Invoke();

        if (offered.Count != scanners.Count)
        {
            _log.Debug("Collapsed {Removed} duplicate WIA-to-TWAIN shim entries.",
                       scanners.Count - offered.Count);
        }

        _log.Information("Reported {Count} scanner(s) to the remote session; first is {Scanner}.",
                         offered.Count, offered.Count > 0 ? offered[0].Name : "(none)");

        await channel.SendAsync(new ScannerEnumResponseMessage(offered), streamId, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task HandleCapabilitiesAsync(FrameChannel channel, Frame frame,
                                               CancellationToken cancellationToken)
    {
        string scannerId = Decode.CapsRequest(frame).ScannerId;
        _remoteUse[scannerId] = DateTime.Now;
        StateChanged?.Invoke();

        bool use64Bit = _scannerBitness.GetValueOrDefault(scannerId, true);

        try
        {
            await foreach (Frame response in _runner.ExecuteAsync(
                use64Bit, MessageType.ScannerCapsRequest, new ScannerCapsRequestMessage(scannerId),
                cancellationToken).ConfigureAwait(false))
            {
                if (response.Type != MessageType.ScannerCapsResponse) continue;

                await channel.SendRawAsync(response.Type, frame.StreamId, response.Payload, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Capability query failed for {Scanner}.", scannerId);
        }

        await channel.SendAsync(new ScannerCapsResponseMessage(
                scannerId, false, Array.Empty<int>(), Array.Empty<PixelType>(), Array.Empty<PaperSize>(),
                ScannerFeatures.None, 0, 0, 0, 0, 0, 0),
            frame.StreamId, cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleScanAsync(FrameChannel channel, Frame frame, CancellationToken cancellationToken)
    {
        ScanRequestMessage request = Decode.ScanRequest(frame);
        _remoteUse[request.ScannerId] = DateTime.Now;
        StateChanged?.Invoke();

        uint jobId = Interlocked.Increment(ref _nextJobId);

        using var job = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _jobs[jobId] = job;

        var flow = new FlowController();
        _flow[jobId] = flow;

        try
        {
            request.Settings.Validate();

            bool use64Bit = _scannerBitness.GetValueOrDefault(request.ScannerId, true);
            _log.Information("Job {Job}: scanning on {Scanner} at {Dpi} dpi ({Bitness} host).",
                             jobId, request.ScannerId, request.Settings.Resolution,
                             use64Bit ? "x64" : "x86");

            await channel.SendAsync(new ScanStartMessage(jobId), frame.StreamId, job.Token)
                .ConfigureAwait(false);

            await foreach (Frame page in _runner.ExecuteAsync(
                use64Bit, MessageType.ScanRequest, request, job.Token).ConfigureAwait(false))
            {
                // Page data is the only thing large enough to need pacing. Blocking here
                // stops a fast ADF filling the RDP send queue and freezing the session.
                if (page.Type == MessageType.ScanPageData)
                    await flow.AcquireAsync(job.Token).ConfigureAwait(false);

                // ScanHost already emits the right message types; only the job id has to be
                // rewritten, since it numbers its own single job 1.
                byte[] payload = Rewrite.JobId(page, jobId);

                await channel.SendRawAsync(page.Type, frame.StreamId, payload, job.Token)
                    .ConfigureAwait(false);
            }

            _log.Information("Job {Job} finished.", jobId);
        }
        catch (OperationCanceledException)
        {
            await TrySendErrorAsync(channel, frame.StreamId, jobId, ScanErrorCode.UserCancelled,
                                    "The scan was cancelled.").ConfigureAwait(false);
        }
        catch (ProtocolException ex)
        {
            await TrySendErrorAsync(channel, frame.StreamId, jobId, ScanErrorCode.UnsupportedSetting,
                                    ex.Message).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Job {Job} failed.", jobId);
            await TrySendErrorAsync(channel, frame.StreamId, jobId, ScanErrorCode.DriverFault,
                                    ex.Message).ConfigureAwait(false);
        }
        finally
        {
            _jobs.TryRemove(jobId, out _);
            _flow.TryRemove(jobId, out _);
            flow.Dispose();
        }
    }

    private async Task TrySendErrorAsync(FrameChannel channel, uint streamId, uint jobId,
                                         ScanErrorCode code, string message)
    {
        try
        {
            await channel.SendAsync(new ScanErrorMessage(jobId, code, message), streamId,
                                    CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The link is already gone; the remote application will see the transfer fail.
        }
    }

    private void CancelJob(uint jobId)
    {
        if (_jobs.TryGetValue(jobId, out CancellationTokenSource? job))
        {
            _log.Information("Job {Job} cancelled by the remote application.", jobId);
            job.Cancel();
        }
    }

    public ValueTask DisposeAsync()
    {
        foreach (var job in _jobs.Values) job.Cancel();
        _jobs.Clear();
        foreach (var flow in _flow.Values) flow.Dispose();
        _flow.Clear();
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Frame decoding helpers. PayloadReader is a ref struct and cannot live across an await,
/// so every decode is pulled into its own synchronous method.
/// </summary>
internal static class Decode
{
    public static HelloMessage Hello(Frame frame)
    {
        var reader = frame.Reader();
        return HelloMessage.Read(ref reader);
    }

    public static AuthenticateMessage Authenticate(Frame frame)
    {
        var reader = frame.Reader();
        return AuthenticateMessage.Read(ref reader);
    }

    public static ScannerEnumResponseMessage EnumResponse(Frame frame)
    {
        var reader = frame.Reader();
        return ScannerEnumResponseMessage.Read(ref reader);
    }

    public static ScannerCapsRequestMessage CapsRequest(Frame frame)
    {
        var reader = frame.Reader();
        return ScannerCapsRequestMessage.Read(ref reader);
    }

    public static ScanRequestMessage ScanRequest(Frame frame)
    {
        var reader = frame.Reader();
        return ScanRequestMessage.Read(ref reader);
    }

    public static ScanCancelMessage ScanCancel(Frame frame)
    {
        var reader = frame.Reader();
        return ScanCancelMessage.Read(ref reader);
    }

    public static FlowCreditMessage FlowCredit(Frame frame)
    {
        var reader = frame.Reader();
        return FlowCreditMessage.Read(ref reader);
    }
}

internal static class Rewrite
{
    /// <summary>
    /// Every scan message begins with a u32 job id. ScanHost numbers its single job 1, so
    /// the agent's real job id is patched in place rather than decoding and re-encoding a
    /// 32 KB page frame.
    /// </summary>
    public static byte[] JobId(Frame frame, uint jobId)
    {
        if (frame.Payload.Length < sizeof(uint)) return frame.Payload;

        bool carriesJobId = frame.Type is MessageType.ScanStart or MessageType.ScanPageBegin
            or MessageType.ScanPageData or MessageType.ScanPageEnd or MessageType.ScanProgress
            or MessageType.ScanComplete or MessageType.ScanError;

        if (!carriesJobId) return frame.Payload;

        byte[] payload = frame.Payload;
        BitConverter.TryWriteBytes(payload.AsSpan(0, sizeof(uint)), jobId);
        return payload;
    }
}
