using System.Security.Cryptography;
using RemoteScanner.Protocol;
using Xunit;

namespace RemoteScanner.Tests.Unit;

/// <summary>
/// The encrypted stream that carries scans when the RDP virtual channel is not usable.
///
/// This one is worth being strict about. Over the RDP channel the data rides Microsoft's
/// encrypted tunnel; over the LAN it rides the customer's network, and it is scanned business
/// documents. So the tests below check not only that a byte written comes out the other end,
/// but that every way of tampering with it fails closed: a flipped bit, a truncated record, a
/// replayed record, a reordered one, and a peer holding a different secret.
///
/// A pair of connected in-memory pipes stands in for the socket. What is under test is the
/// record layer, not TCP.
/// </summary>
public sealed class SecureLinkTests
{
    private static readonly byte[] Secret = Enumerable.Range(0, 32).Select(i => (byte)(i * 7)).ToArray();
    private static readonly byte[] InitiatorNonce = Enumerable.Range(0, 32).Select(i => (byte)(i + 1)).ToArray();
    private static readonly byte[] ResponderNonce = Enumerable.Range(0, 32).Select(i => (byte)(i + 100)).ToArray();

    /// <summary>Two SecureLinks wired to each other through a shared buffer, as on a socket.</summary>
    private static (SecureLink Initiator, SecureLink Responder) ConnectedPair(byte[]? responderSecret = null)
    {
        var initiatorToResponder = new BlockingBuffer();
        var responderToInitiator = new BlockingBuffer();

        var (ik, rk) = SecureLink.DeriveKeys(Secret, InitiatorNonce, ResponderNonce);
        var (ik2, rk2) = SecureLink.DeriveKeys(responderSecret ?? Secret, InitiatorNonce, ResponderNonce);

        var initiator = new SecureLink(new DuplexBuffer(responderToInitiator, initiatorToResponder), ik, rk);
        var responder = new SecureLink(new DuplexBuffer(initiatorToResponder, responderToInitiator), rk2, ik2);
        return (initiator, responder);
    }

    [Fact]
    public async Task ABytePatternSurvivesTheRoundTrip()
    {
        var (initiator, responder) = ConnectedPair();
        byte[] frame = { 0x52, 0x01, 0x10, 0x00, 1, 0, 0, 0, 0, 0, 0, 0 };

        await initiator.WriteAsync(frame);

        var received = new byte[frame.Length];
        await ReadExactlyAsync(responder, received);
        Assert.Equal(frame, received);
    }

    [Fact]
    public async Task BothDirectionsWorkIndependently()
    {
        var (initiator, responder) = ConnectedPair();

        await initiator.WriteAsync(new byte[] { 1, 2, 3 });
        await responder.WriteAsync(new byte[] { 9, 8, 7, 6 });

        var toResponder = new byte[3];
        await ReadExactlyAsync(responder, toResponder);

        var toInitiator = new byte[4];
        await ReadExactlyAsync(initiator, toInitiator);

        Assert.Equal(new byte[] { 1, 2, 3 }, toResponder);
        Assert.Equal(new byte[] { 9, 8, 7, 6 }, toInitiator);
    }

    [Fact]
    public async Task AHeaderReadFollowedByABodyReadWorks()
    {
        // How FrameChannel reads: 12 bytes, then the payload the header declares. A record is
        // larger than either, so partial reads have to be served from the decrypted buffer.
        var (initiator, responder) = ConnectedPair();
        var message = new byte[3000];
        RandomNumberGenerator.Fill(message);

        await initiator.WriteAsync(message);

        var header = new byte[12];
        await ReadExactlyAsync(responder, header);
        var body = new byte[message.Length - 12];
        await ReadExactlyAsync(responder, body);

        Assert.Equal(message[..12], header);
        Assert.Equal(message[12..], body);
    }

