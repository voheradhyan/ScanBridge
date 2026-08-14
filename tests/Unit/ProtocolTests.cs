using ScanBridge.Protocol;
using Xunit;

namespace ScanBridge.Tests.Unit;

/// <summary>
/// The wire format is implemented twice — once in C# and once by hand in C++ for the data
/// source and the DVC plugin. These tests pin the C# side's byte layout so drift shows up
/// here rather than as a corrupt page inside Acrobat.
/// </summary>
public class FrameCodecTests
{
    [Fact]
    public void Header_RoundTrips()
    {
        byte[] frame = FrameCodec.Encode(MessageType.ScanPageData, 42, new byte[] { 1, 2, 3 });

        Assert.Equal(Wire.HeaderSize + 3, frame.Length);
        Assert.Equal(Wire.Magic, frame[0]);
        Assert.Equal(Wire.Version, frame[1]);

        int length = FrameCodec.ParseHeader(frame, out MessageType type, out uint streamId);

        Assert.Equal(3, length);
        Assert.Equal(MessageType.ScanPageData, type);
        Assert.Equal(42u, streamId);
    }

    [Fact]
    public void ParseHeader_RejectsBadMagic()
    {
        byte[] frame = FrameCodec.Encode(MessageType.Heartbeat, 1, Array.Empty<byte>());
        frame[0] = 0x00;

        var ex = Assert.Throws<ProtocolException>(() => FrameCodec.ParseHeader(frame, out _, out _));
        Assert.Contains("magic", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParseHeader_RejectsUnknownVersion()
    {
        byte[] frame = FrameCodec.Encode(MessageType.Heartbeat, 1, Array.Empty<byte>());
        frame[1] = 0x7F;

        Assert.Throws<ProtocolException>(() => FrameCodec.ParseHeader(frame, out _, out _));
    }

    [Fact]
    public void Encode_RejectsOversizePayload()
    {
        var payload = new byte[Wire.MaxPayload + 1];
        Assert.Throws<ProtocolException>(() => FrameCodec.Encode(MessageType.ScanPageData, 1, payload));
    }

    /// <summary>A truncated frame must be rejected, not read past its end.</summary>
    [Fact]
    public void Reader_RejectsTruncatedPayload()
    {
        var writer = new PayloadWriter();
        writer.WriteInt32(1);

        Assert.Throws<ProtocolException>(() => ReadTwoInt32(writer.ToArray()));
    }

    [Fact]
    public void Reader_RejectsAbsurdElementCount()
    {
        var writer = new PayloadWriter();
        writer.WriteInt32(int.MaxValue);

        Assert.Throws<ProtocolException>(() => ReadCount(writer.ToArray()));
    }

    // PayloadReader is a ref struct, so it cannot be captured by the assertion lambdas.
    private static void ReadTwoInt32(byte[] payload)
    {
        var reader = new PayloadReader(payload);
        reader.ReadInt32();
        reader.ReadInt32();
    }

    private static void ReadCount(byte[] payload)
    {
        var reader = new PayloadReader(payload);
        reader.ReadCount(256);
    }
}

public class MessageRoundTripTests
{
    private static T RoundTrip<T>(IMessage message, ReadFunc<T> read)
    {
        var writer = new PayloadWriter();
        message.Write(writer);

        var reader = new PayloadReader(writer.AsMemory().Span);
        return read(ref reader);
    }

    private delegate T ReadFunc<out T>(ref PayloadReader reader);

    [Fact]
    public void Hello_RoundTrips()
    {
        byte[] nonce = ChannelAuth.NewNonce();
        var original = new HelloMessage(Wire.Version, PeerRole.TwainDataSource, "SERVER01", nonce,
                                        PeerCapabilities.Duplex | PeerCapabilities.FlowControl);

        HelloMessage result = RoundTrip(original, HelloMessage.Read);

        Assert.Equal(original.ProtocolVersion, result.ProtocolVersion);
        Assert.Equal(original.Role, result.Role);
        Assert.Equal(original.MachineName, result.MachineName);
        Assert.Equal(original.Capabilities, result.Capabilities);
        Assert.Equal(nonce, result.Nonce);
    }

    [Fact]
    public void ScanSettings_RoundTripPreservesEveryField()
    {
        var original = new ScanSettings(
            Resolution: 600, PixelType: PixelType.Grayscale, Source: PageSource.Feeder, Duplex: true,
            PaperSize: PaperSize.Legal, Orientation: PageOrientation.Landscape,
            Brightness: -12.5, Contrast: 33.25, PageLimit: 25,
            AutoDeskew: true, AutoPageSize: true, BlankPageRemoval: true, ShowScannerUi: false,
            PreferredEncoding: PageEncoding.Jpeg, JpegQuality: 92,
            CustomWidthThousandthsInch: 8500, CustomHeightThousandthsInch: 14000);

        var writer = new PayloadWriter();
        original.Write(writer);
        var reader = new PayloadReader(writer.AsMemory().Span);
        ScanSettings result = ScanSettings.Read(ref reader);

        Assert.Equal(original, result);
        // Fixed-point survives the C++ boundary exactly at three decimal places.
        Assert.Equal(-12.5, result.Brightness, 3);
        Assert.Equal(33.25, result.Contrast, 3);
    }

    [Fact]
    public void ScannerEnumResponse_RoundTrips()
    {
        var original = new ScannerEnumResponseMessage(new[]
        {
            new ScannerInfo("twain:Canon DR-C225", "Canon DR-C225", "Canon", ScannerInterface.Twain,
                            ScannerStatus.Ready,
                            ScannerFeatures.Feeder | ScannerFeatures.Duplex | ScannerFeatures.Color, true),
            new ScannerInfo("wia:{GUID}", "Brother DCP", "Brother", ScannerInterface.Wia,
                            ScannerStatus.Offline, ScannerFeatures.Flatbed, false),
        });

        var writer = new PayloadWriter();
        original.Write(writer);
        var reader = new PayloadReader(writer.AsMemory().Span);
        ScannerEnumResponseMessage result = ScannerEnumResponseMessage.Read(ref reader);

        Assert.Equal(2, result.Scanners.Count);
        Assert.Equal("Canon DR-C225", result.Scanners[0].Name);
        Assert.True(result.Scanners[0].Is32BitOnly);
        Assert.Equal(ScannerStatus.Offline, result.Scanners[1].Status);
    }

    [Fact]
    public void ScanPageBegin_RoundTrips()
    {
        var original = new ScanPageBeginMessage(7, 3, PageSide.Back, 2480, 3508, 300, 300,
                                                PixelType.Rgb, PageEncoding.Jpeg, 1_234_567);

        ScanPageBeginMessage result = RoundTrip(original, ScanPageBeginMessage.Read);
        Assert.Equal(original, result);
    }

    [Fact]
    public void ScanError_RoundTripsMessageText()
    {
        var original = new ScanErrorMessage(9, ScanErrorCode.PaperJam, "There is a paper jam.");
        ScanErrorMessage result = RoundTrip(original, ScanErrorMessage.Read);
        Assert.Equal(original, result);
    }

    /// <summary>A page chunk must fit in one frame with its header fields.</summary>
    [Fact]
    public void ScanPageData_MaxChunkFitsInAFrame()
    {
        var message = new ScanPageDataMessage(1, 1, 0, new byte[ScanPageDataMessage.MaxChunk]);

        var writer = new PayloadWriter();
        message.Write(writer);

        Assert.True(writer.Length <= Wire.MaxPayload,
                    $"A maximum chunk encodes to {writer.Length} bytes, over the {Wire.MaxPayload} limit.");
    }
}

public class ScanSettingsValidationTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(24)]
    [InlineData(9601)]
    [InlineData(-300)]
    public void Validate_RejectsImpossibleResolution(int dpi)
    {
        ScanSettings settings = ScanSettings.Default with { Resolution = dpi };
        Assert.Throws<ProtocolException>(settings.Validate);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public void Validate_RejectsBadJpegQuality(int quality)
    {
        ScanSettings settings = ScanSettings.Default with { JpegQuality = quality };
        Assert.Throws<ProtocolException>(settings.Validate);
    }

    [Fact]
    public void Validate_RejectsUnknownEnumValue()
    {
        ScanSettings settings = ScanSettings.Default with { PixelType = (PixelType)99 };
        Assert.Throws<ProtocolException>(settings.Validate);
    }

    [Fact]
    public void Validate_AcceptsDefaults()
    {
        ScanSettings.Default.Validate();
    }

    [Fact]
    public void Validate_AcceptsUnlimitedPageCount()
    {
        ScanSettings settings = ScanSettings.Default with { PageLimit = ScanSettings.UnlimitedPages };
        settings.Validate();
    }
}

public class Crc32Tests
{
    /// <summary>Known-answer test against the standard CRC-32 check value.</summary>
    [Fact]
    public void Crc32_MatchesKnownVector()
    {
        byte[] data = "123456789"u8.ToArray();
        Assert.Equal(0xCBF43926u, Crc32.Compute(data));
    }

