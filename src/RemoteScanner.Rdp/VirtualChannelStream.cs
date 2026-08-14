using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace RemoteScanner.Rdp;

/// <summary>
/// The server end of an RDP dynamic virtual channel, presented as a byte stream.
///
/// Two things about this handle are not obvious and both have cost real time here.
///
/// <para><b>It is not a file.</b> WTSVirtualChannelQuery hands back a handle to a device that
/// completes asynchronously. A FileStream over it reports every write as successful while
/// carrying nothing, so every layer above looks healthy and the request simply never arrives.
/// All I/O here therefore goes through ReadFile/WriteFile with an explicit OVERLAPPED, waited
/// to completion.</para>
///
/// <para><b>Inbound data is framed by Windows, outbound data is not.</b> A write goes on the
/// wire as plain application bytes — Microsoft's DVC server sample calls WriteFile with the
/// message and nothing else. A read does not: each ReadFile returns one CHANNEL_PDU_HEADER
/// followed by up to CHANNEL_CHUNK_LENGTH bytes of a message, and a message longer than that
/// arrives as several such packets, the last one flagged CHANNEL_FLAG_LAST. Treating those
/// bytes as application data puts eight bytes of header where the protocol expects a frame
/// header, and the connection is dropped as corrupt the first time the client PC answers.
/// That is exactly what happens when a scan is requested: the request goes out fine, the
/// reply comes back framed, the relay rejects it, and the channel is torn down and
/// reconnected — which is visible to the user as the RDP link vanishing and its uptime
/// resetting to zero.</para>
///
/// So: writes pass straight through, reads strip the header and reassemble. Callers see only
/// application bytes, which is what <c>FrameChannel</c> expects.
///
/// Layout and constants are from pchannel.h in the Windows SDK; the read loop follows the
/// documented DVC server sample.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class VirtualChannelStream : Stream
{
    /// <summary>Largest application payload one inbound packet can carry (CHANNEL_CHUNK_LENGTH).</summary>
    private const int ChannelChunkLength = 1600;

    /// <summary>sizeof(CHANNEL_PDU_HEADER): two UINT32s, length then flags.</summary>
    private const int ChannelPduHeaderSize = 8;

    /// <summary>CHANNEL_PDU_LENGTH. Reads use exactly this, so one read is one packet.</summary>
    private const int ChannelPduLength = ChannelChunkLength + ChannelPduHeaderSize;

    private const uint ChannelFlagLast = 0x02;

    /// <summary>
    /// Refuses an absurd declared length before it is used to size a buffer. The protocol's
    /// own ceiling is a 32 KB payload plus header, so anything near this is already wrong;
    /// the limit exists so a corrupt header cannot ask for a gigabyte allocation.
    /// </summary>
    private const int MaxMessageBytes = 1024 * 1024;

    private readonly SafeFileHandle _handle;

    /// <summary>One inbound packet, header included.</summary>
    private readonly byte[] _chunk = new byte[ChannelPduLength];

    /// <summary>The reassembled message, header stripped. Grown on demand, never shrunk.</summary>
    private byte[] _message = new byte[ChannelChunkLength];

    private int _messageStart;
    private int _messageEnd;

    public VirtualChannelStream(SafeFileHandle handle)
        => _handle = handle ?? throw new ArgumentNullException(nameof(handle));

    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush() { }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    // ------------------------------------------------------------------ reading

    /// <summary>
    /// Serves the caller from the reassembled message, reading more packets when it runs out.
    ///
    /// Only one thread reads — the relay runs a single read pump — so no lock is needed here.
    /// Writes never touch this state.
    /// </summary>
    public override int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (count == 0) return 0;

        if (_messageStart == _messageEnd && !ReadMessage())
            return 0;                                   // the channel closed

        int take = Math.Min(count, _messageEnd - _messageStart);
        Array.Copy(_message, _messageStart, buffer, offset, take);
        _messageStart += take;
        return take;
    }

    /// <summary>
    /// Reads packets until one carries CHANNEL_FLAG_LAST, leaving the whole message in
    /// <see cref="_message"/>. Returns false when the channel closed.
    /// </summary>
    private bool ReadMessage()
    {
        _messageStart = 0;
        _messageEnd = 0;

        int declaredTotal = 0;

        while (true)
        {
            int read = Transfer(_chunk.AsSpan(), reading: true);
            if (read == 0) return false;

            if (read < ChannelPduHeaderSize)
            {
                throw new IOException(
                    $"The RDP virtual channel returned a {read}-byte packet, which is shorter " +
                    "than the header every packet must carry.");
            }

            uint declared = BinaryPrimitives.ReadUInt32LittleEndian(_chunk.AsSpan(0, 4));
            uint flags = BinaryPrimitives.ReadUInt32LittleEndian(_chunk.AsSpan(4, 4));
            int fragment = read - ChannelPduHeaderSize;

            if (declared > MaxMessageBytes)
            {
                throw new IOException(
                    $"The RDP virtual channel announced a {declared}-byte message, past the " +
                    $"{MaxMessageBytes}-byte limit. The channel is out of step.");
            }

            // Every packet of a message repeats the message's total length, so a mismatch
            // means packets from two messages have been mixed — worth failing loudly rather
            // than delivering a spliced one.
            if (_messageEnd == 0)
            {
                declaredTotal = (int)declared;
                EnsureCapacity(declaredTotal);
            }
            else if (declared != declaredTotal)
            {
                throw new IOException(
                    $"The RDP virtual channel declared {declared} bytes part-way through a " +
                    $"{declaredTotal}-byte message.");
            }

            if (_messageEnd + fragment > declaredTotal)
            {
                throw new IOException(
                    $"The RDP virtual channel sent more than the {declaredTotal} bytes it declared.");
            }

            Array.Copy(_chunk, ChannelPduHeaderSize, _message, _messageEnd, fragment);
            _messageEnd += fragment;

            if ((flags & ChannelFlagLast) == 0) continue;

            if (_messageEnd != declaredTotal)
            {
                throw new IOException(
                    $"The RDP virtual channel ended a message after {_messageEnd} of " +
                    $"{declaredTotal} bytes.");
            }

            // A zero-length message carries nothing to hand up, and returning zero here would
            // read as end-of-stream and close the channel. Wait for the next one instead.
            if (_messageEnd > 0) return true;
            declaredTotal = 0;
        }
    }

    private void EnsureCapacity(int required)
    {
        if (_message.Length >= required) return;

        int size = _message.Length;
        while (size < required) size *= 2;
        _message = new byte[size];
    }

    // ------------------------------------------------------------------ writing

    /// <summary>
    /// Outbound data is plain: the channel adds no header in this direction and fragments
    /// large messages itself.
    /// </summary>
    public override void Write(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        int written = 0;
        while (written < count)
        {
            int sent = Transfer(buffer.AsSpan(offset + written, count - written), reading: false);
            if (sent <= 0) throw new IOException("The RDP virtual channel accepted no bytes.");
            written += sent;
        }
    }

    // The channel handle is not bound to a completion port, so there is no genuinely
    // asynchronous path to offer. These exist because FrameChannel only calls the async
    // overloads; the work they do is the same work, on the caller's task.

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        byte[] scratch = new byte[buffer.Length];
        int read = Read(scratch, 0, scratch.Length);
        scratch.AsSpan(0, read).CopyTo(buffer.Span);
        return ValueTask.FromResult(read);
    }

    public override Task<int> ReadAsync(
        byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Read(buffer, offset, count));
    }

    public override ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Write(buffer.ToArray(), 0, buffer.Length);
        return ValueTask.CompletedTask;
    }

    public override Task WriteAsync(
        byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Write(buffer, offset, count);
        return Task.CompletedTask;
    }

    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    // ------------------------------------------------------------------ the device

    /// <summary>
    /// One ReadFile or WriteFile, issued with an OVERLAPPED and waited to completion.
    ///
    /// The byte count pointer must be NULL when an OVERLAPPED is supplied — passing both is
    /// accepted by the compiler and then quietly never completes, which is how this stream
    /// managed to carry nothing at all while reporting success.
    /// </summary>
    private unsafe int Transfer(Span<byte> buffer, bool reading)
    {
        using var completed = new ManualResetEvent(initialState: false);

        var overlapped = new NativeOverlapped
        {
            OffsetLow = 0,
            OffsetHigh = 0,
            EventHandle = completed.SafeWaitHandle.DangerousGetHandle(),
        };

        fixed (byte* pinned = buffer)
        {
            bool finished = reading
                ? ReadFile(_handle, pinned, (uint)buffer.Length, IntPtr.Zero, ref overlapped)
                : WriteFile(_handle, pinned, (uint)buffer.Length, IntPtr.Zero, ref overlapped);

            if (!finished)
            {
                int error = Marshal.GetLastWin32Error();
                if (error != ERROR_IO_PENDING) throw Failure(reading, error);
                completed.WaitOne();
            }

            if (!GetOverlappedResult(_handle, ref overlapped, out uint transferred, bWait: true))
            {
                // ERROR_MORE_DATA means the buffer could not hold the whole packet. The bytes
                // that fit are valid and counted, so it is a short read rather than a fault.
                // Unreachable while the buffer is CHANNEL_PDU_LENGTH, which is the largest
                // packet the channel can produce.
                int error = Marshal.GetLastWin32Error();
                if (error != ERROR_MORE_DATA) throw Failure(reading, error);
            }

            return (int)transferred;
        }
    }

    private static IOException Failure(bool reading, int error)
    {
        // A closed channel is the ordinary end of a session, not a fault: the relay treats an
        // IOException as "reconnect" either way, but the message is what a person reads.
        string what = error is ERROR_BROKEN_PIPE or ERROR_NO_DATA or ERROR_OPERATION_ABORTED
            ? "The RDP virtual channel closed."
            : reading
                ? "Reading from the RDP virtual channel failed."
                : "Writing to the RDP virtual channel failed.";

        return new IOException(what, new Win32Exception(error));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _handle.Dispose();
        base.Dispose(disposing);
    }

    // ------------------------------------------------------------------ interop

    private const int ERROR_IO_PENDING = 997;
    private const int ERROR_BROKEN_PIPE = 109;
    private const int ERROR_MORE_DATA = 234;
    private const int ERROR_NO_DATA = 232;
    private const int ERROR_OPERATION_ABORTED = 995;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern unsafe bool ReadFile(
        SafeFileHandle handle, byte* buffer, uint bytesToRead,
        IntPtr bytesRead, ref NativeOverlapped overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern unsafe bool WriteFile(
        SafeFileHandle handle, byte* buffer, uint bytesToWrite,
        IntPtr bytesWritten, ref NativeOverlapped overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetOverlappedResult(
        SafeFileHandle handle, ref NativeOverlapped overlapped,
        out uint bytesTransferred, [MarshalAs(UnmanagedType.Bool)] bool bWait);
}
