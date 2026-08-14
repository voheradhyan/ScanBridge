using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace RemoteScanner.Common;

/// <summary>
/// Creates named pipes with an explicit, locked-down DACL.
///
/// The default DACL on a named pipe is far too generous for this product: the pipe carries
/// scanned business documents, and on a Session Host there are other users' processes on the
/// same machine. Every pipe here grants access to exactly one SID plus LocalSystem, blocks
/// inheritance, and rejects remote clients outright.
///
/// The pipe is created through CreateNamedPipeW rather than NamedPipeServerStreamAcl so the
/// security descriptor is expressed as SDDL that can be read and audited directly, and so no
/// extra package is needed in a service process.
/// </summary>
[SupportedOSPlatform("windows")]
public static class SecurePipe
{
    /// <summary>Buffer sizes are one wire frame each way — the protocol never needs more.</summary>
    private const uint BufferSize = 64 * 1024;

    /// <summary>
    /// Builds the SDDL for a pipe only <paramref name="ownerSid"/> and LocalSystem may open.
    /// "D:P" makes the DACL protected so no inherited ACE can widen it.
    /// </summary>
    public static string BuildSddl(SecurityIdentifier ownerSid)
        => $"D:P(A;;GA;;;SY)(A;;GA;;;{ownerSid.Value})";

    public static SecurityIdentifier CurrentUserSid()
        => WindowsIdentity.GetCurrent().User
           ?? throw new InvalidOperationException("The current process token has no user SID.");

    /// <summary>
    /// Creates one server instance of <paramref name="pipeName"/> (without the \\.\pipe\ prefix).
    /// </summary>
    /// <param name="firstInstance">
    /// True for the first instance created by a process. This sets FILE_FLAG_FIRST_PIPE_INSTANCE,
    /// which makes creation fail if the name already exists — the defence against another
    /// process squatting our pipe name and harvesting scans.
    /// </param>
    public static NamedPipeServerStream Create(
        string pipeName, SecurityIdentifier ownerSid, uint maxInstances = 8, bool firstInstance = false)
    {
        string sddl = BuildSddl(ownerSid);

        if (!Win32.ConvertStringSecurityDescriptorToSecurityDescriptorW(
                sddl, Win32.SDDL_REVISION_1, out IntPtr descriptor, IntPtr.Zero))
        {
            Win32.ThrowLastError($"Building the security descriptor for pipe '{pipeName}'");
        }

        try
        {
            var attributes = new Win32.SECURITY_ATTRIBUTES
            {
                nLength = Marshal.SizeOf<Win32.SECURITY_ATTRIBUTES>(),
                lpSecurityDescriptor = descriptor,
                bInheritHandle = 0,
            };

            uint openMode = Win32.PIPE_ACCESS_DUPLEX | Win32.FILE_FLAG_OVERLAPPED;
            if (firstInstance) openMode |= Win32.FILE_FLAG_FIRST_PIPE_INSTANCE;

            const uint pipeMode = Win32.PIPE_TYPE_BYTE | Win32.PIPE_READMODE_BYTE
                                  | Win32.PIPE_WAIT | Win32.PIPE_REJECT_REMOTE_CLIENTS;

            SafePipeHandleWrapper handle = Win32.CreateNamedPipeW(
                $@"\\.\pipe\{pipeName}", openMode, pipeMode, maxInstances,
                BufferSize, BufferSize, 0, ref attributes);

            if (handle.IsInvalid)
            {
                int error = Marshal.GetLastWin32Error();
                handle.Dispose();

                // With FILE_FLAG_FIRST_PIPE_INSTANCE, ACCESS_DENIED means the name is already
                // taken - almost always our own second copy, not a permissions problem. Saying
                // "access denied" would send anyone reading it down the wrong path entirely.
                const int ERROR_ACCESS_DENIED = 5;
                const int ERROR_PIPE_BUSY = 231;

                string explanation = (firstInstance, error) switch
                {
                    (true, ERROR_ACCESS_DENIED) or (_, ERROR_PIPE_BUSY) =>
                        $"The pipe '{pipeName}' already exists. Another copy of Remote Scanner " +
                        "is already running for this user.",
                    (_, ERROR_ACCESS_DENIED) =>
                        $"Access was denied creating the pipe '{pipeName}'.",
                    _ => $"Creating the pipe '{pipeName}' failed.",
                };

                throw new Win32Exception(error, explanation);
            }

            // Ownership moves to the stream; the wrapper must not also close it.
            return new NamedPipeServerStream(PipeDirection.InOut, isAsync: true,
                                             isConnected: false, handle.Detach());
        }
        finally
        {
            Win32.LocalFree(descriptor);
        }
    }
}
