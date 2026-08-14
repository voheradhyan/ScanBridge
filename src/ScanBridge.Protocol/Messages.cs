namespace ScanBridge.Protocol;

/// <summary>
/// Every message knows how to write its own payload and how to read itself back.
/// The layouts here are mirrored byte-for-byte in native/include/rs_protocol.h.
/// </summary>
public interface IMessage
{
    MessageType Type { get; }
    void Write(PayloadWriter writer);
}

public sealed record HelloMessage(
    byte ProtocolVersion,
    PeerRole Role,
    string MachineName,
    byte[] Nonce,
    PeerCapabilities Capabilities) : IMessage
{
    public MessageType Type => MessageType.Hello;

    public const int NonceLength = 32;

    public void Write(PayloadWriter w)
    {
        w.WriteByte(ProtocolVersion);
        w.WriteByte((byte)Role);
        w.WriteString(MachineName);
        w.WriteBytes(Nonce.Length == NonceLength
            ? Nonce
            : throw new ProtocolException($"Nonce must be {NonceLength} bytes."));
        w.WriteUInt32((uint)Capabilities);
    }

    public static HelloMessage Read(ref PayloadReader r) => new(
        r.ReadByte(),
        (PeerRole)r.ReadByte(),
        r.ReadString(),
        r.ReadBytes(NonceLength),
        (PeerCapabilities)r.ReadUInt32());
}

public sealed record HelloAckMessage(
    byte NegotiatedVersion,
    PeerRole Role,
    string MachineName,
    byte[] Nonce,
    PeerCapabilities Capabilities) : IMessage
{
    public MessageType Type => MessageType.HelloAck;

    public void Write(PayloadWriter w)
    {
        w.WriteByte(NegotiatedVersion);
        w.WriteByte((byte)Role);
        w.WriteString(MachineName);
        w.WriteBytes(Nonce);
        w.WriteUInt32((uint)Capabilities);
    }

    public static HelloAckMessage Read(ref PayloadReader r) => new(
        r.ReadByte(),
        (PeerRole)r.ReadByte(),
        r.ReadString(),
        r.ReadBytes(HelloMessage.NonceLength),
        (PeerCapabilities)r.ReadUInt32());
}

/// <summary>HMAC-SHA256 proof of possession of the pre-shared key. The key itself never travels.</summary>
public sealed record AuthenticateMessage(byte[] Mac) : IMessage
{
    public MessageType Type => MessageType.Authenticate;

    public const int MacLength = 32;

    public void Write(PayloadWriter w) => w.WriteBytes(
        Mac.Length == MacLength ? Mac : throw new ProtocolException($"MAC must be {MacLength} bytes."));

    public static AuthenticateMessage Read(ref PayloadReader r) => new(r.ReadBytes(MacLength));
}

/// <param name="ResponderMac">
/// Optional proof that the accepting end also holds the shared key.
///
/// Added for the direct network transport, where only the dialling end used to authenticate.
/// Without it, anything that can occupy the port — or answer for that address — completes a
/// handshake and learns the caller's nonce and machine name before the first encrypted record
/// fails. It cannot read or forge traffic either way, since the keys come from a secret it does
/// not have, but a link should fail at the handshake and say why, not several frames later.
///
/// It is appended after the existing fields, and readers that predate it stop after the detail
/// string, so this stays compatible with the native handshake in rs_pipe.h, which ignores
/// anything trailing. Empty on the pipe hops, which are protected by the pipe's own ACL.
/// </param>
public sealed record AuthResultMessage(AuthStatus Status, string Detail, byte[]? ResponderMac = null) : IMessage
{
    public MessageType Type => MessageType.AuthResult;

    public void Write(PayloadWriter w)
    {
        w.WriteByte((byte)Status);
        w.WriteString(Detail);
        if (ResponderMac is { Length: > 0 }) w.WriteBlob(ResponderMac);
    }

