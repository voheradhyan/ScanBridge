using System.IO.Pipes;
using System.Runtime.Versioning;
using ScanBridge.Common;
using ScanBridge.Protocol;
using CommonLog = ScanBridge.Common.Log;

namespace ScanBridge.SessionAgent;

/// <summary>
/// Stands in for the RDP dynamic virtual channel so the whole stack can be exercised on one
/// PC, with no server and no RDP session.
///
/// In production the session agent reaches the user's PC like this:
///
///     data source -> session pipe -> SESSION AGENT -> DVC -> mstsc plugin -> tray agent
///
/// and everything to the right of the session agent needs a real RDP connection to exist. In
/// loopback the same session agent connects straight to the tray agent's pipe instead:
///
///     data source -> session pipe -> SESSION AGENT -> local pipe -> tray agent
///
/// Every other component is the production one, unmodified, running its real code path: the
/// data source, the frame protocol, the authentication handshake, the relay, the tray agent,
/// the out-of-process ScanHost and the physical scanner. Only the transport between the two
/// machines is replaced — because in loopback there is only one machine.
///
/// That makes it possible to answer "does scanning work at all?" separately from "does RDP
/// redirection work?", which otherwise can only fail as one indivisible thing.
///
/// This is a diagnostic path. It is reached only via --loopback and is refused outright in a
/// real remote session, where it would silently bypass the redirection it is meant to test.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class LoopbackLink : IDisposable
{
    private readonly NamedPipeClientStream _pipe;

    private LoopbackLink(NamedPipeClientStream pipe) => _pipe = pipe;

    public Stream Stream => _pipe;

    /// <summary>
    /// Connects to the tray agent and completes the handshake the DVC plugin would normally
    /// perform, using this user's local secret.
    /// </summary>
    public static async Task<LoopbackLink> ConnectAsync(CancellationToken cancellationToken)
    {
        var log = CommonLog.Logger;

        var pipe = new NamedPipeClientStream(
            ".", Wire.AgentPipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous, System.Security.Principal.TokenImpersonationLevel.None);

        try
        {
            // Short timeout: if the tray agent is not running, saying so immediately is far
            // more useful than a long silent wait.
            await pipe.ConnectAsync(5000, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw new IOException(
                $"No tray agent is listening on '{Wire.AgentPipeName}'. Start ScanBridge " +
                "on this PC first — it is the component that owns the scanner.");
        }

        try
        {
            // The tray agent authenticates this hop with the machine-local secret and a
            // session id of zero: it is scoped by the user's DPAPI key and the pipe ACL, not
            // by a terminal services session. AgentHost does the same on its side.
            await HandshakeAsync(pipe, SecretStore.GetOrCreateLocalSecret(), cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        log.Information("Loopback link to the local tray agent established.");
        return new LoopbackLink(pipe);
    }

    /// <summary>
    /// HELLO / HELLO_ACK / AUTHENTICATE / AUTH_RESULT as the initiator — the same exchange
    /// rs_pipe.h performs from the native DVC plugin, so the tray agent cannot tell the
    /// difference and no special case is needed on its side.
    ///
    /// Deliberately done with direct stream reads rather than through a FrameChannel. A
    /// FrameChannel starts a background pump that drains the stream into its own queue, so
    /// anything the peer sent after AUTH_RESULT would be sitting in a channel that is about to
    /// be thrown away, and the relay's channel would never see it. Reading exactly the four
    /// handshake frames and not one byte more leaves the stream where the relay expects it.
    /// </summary>
    private static async Task HandshakeAsync(
        Stream stream, byte[] secret, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));

        byte[] nonce = ChannelAuth.NewNonce();

        await SendAsync(stream,
            new HelloMessage(Wire.Version, PeerRole.DvcPlugin, Environment.MachineName, nonce,
                             PeerCapabilities.FlowControl | PeerCapabilities.Cancellation),
            timeout.Token).ConfigureAwait(false);

        Frame ack = await ReadAsync(stream, timeout.Token).ConfigureAwait(false);
        if (ack.Type != MessageType.HelloAck)
            throw new IOException($"Expected HELLO_ACK from the tray agent, got {ack.Type}.");

        HelloAckMessage peer = ParseHelloAck(ack);
        if (peer.NegotiatedVersion != Wire.Version)
        {
            throw new IOException(
                $"Protocol mismatch: this agent speaks v{Wire.Version}, " +
                $"the tray agent offered v{peer.NegotiatedVersion}. Install the same build on both.");
        }

        byte[] mac = ChannelAuth.ComputeMac(
            secret, ChannelAuth.InitiatorLabel, nonce, peer.Nonce, sessionId: 0);

        await SendAsync(stream, new AuthenticateMessage(mac), timeout.Token).ConfigureAwait(false);

        Frame result = await ReadAsync(stream, timeout.Token).ConfigureAwait(false);
        if (result.Type != MessageType.AuthResult)
            throw new IOException($"Expected AUTH_RESULT from the tray agent, got {result.Type}.");

        AuthResultMessage outcome = ParseAuthResult(result);
        if (outcome.Status != AuthStatus.Ok)
        {
            throw new IOException(
                $"The tray agent rejected the loopback link: {outcome.Detail} " +
                $"This agent is using key {SecretStore.Fingerprint(secret)}, read from the " +
                "registry just now; the tray agent is using whatever it read when it started. " +
                "Restarting ScanBridge on this PC makes them agree.");
        }
    }

    private static async Task SendAsync(Stream stream, IMessage message, CancellationToken cancellationToken)
    {
        var writer = new PayloadWriter();
        message.Write(writer);

        byte[] frame = FrameCodec.Encode(message.Type, streamId: 0, writer);
        await stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<Frame> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[Wire.HeaderSize];
        await ReadExactAsync(stream, header, cancellationToken).ConfigureAwait(false);

        int length = FrameCodec.ParseHeader(header, out MessageType type, out uint streamId);

        byte[] payload = length == 0 ? Array.Empty<byte>() : new byte[length];
        if (length > 0)
            await ReadExactAsync(stream, payload, cancellationToken).ConfigureAwait(false);

        return new Frame(type, streamId, payload);
    }

    private static async Task ReadExactAsync(
        Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer[offset..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
                throw new IOException("The tray agent closed the connection during the handshake.");
            offset += read;
        }
    }

    // PayloadReader is a ref struct and cannot cross an await.
    private static HelloAckMessage ParseHelloAck(Frame frame)
    {
        var reader = frame.Reader();
        return HelloAckMessage.Read(ref reader);
    }

    private static AuthResultMessage ParseAuthResult(Frame frame)
    {
        var reader = frame.Reader();
        return AuthResultMessage.Read(ref reader);
    }

    public void Dispose() => _pipe.Dispose();
}
