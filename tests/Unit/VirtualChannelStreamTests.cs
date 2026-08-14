using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using RemoteScanner.Rdp;
using Xunit;

namespace RemoteScanner.Tests.Unit;

/// <summary>
/// The RDP dynamic virtual channel transport.
///
/// The channel handle cannot be created without a live RDP session, but the handle is not the
/// interesting part — what happens around it is: overlapped I/O with a NULL byte-count
/// pointer, and the asymmetric framing that Windows applies to one direction and not the
/// other. Outbound data goes on the wire verbatim. Inbound data arrives as one
/// CHANNEL_PDU_HEADER plus up to CHANNEL_CHUNK_LENGTH bytes per read, with long messages split
/// across several reads and the final one flagged CHANNEL_FLAG_LAST.
///
/// A message-mode named pipe reproduces that faithfully enough to test against: one write by
/// the far end is one read here, which is exactly how the channel device behaves and why the
/// read buffer is sized at CHANNEL_PDU_LENGTH.
///
/// The pipes are created through raw CreateNamedPipe/CreateFile rather than
/// NamedPipeServerStream. That matters: .NET binds a handle opened with PipeOptions.Asynchronous
/// to the thread pool's I/O completion port, and completions then go to the port instead of
/// behaving as plain event-signalled overlapped I/O. A test built on those handles would be
/// testing .NET's plumbing, not ours, and would hang. The channel handle is bound to no
/// completion port, and neither are these.
///
/// This file exists because two separate defects lived here undetected. The first wrapped the
/// handle in a FileStream, which carried nothing while reporting success. The second read the
/// framing bytes as application data, which corrupted the first reply the client PC ever sent
/// and tore the channel down — visible to the user as the RDP link vanishing the moment a scan
/// was requested.
/// </summary>
public sealed class VirtualChannelStreamTests : IDisposable
{
    private const int ChunkLength = 1600;
    private const int PduHeaderSize = 8;
    private const uint FlagFirst = 0x01;
    private const uint FlagLast = 0x02;

    private readonly SafeFileHandle _server;
    private readonly SafeFileHandle _client;

    public VirtualChannelStreamTests()
    {
        string name = $@"\\.\pipe\RemoteScanner.Dvc.{Guid.NewGuid():N}";

        // Message mode: one write by the far end is one read here, as on the real device.
        _server = CreateNamedPipeW(name, PIPE_ACCESS_DUPLEX,
                                   PIPE_TYPE_MESSAGE | PIPE_READMODE_MESSAGE | PIPE_WAIT,
                                   1, 256 * 1024, 256 * 1024, 0, IntPtr.Zero);
        if (_server.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateNamedPipe failed.");

        var opened = Task.Run(() =>
        {
            var handle = CreateFileW(name, GENERIC_READ | GENERIC_WRITE, 0, IntPtr.Zero,
                                     OPEN_EXISTING, FILE_FLAG_OVERLAPPED, IntPtr.Zero);
            if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateFile failed.");

            uint mode = PIPE_READMODE_MESSAGE;
            if (!SetNamedPipeHandleState(handle, ref mode, IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "SetNamedPipeHandleState failed.");

            return handle;
        });

        if (!ConnectNamedPipe(_server, IntPtr.Zero))
        {
            int error = Marshal.GetLastWin32Error();
            if (error != ERROR_PIPE_CONNECTED) throw new Win32Exception(error, "ConnectNamedPipe failed.");
        }

        _client = opened.GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        _client.Dispose();
        _server.Dispose();
    }

    private VirtualChannelStream Channel()
        => new(new SafeFileHandle(_client.DangerousGetHandle(), ownsHandle: false));

    // ------------------------------------------------------------ far-end helpers

    /// <summary>Sends one raw packet, exactly as the channel would deliver it.</summary>
    private void SendPacket(uint declaredTotal, uint flags, ReadOnlySpan<byte> fragment)
    {
        var packet = new byte[PduHeaderSize + fragment.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(0, 4), declaredTotal);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4, 4), flags);
        fragment.CopyTo(packet.AsSpan(PduHeaderSize));

        if (!WriteFileSync(_server, packet, packet.Length, out uint written, IntPtr.Zero)
            || written != packet.Length)
        {
            throw new IOException("the test far end could not write the packet");
        }
    }

    /// <summary>Sends one logical message, split across packets the way the channel splits it.</summary>
    private void SendMessage(byte[] message)
    {
        int sent = 0;
        bool first = true;

        do
        {
            int take = Math.Min(ChunkLength, message.Length - sent);
            bool last = sent + take >= message.Length;

            uint flags = (first ? FlagFirst : 0) | (last ? FlagLast : 0);
            SendPacket((uint)message.Length, flags, message.AsSpan(sent, take));

            sent += take;
            first = false;
        }
        while (sent < message.Length);
    }