    [Fact]
    public async Task APageSizedPayloadSurvivesAcrossManyRecords()
    {
        // A scanned page is megabytes, far beyond one record, so this exercises the split in
        // Write and the counter advancing in step on both sides.
        var (initiator, responder) = ConnectedPair();
        var payload = new byte[512 * 1024];
        RandomNumberGenerator.Fill(payload);

        Task writing = initiator.WriteAsync(payload).AsTask();

        var received = new byte[payload.Length];
        await ReadExactlyAsync(responder, received);
        await writing;

        Assert.Equal(payload, received);
    }

    [Fact]
    public async Task NothingReadableAppearsOnTheWire()
    {
        // The point of the exercise: a document must not be recoverable by watching the network.
        var wire = new BlockingBuffer();
        var (ik, _) = SecureLink.DeriveKeys(Secret, InitiatorNonce, ResponderNonce);
        var link = new SecureLink(new DuplexBuffer(new BlockingBuffer(), wire), ik, ik);

        byte[] plaintext = System.Text.Encoding.ASCII.GetBytes("INVOICE 2026-0042 TOTAL 18,400.00");
        await link.WriteAsync(plaintext);

        byte[] onTheWire = wire.Snapshot();
        Assert.DoesNotContain("INVOICE", System.Text.Encoding.ASCII.GetString(onTheWire));
        Assert.False(ContainsSequence(onTheWire, plaintext));
    }

