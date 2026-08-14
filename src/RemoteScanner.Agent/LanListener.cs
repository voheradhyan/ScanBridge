using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using RemoteScanner.Common;
using RemoteScanner.Protocol;
using Serilog;
using CommonLog = RemoteScanner.Common.Log;

namespace RemoteScanner.Agent;

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

    private async Task ServeAsync(TcpClient client, CancellationToken cancellationToken)
    {
        string remote = client.Client.RemoteEndPoint?.ToString() ?? "an unknown address";

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
            client.Dispose();
        }
    }
}
