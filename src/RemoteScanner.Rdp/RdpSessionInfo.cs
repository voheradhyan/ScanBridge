using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace RemoteScanner.Rdp;

public enum WtsConnectState
{
    Active = 0, Connected = 1, ConnectQuery = 2, Shadow = 3, Disconnected = 4,
    Idle = 5, Listen = 6, Reset = 7, Down = 8, Init = 9,
}

public sealed record RdpSession(
    uint SessionId,
    string UserName,
    string ClientName,
    string ClientAddress,
    WtsConnectState State,
    bool IsRemote)
{
    /// <summary>A session that can actually carry a virtual channel right now.</summary>
    public bool IsUsable => IsRemote && State is WtsConnectState.Active or WtsConnectState.Connected;
}

/// <summary>
/// Reads Terminal Services session state. Used on the server to decide which sessions need
/// an agent, and on the client to show the user which RDP connections are live.
/// </summary>
[SupportedOSPlatform("windows")]
public static class RdpSessionInfo
{
    private static readonly IntPtr CurrentServer = IntPtr.Zero;   // WTS_CURRENT_SERVER_HANDLE

    private const int WTSUserName = 5;
    private const int WTSConnectState = 8;
    private const int WTSClientName = 10;
    private const int WTSClientAddress = 14;

    public static uint CurrentSessionId
    {
        get
        {
            ProcessIdToSessionId((uint)Environment.ProcessId, out uint sessionId);
            return sessionId;
        }
    }

    /// <summary>
    /// True when this process is running inside a remote session. The console session is
    /// never remote, and neither is session 0.
    /// </summary>
    public static bool IsRemoteSession(uint sessionId)
    {
        if (sessionId == 0) return false;
        // An empty client name means the session was not established over the network.
        return !string.IsNullOrEmpty(QueryString(sessionId, WTSClientName));
    }

    public static RdpSession? Describe(uint sessionId)
    {
        string clientName = QueryString(sessionId, WTSClientName);
        var state = (WtsConnectState)QueryInt32(sessionId, WTSConnectState);

        return new RdpSession(
            sessionId,
            QueryString(sessionId, WTSUserName),
            clientName,
            QueryClientAddress(sessionId),
            state,
            IsRemote: sessionId != 0 && !string.IsNullOrEmpty(clientName));
    }

    public static IReadOnlyList<RdpSession> Enumerate()
    {
        var sessions = new List<RdpSession>();

        if (!WTSEnumerateSessionsW(CurrentServer, 0, 1, out IntPtr buffer, out int count))
            return sessions;

        try
        {
            int size = Marshal.SizeOf<WTS_SESSION_INFO>();
            for (int i = 0; i < count; i++)
            {
                var info = Marshal.PtrToStructure<WTS_SESSION_INFO>(buffer + i * size);
                if (Describe(info.SessionId) is { } session) sessions.Add(session);
            }
        }
        finally
        {
            WTSFreeMemory(buffer);
        }

        return sessions;
    }

    private static string QueryString(uint sessionId, int infoClass)
    {
        if (!WTSQuerySessionInformationW(CurrentServer, sessionId, infoClass,
                                         out IntPtr buffer, out uint _))
            return string.Empty;

        try { return Marshal.PtrToStringUni(buffer) ?? string.Empty; }
        finally { WTSFreeMemory(buffer); }
    }

    private static int QueryInt32(uint sessionId, int infoClass)
    {
        if (!WTSQuerySessionInformationW(CurrentServer, sessionId, infoClass,
                                         out IntPtr buffer, out uint returned)
            || returned < sizeof(int))
            return -1;

        try { return Marshal.ReadInt32(buffer); }
        finally { WTSFreeMemory(buffer); }
    }

    /// <summary>
    /// WTSClientAddress returns a WTS_CLIENT_ADDRESS whose Address field is family-specific:
    /// for AF_INET the four octets start at offset 2, not 0.
    /// </summary>
    private static string QueryClientAddress(uint sessionId)
    {
        if (!WTSQuerySessionInformationW(CurrentServer, sessionId, WTSClientAddress,
                                         out IntPtr buffer, out uint _))
            return string.Empty;

        try
        {
            var address = Marshal.PtrToStructure<WTS_CLIENT_ADDRESS>(buffer);
            const int AF_INET = 2;
            if (address.AddressFamily != AF_INET) return string.Empty;
            return $"{address.Address[2]}.{address.Address[3]}.{address.Address[4]}.{address.Address[5]}";
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WTS_SESSION_INFO
    {
        public uint SessionId;
        [MarshalAs(UnmanagedType.LPWStr)] public string WinStationName;
        public int State;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WTS_CLIENT_ADDRESS
    {
        public int AddressFamily;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 20)] public byte[] Address;
    }

    [DllImport("wtsapi32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSEnumerateSessionsW(
        IntPtr hServer, int Reserved, int Version, out IntPtr ppSessionInfo, out int pCount);

    [DllImport("wtsapi32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQuerySessionInformationW(
        IntPtr hServer, uint SessionId, int WTSInfoClass, out IntPtr ppBuffer, out uint pBytesReturned);

    [DllImport("wtsapi32.dll", ExactSpelling = true)]
    private static extern void WTSFreeMemory(IntPtr pMemory);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ProcessIdToSessionId(uint dwProcessId, out uint pSessionId);
}