    private byte[] ReadFromFarEnd(int count)
    {
        var buffer = new byte[count];
        int read = 0;
        while (read < count)
        {
            if (!ReadFileSync(_server, buffer, read, count - read, out uint got) || got == 0)
                throw new IOException("the pipe closed mid-read");
            read += (int)got;
        }
        return buffer;
    }

    private static byte[] ReadExactly(Stream channel, int count)
    {
        var buffer = new byte[count];
        int read = 0;
        while (read < count)
        {
            int got = channel.Read(buffer, read, count - read);
            if (got == 0) throw new IOException($"the channel ended after {read} of {count} bytes");
            read += got;
        }
        return buffer;
    }

    private static byte[] Random(int size, int seed)
    {
        var data = new byte[size];
        new Random(seed).NextBytes(data);
        return data;
    }

    // ------------------------------------------------------------------ outbound

    [Fact]
    public void WritesGoOnTheWireVerbatim()
    {
        // The channel adds no header in this direction and the far end must see exactly the
        // bytes handed over — adding framing here would corrupt every request.
        var channel = Channel();
        byte[] frame = { 0x52, 0x01, 0x10, 0x00, 1, 0, 0, 0, 0, 0, 0, 0 };

        channel.Write(frame, 0, frame.Length);

        Assert.Equal(frame, ReadFromFarEnd(frame.Length));
    }

    [Fact]
    public async Task TheAsyncWriteWrapperGoesThroughTheSamePath()
    {
        // FrameChannel only ever calls the async overloads, so those are what production uses.
        var channel = Channel();
        byte[] frame = { 0x52, 0x01, 0x23, 0x00, 4, 4, 4, 4, 0, 0, 0, 0 };

        await channel.WriteAsync(frame, CancellationToken.None);

        Assert.Equal(frame, ReadFromFarEnd(frame.Length));
    }

    [Fact]
    public async Task ALargeWriteArrivesWhole()
    {
        // A scanned page is hundreds of frames. A write that quietly stopped short would
        // truncate an image rather than fail, which is far worse.
        var channel = Channel();
        byte[] payload = Random(32 * 1024, 1234);

        Task writing = Task.Run(() => channel.Write(payload, 0, payload.Length));
        byte[] received = ReadFromFarEnd(payload.Length);
        await writing;

        Assert.Equal(payload, received);
    }

    // ------------------------------------------------------------------- inbound

    [Fact]
    public void ASinglePacketMessageIsDeliveredWithoutItsHeader()
    {
        // The exact shape of the first reply that ever crosses the channel.
        var channel = Channel();
        byte[] message = { 0x52, 0x01, 0x11, 0x00, 7, 0, 0, 0, 9, 8, 7, 6 };

        SendPacket((uint)message.Length, FlagFirst | FlagLast, message);

        Assert.Equal(message, ReadExactly(channel, message.Length));
    }

    [Fact]
    public void TheFramingBytesNeverReachTheCaller()
    {
        // The defect stated plainly: the header used to be handed up as if it were protocol
        // data, so the first four bytes read were a length instead of the frame's magic.
        var channel = Channel();
        byte[] message = { 0x52, 0x01, 0x11, 0x00, 1, 2, 3, 4, 5, 6, 7, 8 };

        SendPacket((uint)message.Length, FlagFirst | FlagLast, message);

        byte[] head = ReadExactly(channel, 4);
        Assert.Equal(0x52, head[0]);          // frame magic, not the low byte of a length
        Assert.Equal(0x01, head[1]);
    }

    [Fact]
    public void AMessageSplitAcrossPacketsIsReassembled()
    {
        // A scanner list is a few hundred bytes, but capability replies and page data are not.
        // Anything past 1600 bytes arrives in pieces and must come back out as one message.
        var channel = Channel();
        byte[] message = Random(4000, 99);      // three packets

        SendMessage(message);

        Assert.Equal(message, ReadExactly(channel, message.Length));
    }

    [Fact]
    public void AMaximumSizeProtocolFrameSurvives()
    {
        // The protocol's ceiling: a 32 KB payload plus header, which is 21 packets.
        var channel = Channel();
        byte[] message = Random(12 + 32 * 1024, 11);

        SendMessage(message);

        Assert.Equal(message, ReadExactly(channel, message.Length));
    }

    [Fact]
    public void AHeaderReadFollowedByABodyReadWorks()
    {
        // How FrameChannel actually reads: 12 bytes for the header, then the payload the
        // header declares. The caller's request size must not reach the device.
        var channel = Channel();
        byte[] message = Random(2500, 5);
        message[0] = 0x52;
        message[1] = 0x01;

        SendMessage(message);

        byte[] header = ReadExactly(channel, 12);
        byte[] body = ReadExactly(channel, message.Length - 12);

        Assert.Equal(message[..12], header);
        Assert.Equal(message[12..], body);
    }