    public static AuthResultMessage Read(ref PayloadReader r)
    {
        var status = (AuthStatus)r.ReadByte();
        string detail = r.ReadString();
        byte[]? mac = r.Remaining > 0 ? r.ReadBlob() : null;
        return new AuthResultMessage(status, detail, mac);
    }
}

public sealed record ScannerEnumRequestMessage : IMessage
{
    public MessageType Type => MessageType.ScannerEnumRequest;
    public void Write(PayloadWriter w) { }
    public static ScannerEnumRequestMessage Read(ref PayloadReader r) => new();
}

public sealed record ScannerInfo(
    string Id,
    string Name,
    string Vendor,
    ScannerInterface Interface,
    ScannerStatus Status,
    ScannerFeatures Features,
    bool Is32BitOnly)
{
    public void Write(PayloadWriter w)
    {
        w.WriteString(Id);
        w.WriteString(Name);
        w.WriteString(Vendor);
        w.WriteByte((byte)Interface);
        w.WriteByte((byte)Status);
        w.WriteUInt32((uint)Features);
        w.WriteBool(Is32BitOnly);
    }

    public static ScannerInfo Read(ref PayloadReader r) => new(
        r.ReadString(),
        r.ReadString(),
        r.ReadString(),
        (ScannerInterface)r.ReadByte(),
        (ScannerStatus)r.ReadByte(),
        (ScannerFeatures)r.ReadUInt32(),
        r.ReadBool());
}

public sealed record ScannerEnumResponseMessage(IReadOnlyList<ScannerInfo> Scanners) : IMessage
{
    public MessageType Type => MessageType.ScannerEnumResponse;

    public const int MaxScanners = 256;

    public void Write(PayloadWriter w)
    {
        w.WriteInt32(Scanners.Count);
        foreach (var scanner in Scanners) scanner.Write(w);
    }

    public static ScannerEnumResponseMessage Read(ref PayloadReader r)
    {
        int count = r.ReadCount(MaxScanners);
        var list = new List<ScannerInfo>(count);
        for (int i = 0; i < count; i++) list.Add(ScannerInfo.Read(ref r));
        return new ScannerEnumResponseMessage(list);
    }
}

public sealed record ScannerCapsRequestMessage(string ScannerId) : IMessage
{
    public MessageType Type => MessageType.ScannerCapsRequest;
    public void Write(PayloadWriter w) => w.WriteString(ScannerId);
    public static ScannerCapsRequestMessage Read(ref PayloadReader r) => new(r.ReadString());
}

/// <summary>
/// What the physical scanner can actually do. The data source publishes exactly this to the
/// remote application, so a capability the hardware lacks is never advertised.
/// </summary>
public sealed record ScannerCapsResponseMessage(
    string ScannerId,
    bool Found,
    IReadOnlyList<int> Resolutions,
    IReadOnlyList<PixelType> PixelTypes,
    IReadOnlyList<PaperSize> PaperSizes,
    ScannerFeatures Features,
    double BrightnessMin,
    double BrightnessMax,
    double ContrastMin,
    double ContrastMax,
    int MaxWidthThousandthsInch,
    int MaxHeightThousandthsInch) : IMessage
{
    public MessageType Type => MessageType.ScannerCapsResponse;

    public const int MaxListLength = 512;

    public void Write(PayloadWriter w)
    {
        w.WriteString(ScannerId);
        w.WriteBool(Found);

        w.WriteInt32(Resolutions.Count);
        foreach (int dpi in Resolutions) w.WriteInt32(dpi);

        w.WriteInt32(PixelTypes.Count);
        foreach (var pixelType in PixelTypes) w.WriteByte((byte)pixelType);

        w.WriteInt32(PaperSizes.Count);
        foreach (var size in PaperSizes) w.WriteByte((byte)size);

        w.WriteUInt32((uint)Features);
        w.WriteFixed(BrightnessMin);
        w.WriteFixed(BrightnessMax);
        w.WriteFixed(ContrastMin);
        w.WriteFixed(ContrastMax);
        w.WriteInt32(MaxWidthThousandthsInch);
        w.WriteInt32(MaxHeightThousandthsInch);
    }

    public static ScannerCapsResponseMessage Read(ref PayloadReader r)
    {
        string id = r.ReadString();
        bool found = r.ReadBool();

        int resolutionCount = r.ReadCount(MaxListLength);
        var resolutions = new List<int>(resolutionCount);
        for (int i = 0; i < resolutionCount; i++) resolutions.Add(r.ReadInt32());

        int pixelTypeCount = r.ReadCount(MaxListLength);
        var pixelTypes = new List<PixelType>(pixelTypeCount);
        for (int i = 0; i < pixelTypeCount; i++) pixelTypes.Add((PixelType)r.ReadByte());

        int paperSizeCount = r.ReadCount(MaxListLength);
        var paperSizes = new List<PaperSize>(paperSizeCount);
        for (int i = 0; i < paperSizeCount; i++) paperSizes.Add((PaperSize)r.ReadByte());

        return new ScannerCapsResponseMessage(
            id, found, resolutions, pixelTypes, paperSizes,
            (ScannerFeatures)r.ReadUInt32(),
            r.ReadFixed(), r.ReadFixed(), r.ReadFixed(), r.ReadFixed(),
            r.ReadInt32(), r.ReadInt32());
    }
}

