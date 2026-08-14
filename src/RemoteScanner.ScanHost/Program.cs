using System.IO.Pipes;
using System.Runtime.Versioning;
using RemoteScanner.Common;
using RemoteScanner.Protocol;
using RemoteScanner.Scanner;
using RemoteScanner.Scanner.Twain;

namespace RemoteScanner.ScanHost;

/// <summary>
/// A sacrificial process that talks to the physical scanner and nothing else.
///
/// Two reasons it exists rather than the agent scanning in-process:
///
///   * Vendor TWAIN drivers are the least reliable code in the chain. They leak, they show
///     modal dialogs, and some call ExitProcess on error. If that happened inside the tray
///     agent it would take scanner redirection down for every RDP session. Here it costs
///     one job, and the agent reports a DriverFault.
///   * A TWAIN data source can only be loaded by a process of its own bitness. This host is
///     built x86 and x64; the agent runs whichever matches, which is what keeps 32-bit-only
///     scanner drivers usable on a 64-bit PC.
///
/// The main thread is STA and pumps messages, because that is what TWAIN requires.
/// </summary>
[SupportedOSPlatform("windows")]
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        Log.Initialize($"scanhost-{(Environment.Is64BitProcess ? "x64" : "x86")}");
        var log = Log.Logger;

        string? pipeName = GetArgument(args, "--pipe");
        if (pipeName is null)
        {
            Console.Error.WriteLine("usage: RemoteScanner.ScanHost --pipe <name>");
            return 2;
        }

        try
        {
            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.None);
            pipe.Connect(10_000);

            Frame command = SyncFrames.Read(pipe);
            log.Debug("ScanHost received {Command}.", command.Type);

            switch (command.Type)
            {
                case MessageType.ScannerEnumRequest:
                    HandleEnumerate(pipe);
                    break;

                case MessageType.ScannerCapsRequest:
                    HandleCapabilities(pipe, command);
                    break;

                case MessageType.ScanRequest:
                    HandleScan(pipe, command, log);
                    break;

                default:
                    SendError(pipe, 0, ScanErrorCode.ProtocolViolation,
                              $"ScanHost cannot handle {command.Type}.");
                    return 3;
            }

            pipe.Flush();
            // Let the agent drain before the pipe tears down with the process.
            pipe.WaitForPipeDrain();
            return 0;
        }
        catch (Exception ex)
        {
            log.Error(ex, "ScanHost failed.");
            return 1;
        }
        finally
        {
            Log.Shutdown();
        }
    }

    private static void HandleEnumerate(Stream pipe)
    {
        var scanners = new List<ScannerInfo>();

        // TWAIN first: it is the interface business scanners are driven through, and its
        // device names are what users recognise.
        using (var twain = new TwainBackend())
        {
            scanners.AddRange(SafeEnumerate(twain));
        }

        // WIA is only enumerated by the 64-bit host. It is not bitness-split the way TWAIN
        // is, so running it in both would list every device twice.
        if (Environment.Is64BitProcess)
        {
            using var wia = new WiaBackend();
            scanners.AddRange(SafeEnumerate(wia));
        }

        SyncFrames.Write(pipe, MessageType.ScannerEnumResponse, new ScannerEnumResponseMessage(scanners));
    }

    private static IEnumerable<ScannerInfo> SafeEnumerate(IScannerBackend backend)
    {
        try
        {
            return backend.Enumerate();
        }
        catch (Exception ex)
        {
            Log.Logger.Warning(ex, "Enumeration failed for {Interface}.", backend.Interface);
            return Array.Empty<ScannerInfo>();
        }
    }

    private static void HandleCapabilities(Stream pipe, Frame command)
    {
        string scannerId = ReadScannerId(command);
        IScannerBackend backend = CreateBackend(scannerId);

        try
        {
            SyncFrames.Write(pipe, MessageType.ScannerCapsResponse, backend.GetCapabilities(scannerId));
        }
        catch (ScanException ex)
        {
            Log.Logger.Warning("Capability query failed for {Scanner}: {Message}", scannerId, ex.Message);
            SyncFrames.Write(pipe, MessageType.ScannerCapsResponse, new ScannerCapsResponseMessage(
                scannerId, false, Array.Empty<int>(), Array.Empty<PixelType>(), Array.Empty<PaperSize>(),
                ScannerFeatures.None, 0, 0, 0, 0, 0, 0));
        }
        finally
        {
            backend.Dispose();
        }
    }

    private static void HandleScan(Stream pipe, Frame command, Serilog.ILogger log)
    {
        (string scannerId, ScanSettings settings) = ReadScanRequest(command);

        IScannerBackend backend = CreateBackend(scannerId);
        var sink = new PipeSink(pipe);

        try
        {
            SyncFrames.Write(pipe, MessageType.ScanStart, new ScanStartMessage(PipeSink.JobId));

            backend.Scan(scannerId, settings, sink, CancellationToken.None);

            SyncFrames.Write(pipe, MessageType.ScanComplete,
                             new ScanCompleteMessage(PipeSink.JobId, sink.PagesSent, sink.BytesSent));

            log.Information("Scan finished: {Pages} pages, {Bytes} bytes.", sink.PagesSent, sink.BytesSent);
        }
        catch (ScanException ex)
        {
            log.Warning("Scan failed ({Code}): {Message}", ex.Code, ex.Message);
            SendError(pipe, PipeSink.JobId, ex.Code, ex.Message);
        }
        catch (Exception ex)
        {
            log.Error(ex, "Scan failed unexpectedly.");
            SendError(pipe, PipeSink.JobId, ScanErrorCode.DriverFault, ex.Message);
        }
        finally
        {
            backend.Dispose();
        }
    }

    /// <summary>The id prefix decides which stack the device belongs to.</summary>
    private static IScannerBackend CreateBackend(string scannerId)
        => scannerId.StartsWith(WiaBackend.IdPrefix, StringComparison.Ordinal)
            ? new WiaBackend()
            : new TwainBackend();

    private static void SendError(Stream pipe, uint jobId, ScanErrorCode code, string message)
        => SyncFrames.Write(pipe, MessageType.ScanError, new ScanErrorMessage(jobId, code, message));

    private static string ReadScannerId(Frame frame)
    {
        var reader = frame.Reader();
        return ScannerCapsRequestMessage.Read(ref reader).ScannerId;
    }

    private static (string ScannerId, ScanSettings Settings) ReadScanRequest(Frame frame)
    {
        var reader = frame.Reader();
        ScanRequestMessage request = ScanRequestMessage.Read(ref reader);
        return (request.ScannerId, request.Settings);
    }

    private static string? GetArgument(string[] args, string name)
    {
        int index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}