    [Fact]
    public void Crc32_ChunkedMatchesWhole()
    {
        var data = new byte[10_000];
        Random.Shared.NextBytes(data);

        uint whole = Crc32.Compute(data);

        uint state = Crc32.Seed;
        for (int offset = 0; offset < data.Length; offset += 997)
        {
            int length = Math.Min(997, data.Length - offset);
            state = Crc32.Append(state, data.AsSpan(offset, length));
        }

        Assert.Equal(whole, Crc32.Finish(state));
    }
}

public class ChannelAuthTests
{
    [Fact]
    public void Mac_MatchesForTheSameInputs()
    {
        byte[] key = ChannelAuth.NewKey();
        byte[] initiator = ChannelAuth.NewNonce();
        byte[] responder = ChannelAuth.NewNonce();

        byte[] a = ChannelAuth.ComputeMac(key, ChannelAuth.InitiatorLabel, initiator, responder, 3);
        byte[] b = ChannelAuth.ComputeMac(key, ChannelAuth.InitiatorLabel, initiator, responder, 3);

        Assert.True(ChannelAuth.Verify(a, b));
    }

    [Fact]
    public void Mac_DiffersForADifferentSession()
    {
        byte[] key = ChannelAuth.NewKey();
        byte[] initiator = ChannelAuth.NewNonce();
        byte[] responder = ChannelAuth.NewNonce();

        byte[] session3 = ChannelAuth.ComputeMac(key, ChannelAuth.InitiatorLabel, initiator, responder, 3);
        byte[] session4 = ChannelAuth.ComputeMac(key, ChannelAuth.InitiatorLabel, initiator, responder, 4);

        Assert.False(ChannelAuth.Verify(session3, session4));
    }

