using System.Net;
using System.Net.Sockets;
using RemoteScanner.Protocol;
using Xunit;

namespace RemoteScanner.Tests.Unit;

/// <summary>
/// The handshake that guards the direct network transport.
///
/// This is the only hop with no operating system in between. The pipe hops are wrapped in a
/// DACL naming one SID, so the kernel has already refused everyone else before a byte is read;
/// here, anything that can route to the port gets to open a socket and speak. So both what the
/// exchange gives away before it is satisfied, and what it insists on before it trusts the far
/// end, are worth pinning down.
///
/// Driven over a real loopback socket rather than an in-memory pair, because a handshake is two
/// parties waiting on each other and the in-memory buffers used elsewhere in these tests return
/// 0 instead of blocking — which a handshake reads as a closed connection.
/// </summary>
public sealed class LanHandshakeTests
{
    private static readonly byte[] Secret = Enumerable.Range(0, 32).Select(i => (byte)(i * 3 + 1)).ToArray();

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    /// <summary>A connected socket pair on the loopback interface, on a port the OS picks.</summary>
    private static async Task<(TcpClient Dialler, TcpClient Answerer, IDisposable Cleanup)> ConnectedPairAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        var dialler = new TcpClient();
        Task connecting = dialler.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        TcpClient answerer = await listener.AcceptTcpClientAsync();
        await connecting;

