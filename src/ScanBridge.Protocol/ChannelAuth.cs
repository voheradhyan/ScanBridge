using System.Security.Cryptography;
using System.Text;

namespace ScanBridge.Protocol;

/// <summary>
/// Mutual proof-of-possession for the pre-shared key.
///
/// RDP already gives us transport confidentiality, so this is not about eavesdroppers on the
/// wire. It exists to stop another process running *inside the same session* from connecting
/// to the pipe and impersonating either endpoint — something TLS cannot help with. The key
/// itself is never transmitted; only an HMAC over both nonces and the session id.
/// </summary>
public static class ChannelAuth
{
    public const int KeyLength = 32;

    public static byte[] NewNonce()
    {
        var nonce = new byte[HelloMessage.NonceLength];
        RandomNumberGenerator.Fill(nonce);
        return nonce;
    }

    public static byte[] NewKey()
    {
        var key = new byte[KeyLength];
        RandomNumberGenerator.Fill(key);
        return key;
    }

    /// <summary>
    /// HMAC-SHA256(key, label ‖ initiatorNonce ‖ responderNonce ‖ sessionId).
    /// The label binds the MAC to a direction so a response cannot be replayed as a request.
    /// </summary>
    public static byte[] ComputeMac(
        byte[] key, string label, byte[] initiatorNonce, byte[] responderNonce, uint sessionId)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(initiatorNonce);
        ArgumentNullException.ThrowIfNull(responderNonce);

        var labelBytes = Encoding.UTF8.GetBytes(label);
        var buffer = new byte[labelBytes.Length + initiatorNonce.Length + responderNonce.Length + sizeof(uint)];

        int offset = 0;
        labelBytes.CopyTo(buffer, offset); offset += labelBytes.Length;
        initiatorNonce.CopyTo(buffer, offset); offset += initiatorNonce.Length;
        responderNonce.CopyTo(buffer, offset); offset += responderNonce.Length;
        BitConverter.TryWriteBytes(buffer.AsSpan(offset), sessionId);

        return HMACSHA256.HashData(key, buffer);
    }

    public const string InitiatorLabel = "ScanBridge/v1/initiator";
    public const string ResponderLabel = "ScanBridge/v1/responder";

    /// <summary>Constant-time comparison — never use SequenceEqual for a MAC check.</summary>
    public static bool Verify(byte[] expected, byte[] actual)
        => CryptographicOperations.FixedTimeEquals(expected, actual);
}