    /// <summary>
    /// The direction label is what stops a captured response being replayed as a request.
    /// </summary>
    [Fact]
    public void Mac_DiffersByDirection()
    {
        byte[] key = ChannelAuth.NewKey();
        byte[] initiator = ChannelAuth.NewNonce();
        byte[] responder = ChannelAuth.NewNonce();

        byte[] forward = ChannelAuth.ComputeMac(key, ChannelAuth.InitiatorLabel, initiator, responder, 1);
        byte[] backward = ChannelAuth.ComputeMac(key, ChannelAuth.ResponderLabel, initiator, responder, 1);

        Assert.False(ChannelAuth.Verify(forward, backward));
    }

    [Fact]
    public void Mac_DiffersForADifferentKey()
    {
        byte[] initiator = ChannelAuth.NewNonce();
        byte[] responder = ChannelAuth.NewNonce();

        byte[] a = ChannelAuth.ComputeMac(ChannelAuth.NewKey(), ChannelAuth.InitiatorLabel,
                                          initiator, responder, 1);
        byte[] b = ChannelAuth.ComputeMac(ChannelAuth.NewKey(), ChannelAuth.InitiatorLabel,
                                          initiator, responder, 1);

        Assert.False(ChannelAuth.Verify(a, b));
    }
}

public class FlowControlTests
{
    [Fact]
    public async Task Sender_BlocksWhenCreditIsExhausted()
    {
        using var flow = new FlowController(window: 2);

        await flow.AcquireAsync(CancellationToken.None);
        await flow.AcquireAsync(CancellationToken.None);

        // The third acquire must not complete until the receiver grants more.
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => flow.AcquireAsync(timeout.Token));

        flow.Grant(1);
        await flow.AcquireAsync(CancellationToken.None);
    }

    [Fact]
    public void Receiver_OnlyRefillsOnceItIsWorthARoundTrip()
    {
        var flow = new FlowController();

        int granted = 0;
        for (int i = 0; i < Wire.CreditRefillThreshold - 1; i++) granted += flow.Consume();
        Assert.Equal(0, granted);

        Assert.Equal(Wire.CreditRefillThreshold, flow.Consume());

        flow.Dispose();
    }
}
