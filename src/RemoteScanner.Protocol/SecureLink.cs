using System.Buffers.Binary;
using System.Security.Cryptography;

namespace RemoteScanner.Protocol;

/// <summary>
/// An encrypted, authenticated byte stream over any transport.
///
/// The RDP virtual channel rides inside Microsoft's own encrypted tunnel, so nothing extra was
/// needed there. The LAN transport does not: it is a plain TCP connection across the customer's
/// network carrying scanned business documents — invoices, contracts, identity documents. Those
/// must not cross the wire in clear, and neither end should accept a byte it cannot prove came
/// from the other.
///
/// Construction, deliberately conservative and deliberately boring:
///
///   * AES-256-GCM, one key per direction, both derived from the shared secret and the two
///     nonces already exchanged by the existing handshake. Nothing new has to be trusted: a
///     peer that cannot produce the handshake MAC cannot derive the keys either.
///   * The nonce is a per-direction record counter, never transmitted. Both ends know which
///     record number they are on, so a replayed, reordered or dropped record does not decrypt
///     and the link fails closed. Keys are unique per connection, so a counter can never repeat
///     under the same key — which is the one thing GCM must never do.
///   * The record length is authenticated as associated data, so it cannot be altered to
///     truncate or extend a record.
///
/// Framing is a length prefix, ciphertext, tag. Callers see a plain stream; records are an
/// implementation detail, and <see cref="Read"/> serves partial records out of a buffer because
/// the frame protocol above reads a 12-byte header before it knows the payload size.
/// </summary>
public sealed class SecureLink : Stream
{
    /// <summary>AES-GCM: 96-bit nonce, 128-bit tag. Both are the only sizes worth using.</summary>
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int LengthSize = 4;

    /// <summary>
    /// Largest plaintext in one record. A protocol frame is at most 32 KB plus a header, and
    /// writes larger than this are split — so a record always fits comfortably in memory and a
    /// corrupt length cannot ask for an unbounded allocation.
    /// </summary>
    public const int MaxRecord = 64 * 1024;

    private readonly Stream _inner;
    private readonly bool _ownsInner;

    private readonly AesGcm _send;
    private readonly AesGcm _receive;

    private ulong _sendCounter;
    private ulong _receiveCounter;

    private byte[] _plaintext = new byte[MaxRecord];
    private int _plaintextStart;
    private int _plaintextEnd;

    private bool _disposed;

    public SecureLink(Stream inner, byte[] sendKey, byte[] receiveKey, bool ownsInner = true)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(sendKey);
        ArgumentNullException.ThrowIfNull(receiveKey);