    [Fact]
    public async Task AFlippedBitIsRejected()
    {
        var (sender, receiver, wire) = OneWay();
        await sender.WriteAsync(new byte[] { 10, 20, 30, 40 });

        wire.Corrupt(index: 6, xor: 0x01);   // inside the ciphertext

        IOException error = await Assert.ThrowsAsync<IOException>(
            async () => await receiver.ReadAsync(new byte[16]));
        Assert.Contains("integrity", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AnAlteredLengthIsRejected()
    {
        // The length is authenticated as associated data, so it cannot be edited to truncate
        // or extend a record.
        var (sender, receiver, wire) = OneWay();
        await sender.WriteAsync(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });

        wire.Corrupt(index: 0, xor: 0x02);

        await Assert.ThrowsAsync<IOException>(async () => await receiver.ReadAsync(new byte[16]));
    }

    [Fact]
    public async Task AReplayedRecordIsRejected()
    {
        // Without a counter in the nonce, a captured record could be injected again and would
        // decrypt perfectly — a second "scan complete", or a duplicated page.
        var (sender, receiver, wire) = OneWay();

        await sender.WriteAsync(new byte[] { 1, 1, 1, 1 });
        byte[] first = wire.Snapshot();
        await sender.WriteAsync(new byte[] { 2, 2, 2, 2 });

        var buffer = new byte[16];
        await receiver.ReadAsync(buffer);       // first record, accepted

        wire.Append(first);                     // replay it

        await ReadExactlyAsync(receiver, new byte[4]);   // second record, still fine
        await Assert.ThrowsAsync<IOException>(async () => await receiver.ReadAsync(buffer));
    }

    [Fact]
    public async Task RecordsOutOfOrderAreRejected()
    {
        var (sender, receiver, wire) = OneWay();

        await sender.WriteAsync(new byte[] { 1, 1, 1, 1 });
        byte[] first = wire.TakeAll();
        await sender.WriteAsync(new byte[] { 2, 2, 2, 2 });
        byte[] second = wire.TakeAll();

        wire.Append(second);                    // deliver the second record first
        wire.Append(first);

        await Assert.ThrowsAsync<IOException>(async () => await receiver.ReadAsync(new byte[16]));
    }

    [Fact]
    public async Task APeerWithADifferentSecretCannotBeRead()
    {
        var stranger = new byte[32];
        RandomNumberGenerator.Fill(stranger);

        var (initiator, responder) = ConnectedPair(responderSecret: stranger);

        await initiator.WriteAsync(new byte[] { 1, 2, 3, 4 });

        await Assert.ThrowsAsync<IOException>(async () => await responder.ReadAsync(new byte[16]));
    }

    [Fact]
    public void DerivedKeysDifferPerDirectionAndPerConnection()
    {
        var (a1, b1) = SecureLink.DeriveKeys(Secret, InitiatorNonce, ResponderNonce);

        // Different directions must not share a key, or a record could be reflected at its sender.
        Assert.NotEqual(a1, b1);

        // Fresh nonces must give fresh keys, or a counter nonce could repeat across connections.
        var otherNonce = new byte[32];
        RandomNumberGenerator.Fill(otherNonce);
        var (a2, _) = SecureLink.DeriveKeys(Secret, InitiatorNonce, otherNonce);
        Assert.NotEqual(a1, a2);

        Assert.Equal(32, a1.Length);
    }

    [Fact]
    public async Task AClosedPeerReadsAsEndOfStreamRatherThanAFault()
    {
        var (sender, receiver, wire) = OneWay();
        wire.Complete();

        Assert.Equal(0, await receiver.ReadAsync(new byte[16]));
        GC.KeepAlive(sender);
    }

    // ------------------------------------------------------------------ helpers

    private static (SecureLink Sender, SecureLink Receiver, BlockingBuffer Wire) OneWay()
    {
        var wire = new BlockingBuffer();
        var (ik, rk) = SecureLink.DeriveKeys(Secret, InitiatorNonce, ResponderNonce);

        var sender = new SecureLink(new DuplexBuffer(new BlockingBuffer(), wire), ik, rk);
        var receiver = new SecureLink(new DuplexBuffer(wire, new BlockingBuffer()), rk, ik);
        return (sender, receiver, wire);
    }

    private static async Task ReadExactlyAsync(Stream stream, byte[] buffer)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int got = await stream.ReadAsync(buffer.AsMemory(read));
            if (got == 0) throw new IOException($"ended after {read} of {buffer.Length} bytes");
            read += got;
        }
    }

    private static bool ContainsSequence(byte[] haystack, byte[] needle)
    {
        for (int i = 0; i + needle.Length <= haystack.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < needle.Length && match; j++) match = haystack[i + j] == needle[j];
            if (match) return true;
        }
        return false;
    }

    /// <summary>A byte queue that reads return from and writes append to, with tamper hooks.</summary>
    private sealed class BlockingBuffer
    {
        private readonly List<byte> _bytes = new();
        private int _position;
        private bool _complete;

        public void Append(byte[] data) { lock (_bytes) _bytes.AddRange(data); }

        public void Complete() { lock (_bytes) _complete = true; }

        public byte[] Snapshot() { lock (_bytes) return _bytes.Skip(_position).ToArray(); }

        public byte[] TakeAll()
        {
            lock (_bytes)
            {
                byte[] taken = _bytes.Skip(_position).ToArray();
                _bytes.Clear();
                _position = 0;
                return taken;
            }
        }

        public void Corrupt(int index, byte xor)
        {
            lock (_bytes) _bytes[_position + index] ^= xor;
        }

        public int Read(Span<byte> buffer)
        {
            lock (_bytes)
            {
                int available = _bytes.Count - _position;
                if (available == 0) return _complete ? 0 : 0;

                int take = Math.Min(buffer.Length, available);
                for (int i = 0; i < take; i++) buffer[i] = _bytes[_position + i];
                _position += take;
                return take;
            }
        }

        public void Write(ReadOnlySpan<byte> buffer)
        {
            lock (_bytes) foreach (byte b in buffer) _bytes.Add(b);
        }
    }

    /// <summary>Stream over a read buffer and a write buffer — one end of a connection.</summary>
    private sealed class DuplexBuffer : Stream
    {
        private readonly BlockingBuffer _in;
        private readonly BlockingBuffer _out;

        public DuplexBuffer(BlockingBuffer inbound, BlockingBuffer outbound)
        {
            _in = inbound;
            _out = outbound;
        }

        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count) => _in.Read(buffer.AsSpan(offset, count));
        public override void Write(byte[] buffer, int offset, int count) => _out.Write(buffer.AsSpan(offset, count));

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(_in.Read(buffer.Span));

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _out.Write(buffer.Span);
            return ValueTask.CompletedTask;
        }
    }
}
