namespace RemoteScanner.Protocol;

/// <summary>
/// Wire-level constants. Any change here MUST be mirrored in native/include/rs_protocol.h.
/// </summary>
public static class Wire
{
    public const byte Magic = 0x52;              // 'R'
    public const byte Version = 0x01;

    /// <summary>Bytes in a frame header: magic, version, type(u16), streamId(u32), length(u32).</summary>
    public const int HeaderSize = 12;

    /// <summary>
    /// Maximum payload per frame. Chosen to sit comfortably below the DVC chunking
    /// threshold so a single logical frame maps to a small number of DVC PDUs, which
    /// keeps interactive traffic (mouse, keyboard, graphics) from being head-of-line
    /// blocked behind a bulk image write.
    /// </summary>
    public const int MaxPayload = 32 * 1024;

    public const int MaxFrameSize = HeaderSize + MaxPayload;

    /// <summary>Frames the receiver will accept in flight before it must issue more credit.</summary>
    public const int DefaultCreditWindow = 64;

    /// <summary>Credit is topped up once half the window has been consumed.</summary>
    public const int CreditRefillThreshold = DefaultCreditWindow / 2;

    public const string DvcChannelName = "RemoteScanner";

    public const string AgentPipeName = "RemoteScanner.Agent";

    public static string SessionPipeName(string userSid, uint sessionId)
        => $"RemoteScanner.Session.{userSid}.{sessionId}";

    public const int HeartbeatIntervalMs = 15_000;
    public const int HeartbeatTimeoutMs = 45_000;

    /// <summary>
    /// Port the PC with the scanner listens on for the direct connection, used when the RDP
    /// virtual channel cannot carry data.
    ///
    /// In the unassigned range and not a value anything common squats on. Fixed rather than
    /// negotiated because the two ends have no channel to negotiate over — needing one is the
    /// situation this exists to handle.
    /// </summary>
    public const int LanPort = 47214;

    /// <summary>
    /// How long the server waits for the RDP channel to prove itself before dialling directly.
    ///
    /// Long enough that a healthy channel is never given up on — confirmation there takes
    /// milliseconds — and short enough that a user who clicks Scan does not sit watching
    /// nothing happen.
    /// </summary>
    public const int ChannelConfirmTimeoutMs = 8_000;
}

public enum MessageType : ushort
{
    Hello = 0x01,
    HelloAck = 0x02,
    Authenticate = 0x03,
    AuthResult = 0x04,

    ScannerEnumRequest = 0x10,
    ScannerEnumResponse = 0x11,
    ScannerCapsRequest = 0x12,
    ScannerCapsResponse = 0x13,

    ScanRequest = 0x20,
    ScanStart = 0x21,
    ScanPageBegin = 0x22,
    ScanPageData = 0x23,
    ScanPageEnd = 0x24,
    ScanProgress = 0x25,
    ScanComplete = 0x26,
    ScanCancel = 0x27,
    ScanError = 0x28,

    FlowCredit = 0x30,

    Heartbeat = 0x40,
    Disconnect = 0x41,
}

public enum PeerRole : byte
{
    Unknown = 0,
    /// <summary>The virtual TWAIN data source running inside a remote application.</summary>
    TwainDataSource = 1,
    /// <summary>The per-session agent on the RDP server.</summary>
    SessionAgent = 2,
    /// <summary>The DVC plugin hosted inside mstsc.exe.</summary>
    DvcPlugin = 3,
    /// <summary>The tray agent on the local PC that owns the physical scanners.</summary>
    LocalAgent = 4,
}

[Flags]
public enum PeerCapabilities : uint
{
    None = 0,
    Duplex = 1 << 0,
    Adf = 1 << 1,
    JpegEncoding = 1 << 2,
    CcittG4Encoding = 1 << 3,
    PngEncoding = 1 << 4,
    RawEncoding = 1 << 5,
    FlowControl = 1 << 6,
    Cancellation = 1 << 7,
}

public enum ScannerInterface : byte
{
    Unknown = 0,
    Twain = 1,
    Wia = 2,
}

public enum ScannerStatus : byte
{
    Unknown = 0,
    Ready = 1,
    Busy = 2,
    Offline = 3,
    Error = 4,
}

[Flags]
public enum ScannerFeatures : uint
{
    None = 0,
    Flatbed = 1 << 0,
    Feeder = 1 << 1,
    Duplex = 1 << 2,
    Color = 1 << 3,
    Grayscale = 1 << 4,
    BlackWhite = 1 << 5,
    AutoPageSize = 1 << 6,
    AutoDeskew = 1 << 7,
    Brightness = 1 << 8,
    Contrast = 1 << 9,
    BlankPageRemoval = 1 << 10,
}

public enum PixelType : byte
{
    BlackWhite = 0,
    Grayscale = 1,
    Rgb = 2,
}

public enum PageSource : byte
{
    Auto = 0,
    Flatbed = 1,
    Feeder = 2,
}

public enum PageSide : byte
{
    Front = 0,
    Back = 1,
}

public enum PaperSize : byte
{
    Auto = 0,
    A4 = 1,
    A3 = 2,
    A5 = 3,
    Letter = 4,
    Legal = 5,
    Executive = 6,
    B4 = 7,
    B5 = 8,
    Custom = 255,
}

public enum PageOrientation : byte
{
    Portrait = 0,
    Landscape = 1,
}

/// <summary>Encoding of a page as it travels over the wire.</summary>
public enum PageEncoding : byte
{
    /// <summary>Uncompressed bottom-up DIB rows, as TWAIN hands them to us.</summary>
    RawBgr = 0,
    Jpeg = 1,
    /// <summary>CCITT Group 4 fax encoding, wrapped as a single-page TIFF. Bitonal only.</summary>
    CcittG4Tiff = 2,
    Png = 3,
}

/// <summary>
/// Stable error codes. These are surfaced to the user and mapped by the data source
/// onto TWAIN condition codes, so the numeric values must not be reused.
/// </summary>
public enum ScanErrorCode : ushort
{
    None = 0,
    Unknown = 1,
    ScannerNotFound = 2,
    ScannerBusy = 3,
    ScannerDisconnected = 4,
    PaperJam = 5,
    FeederEmpty = 6,
    DoubleFeed = 7,
    CoverOpen = 8,
    DriverFault = 9,
    UserCancelled = 10,
    UnsupportedSetting = 11,
    TransportFailure = 12,
    AuthenticationFailed = 13,
    ProtocolViolation = 14,
    Timeout = 15,
    OutOfMemory = 16,
    NoLocalAgent = 17,
    SessionLost = 18,
}

public enum AuthStatus : byte
{
    Ok = 0,
    BadCredentials = 1,
    VersionMismatch = 2,
    NotConfigured = 3,
}

public enum DisconnectReason : byte
{
    Normal = 0,
    ProtocolError = 1,
    Timeout = 2,
    Shutdown = 3,
    SessionEnded = 4,
}