        _inner = inner;
        _ownsInner = ownsInner;
        _send = new AesGcm(sendKey, TagSize);
        _receive = new AesGcm(receiveKey, TagSize);
    }

    /// <summary>
    /// Derives the two directional keys from the shared secret and the handshake nonces.
    ///
    /// Both ends run this with the same inputs and get the same pair; which one they send with
    /// is decided by which role they played. Distinct labels keep the two directions
    /// cryptographically separate, so a record cannot be reflected back at its sender.
    /// </summary>
    public static (byte[] InitiatorKey, byte[] ResponderKey) DeriveKeys(
        byte[] secret, byte[] initiatorNonce, byte[] responderNonce)
    {
        ArgumentNullException.ThrowIfNull(secret);
        ArgumentNullException.ThrowIfNull(initiatorNonce);
        ArgumentNullException.ThrowIfNull(responderNonce);

        return (Derive("RemoteScanner/v1/lan/initiator"), Derive("RemoteScanner/v1/lan/responder"));

        byte[] Derive(string label)
        {
            byte[] message = new byte[label.Length + initiatorNonce.Length + responderNonce.Length];
            int offset = System.Text.Encoding.ASCII.GetBytes(label, message);

            initiatorNonce.CopyTo(message, offset);
            offset += initiatorNonce.Length;
            responderNonce.CopyTo(message, offset);

            return HMACSHA256.HashData(secret, message);
        }
    }

    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush() => _inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    // ------------------------------------------------------------------ reading

    public override int Read(byte[] buffer, int offset, int count)
        => ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.Length == 0) return 0;

        if (_plaintextStart == _plaintextEnd && !await ReadRecordAsync(cancellationToken).ConfigureAwait(false))
            return 0;

        int take = Math.Min(buffer.Length, _plaintextEnd - _plaintextStart);
        _plaintext.AsMemory(_plaintextStart, take).CopyTo(buffer);
        _plaintextStart += take;
        return take;
    }

    public override async Task<int> ReadAsync(
        byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => await ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);

    /// <summary>Reads and decrypts one record. False when the peer closed cleanly.</summary>
    private async ValueTask<bool> ReadRecordAsync(CancellationToken cancellationToken)
    {
        var header = new byte[LengthSize];
        if (!await ReadExactlyOrEofAsync(header, cancellationToken).ConfigureAwait(false)) return false;

        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length < 0 || length > MaxRecord)
        {
            throw new IOException(
                $"The encrypted link announced a {length}-byte record, which is outside the " +
                $"permitted range. The link is corrupt or is not a RemoteScanner peer.");
        }

        var payload = new byte[length + TagSize];
        if (!await ReadExactlyOrEofAsync(payload, cancellationToken).ConfigureAwait(false))
            throw new IOException("The encrypted link ended part-way through a record.");

        EnsurePlaintextCapacity(length);
        DecryptInto(payload, length, header);

        _plaintextStart = 0;
        _plaintextEnd = length;

        // A zero-length record carries nothing; returning it would read as end of stream.
        return length > 0 || await ReadRecordAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Separate from the async read because a Span cannot live across an await, which is the
    /// only reason this is not inlined above.
    /// </summary>
    private void DecryptInto(byte[] payload, int length, byte[] associatedData)
    {
        Span<byte> nonce = stackalloc byte[NonceSize];
        WriteNonce(nonce, _receiveCounter);

        try
        {
            _receive.Decrypt(nonce,
                             payload.AsSpan(0, length),
                             payload.AsSpan(length, TagSize),
                             _plaintext.AsSpan(0, length),
                             associatedData);
        }
        catch (CryptographicException ex)
        {
            // Wrong key, altered bytes, or a record out of sequence. All of them mean the same
            // thing to the caller and none of them are recoverable: fail closed.
            throw new IOException(
                "A record on the encrypted link failed its integrity check. The data was " +
                "altered in transit, replayed, or the two ends do not share a secret.", ex);
        }

        _receiveCounter++;
    }

    private async ValueTask<bool> ReadExactlyOrEofAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int got = await _inner.ReadAsync(buffer[read..], cancellationToken).ConfigureAwait(false);
            if (got == 0) return read == 0 ? false : throw new IOException("The encrypted link closed mid-record.");
            read += got;
        }
        return true;
    }

    private void EnsurePlaintextCapacity(int required)
    {
        if (_plaintext.Length >= required) return;
        _plaintext = new byte[Math.Max(required, MaxRecord)];
    }

    // ------------------------------------------------------------------ writing

    public override void Write(byte[] buffer, int offset, int count)
        => WriteAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

    public override async ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        int sent = 0;
        while (sent < buffer.Length)
        {
            int take = Math.Min(MaxRecord, buffer.Length - sent);
            await WriteRecordAsync(buffer.Slice(sent, take), cancellationToken).ConfigureAwait(false);
            sent += take;
        }
    }

    public override Task WriteAsync(
        byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    private async ValueTask WriteRecordAsync(ReadOnlyMemory<byte> plaintext, CancellationToken cancellationToken)
    {
        byte[] record = BuildRecord(plaintext.Span);

        await _inner.WriteAsync(record, cancellationToken).ConfigureAwait(false);
        await _inner.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Length prefix, ciphertext, tag. Sync for the same Span reason as the read side.</summary>
    private byte[] BuildRecord(ReadOnlySpan<byte> plaintext)
    {
        var record = new byte[LengthSize + plaintext.Length + TagSize];
        BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(0, LengthSize), plaintext.Length);

        Span<byte> nonce = stackalloc byte[NonceSize];
        WriteNonce(nonce, _sendCounter);

        _send.Encrypt(nonce,
                      plaintext,
                      record.AsSpan(LengthSize, plaintext.Length),
                      record.AsSpan(LengthSize + plaintext.Length, TagSize),
                      record.AsSpan(0, LengthSize));

        _sendCounter++;
        return record;
    }

    /// <summary>
    /// Nonce for a record: four zero bytes then the record number, big-endian.
    ///
    /// Not transmitted, and it does not need to be — both ends count the same records in the
    /// same order. That is what makes a replayed or reordered record fail to decrypt rather
    /// than being accepted a second time.
    /// </summary>
    private static void WriteNonce(Span<byte> nonce, ulong counter)
    {
        nonce.Clear();
        BinaryPrimitives.WriteUInt64BigEndian(nonce[4..], counter);
    }

    protected override void Dispose(bool disposing)
    {
        if (!_disposed && disposing)
        {
            _disposed = true;
            _send.Dispose();
            _receive.Dispose();
            if (_ownsInner) _inner.Dispose();
        }
        base.Dispose(disposing);
    }
}