    [Fact]
    public void BackToBackMessagesAreNotMergedOrDropped()
    {
        // Several frames can be in flight during a scan. Reading the first must not consume
        // or discard any part of the second.
        var channel = Channel();
        byte[] first = Random(300, 7);
        byte[] second = Random(4096, 8);

        SendMessage(first);
        SendMessage(second);

        Assert.Equal(first, ReadExactly(channel, first.Length));
        Assert.Equal(second, ReadExactly(channel, second.Length));
    }

    [Fact]
    public void AnEmptyMessageIsSkippedRatherThanReadAsEndOfStream()
    {
        // Returning zero from Read means end of stream, and the relay would close a perfectly
        // good channel on it.
        var channel = Channel();
        byte[] message = Random(64, 3);

        SendPacket(0, FlagFirst | FlagLast, ReadOnlySpan<byte>.Empty);
        SendMessage(message);

        Assert.Equal(message, ReadExactly(channel, message.Length));
    }

    // ------------------------------------------------------------------ refusals

    [Fact]
    public void APacketShorterThanItsHeaderIsRejected()
    {
        var channel = Channel();
        byte[] runt = { 1, 2, 3 };

        if (!WriteFileSync(_server, runt, runt.Length, out uint _, IntPtr.Zero))
            throw new IOException("the test far end could not write");

        IOException error = Assert.Throws<IOException>(() => channel.Read(new byte[16], 0, 16));
        Assert.Contains("header", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PacketsFromTwoDifferentMessagesAreRejected()
    {
        // Delivering a spliced message would corrupt a scan silently. Failing closes the
        // channel, and the relay reconnects.
        var channel = Channel();

        SendPacket(4000, FlagFirst, Random(1600, 1));    // says 4000, no LAST
        SendPacket(2000, FlagLast, Random(400, 2));      // says 2000 — different message

        IOException error = Assert.Throws<IOException>(() => channel.Read(new byte[16], 0, 16));
        Assert.Contains("part-way", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AMessageThatOverrunsItsDeclaredLengthIsRejected()
    {
        var channel = Channel();

        SendPacket(100, FlagFirst | FlagLast, Random(200, 4));

        IOException error = Assert.Throws<IOException>(() => channel.Read(new byte[16], 0, 16));
        Assert.Contains("declared", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnAbsurdDeclaredLengthIsRejectedBeforeAllocating()
    {
        var channel = Channel();

        SendPacket(uint.MaxValue, FlagFirst, Random(16, 6));

        IOException error = Assert.Throws<IOException>(() => channel.Read(new byte[16], 0, 16));
        Assert.Contains("limit", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AClosedChannelIsReportedAsClosedRatherThanAsAFault()
    {
        var channel = Channel();
        _server.Dispose();

        // The relay treats an IOException as "the session went away" and reconnects. Anything
        // else would surface to the user as a crash.
        IOException error = Assert.Throws<IOException>(() => channel.Read(new byte[16], 0, 16));
        Assert.Contains("channel", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------ interop

    private const uint PIPE_ACCESS_DUPLEX = 0x00000003;
    private const uint PIPE_TYPE_MESSAGE = 0x00000004;
    private const uint PIPE_READMODE_MESSAGE = 0x00000002;
    private const uint PIPE_WAIT = 0x00000000;
    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_OVERLAPPED = 0x40000000;
    private const int ERROR_PIPE_CONNECTED = 535;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateNamedPipeW(
        string name, uint openMode, uint pipeMode, uint maxInstances,
        uint outBufferSize, uint inBufferSize, uint defaultTimeOut, IntPtr security);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(
        string name, uint access, uint share, IntPtr security,
        uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConnectNamedPipe(SafeFileHandle pipe, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetNamedPipeHandleState(
        SafeFileHandle pipe, ref uint mode, IntPtr maxCollectionCount, IntPtr collectDataTimeout);

    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "ReadFile")]
    private static extern bool ReadFileSyncCore(
        SafeFileHandle handle, byte[] buffer, uint count, out uint read, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "WriteFile")]
    private static extern bool WriteFileSync(
        SafeFileHandle handle, byte[] buffer, int count, out uint written, IntPtr overlapped);

    private static bool ReadFileSync(SafeFileHandle handle, byte[] buffer, int offset, int count, out uint read)
    {
        var slice = new byte[count];
        bool ok = ReadFileSyncCore(handle, slice, (uint)count, out read, IntPtr.Zero);
        if (ok) Array.Copy(slice, 0, buffer, offset, (int)read);
        return ok;
    }
}