/// <summary>Everything the remote application asked for, in scanner-neutral terms.</summary>
public sealed record ScanSettings(
    int Resolution,
    PixelType PixelType,
    PageSource Source,
    bool Duplex,
    PaperSize PaperSize,
    PageOrientation Orientation,
    double Brightness,
    double Contrast,
    int PageLimit,
    bool AutoDeskew,
    bool AutoPageSize,
    bool BlankPageRemoval,
    bool ShowScannerUi,
    PageEncoding PreferredEncoding,
    int JpegQuality,
    int CustomWidthThousandthsInch,
    int CustomHeightThousandthsInch)
{
    /// <summary>0 means "scan until the feeder is empty".</summary>
    public const int UnlimitedPages = 0;

    public static ScanSettings Default { get; } = new(
        Resolution: 300,
        PixelType: PixelType.Rgb,
        Source: PageSource.Auto,
        Duplex: false,
        PaperSize: PaperSize.Auto,
        Orientation: PageOrientation.Portrait,
        Brightness: 0,
        Contrast: 0,
        PageLimit: UnlimitedPages,
        AutoDeskew: false,
        AutoPageSize: false,
        BlankPageRemoval: false,
        ShowScannerUi: false,
        PreferredEncoding: PageEncoding.Jpeg,
        JpegQuality: 85,
        CustomWidthThousandthsInch: 0,
        CustomHeightThousandthsInch: 0);

    public void Write(PayloadWriter w)
    {
        w.WriteInt32(Resolution);
        w.WriteByte((byte)PixelType);
        w.WriteByte((byte)Source);
        w.WriteBool(Duplex);
        w.WriteByte((byte)PaperSize);
        w.WriteByte((byte)Orientation);
        w.WriteFixed(Brightness);
        w.WriteFixed(Contrast);
        w.WriteInt32(PageLimit);
        w.WriteBool(AutoDeskew);
        w.WriteBool(AutoPageSize);
        w.WriteBool(BlankPageRemoval);
        w.WriteBool(ShowScannerUi);
        w.WriteByte((byte)PreferredEncoding);
        w.WriteInt32(JpegQuality);
        w.WriteInt32(CustomWidthThousandthsInch);
        w.WriteInt32(CustomHeightThousandthsInch);
    }

    public static ScanSettings Read(ref PayloadReader r) => new(
        r.ReadInt32(),
        (PixelType)r.ReadByte(),
        (PageSource)r.ReadByte(),
        r.ReadBool(),
        (PaperSize)r.ReadByte(),
        (PageOrientation)r.ReadByte(),
        r.ReadFixed(),
        r.ReadFixed(),
        r.ReadInt32(),
        r.ReadBool(),
        r.ReadBool(),
        r.ReadBool(),
        r.ReadBool(),
        (PageEncoding)r.ReadByte(),
        r.ReadInt32(),
        r.ReadInt32(),
        r.ReadInt32());

    /// <summary>
    /// Rejects values that no scanner could honour. Called on the receiving side before the
    /// settings reach any driver, so a malformed request cannot be handed to vendor code.
    /// </summary>
    public void Validate()
    {
        if (Resolution is < 25 or > 9600)
            throw new ProtocolException($"Resolution {Resolution} dpi is outside the supported 25-9600 range.");
        if (!Enum.IsDefined(PixelType)) throw new ProtocolException($"Unknown pixel type {PixelType}.");
        if (!Enum.IsDefined(Source)) throw new ProtocolException($"Unknown page source {Source}.");
        if (!Enum.IsDefined(Orientation)) throw new ProtocolException($"Unknown orientation {Orientation}.");
        if (!Enum.IsDefined(PreferredEncoding)) throw new ProtocolException($"Unknown encoding {PreferredEncoding}.");
        if (PageLimit is < 0 or > 10_000)
            throw new ProtocolException($"Page limit {PageLimit} is outside the supported 0-10000 range.");
        if (JpegQuality is < 1 or > 100)
            throw new ProtocolException($"JPEG quality {JpegQuality} is outside 1-100.");
        if (Brightness is < -1000 or > 1000 || Contrast is < -1000 or > 1000)
            throw new ProtocolException("Brightness/contrast outside the -1000..1000 range.");
        if (CustomWidthThousandthsInch is < 0 or > 60_000 || CustomHeightThousandthsInch is < 0 or > 200_000)
            throw new ProtocolException("Custom page dimensions are implausible.");
    }
}

