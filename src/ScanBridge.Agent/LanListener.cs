using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using ScanBridge.Common;
using ScanBridge.Protocol;
using Serilog;
using CommonLog = ScanBridge.Common.Log;

namespace ScanBridge.Agent;

/// <summary>
/// Accepts direct connections from a Remote Desktop server when the RDP virtual channel cannot
/// carry data.
///
/// Why this exists: the virtual channel is the correct transport and is tried first, but it
/// depends on the client's Remote Desktop client loading a plugin, on that plugin being allowed
/// to run, and on the channel actually moving bytes once open. When any of that fails there is
/// no fallback and no scanning at all — and the failure is invisible from the server, because an
/// open channel that carries nothing looks exactly like a healthy one.
///
/// This link carries the same protocol over TCP instead, authenticated with the same shared
/// secret and encrypted with AES-GCM. Everything above the transport is unchanged; the agent
/// cannot tell which one a request arrived on, and neither can the scanner.
///
/// Security: a caller that cannot produce a MAC over both handshake nonces with this user's
/// secret is refused before a single protocol frame is read, and nothing after the handshake
/// crosses the network in the clear.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class LanListener
{
    private readonly AgentHost _host;
    private readonly Func<byte[]> _secret;
    private readonly int _port;
    private readonly ILogger _log = CommonLog.Logger;

    /// <summary>
    /// How many connections may be mid-handshake at once.
    ///
    /// Each one costs a task, a buffer and up to ten seconds of patience before it is timed
    /// out, and none of that requires the caller to have proved anything. Without a ceiling,
    /// anything that can reach the port can make the tray agent hold thousands of them. The
    /// real number of Remote Desktop sessions scanning to one PC is one, occasionally a few.
    /// </summary>
    private const int MaxHandshakesInFlight = 8;

    private readonly SemaphoreSlim _handshakeSlots = new(MaxHandshakesInFlight, MaxHandshakesInFlight);

    /// <param name="secret">
    /// Read per connection rather than captured once. The key can be rewritten while this
    /// process runs, and a listener holding a stale copy would refuse every connection for the
    /// rest of the session — which is exactly the failure that made the pipe hop unusable.
    /// </param>
    public LanListener(AgentHost host, Func<byte[]> secret, int port = Wire.LanPort)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _secret = secret ?? throw new ArgumentNullException(nameof(secret));
        _port = port;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        TcpListener listener;
        try
        {
            listener = new TcpListener(IPAddress.Any, _port);
            listener.Start();
        }
        catch (SocketException ex)
        {
            // Not fatal. The virtual channel is the primary transport; losing the fallback is
            // worth a warning, not a dead tray agent.
            _log.Warning(ex,
                "Could not listen on port {Port} for direct connections. Scanning over Remote " +
                "Desktop will still work if the virtual channel does.", _port);
            return;
        }

        // Touch the key now so the pairing code exists the moment the app is running, rather
        // than the first time somebody connects. A user asked to read their code should never
        // be told there isn't one yet.
        string fingerprint;
        try { fingerprint = SecretStore.Fingerprint(_secret()); }
        catch (Exception ex) { fingerprint = $"unavailable ({ex.Message})"; }

        _log.Information(
            "Listening on port {Port} for direct connections from Remote Desktop servers; " +
            "pairing key {Key}.", _port, fingerprint);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }

                _ = Task.Run(() => ServeAsync(client, cancellationToken), CancellationToken.None);
            }
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>
    /// Whether a caller is close enough to be allowed to try to authenticate.
    ///
    /// The installer's firewall rule already scopes this port to the local subnet, and that is
    /// the primary control — but it lives outside this program, in a rule a user can widen, an
    /// administrator can replace, or a "allow this app through the firewall" prompt can answer
    /// generously on somebody's behalf. This is the same restriction stated where it cannot be
    /// edited by accident.
    ///
    /// Loopback is allowed for the self-test, which drives the whole stack on one machine.
    /// </summary>
    private static bool IsCallerOnALocalNetwork(IPAddress address)
    {
        if (IPAddress.IsLoopback(address)) return true;

        // IPv4-mapped IPv6 arrives on a dual-stack socket; compare it as what it is.
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
            return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal;

        Span<byte> octets = stackalloc byte[4];
        if (!address.TryWriteBytes(octets, out _)) return false;

        return octets[0] switch
        {
            10 => true,                                        // 10.0.0.0/8
            172 => octets[1] >= 16 && octets[1] <= 31,          // 172.16.0.0/12
            192 => octets[1] == 168,                            // 192.168.0.0/16
            169 => octets[1] == 254,                            // 169.254.0.0/16, link-local
            100 => octets[1] >= 64 && octets[1] <= 127,         // 100.64.0.0/10, carrier NAT
            _ => false,
        };
    }

    private async Task ServeAsync(TcpClient client, CancellationToken cancellationToken)
    {
        string remote = client.Client.RemoteEndPoint?.ToString() ?? "an unknown address";

        if (client.Client.RemoteEndPoint is not IPEndPoint endpoint ||
            !IsCallerOnALocalNetwork(endpoint.Address))
        {
            _log.Warning(
                "Refused a direct connection from {Address}: this port only accepts callers on " +
                "a local network. A Remote Desktop server reaching this PC across the internet " +
                "is not a supported configuration.", remote);
            client.Dispose();
            return;
        }

        if (!await _handshakeSlots.WaitAsync(TimeSpan.Zero, cancellationToken).ConfigureAwait(false))
        {
            _log.Warning(
                "Refused a direct connection from {Address}: {Count} connections are already " +
                "waiting to authenticate.", remote, MaxHandshakesInFlight);
            client.Dispose();
            return;
        }

        bool authenticated = false;

        try
        {
            // Nagle would hold back the small control frames that a scan is mostly made of.
            client.NoDelay = true;

            NetworkStream socket = client.GetStream();

            var (link, peerName) = await LanHandshake
                .AcceptAsync(socket, _secret(), cancellationToken)
                .ConfigureAwait(false);

            _log.Information("Direct connection from {Peer} ({Address}) accepted and encrypted.",
                             peerName, remote);

            // The slot exists to bound *unauthenticated* callers. Holding it for the whole scan
            // would cap real sessions at the same small number.
            _handshakeSlots.Release();
            authenticated = true;

            await using (link)
            {
                await _host.ServeAuthenticatedAsync(link, peerName, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            // Includes a refused handshake. Logged at warning rather than error: an unauthorised
            // caller being turned away is the system working, not failing.
            _log.Warning("Direct connection from {Address} ended: {Reason}", remote, ex.Message);
        }
        finally
        {
            if (!authenticated) _handshakeSlots.Release();
            client.Dispose();
        }
    }
}
