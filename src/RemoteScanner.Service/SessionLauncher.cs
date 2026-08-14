using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using RemoteScanner.Common;
using CommonLog = RemoteScanner.Common.Log;

namespace RemoteScanner.Service;

/// <summary>
/// Starts a session agent inside a user's RDP session.
///
/// This is the whole reason a service exists. The session agent must run *as the user, in
/// their session*: WTSVirtualChannelOpenEx is scoped to the caller's session, and the pipe
/// it serves has to be reachable by data sources loaded into that user's applications. A
/// service in session 0 can do neither, so it duplicates the user's token and launches a
/// child with it.
///
/// Requires SeTcbPrivilege, which LocalSystem has.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SessionLauncher : IDisposable
{
    private readonly string _agentPath;
    private readonly Dictionary<uint, Process> _agents = new();
    private readonly object _gate = new();

    public SessionLauncher(string agentPath) => _agentPath = agentPath;

    public IReadOnlyCollection<uint> ActiveSessions
    {
        get { lock (_gate) return _agents.Keys.ToArray(); }
    }

    public void Start(uint sessionId)
    {
        lock (_gate)
        {
            if (_agents.TryGetValue(sessionId, out Process? existing))
            {
                if (!existing.HasExited) return;
                _agents.Remove(sessionId);
                existing.Dispose();
            }

            try
            {
                Process agent = Launch(sessionId);
                _agents[sessionId] = agent;
                CommonLog.Logger.Information(
                    "Started session agent {Pid} for session {Session}.", agent.Id, sessionId);
            }
            catch (Win32Exception ex)
            {
                // A session that is connecting but has no user token yet is normal; the
                // session-change notification will fire again once logon completes.
                CommonLog.Logger.Warning(
                    "Could not start a session agent for session {Session}: {Message}", sessionId, ex.Message);
            }
        }
    }

    public void Stop(uint sessionId)
    {
        lock (_gate)
        {
            if (!_agents.Remove(sessionId, out Process? agent)) return;

            try
            {
                if (!agent.HasExited)
                {
                    agent.Kill(entireProcessTree: true);
                    agent.WaitForExit(5000);
                }
                CommonLog.Logger.Information("Stopped the session agent for session {Session}.", sessionId);
            }
            catch (InvalidOperationException) { /* already gone */ }
            catch (Win32Exception ex)
            {
                CommonLog.Logger.Warning(ex, "Could not stop the agent for session {Session}.", sessionId);
            }
            finally
            {
                agent.Dispose();
            }
        }
    }

    public void StopAll()
    {
        foreach (uint sessionId in ActiveSessions) Stop(sessionId);
    }

    private Process Launch(uint sessionId)
    {
        if (!WTSQueryUserToken(sessionId, out SafeAccessTokenHandle userToken))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "WTSQueryUserToken failed.");

        using (userToken)
        {
            if (!DuplicateTokenEx(userToken, TOKEN_ALL_ACCESS, IntPtr.Zero,
                                  SecurityImpersonation, TokenPrimary, out SafeAccessTokenHandle primary))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "DuplicateTokenEx failed.");
            }

            using (primary)
            {
                IntPtr environment = IntPtr.Zero;
                if (!CreateEnvironmentBlock(out environment, primary, false))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateEnvironmentBlock failed.");

                try
                {
                    var startup = new STARTUPINFO
                    {
                        cb = Marshal.SizeOf<STARTUPINFO>(),
                        // Without an explicit desktop the child lands nowhere and cannot
                        // create the window TWAIN-style drivers expect.
                        lpDesktop = @"winsta0\default",
                    };

                    // Same executable, session-agent role.
                    string commandLine = $"\"{_agentPath}\" --session-agent";

                    if (!CreateProcessAsUserW(
                            primary, null, commandLine, IntPtr.Zero, IntPtr.Zero, false,
                            CREATE_UNICODE_ENVIRONMENT | CREATE_NO_WINDOW,
                            environment, Path.GetDirectoryName(_agentPath),
                            ref startup, out PROCESS_INFORMATION info))
                    {
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateProcessAsUser failed.");
                    }

                    CloseHandle(info.hThread);

                    try
                    {
                        return Process.GetProcessById(info.dwProcessId);
                    }
                    finally
                    {
                        CloseHandle(info.hProcess);
                    }
                }
                finally
                {
                    DestroyEnvironmentBlock(environment);
                }
            }
        }
    }

    public void Dispose() => StopAll();

    // ------------------------------------------------------------------ interop

    private const uint TOKEN_ALL_ACCESS = 0xF01FF;
    private const int SecurityImpersonation = 2;
    private const int TokenPrimary = 1;
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    private const uint CREATE_NO_WINDOW = 0x08000000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQueryUserToken(uint sessionId, out SafeAccessTokenHandle token);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateTokenEx(
        SafeAccessTokenHandle existingToken, uint desiredAccess, IntPtr attributes,
        int impersonationLevel, int tokenType, out SafeAccessTokenHandle newToken);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessAsUserW(
        SafeAccessTokenHandle token, string? applicationName, string? commandLine,
        IntPtr processAttributes, IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint creationFlags,
        IntPtr environment, string? currentDirectory,
        ref STARTUPINFO startupInfo, out PROCESS_INFORMATION processInformation);

    [DllImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateEnvironmentBlock(
        out IntPtr environment, SafeAccessTokenHandle token,
        [MarshalAs(UnmanagedType.Bool)] bool inherit);

    [DllImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyEnvironmentBlock(IntPtr environment);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
