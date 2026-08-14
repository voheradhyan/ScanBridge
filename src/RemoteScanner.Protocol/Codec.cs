using System.Buffers.Binary;
using System.Text;

namespace RemoteScanner.Protocol;

/// <summary>Thrown whenever a peer sends something that cannot be parsed under the negotiated version.</summary>
public sealed class ProtocolException : Exception
{
    public ProtocolException(string message) : base(message) { }
    public ProtocolException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Little-endian payload writer. Explicit field-by-field encoding is used instead of a
/// serialization library because the same layout is hand-implemented in C++ for the
/// data source and the DVC plugin; anything reflective would silently drift.
/// </summary>
public sealed class PayloadWriter
{
    private byte[] _buffer;
    private int _length;

    public PayloadWriter(int capacity = 256)
    {
        _buffer = new byte[Math.Max(capacity, 16)];
    }

    public int Length => _length;

    private Span<byte> Reserve(int count)
    {
        if (_length + count > _buffer.Length)
        {
            int size = _buffer.Length;
            while (size < _length + count) size *= 2;
            Array.Resize(ref _buffer, size);
        }
        var span = _buffer.AsSpan(_length, count);
        _length += count;
        return span;
    }

    public void WriteByte(byte value) => Reserve(1)[0] = value;

    public void WriteBool(bool value) => WriteByte(value ? (byte)1 : (byte)0);

    public void WriteUInt16(ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(Reserve(2), value);

    public void WriteInt16(short value) => BinaryPrimitives.WriteInt16LittleEndian(Reserve(2), value);

    public void WriteUInt32(uint value) => BinaryPrimitives.WriteUInt32LittleEndian(Reserve(4), value);

    public void WriteInt32(int value) => BinaryPrimitives.WriteInt32LittleEndian(Reserve(4), value);

    public void WriteUInt64(ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(Reserve(8), value);

    public void WriteInt64(long value) => BinaryPrimitives.WriteInt64LittleEndian(Reserve(8), value);

    /// <summary>Fixed-point with 3 decimal places, so DPI/brightness survive the C++ boundary exactly.</summary>
    public void WriteFixed(double value) => WriteInt32(checked((int)Math.Round(value * 1000.0)));

    public void WriteBytes(ReadOnlySpan<byte> value) => value.CopyTo(Reserve(value.Length));

    /// <summary>UTF-8, length-prefixed with a u16. Null is encoded as an empty string.</summary>
    public void WriteString(string? value)
    {
        value ??= string.Empty;
        int byteCount = Encoding.UTF8.GetByteCount(value);
        if (byteCount > ushort.MaxValue)
            throw new ProtocolException($"String too long to encode: {byteCount} bytes.");
        WriteUInt16((ushort)byteCount);
        Encoding.UTF8.GetBytes(value, Reserve(byteCount));
    }

    /// <summary>Length-prefixed byte blob (u32 length).</summary>
    public void WriteBlob(ReadOnlySpan<byte> value)
    {
        WriteInt32(value.Length);
        WriteBytes(value);
    }

    public byte[] ToArray() => _buffer.AsSpan(0, _length).ToArray();

    public ReadOnlyMemory<byte> AsMemory() => _buffer.AsMemory(0, _length);
}

/// <summary>Bounds-checked little-endian payload reader. Every read validates remaining length.</summary>
public ref struct PayloadReader
{
    private readonly ReadOnlySpan<byte> _data;
    private int _position;

    public PayloadReader(ReadOnlySpan<byte> data)
    {
        _data = data;
        _position = 0;
    }

    public int Remaining => _data.Length - _position;

    private ReadOnlySpan<byte> Take(int count)
    {
        if (count < 0 || Remaining < count)
            throw new ProtocolException($"Truncated payload: needed {count} bytes, {Remaining} remain.");
        var span = _data.Slice(_position, count);
        _position += count;
        return span;
    }

    public byte ReadByte() => Take(1)[0];

    public bool ReadBool() => ReadByte() != 0;

    public ushort ReadUInt16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));