public sealed record ScanRequestMessage(string ScannerId, ScanSettings Settings) : IMessage
{
    public MessageType Type => MessageType.ScanRequest;

    public void Write(PayloadWriter w)
    {
        w.WriteString(ScannerId);
        Settings.Write(w);
    }

    public static ScanRequestMessage Read(ref PayloadReader r) => new(r.ReadString(), ScanSettings.Read(ref r));
}

public sealed record ScanStartMessage(uint JobId) : IMessage
{
    public MessageType Type => MessageType.ScanStart;
    public void Write(PayloadWriter w) => w.WriteUInt32(JobId);
    public static ScanStartMessage Read(ref PayloadReader r) => new(r.ReadUInt32());
}

public sealed record ScanPageBeginMessage(
    uint JobId,
    int PageNumber,
    PageSide Side,
    int WidthPixels,
    int HeightPixels,
    int DpiX,
    int DpiY,
    PixelType PixelType,
    PageEncoding Encoding,
    int EncodedLength) : IMessage
{
    public MessageType Type => MessageType.ScanPageBegin;

    public void Write(PayloadWriter w)
    {
        w.WriteUInt32(JobId);
        w.WriteInt32(PageNumber);
        w.WriteByte((byte)Side);
        w.WriteInt32(WidthPixels);
        w.WriteInt32(HeightPixels);
        w.WriteInt32(DpiX);
        w.WriteInt32(DpiY);
        w.WriteByte((byte)PixelType);
        w.WriteByte((byte)Encoding);
        w.WriteInt32(EncodedLength);
    }

    public static ScanPageBeginMessage Read(ref PayloadReader r) => new(
        r.ReadUInt32(), r.ReadInt32(), (PageSide)r.ReadByte(),
        r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32(),
        (PixelType)r.ReadByte(), (PageEncoding)r.ReadByte(), r.ReadInt32());
}

public sealed record ScanPageDataMessage(uint JobId, int PageNumber, long Offset, byte[] Data) : IMessage
{
    public MessageType Type => MessageType.ScanPageData;

    /// <summary>Payload budget left for pixel bytes after the fixed header fields.</summary>
    public const int MaxChunk = Wire.MaxPayload - 4 - 4 - 8 - 4;

