using System.Net.Sockets;
using System.Runtime.Versioning;
using ScanBridge.Protocol;
using CommonLog = ScanBridge.Common.Log;

namespace ScanBridge.SessionAgent;

/// <summary>
/// Dials the PC that owns the scanner directly, when the RDP virtual channel will not carry
/// data.
///
/// The virtual channel is the right transport and is always tried first: it needs no ports, no
/// firewall rules, and it rides the encrypted RDP connection that is already there. But it is
/// also the one part of the chain that can fail silently and cannot be diagnosed from either
/// end — an open channel carrying nothing looks identical to a healthy one, and the user is
/// simply told their scanner is offline.
///
/// So when the channel does not prove itself, this connects to the client PC over the network
/// instead. The address is not configured or discovered: the Remote Desktop session already
/// reports where the client is, which is the same information the session agent logs on every
/// startup.
///
/// Everything above this class is unchanged. The relay is handed a stream and does not care
/// what it is.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class LanLink : IDisposable
{
    private readonly TcpClient _client;
    private readonly SecureLink _link;

    private LanLink(TcpClient client, SecureLink link)
    {
        _client = client;
        _link = link;
    }

    /// <summary>The authenticated, encrypted stream. Framing is the caller's business.</summary>
    public Stream Stream => _link;

    /// <summary>Machine name the scanner PC reported during the handshake.</summary>
    public string PeerName { get; private init; } = string.Empty;

    public static async Task<LanLink> ConnectAsync(
        string clientAddress, byte[] secret, CancellationToken cancellationToken, int port = Wire.LanPort)
    {
        ArgumentException.ThrowIfNullOrEmpty(clientAddress);
        ArgumentNullException.ThrowIfNull(secret);

        var log = CommonLog.Logger;
        var client = new TcpClient { NoDelay = true };

        try
        {
            // Short: this runs after the virtual channel has already been given its chance, and
            // a user waiting to scan has waited long enough by now.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));

            try
            {
                await client.ConnectAsync(clientAddress, port, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new IOException(
                    $"No answer from {clientAddress} on port {port}. ScanBridge may not be " +
                    "running on that PC, or its firewall is blocking the connection.");
            }
            catch (SocketException ex)
            {
                throw new IOException(
                    $"Could not reach {clientAddress} on port {port}: {ex.Message} " +
                    "ScanBridge may not be running on that PC, or its firewall is blocking " +
                    "the connection.", ex);
            }

            var (link, peerName) = await LanHandshake
                .InitiateAsync(client.GetStream(), secret, cancellationToken)
                .ConfigureAwait(false);

            log.Information("Direct connection to {Peer} at {Address}:{Port} established and encrypted.",
                            peerName, clientAddress, port);

            return new LanLink(client, link) { PeerName = peerName };
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        _link.Dispose();
        _client.Dispose();
    }
}