        return (dialler, answerer, new Cleanup(() =>
        {
            dialler.Dispose();
            answerer.Dispose();
            listener.Stop();
        }));
    }

    [Fact]
    public async Task BothEndsAgreeWhenTheyHoldTheSameKey()
    {
        var (server, pc, cleanup) = await ConnectedPairAsync();
        using (cleanup)
        {
            using var deadline = new CancellationTokenSource(Patience);

            Task<(SecureLink Link, string PeerName)> dialling =
                LanHandshake.InitiateAsync(server.GetStream(), Secret, deadline.Token);
            Task<(SecureLink Link, string PeerName)> accepting =
                LanHandshake.AcceptAsync(pc.GetStream(), Secret, deadline.Token);

            var (serverSide, pcName) = await dialling;
            var (pcSide, serverName) = await accepting;

            await using (serverSide)
            await using (pcSide)
            {
                // Each end learns who the other is, and only once the exchange has succeeded.
                Assert.Equal(Environment.MachineName, pcName);
                Assert.Equal(Environment.MachineName, serverName);
            }
        }
    }

    [Fact]
    public async Task AServerWithTheWrongKeyIsRefused()
    {
        var (server, pc, cleanup) = await ConnectedPairAsync();
        using (cleanup)
        {
            using var deadline = new CancellationTokenSource(Patience);
            byte[] wrong = Secret.Select(b => (byte)(b ^ 0xFF)).ToArray();

            Task dialling = LanHandshake.InitiateAsync(server.GetStream(), wrong, deadline.Token);
            Task accepting = LanHandshake.AcceptAsync(pc.GetStream(), Secret, deadline.Token);

            await Assert.ThrowsAnyAsync<IOException>(() => accepting);
            await Assert.ThrowsAnyAsync<IOException>(() => dialling);
        }
    }

    /// <summary>
    /// The case the responder's proof exists for.
    ///
    /// Something answers on the address without holding the key — an impostor, a stale port
    /// forward, a PC whose pairing key has been reset. It can never read or forge traffic,
    /// because the record keys derive from a secret it does not have. But until the responder
    /// had to prove anything, the dialling end would consider the link up, having already sent
    /// its nonce and machine name, and would only discover the problem when records failed to
    /// decrypt several frames later. Now it fails at the handshake and says so.
    /// </summary>
    [Fact]
    public async Task AnImpostorThatAnswersWithoutTheKeyIsCaughtAtTheHandshake()
    {
        var (server, pc, cleanup) = await ConnectedPairAsync();
        using (cleanup)
        {
            using var deadline = new CancellationTokenSource(Patience);

            Task<(SecureLink Link, string PeerName)> dialling =
                LanHandshake.InitiateAsync(server.GetStream(), Secret, deadline.Token);

            // Hand-rolled rather than AcceptAsync with a wrong key: that path refuses the
            // caller's MAC first, and the caller would fail on the rejection without ever
            // reaching the check under test. A real impostor does the opposite — it says yes to
            // everything, because saying yes costs it nothing.
            Task impostor = Task.Run(async () =>
            {
                NetworkStream wire = pc.GetStream();

                var hello = await ReadFrameAsync(wire, deadline.Token);
                Assert.Equal(MessageType.Hello, hello.Type);

                var ack = new PayloadWriter();
                new HelloAckMessage(Wire.Version, PeerRole.LocalAgent, string.Empty,
                                    ChannelAuth.NewNonce(), PeerCapabilities.None).Write(ack);
                await wire.WriteAsync(FrameCodec.Encode(MessageType.HelloAck, 0, ack), deadline.Token);

                var auth = await ReadFrameAsync(wire, deadline.Token);
                Assert.Equal(MessageType.Authenticate, auth.Type);

                var result = new PayloadWriter();
                new AuthResultMessage(AuthStatus.Ok, "IMPOSTOR-PC").Write(result);
                await wire.WriteAsync(FrameCodec.Encode(MessageType.AuthResult, 0, result), deadline.Token);
                await wire.FlushAsync(deadline.Token);
            });

            IOException failure = await Assert.ThrowsAnyAsync<IOException>(() => dialling);
            await impostor;

            Assert.Contains("prove", failure.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("IMPOSTOR-PC", failure.Message);
        }
    }

    /// <summary>
    /// Nothing identifying this PC — least of all its name, which is very often its owner's
    /// name — reaches a caller that has not authenticated. The name arrives with AUTH_RESULT,
    /// and only when the answer is yes.
    /// </summary>
    [Fact]
    public async Task TheMachineNameIsNotDisclosedBeforeTheCallerAuthenticates()
    {
        var (probe, pc, cleanup) = await ConnectedPairAsync();
        using (cleanup)
        {
            using var deadline = new CancellationTokenSource(Patience);

            Task accepting = LanHandshake.AcceptAsync(pc.GetStream(), Secret, deadline.Token);

            NetworkStream wire = probe.GetStream();

            var writer = new PayloadWriter();
            new HelloMessage(Wire.Version, PeerRole.SessionAgent, "SOME-SERVER",
                             ChannelAuth.NewNonce(), PeerCapabilities.None).Write(writer);
            await wire.WriteAsync(FrameCodec.Encode(MessageType.Hello, 0, writer), deadline.Token);
            await wire.FlushAsync(deadline.Token);

            var header = new byte[Wire.HeaderSize];
            await wire.ReadExactlyAsync(header, deadline.Token);
            int length = FrameCodec.ParseHeader(header, out MessageType type, out _);
            var payload = new byte[length];
            await wire.ReadExactlyAsync(payload, deadline.Token);

            Assert.Equal(MessageType.HelloAck, type);
            Assert.Equal(string.Empty, NameFrom(payload));

            probe.Dispose();
            try { await accepting; } catch { /* abandoned on purpose */ }
        }

        // PayloadReader is a ref struct and cannot live across an await.
        static string NameFrom(byte[] payload)
        {
            var reader = new PayloadReader(payload);
            return HelloAckMessage.Read(ref reader).MachineName;
        }
    }

    private static async Task<(MessageType Type, byte[] Payload)> ReadFrameAsync(
        Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[Wire.HeaderSize];
        await stream.ReadExactlyAsync(header, cancellationToken);
        int length = FrameCodec.ParseHeader(header, out MessageType type, out _);

        var payload = new byte[length];
        if (length > 0) await stream.ReadExactlyAsync(payload, cancellationToken);
        return (type, payload);
    }

    private sealed class Cleanup : IDisposable
    {
        private readonly Action _dispose;
        public Cleanup(Action dispose) => _dispose = dispose;
        public void Dispose() => _dispose();
    }
}