    public short ReadInt16() => BinaryPrimitives.ReadInt16LittleEndian(Take(2));

    public uint ReadUInt32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));

    public int ReadInt32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));

    public ulong ReadUInt64() => BinaryPrimitives.ReadUInt64LittleEndian(Take(8));

    public long ReadInt64() => BinaryPrimitives.ReadInt64LittleEndian(Take(8));

    public double ReadFixed() => ReadInt32() / 1000.0;

    public byte[] ReadBytes(int count) => Take(count).ToArray();

    public string ReadString()
    {
        int length = ReadUInt16();
        return Encoding.UTF8.GetString(Take(length));
    }

    public byte[] ReadBlob()
    {
        int length = ReadInt32();
        if (length < 0 || length > Wire.MaxPayload)
            throw new ProtocolException($"Blob length out of range: {length}.");
        return Take(length).ToArray();
    }

    /// <summary>
    /// Reads a count that will drive a loop, rejecting absurd values before any allocation.
    /// Guards against a hostile or corrupt peer causing a large allocation.
    /// </summary>
    public int ReadCount(int max)
    {
        int value = ReadInt32();
        if (value < 0 || value > max)
            throw new ProtocolException($"Element count out of range: {value} (max {max}).");
        return value;
    }
}

/// <summary>A decoded frame. <see cref="Payload"/> is owned by the caller.</summary>
public readonly struct Frame
{
    public MessageType Type { get; }
    public uint StreamId { get; }
    public byte[] Payload { get; }

    public Frame(MessageType type, uint streamId, byte[] payload)
    {
        Type = type;
        StreamId = streamId;
        Payload = payload;
    }

    public PayloadReader Reader() => new(Payload);

    public override string ToString() => $"{Type} stream={StreamId} len={Payload.Length}";
}

public static class FrameCodec
{
    public static void WriteHeader(Span<byte> destination, MessageType type, uint streamId, int payloadLength)
    {
        if (destination.Length < Wire.HeaderSize)
            throw new ArgumentException("Destination too small for a frame header.", nameof(destination));
        if (payloadLength < 0 || payloadLength > Wire.MaxPayload)
            throw new ProtocolException($"Payload length {payloadLength} exceeds {Wire.MaxPayload}.");

        destination[0] = Wire.Magic;
        destination[1] = Wire.Version;
        BinaryPrimitives.WriteUInt16LittleEndian(destination[2..], (ushort)type);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[4..], streamId);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[8..], (uint)payloadLength);
    }

    public static byte[] Encode(MessageType type, uint streamId, ReadOnlySpan<byte> payload)
    {
        var buffer = new byte[Wire.HeaderSize + payload.Length];
        WriteHeader(buffer, type, streamId, payload.Length);
        payload.CopyTo(buffer.AsSpan(Wire.HeaderSize));
        return buffer;
    }

    public static byte[] Encode(MessageType type, uint streamId, PayloadWriter writer)
        => Encode(type, streamId, writer.AsMemory().Span);

    /// <summary>Parses a header, validating magic and version. Returns the payload length.</summary>
    public static int ParseHeader(ReadOnlySpan<byte> header, out MessageType type, out uint streamId)
    {
        if (header.Length < Wire.HeaderSize)
            throw new ProtocolException("Header buffer too small.");
        if (header[0] != Wire.Magic)
            throw new ProtocolException($"Bad frame magic 0x{header[0]:X2}; stream is out of sync.");
        if (header[1] != Wire.Version)
            throw new ProtocolException($"Unsupported protocol version {header[1]}; this build speaks {Wire.Version}.");

        type = (MessageType)BinaryPrimitives.ReadUInt16LittleEndian(header[2..]);
        streamId = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(header[8..]);

        if (length > Wire.MaxPayload)
            throw new ProtocolException($"Frame declares {length} payload bytes, above the {Wire.MaxPayload} limit.");

        return (int)length;
    }
}
