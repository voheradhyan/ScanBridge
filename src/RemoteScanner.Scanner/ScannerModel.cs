using RemoteScanner.Protocol;

namespace RemoteScanner.Scanner;

/// <summary>
/// One acquired page, already encoded in the form it will travel in.
///
/// Encoding happens at the source rather than on the wire: a 600 dpi A4 colour page is
/// ~100 MB raw and ~4 MB as JPEG, and there is no point pushing the raw form through a
/// pipe and an RDP channel first. Exactly one of these exists at a time per job, which is
/// what keeps a 200-page scan off the heap.
/// </summary>
public sealed record ScannedPage(
    int PageNumber,
    PageSide Side,
    int WidthPixels,
    int HeightPixels,
    int DpiX,
    int DpiY,
    PixelType PixelType,
    PageEncoding Encoding,
    byte[] Data);

/// <summary>
/// Receives pages as the scanner produces them.
///
/// Deliberately synchronous: scanner drivers are COM objects that must be driven from a
/// single STA thread, and blocking that thread inside the sink is exactly how backpressure
/// should work — a full channel stops the ADF pulling the next sheet. Making this async
/// would mean blocking on a Task from an STA thread, which is a deadlock waiting to happen.
/// </summary>
public interface IScanSink
{
    /// <summary>Called once per page, in order. May block to apply backpressure.</summary>
    void Page(ScannedPage page);

    /// <summary>Progress for the UI. Never carries document content.</summary>
    void Progress(int pagesDone, long bytesTransferred);
}

/// <summary>
/// A scanning error that already knows which protocol code it maps to, so the reason the
/// user sees ("feeder is empty", "cover is open") survives all the way to the remote
/// application instead of collapsing into a generic failure.
/// </summary>
public sealed class ScanException : Exception
{
    public ScanException(ScanErrorCode code, string message, Exception? inner = null)
        : base(message, inner) => Code = code;

    public ScanErrorCode Code { get; }

    public static ScanException NotFound(string scannerId)
        => new(ScanErrorCode.ScannerNotFound, $"Scanner '{scannerId}' is no longer connected.");

    public static ScanException Cancelled()
        => new(ScanErrorCode.UserCancelled, "The scan was cancelled.");
}

/// <summary>
/// A source of physical scanners. Implemented once for WIA and once for TWAIN; the agent
/// merges both so the user sees a single list.
/// </summary>
public interface IScannerBackend : IDisposable
{
    ScannerInterface Interface { get; }

    /// <summary>Enumerates devices. Must not throw for an offline device — report it instead.</summary>
    IReadOnlyList<ScannerInfo> Enumerate();

    /// <summary>What this device can actually do. Never invents a capability.</summary>
    ScannerCapsResponseMessage GetCapabilities(string scannerId);

    /// <summary>
    /// Runs a scan to completion, pushing pages into <paramref name="sink"/>. Blocks until
    /// the job finishes; callers run it on a dedicated STA thread.
    /// Throws <see cref="ScanException"/> for anything the user needs to be told about.
    /// </summary>
    void Scan(string scannerId, ScanSettings settings, IScanSink sink,
              CancellationToken cancellationToken);
}

/// <summary>Resolutions offered when a driver reports a continuous range rather than a list.</summary>
public static class StandardResolutions
{
    public static readonly int[] All = { 75, 100, 150, 200, 240, 300, 400, 600, 1200 };

    public static int[] WithinRange(int minimum, int maximum)
        => All.Where(dpi => dpi >= minimum && dpi <= maximum).ToArray();
}