    public void Write(PayloadWriter w)
    {
        w.WriteUInt32(JobId);
        w.WriteInt32(PageNumber);
        w.WriteInt64(Offset);
        w.WriteBlob(Data);
    }

    public static ScanPageDataMessage Read(ref PayloadReader r) => new(
        r.ReadUInt32(), r.ReadInt32(), r.ReadInt64(), r.ReadBlob());
}

public sealed record ScanPageEndMessage(uint JobId, int PageNumber, uint Crc32) : IMessage
{
    public MessageType Type => MessageType.ScanPageEnd;

    public void Write(PayloadWriter w)
    {
        w.WriteUInt32(JobId);
        w.WriteInt32(PageNumber);
        w.WriteUInt32(Crc32);
    }

    public static ScanPageEndMessage Read(ref PayloadReader r) => new(r.ReadUInt32(), r.ReadInt32(), r.ReadUInt32());
}

public sealed record ScanProgressMessage(uint JobId, int PagesDone, long BytesTransferred, long BytesPerSecond) : IMessage
{
    public MessageType Type => MessageType.ScanProgress;

    public void Write(PayloadWriter w)
    {
        w.WriteUInt32(JobId);
        w.WriteInt32(PagesDone);
        w.WriteInt64(BytesTransferred);
        w.WriteInt64(BytesPerSecond);
    }

    public static ScanProgressMessage Read(ref PayloadReader r) => new(
        r.ReadUInt32(), r.ReadInt32(), r.ReadInt64(), r.ReadInt64());
}

public sealed record ScanCompleteMessage(uint JobId, int TotalPages, long TotalBytes) : IMessage
{
    public MessageType Type => MessageType.ScanComplete;

    public void Write(PayloadWriter w)
    {
        w.WriteUInt32(JobId);
        w.WriteInt32(TotalPages);
        w.WriteInt64(TotalBytes);
    }

    public static ScanCompleteMessage Read(ref PayloadReader r) => new(r.ReadUInt32(), r.ReadInt32(), r.ReadInt64());
}

public sealed record ScanCancelMessage(uint JobId) : IMessage
{
    public MessageType Type => MessageType.ScanCancel;
    public void Write(PayloadWriter w) => w.WriteUInt32(JobId);
    public static ScanCancelMessage Read(ref PayloadReader r) => new(r.ReadUInt32());
}

public sealed record ScanErrorMessage(uint JobId, ScanErrorCode Code, string Message) : IMessage
{
    public MessageType Type => MessageType.ScanError;

    public void Write(PayloadWriter w)
    {
        w.WriteUInt32(JobId);
        w.WriteUInt16((ushort)Code);
        w.WriteString(Message);
    }

    public static ScanErrorMessage Read(ref PayloadReader r) => new(
        r.ReadUInt32(), (ScanErrorCode)r.ReadUInt16(), r.ReadString());
}

public sealed record FlowCreditMessage(uint JobId, int Frames) : IMessage
{
    public MessageType Type => MessageType.FlowCredit;

    public void Write(PayloadWriter w)
    {
        w.WriteUInt32(JobId);
        w.WriteInt32(Frames);
    }

    public static FlowCreditMessage Read(ref PayloadReader r) => new(r.ReadUInt32(), r.ReadInt32());
}

public sealed record HeartbeatMessage(long TicksUtc) : IMessage
{
    public MessageType Type => MessageType.Heartbeat;
    public void Write(PayloadWriter w) => w.WriteInt64(TicksUtc);
    public static HeartbeatMessage Read(ref PayloadReader r) => new(r.ReadInt64());
}

public sealed record DisconnectMessage(DisconnectReason Reason, string Detail) : IMessage
{
    public MessageType Type => MessageType.Disconnect;

    public void Write(PayloadWriter w)
    {
        w.WriteByte((byte)Reason);
        w.WriteString(Detail);
    }

    public static DisconnectMessage Read(ref PayloadReader r) => new((DisconnectReason)r.ReadByte(), r.ReadString());
}