/// <summary>
/// Writes pages straight down the pipe as the scanner produces them.
///
/// Synchronous on purpose: blocking here is the backpressure that stops a fast ADF
/// outrunning the RDP channel, and the STA thread driving the scanner must not await.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class PipeSink : IScanSink
{
    /// <summary>One job per host process, so the id is a constant.</summary>
    public const uint JobId = 1;

    private readonly Stream _pipe;

    public PipeSink(Stream pipe) => _pipe = pipe;

    public int PagesSent { get; private set; }
    public long BytesSent { get; private set; }

    public void Page(ScannedPage page)
    {
        SyncFrames.Write(_pipe, MessageType.ScanPageBegin, new ScanPageBeginMessage(
            JobId, page.PageNumber, page.Side, page.WidthPixels, page.HeightPixels,
            page.DpiX, page.DpiY, page.PixelType, page.Encoding, page.Data.Length));

        // The page is already encoded; it goes out in frame-sized chunks so no single write
        // can exceed what the channel will accept.
        int offset = 0;
        while (offset < page.Data.Length)
        {
            int chunk = Math.Min(ScanPageDataMessage.MaxChunk, page.Data.Length - offset);
            SyncFrames.Write(_pipe, MessageType.ScanPageData, new ScanPageDataMessage(
                JobId, page.PageNumber, offset, page.Data[offset..(offset + chunk)]));
            offset += chunk;
        }

        SyncFrames.Write(_pipe, MessageType.ScanPageEnd,
                         new ScanPageEndMessage(JobId, page.PageNumber, Crc32.Compute(page.Data)));

        PagesSent = page.PageNumber;
        BytesSent += page.Data.Length;
    }

    public void Progress(int pagesDone, long bytesTransferred)
        => SyncFrames.Write(_pipe, MessageType.ScanProgress,
                            new ScanProgressMessage(JobId, pagesDone, bytesTransferred, 0));
}

/// <summary>
/// Blocking frame reader/writer. The async FrameChannel is the wrong tool inside ScanHost:
/// everything here runs on one STA thread that must not await.
/// </summary>
internal static class SyncFrames
{
    public static void Write(Stream stream, MessageType type, IMessage message)
    {
        var writer = new PayloadWriter();
        message.Write(writer);
        byte[] frame = FrameCodec.Encode(type, 1, writer.AsMemory().Span);
        stream.Write(frame, 0, frame.Length);
    }

    public static Frame Read(Stream stream)
    {
        byte[] header = new byte[Wire.HeaderSize];
        ReadExact(stream, header);

        int length = FrameCodec.ParseHeader(header, out MessageType type, out uint streamId);
        byte[] payload = length == 0 ? Array.Empty<byte>() : new byte[length];
        if (length > 0) ReadExact(stream, payload);

        return new Frame(type, streamId, payload);
    }

    private static void ReadExact(Stream stream, byte[] buffer)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = stream.Read(buffer, offset, buffer.Length - offset);
            if (read == 0) throw new EndOfStreamException("The agent closed the connection.");
            offset += read;
        }
    }
}
