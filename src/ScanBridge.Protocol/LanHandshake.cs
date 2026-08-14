namespace ScanBridge.Protocol;

/// <summary>
/// Brings up an authenticated, encrypted link over a plain network connection.
///
/// This is the same four-message exchange the pipe hops use — HELLO, HELLO_ACK, AUTHENTICATE,
/// AUTH_RESULT — with one addition: both ends finish by deriving AES-GCM keys from the shared
/// secret and the two nonces, and everything after the handshake is encrypted. Nothing secret
/// crosses the wire in the clear; the nonces are public by design and the MAC proves possession
/// of the key without disclosing it.
///
/// Both sides live here rather than one in each project, because a handshake implemented twice
/// is a handshake that will eventually disagree with itself.
///
/// The server dials and is the initiator; the PC with the scanner listens and is the responder.
/// That direction is deliberate: the server already knows the client's address — the Remote
/// Desktop session tells it — whereas the PC has no way to discover which of its sessions is on
/// which host.
/// </summary>
public static class LanHandshake
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>Server side: prove we hold the secret, then encrypt.</summary>
    public static async Task<(SecureLink Link, string PeerName)> InitiateAsync(
        Stream transport, byte[] secret, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(secret);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(Timeout);

        byte[] nonce = ChannelAuth.NewNonce();

        await SendAsync(transport,
            new HelloMessage(Wire.Version, PeerRole.SessionAgent, Environment.MachineName, nonce,
                             PeerCapabilities.FlowControl | PeerCapabilities.Cancellation),
            deadline.Token).ConfigureAwait(false);

        Frame ack = await ReadAsync(transport, deadline.Token).ConfigureAwait(false);
        if (ack.Type != MessageType.HelloAck)
            throw new IOException($"Expected HELLO_ACK from the scanner PC, got {ack.Type}.");

        HelloAckMessage peer = ParseHelloAck(ack);
        if (peer.NegotiatedVersion != Wire.Version)
        {
            throw new IOException(
                $"Protocol mismatch: this server speaks v{Wire.Version}, the scanner PC offered " +
                $"v{peer.NegotiatedVersion}. Install the same build on both.");
        }

        byte[] mac = ChannelAuth.ComputeMac(secret, ChannelAuth.InitiatorLabel, nonce, peer.Nonce, sessionId: 0);
        await SendAsync(transport, new AuthenticateMessage(mac), deadline.Token).ConfigureAwait(false);

        Frame result = await ReadAsync(transport, deadline.Token).ConfigureAwait(false);
        if (result.Type != MessageType.AuthResult)
            throw new IOException($"Expected AUTH_RESULT from the scanner PC, got {result.Type}.");

        AuthResultMessage outcome = ParseAuthResult(result);
        if (outcome.Status != AuthStatus.Ok)
            throw new IOException($"The scanner PC rejected this server: {outcome.Detail}");

        // The far end proves it holds the key too, over the same two nonces with the opposite
        // label so neither side's proof can be replayed as the other's. Anything that merely
        // answers on this address gets no further than here.
        byte[] expectedResponder = ChannelAuth.ComputeMac(
            secret, ChannelAuth.ResponderLabel, nonce, peer.Nonce, sessionId: 0);

        if (outcome.ResponderMac is null || !ChannelAuth.Verify(expectedResponder, outcome.ResponderMac))
        {
            throw new IOException(
                "The machine answering on that address accepted the connection but could not " +
                "prove it holds this pairing key. It is not the PC that was paired with this " +
                "session. Re-run the pairing step if the PC's key was reset.");
        }

        // Sent only once the caller has authenticated, so an unauthenticated connection learns
        // nothing about the machine it reached.
        string peerName = string.IsNullOrEmpty(outcome.Detail) ? peer.MachineName : outcome.Detail;

        var (initiatorKey, responderKey) = SecureLink.DeriveKeys(secret, nonce, peer.Nonce);
        return (new SecureLink(transport, sendKey: initiatorKey, receiveKey: responderKey), peerName);
    }

    /// <summary>Client side: check the caller holds the secret, then encrypt.</summary>
    public static async Task<(SecureLink Link, string PeerName)> AcceptAsync(
        Stream transport, byte[] secret, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(secret);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(Timeout);

        Frame hello = await ReadAsync(transport, deadline.Token).ConfigureAwait(false);
        if (hello.Type != MessageType.Hello)
            throw new IOException($"Expected HELLO from the server, got {hello.Type}.");

        HelloMessage peer = ParseHello(hello);

        byte[] nonce = ChannelAuth.NewNonce();

        if (peer.ProtocolVersion != Wire.Version)
        {
            await SendAsync(transport,
                new AuthResultMessage(AuthStatus.VersionMismatch,
                    $"This PC speaks protocol v{Wire.Version}; the server offered v{peer.ProtocolVersion}."),
                deadline.Token).ConfigureAwait(false);

            throw new IOException(
                $"Protocol mismatch: this PC speaks v{Wire.Version}, the server offered " +
                $"v{peer.ProtocolVersion}. Install the same build on both.");
        }

        // No machine name yet. Whoever has connected has not proved anything at this point, and
        // this port is reachable by anything that can route to it — the name of the PC, and by
        // extension often the name of its owner, is not something to hand out for the asking.
        await SendAsync(transport,
            new HelloAckMessage(Wire.Version, PeerRole.LocalAgent, string.Empty, nonce,
                                PeerCapabilities.FlowControl | PeerCapabilities.Cancellation),
            deadline.Token).ConfigureAwait(false);

        Frame auth = await ReadAsync(transport, deadline.Token).ConfigureAwait(false);
        if (auth.Type != MessageType.Authenticate)
            throw new IOException($"Expected AUTHENTICATE from the server, got {auth.Type}.");

        byte[] expected = ChannelAuth.ComputeMac(
            secret, ChannelAuth.InitiatorLabel, peer.Nonce, nonce, sessionId: 0);

        bool ok = ChannelAuth.Verify(expected, ParseAuthenticate(auth).Mac);

        // Our own proof, and our name, only once theirs has checked out.
        byte[]? responderMac = ok
            ? ChannelAuth.ComputeMac(secret, ChannelAuth.ResponderLabel, peer.Nonce, nonce, sessionId: 0)
            : null;

        await SendAsync(transport,
            new AuthResultMessage(ok ? AuthStatus.Ok : AuthStatus.BadCredentials,
                                  ok ? Environment.MachineName : "Shared secret mismatch.",
                                  responderMac),
            deadline.Token).ConfigureAwait(false);

        if (!ok)
        {
            throw new IOException(
                $"A connection from {peer.MachineName} could not prove it holds this PC's shared " +
                "secret and was refused.");
        }

        var (initiatorKey, responderKey) = SecureLink.DeriveKeys(secret, peer.Nonce, nonce);
        return (new SecureLink(transport, sendKey: responderKey, receiveKey: initiatorKey), peer.MachineName);
    }

    // ---------------------------------------------------------------- plumbing

    // Direct stream reads rather than a FrameChannel, on purpose. A FrameChannel starts a pump
    // that drains the stream into its own queue, so anything the peer sent after AUTH_RESULT
    // would be stranded in a channel that is about to be discarded. Reading exactly four frames
    // and not one byte more leaves the stream where the caller expects it.

    private static async Task SendAsync(Stream stream, IMessage message, CancellationToken cancellationToken)
    {
        var writer = new PayloadWriter();
        message.Write(writer);

        await stream.WriteAsync(FrameCodec.Encode(message.Type, streamId: 0, writer), cancellationToken)
            .ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<Frame> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[Wire.HeaderSize];
        await ReadExactAsync(stream, header, cancellationToken).ConfigureAwait(false);

        int length = FrameCodec.ParseHeader(header, out MessageType type, out uint streamId);

        byte[] payload = length == 0 ? Array.Empty<byte>() : new byte[length];
        if (length > 0) await ReadExactAsync(stream, payload, cancellationToken).ConfigureAwait(false);

        return new Frame(type, streamId, payload);
    }

    private static async Task ReadExactAsync(
        Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer[offset..], cancellationToken).ConfigureAwait(false);
            if (read == 0) throw new IOException("The connection closed during the handshake.");
            offset += read;
        }
    }

    // PayloadReader is a ref struct and cannot cross an await.
    private static HelloMessage ParseHello(Frame frame)
    {
        var reader = frame.Reader();
        return HelloMessage.Read(ref reader);
    }

    private static HelloAckMessage ParseHelloAck(Frame frame)
    {
        var reader = frame.Reader();
        return HelloAckMessage.Read(ref reader);
    }

    private static AuthenticateMessage ParseAuthenticate(Frame frame)
    {
        var reader = frame.Reader();
        return AuthenticateMessage.Read(ref reader);
    }

    private static AuthResultMessage ParseAuthResult(Frame frame)
    {
        var reader = frame.Reader();
        return AuthResultMessage.Read(ref reader);
    }
}
