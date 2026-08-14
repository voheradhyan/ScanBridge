using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.Principal;
using RemoteScanner.Common;
using RemoteScanner.Protocol;
using RemoteScanner.Rdp;
using Serilog;
using CommonLog = RemoteScanner.Common.Log;

namespace RemoteScanner.SessionAgent;

/// <summary>
/// Splices the virtual TWAIN data sources running in this RDP session onto the single
/// dynamic virtual channel back to the user's local PC.
///
/// Several applications can scan at once — Acrobat and an ERP, say — so each pipe client is
/// given a stream id and frames are re-tagged as they cross. The data sources themselves
/// always use stream 1 and know nothing about the multiplexing.
///
/// This process is the local authentication endpoint (it proves the shared secret to each
/// data source) but *not* the protocol endpoint: enumeration and scan messages are relayed
/// untouched to the tray agent on the client PC, which owns the real scanner.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class Relay : IAsyncDisposable
{
    private readonly uint _sessionId;
    private readonly byte[] _secret;
    private readonly SecurityIdentifier _userSid;
    private readonly string _pipeName;
    private readonly ILogger _log = CommonLog.Logger;

    private readonly ConcurrentDictionary<uint, FrameChannel> _clients = new();
    private uint _nextStreamId;

    private readonly RelayTransport _transport;

    private readonly string _clientAddress;

    private FrameChannel? _channel;
    private WtsVirtualChannel? _dvc;
    private LoopbackLink? _loopbackLink;
    private LanLink? _lanLink;

    /// <summary>
    /// Set once the virtual channel has been shown to open without carrying data. Sticky for
    /// the life of the session agent: re-testing it on every reconnect would put the
    /// confirmation timeout in front of the user each time, for a channel already known to be
    /// no use.
    /// </summary>
    private bool _channelIsUseless;

    /// <summary>
    /// How many times the virtual channel may fail to open before the direct connection is
    /// tried instead. Not one: a channel is legitimately unavailable for a few seconds while a
    /// session is still coming up, and giving up immediately would take the good transport away
    /// from everybody whose setup is fine.
    /// </summary>
    private const int ChannelOpenAttempts = 4;

    private int _channelOpenFailures;

    /// <summary>
    /// Stream id used for the channel confirmation exchange. Data sources are numbered from
    /// 1 by <see cref="_nextStreamId"/>, so zero belongs to nothing else.
    /// </summary>
    private const uint ProbeStreamId = 0;

    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);

    private TaskCompletionSource? _echo;

    /// <param name="clientAddress">
    /// Where the Remote Desktop client is, as the session itself reports it. Used only when the
    /// virtual channel turns out not to carry data; empty is tolerated, and simply means there
    /// is no fallback available.
    /// </param>
    public Relay(uint sessionId, byte[] secret, SecurityIdentifier userSid,
                 string clientAddress = "", RelayTransport transport = RelayTransport.Auto)
    {
        _sessionId = sessionId;
        _secret = secret;
        _userSid = userSid;
        _clientAddress = clientAddress;
        _transport = transport;
        _pipeName = Wire.SessionPipeName(userSid.Value, sessionId);
    }

    /// <summary>
    /// Runs until cancelled. Reconnects the virtual channel whenever the RDP session drops
    /// and comes back, so the user never has to restart anything after a reconnect.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var backoff = TimeSpan.FromSeconds(1);

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(cancellationToken).ConfigureAwait(false);
                backoff = TimeSpan.FromSeconds(1);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.Warning("Virtual channel unavailable ({Reason}); retrying in {Delay}.",
                             ex.Message, backoff);
            }

            await CloseChannelAsync().ConfigureAwait(false);

            try { await Task.Delay(backoff, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }

            // Cap the backoff: an idle disconnected session should poll rarely, but a user
            // reconnecting must not wait minutes for their scanner to reappear.
            backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, 30));
        }

        await CloseChannelAsync().ConfigureAwait(false);
    }

    private async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        if (_transport == RelayTransport.Loopback)
        {
            // Diagnostic path: the tray agent is on this machine, so there is no RDP hop to
            // make. Everything downstream of here is identical to production.
            _loopbackLink = await LoopbackLink.ConnectAsync(cancellationToken).ConfigureAwait(false);

            _log.Information("Loopback transport in use for session {Session}; " +
                             "no RDP virtual channel is involved.", _sessionId);

            await ServeAsync(_loopbackLink.Stream, cancellationToken).ConfigureAwait(false);
            return;
        }

        // The virtual channel first, always: it needs no ports and no firewall rules, and it
        // rides the encrypted RDP connection that already exists. It is only given up on once
        // it has been shown not to carry data.
        if (_transport == RelayTransport.Auto && !_channelIsUseless)
        {
            try
            {
                _dvc = WtsVirtualChannel.Open(Wire.DvcChannelName);
                _channelOpenFailures = 0;

                _log.Information("Virtual channel '{Channel}' open for session {Session}.",
                                 Wire.DvcChannelName, _sessionId);

                await ServeAsync(_dvc.Stream, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (ChannelCarriesNothingException ex)
            {
                // Do not keep paying the confirmation timeout on every reconnect for the rest
                // of this session; the channel has had its chance.
                _channelIsUseless = true;

                _log.Warning("{Reason} Falling back to a direct connection to {Client}.",
                             ex.Message, _clientAddress);

                await CloseChannelAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The channel would not open at all. Usually the client's plugin declined it —
                // it is not loaded, it is blocked by policy, or the tray agent turned it away.
                // Retrying forever was the old behaviour and it meant no scanning at all, with
                // a message that blamed the scanner. After a few honest attempts, try the
                // direct connection instead; if that is not set up either, the error says so.
                _channelOpenFailures++;
                await CloseChannelAsync().ConfigureAwait(false);

                if (_channelOpenFailures < ChannelOpenAttempts || string.IsNullOrEmpty(_clientAddress))
                    throw;

                _channelIsUseless = true;

                _log.Warning(
                    "The RDP virtual channel has not opened in {Attempts} attempts ({Reason}) " +
                    "Falling back to a direct connection to {Client}.",
                    _channelOpenFailures, ex.Message, _clientAddress);
            }
        }

        if (string.IsNullOrEmpty(_clientAddress))
        {
            throw new IOException(
                "The RDP virtual channel is not carrying data and this session does not report " +
                "a client address, so there is nowhere to connect directly. Scanning cannot work " +
                "for this session.");
        }

        // The direct connection is authenticated with a pairing key, not the session secret.
        // The session secret protects the hop between the data source and this process, both
        // of which are on this server. The PC with the scanner is a different machine and a
        // different account and has never seen it; the pairing key is the only thing the two
        // ends can both hold.
        byte[]? pairing = SecretStore.TryGetPairingKey();
        if (pairing is null)
        {
            throw new IOException(
                "The RDP virtual channel is not carrying data, and this session has not been " +
                "paired with the PC that has the scanner, so there is no way to reach it. " +
                "Run PAIR-WITH-MY-PC.bat in this session and enter the code shown by Remote " +
                "Scanner on that PC.");
        }

        _lanLink = await LanLink.ConnectAsync(_clientAddress, pairing, cancellationToken)
            .ConfigureAwait(false);

        _log.Information("Direct connection in use for session {Session}; the RDP virtual " +
                         "channel is not carrying data.", _sessionId);

        await ServeAsync(_lanLink.Stream, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs the relay over whichever transport it is given, until something ends it.
    /// </summary>
    private async Task ServeAsync(Stream transport, CancellationToken cancellationToken)
    {
        _channel = new FrameChannel(transport, ownsStream: false);
        _channel.Start();

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        Task inbound = PumpChannelToClientsAsync(_channel, linked.Token);
        Task listener = AcceptClientsAsync(linked.Token);
        Task confirm = ConfirmChannelAsync(_channel, linked.Token);

        Task finished = await Task.WhenAny(inbound, listener, confirm).ConfigureAwait(false);
        linked.Cancel();

        // Surface whichever side failed; the outer loop decides whether to retry.
        await finished.ConfigureAwait(false);
    }

    /// <summary>
    /// The virtual channel opened but nothing crossed it. Distinct from every other failure
    /// because it is the only one worth switching transports over — anything else is a
    /// connection that dropped and will be retried.
    /// </summary>
    private sealed class ChannelCarriesNothingException(string message) : IOException(message);

    /// <summary>
    /// Proves the channel actually carries data in both directions before anything relies on it.
    ///
    /// An open channel means only that the client is running a plugin of the right name. It
    /// does not mean a byte can cross, and for a long time none could: the request was written,
    /// accepted, and never delivered, while both ends logged themselves as healthy and the
    /// user was told their scanner was offline. A link that reports itself up without ever
    /// having carried anything is worse than no link, because it sends everyone looking at the
    /// scanner instead.
    ///
    /// A heartbeat is used rather than a new message because the tray agent already answers
    /// one, so this works against any build and costs nothing on the wire.
    ///
    /// On success this never returns: it is one of the tasks the caller races, and completing
    /// would be read as the channel ending.
    /// </summary>
    private async Task ConfirmChannelAsync(FrameChannel channel, CancellationToken cancellationToken)
    {
        var echoed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _echo = echoed;

        long started = Environment.TickCount64;

        await channel.SendAsync(new HeartbeatMessage(DateTime.UtcNow.Ticks),
                                ProbeStreamId, cancellationToken).ConfigureAwait(false);

        Task expired = Task.Delay(ProbeTimeout, cancellationToken);
        Task first = await Task.WhenAny(echoed.Task, expired).ConfigureAwait(false);

        if (first != echoed.Task)
        {
            throw new ChannelCarriesNothingException(
                $"The RDP virtual channel opened but carried nothing: no answer to a heartbeat " +
                $"in {ProbeTimeout.TotalSeconds:0} seconds.");
        }

        _log.Information("Channel to the client PC confirmed: heartbeat answered in {Elapsed} ms.",
                         Environment.TickCount64 - started);

        await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Accepts data sources connecting from applications in this session.</summary>
    private async Task AcceptClientsAsync(CancellationToken cancellationToken)
    {
        bool first = true;

        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeServerStream server = SecurePipe.Create(
                _pipeName, _userSid, maxInstances: 16, firstInstance: first);
            first = false;

            try
            {
                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception)
            {
                await server.DisposeAsync().ConfigureAwait(false);
                throw;
            }

            // Each data source is handled independently: one misbehaving application must
            // not stall the others or the channel.
            _ = Task.Run(() => ServeClientAsync(server, cancellationToken), CancellationToken.None);
        }
    }

    private async Task ServeClientAsync(NamedPipeServerStream server, CancellationToken cancellationToken)
    {
        uint streamId = Interlocked.Increment(ref _nextStreamId);
        var client = new FrameChannel(server, ownsStream: true);

        try
        {
            client.Start();

            if (!await AuthenticateAsync(client, cancellationToken).ConfigureAwait(false))
            {
                _log.Warning("Rejected a data source on stream {Stream}: authentication failed.", streamId);
                return;
            }

            _clients[streamId] = client;
            _log.Information("Data source attached on stream {Stream}.", streamId);

            await PumpClientToChannelAsync(client, streamId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Session shutting down.
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Data source on stream {Stream} ended unexpectedly.", streamId);
        }
        finally
        {
            _clients.TryRemove(streamId, out _);
            await client.DisposeAsync().ConfigureAwait(false);
            _log.Information("Data source on stream {Stream} detached.", streamId);
        }
    }

    /// <summary>
    /// Proves to the data source that we hold the session's shared secret, and checks that
    /// it does too. This is what stops another process in the same session from opening the
    /// pipe and harvesting scanned documents.
    /// </summary>
    private async Task<bool> AuthenticateAsync(FrameChannel client, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));

        Frame? helloFrame = await client.ReadAsync(timeout.Token).ConfigureAwait(false);
        if (helloFrame is not { Type: MessageType.Hello } hello) return false;

        HelloMessage peer = ParseHello(hello);

        if (peer.ProtocolVersion != Wire.Version)
        {
            await client.SendAsync(
                new AuthResultMessage(AuthStatus.VersionMismatch,
                    $"This server speaks protocol v{Wire.Version}; the data source offered v{peer.ProtocolVersion}."),
                hello.StreamId, timeout.Token).ConfigureAwait(false);
            return false;
        }

        byte[] nonce = ChannelAuth.NewNonce();
        await client.SendAsync(
            new HelloAckMessage(Wire.Version, PeerRole.SessionAgent, Environment.MachineName, nonce,
                                PeerCapabilities.FlowControl | PeerCapabilities.Cancellation),
            hello.StreamId, timeout.Token).ConfigureAwait(false);

        Frame? authFrame = await client.ReadAsync(timeout.Token).ConfigureAwait(false);
        if (authFrame is not { Type: MessageType.Authenticate } auth) return false;

        AuthenticateMessage presented = ParseAuthenticate(auth);

        byte[] expected = ChannelAuth.ComputeMac(
            _secret, ChannelAuth.InitiatorLabel, peer.Nonce, nonce, _sessionId);

        bool ok = ChannelAuth.Verify(expected, presented.Mac);

        await client.SendAsync(
            new AuthResultMessage(ok ? AuthStatus.Ok : AuthStatus.BadCredentials,
                                  ok ? string.Empty : "Shared secret mismatch."),
            hello.StreamId, timeout.Token).ConfigureAwait(false);

        return ok;
    }

    // PayloadReader is a ref struct, so it cannot live across an await. Decoding is pulled
    // into these helpers rather than inlined in the async handshake.
    private static HelloMessage ParseHello(Frame frame)
    {
        var reader = frame.Reader();
        return HelloMessage.Read(ref reader);
    }

    private static AuthenticateMessage ParseAuthenticate(Frame frame)
    {
        var reader = frame.Reader();
        return AuthenticateMessage.Read(ref reader);
    }

    /// <summary>Data source -> client PC. Frames are re-tagged with the client's stream id.</summary>
    private async Task PumpClientToChannelAsync(
        FrameChannel client, uint streamId, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            Frame? frame = await client.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (frame is null) break;

            FrameChannel? channel = _channel;

            // This used to break out silently, which produced the least diagnosable failure in
            // the whole product: the data source connected, authenticated, sent its request and
            // then waited fifteen seconds for a reply that had been dropped on the floor here,
            // with nothing written anywhere to say so. The application reported only that the
            // scanner was offline.
            //
            // Throwing also ends this data source's connection immediately, so the application
            // gets a prompt error instead of a fifteen-second stall. The channel itself is
            // recovered by the inbound pump, which fails and drives the reconnect in RunAsync.
            if (channel is null || channel.IsFaulted)
            {
                _log.Warning(
                    "Dropping {Type} from stream {Stream}: the virtual channel to the client PC " +
                    "is {State}. The application will see the scanner as unavailable.",
                    frame.Value.Type, streamId, channel is null ? "not open" : "faulted");

                throw new IOException(
                    "The RDP virtual channel is unavailable; the data source cannot be served.");
            }

            await channel.SendRawAsync(frame.Value.Type, streamId, frame.Value.Payload, cancellationToken)
                .ConfigureAwait(false);

            LogCrossing("-> client PC", frame.Value.Type, streamId, frame.Value.Payload.Length);
        }
    }

    /// <summary>
    /// Client PC -> data source. Frames are routed by stream id and re-tagged back to 1,
    /// which is the only stream id a data source knows about.
    /// </summary>
    private async Task PumpChannelToClientsAsync(FrameChannel channel, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            Frame? frame = await channel.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (frame is null)
                throw new IOException("The RDP virtual channel closed.");

            LogCrossing("<- client PC", frame.Value.Type, frame.Value.StreamId, frame.Value.Payload.Length);

            // The confirmation echo belongs to no data source; it is proof the channel works.
            if (frame.Value is { Type: MessageType.Heartbeat, StreamId: ProbeStreamId })
            {
                _echo?.TrySetResult();
                continue;
            }

            if (!_clients.TryGetValue(frame.Value.StreamId, out FrameChannel? client))
            {
                // The application closed while a reply was in flight. Dropping it is correct;
                // the far end will see the stream go quiet and clean up.
                _log.Debug("Dropped {Type} for unknown stream {Stream}.",
                           frame.Value.Type, frame.Value.StreamId);
                continue;
            }

            try
            {
                await client.SendRawAsync(frame.Value.Type, 1, frame.Value.Payload, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.Debug(ex, "Stream {Stream} could not be written; dropping it.", frame.Value.StreamId);
                _clients.TryRemove(frame.Value.StreamId, out _);
            }
        }
    }

    /// <summary>
    /// Records a frame crossing the RDP virtual channel.
    ///
    /// Control frames are logged at Information — that is, on by default, with no diagnostic
    /// mode to enable first. There are only a handful per scan (ask for scanners, answer, start,
    /// finish) so the cost is nothing, and they are exactly the frames that matter when the
    /// question is "did the request reach the user's PC?".
    ///
    /// This is deliberately not behind a switch. The one time the answer was needed, it sat
    /// behind a step that had to be performed before reproducing the fault, and so it was not
    /// there when the logs were finally collected. A diagnostic that is only present when
    /// somebody predicted they would need it is not a diagnostic.
    ///
    /// Page data is the exception and stays at Debug: a single A4 page at 300 dpi is hundreds
    /// of frames, and logging each one would bury everything else.
    /// </summary>
    private void LogCrossing(string direction, MessageType type, uint streamId, int payloadBytes)
    {
        // FlowCredit is held back with the page data, not counted as control traffic: one is
        // returned for roughly every data frame, so at Information a single page would bury the
        // handful of lines that actually say what happened.
        if (type is MessageType.ScanPageData or MessageType.FlowCredit)
        {
            _log.Debug("{Direction}: {Type}, stream {Stream}, {Bytes} bytes.",
                       direction, type, streamId, payloadBytes);
            return;
        }

        _log.Information("{Direction}: {Type}, stream {Stream}, {Bytes} bytes.",
                         direction, type, streamId, payloadBytes);
    }

    private async Task CloseChannelAsync()
    {
        foreach (var entry in _clients)
        {
            _clients.TryRemove(entry.Key, out _);
            await entry.Value.DisposeAsync().ConfigureAwait(false);
        }

        if (_channel is not null)
        {
            await _channel.DisposeAsync().ConfigureAwait(false);
            _channel = null;
        }

        _dvc?.Dispose();
        _dvc = null;

        _loopbackLink?.Dispose();
        _loopbackLink = null;

        _lanLink?.Dispose();
        _lanLink = null;
    }

    public async ValueTask DisposeAsync() => await CloseChannelAsync().ConfigureAwait(false);
}

/// <summary>How the relay should reach the PC that owns the scanner.</summary>
public enum RelayTransport
{
    /// <summary>Virtual channel first, direct connection if it carries nothing. Production.</summary>
    Auto,

    /// <summary>Straight to a tray agent on this same machine. Diagnostic; no RDP involved.</summary>
    Loopback,

    /// <summary>Direct connection only, skipping the virtual channel. Diagnostic and testing.</summary>
    Direct,
}
