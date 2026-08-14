using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ScanBridge.Common;

/// <summary>
/// The Win32 surface the managed components need. Kept in one place so the P/Invoke
/// signatures can be reviewed together — several of them (WTS virtual channels, token
/// duplication) are easy to get subtly wrong.
/// </summary>
internal static class Win32
{
    // ---------------------------------------------------------------- constants

    internal const uint GENERIC_READ = 0x80000000;
    internal const uint GENERIC_WRITE = 0x40000000;

    internal const uint PIPE_ACCESS_DUPLEX = 0x00000003;
    internal const uint FILE_FLAG_OVERLAPPED = 0x40000000;
    internal const uint FILE_FLAG_FIRST_PIPE_INSTANCE = 0x00080000;

    internal const uint PIPE_TYPE_BYTE = 0x00000000;
    internal const uint PIPE_READMODE_BYTE = 0x00000000;
    internal const uint PIPE_WAIT = 0x00000000;
    internal const uint PIPE_REJECT_REMOTE_CLIENTS = 0x00000008;

    internal const int SDDL_REVISION_1 = 1;

    internal const uint CRYPTPROTECT_UI_FORBIDDEN = 0x1;

    // ---------------------------------------------------------------- structures

    [StructLayout(LayoutKind.Sequential)]
    internal struct SECURITY_ATTRIBUTES
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        public int bInheritHandle;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DATA_BLOB
    {
        public int cbData;
        public IntPtr pbData;
    }

    // ---------------------------------------------------------------- kernel32

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern SafePipeHandleWrapper CreateNamedPipeW(
        string lpName, uint dwOpenMode, uint dwPipeMode, uint nMaxInstances,
        uint nOutBufferSize, uint nInBufferSize, uint nDefaultTimeOut,
        ref SECURITY_ATTRIBUTES lpSecurityAttributes);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ProcessIdToSessionId(uint dwProcessId, out uint pSessionId);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern uint WTSGetActiveConsoleSessionId();

    // ---------------------------------------------------------------- advapi32

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(
        string stringSecurityDescriptor, int stringSDRevision,
        out IntPtr securityDescriptor, IntPtr securityDescriptorSize);

    // ---------------------------------------------------------------- crypt32

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CryptProtectData(
        ref DATA_BLOB pDataIn, string? szDataDescr, IntPtr pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, uint dwFlags, out DATA_BLOB pDataOut);

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CryptUnprotectData(
        ref DATA_BLOB pDataIn, IntPtr ppszDataDescr, IntPtr pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, uint dwFlags, out DATA_BLOB pDataOut);

    [DllImport("kernel32.dll")]
    internal static extern IntPtr LocalFree(IntPtr hMem);

    internal static void ThrowLastError(string operation)
        => throw new Win32Exception(Marshal.GetLastWin32Error(), $"{operation} failed.");
}

/// <summary>
/// Owns a pipe handle created by <c>CreateNamedPipeW</c> so it is released even if
/// wrapping it in a stream throws.
/// </summary>
internal sealed class SafePipeHandleWrapper : SafeHandleZeroOrMinusOneIsInvalid
{
    public SafePipeHandleWrapper() : base(ownsHandle: true) { }

    protected override bool ReleaseHandle() => CloseHandle(handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    /// <summary>
    /// Hands ownership to a <see cref="SafePipeHandle"/>. After this the wrapper must not
    /// close the handle — the pipe stream owns it.
    /// </summary>
    public SafePipeHandle Detach()
    {
        IntPtr raw = handle;
        SetHandleAsInvalid();
        return new SafePipeHandle(raw, ownsHandle: true);
    }
}
