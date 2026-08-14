using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace RemoteScanner.Rdp;

/// <summary>
/// The server end of an RDP Dynamic Virtual Channel.
///
/// This must be constructed from a process running *inside* the target RDP session —
/// <c>WTS_CURRENT_SESSION</c> is scoped by the kernel, which is precisely what gives us
/// session isolation for free: a process in session 3 cannot open session 4's channel.
///
/// The channel rides the existing RDP connection on port 3389. No listener is created and no
/// firewall rule is needed.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WtsVirtualChannel : IDisposable
{
    private const int WTS_CURRENT_SESSION = -1;

    private const uint WTS_CHANNEL_OPTION_DYNAMIC = 0x00000001;

    /// <summary>
    /// Medium priority. Bulk page data must not be able to starve the input and graphics
    /// channels — a scan should never make the user's remote desktop feel frozen.
    ///
    /// 0x02, checked against WtsApi32.h. This was 0x04, which is PRI_HIGH — the priorities are
    /// LOW 0x00, MED 0x02, HIGH 0x04, REAL 0x06, and the value was guessed as a bit flag when
    /// it is really a two-bit field. The channel still opened, at the wrong priority.
    /// </summary>
    private const uint WTS_CHANNEL_OPTION_DYNAMIC_PRI_MED = 0x00000002;

    private const int WTSVirtualFileHandle = 1;

    private const uint DUPLICATE_SAME_ACCESS = 0x00000002;

    private readonly SafeChannelHandle _channel;
    private readonly VirtualChannelStream _stream;

    private WtsVirtualChannel(SafeChannelHandle channel, VirtualChannelStream stream)
    {
        _channel = channel;
        _stream = stream;
    }

    /// <summary>Duplex byte stream carrying the protocol. Framing is the caller's business.</summary>
    public Stream Stream => _stream;

    /// <summary>
    /// Opens the named dynamic channel for the calling process's session.
    /// </summary>
    /// <exception cref="Win32Exception">
    /// Thrown when there is no RDP session, or the client did not load a plugin that listens
    /// on this channel name. Both are ordinary conditions the caller reports and retries.
    /// </exception>
    public static WtsVirtualChannel Open(string channelName)
    {
        ArgumentException.ThrowIfNullOrEmpty(channelName);

        SafeChannelHandle channel = WTSVirtualChannelOpenEx(
            WTS_CURRENT_SESSION, channelName,
            WTS_CHANNEL_OPTION_DYNAMIC | WTS_CHANNEL_OPTION_DYNAMIC_PRI_MED);

        if (channel.IsInvalid)
        {
            channel.Dispose();
            throw new Win32Exception(Marshal.GetLastWin32Error(),
                $"WTSVirtualChannelOpenEx('{channelName}') failed. " +
                "There is no RDP session, or the client is not running the RemoteScanner plugin.");
        }

        try
        {
            SafeFileHandle file = DuplicateChannelFile(channel);
            return new WtsVirtualChannel(channel, new VirtualChannelStream(file));
        }
        catch
        {
            channel.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Extracts the channel's file handle, which is what data actually flows through.
    ///
    /// WTSVirtualChannelQuery hands back a handle it still owns and frees with the buffer, so
    /// it is duplicated before that buffer is released.
    ///
    /// The duplicate is handed to <see cref="VirtualChannelStream"/> rather than a FileStream:
    /// this is a message-oriented device, not a file, and the difference matters — see the
    /// remarks there.
    /// </summary>
    private static SafeFileHandle DuplicateChannelFile(SafeChannelHandle channel)
    {
        if (!WTSVirtualChannelQuery(channel, WTSVirtualFileHandle, out IntPtr buffer, out uint returned)
            || buffer == IntPtr.Zero || returned < (uint)IntPtr.Size)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(),
                "WTSVirtualChannelQuery(WTSVirtualFileHandle) failed.");
        }

        SafeFileHandle duplicate;
        try
        {
            IntPtr raw = Marshal.ReadIntPtr(buffer);
            IntPtr process = GetCurrentProcess();

            if (!DuplicateHandle(process, raw, process, out duplicate, 0, false, DUPLICATE_SAME_ACCESS))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    "Duplicating the virtual channel file handle failed.");
            }
        }
        finally
        {
            WTSFreeMemory(buffer);
        }

        return duplicate;
    }

    public void Dispose()
    {
        _stream.Dispose();
        _channel.Dispose();
    }

    // ------------------------------------------------------------------ interop

    // The channel name is ANSI in this API even on Unicode Windows.
    [DllImport("wtsapi32.dll", CharSet = CharSet.Ansi, SetLastError = true, ExactSpelling = true)]
    private static extern SafeChannelHandle WTSVirtualChannelOpenEx(
        int SessionId, [MarshalAs(UnmanagedType.LPStr)] string pVirtualName, uint flags);

    [DllImport("wtsapi32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSVirtualChannelQuery(
        SafeChannelHandle hChannelHandle, int WtsVirtualClass, out IntPtr ppBuffer, out uint pBytesReturned);

    [DllImport("wtsapi32.dll", ExactSpelling = true)]
    private static extern void WTSFreeMemory(IntPtr pMemory);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateHandle(
        IntPtr hSourceProcessHandle, IntPtr hSourceHandle, IntPtr hTargetProcessHandle,
        out SafeFileHandle lpTargetHandle, uint dwDesiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwOptions);

    /// <summary>Channel handles are closed with WTSVirtualChannelClose, not CloseHandle.</summary>
    private sealed class SafeChannelHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeChannelHandle() : base(ownsHandle: true) { }

        protected override bool ReleaseHandle() => WTSVirtualChannelClose(handle);

        [DllImport("wtsapi32.dll", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool WTSVirtualChannelClose(IntPtr hChannelHandle);
    }
}
