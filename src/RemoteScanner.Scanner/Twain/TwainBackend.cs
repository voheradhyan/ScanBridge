using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using RemoteScanner.Protocol;

namespace RemoteScanner.Scanner.Twain;

/// <summary>
/// Scanner access through TWAIN — the interface every business scanner and every
/// professional scanning application actually speaks.
///
/// Two constraints shape this code:
///
///   * TWAIN is single-threaded and window-driven. A data source shows dialogs and posts
///     messages, so all of this must run on one STA thread that pumps a real message loop.
///     Callers guarantee that by running it inside ScanHost.
///   * A data source can only be loaded by a process of its own bitness. This class reports
///     whatever the current process can see; the agent runs both an x86 and an x64 ScanHost
///     and merges the results, which is how 32-bit-only vendor drivers stay usable on a
///     64-bit PC.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class TwainBackend : IScannerBackend
{
    public const string IdPrefix = "twain:";

    private string _diagnostic = string.Empty;

    public ScannerInterface Interface => ScannerInterface.Twain;

    /// <summary>Why TWAIN is unavailable, when it is. Surfaced in the diagnostics report.</summary>
    public string Diagnostic => _diagnostic;

    public IReadOnlyList<ScannerInfo> Enumerate()
    {
        using TwainDsm? dsm = TwainDsm.TryLoad(out _diagnostic);
        if (dsm is null) return Array.Empty<ScannerInfo>();

        using var window = new MessageWindow();
        using var session = new TwainSession(dsm, window.Handle);

        if (!session.OpenDsm()) return Array.Empty<ScannerInfo>();

        var scanners = new List<ScannerInfo>();
        foreach (TW_IDENTITY identity in session.EnumerateSources())
        {
            scanners.Add(new ScannerInfo(
                Id: IdPrefix + identity.ProductName,
                Name: identity.ProductName,
                Vendor: identity.Manufacturer,
                Interface: ScannerInterface.Twain,
                Status: ScannerStatus.Ready,
                // The real feature set needs the source open, which locks the device. The
                // list is meant to be cheap, so capabilities are reported as unknown here
                // and filled in by GetCapabilities when the user picks a scanner.
                Features: ScannerFeatures.None,
                Is32BitOnly: !Environment.Is64BitProcess));
        }

        return scanners;
    }

    public ScannerCapsResponseMessage GetCapabilities(string scannerId)
    {
        string productName = StripPrefix(scannerId);

        using TwainDsm? dsm = TwainDsm.TryLoad(out _diagnostic);
        if (dsm is null) return NotFound(scannerId);

        using var window = new MessageWindow();
        using var session = new TwainSession(dsm, window.Handle);

        if (!session.OpenDsm()) return NotFound(scannerId);
        if (!session.OpenSource(productName)) return NotFound(scannerId);

        try
        {
            var features = ScannerFeatures.Flatbed;

            var pixelTypes = new List<PixelType>();
            foreach (uint value in session.GetCapabilityValues(Twain.ICAP_PIXELTYPE))
            {
                switch (value)
                {
                    case Twain.TWPT_BW: pixelTypes.Add(PixelType.BlackWhite); break;
                    case Twain.TWPT_GRAY: pixelTypes.Add(PixelType.Grayscale); break;
                    case Twain.TWPT_RGB: pixelTypes.Add(PixelType.Rgb); break;
                }
            }
            if (pixelTypes.Count == 0) pixelTypes.Add(PixelType.Rgb);

            if (pixelTypes.Contains(PixelType.Rgb)) features |= ScannerFeatures.Color;
            if (pixelTypes.Contains(PixelType.Grayscale)) features |= ScannerFeatures.Grayscale;
            if (pixelTypes.Contains(PixelType.BlackWhite)) features |= ScannerFeatures.BlackWhite;

            var resolutions = session.GetCapabilityValues(Twain.ICAP_XRESOLUTION)
                .Select(raw => (int)Math.Round(TW_FIX32.FromRaw(raw).ToDouble()))
                .Where(dpi => dpi is >= 25 and <= 9600)
                .Distinct()
                .OrderBy(dpi => dpi)
                .ToArray();
            if (resolutions.Length == 0) resolutions = new[] { 150, 200, 300, 600 };

            if (session.SupportsCapability(Twain.CAP_FEEDERENABLED)) features |= ScannerFeatures.Feeder;
            if (session.GetCapabilityCurrent(Twain.CAP_DUPLEX) is > 0) features |= ScannerFeatures.Duplex;
            if (session.SupportsCapability(Twain.ICAP_AUTOMATICDESKEW)) features |= ScannerFeatures.AutoDeskew;
            if (session.SupportsCapability(Twain.ICAP_AUTOSIZE)) features |= ScannerFeatures.AutoPageSize;
            if (session.SupportsCapability(Twain.ICAP_AUTODISCARDBLANKPAGES))
                features |= ScannerFeatures.BlankPageRemoval;

            (double brightnessMin, double brightnessMax) = session.GetFix32Range(Twain.ICAP_BRIGHTNESS);
            (double contrastMin, double contrastMax) = session.GetFix32Range(Twain.ICAP_CONTRAST);
            if (brightnessMax > brightnessMin) features |= ScannerFeatures.Brightness;
            if (contrastMax > contrastMin) features |= ScannerFeatures.Contrast;

            var paperSizes = new List<PaperSize> { PaperSize.Auto };
            foreach (uint value in session.GetCapabilityValues(Twain.ICAP_SUPPORTEDSIZES))
            {
                PaperSize? size = value switch
                {
                    Twain.TWSS_A4 => PaperSize.A4,
                    Twain.TWSS_A3 => PaperSize.A3,
                    Twain.TWSS_A5 => PaperSize.A5,
                    Twain.TWSS_USLETTER => PaperSize.Letter,
                    Twain.TWSS_USLEGAL => PaperSize.Legal,
                    Twain.TWSS_USEXECUTIVE => PaperSize.Executive,
                    Twain.TWSS_B4 => PaperSize.B4,
                    Twain.TWSS_B5 => PaperSize.B5,
                    _ => null,
                };
                if (size is { } paper && !paperSizes.Contains(paper)) paperSizes.Add(paper);
            }

            // Values from a data source are untrusted input. Windows' own WIA-to-TWAIN
            // compatibility layer, for one, answers TWRC_SUCCESS for ICAP_PHYSICALWIDTH and
            // hands back an uninitialised TW_FIX32 — observed as a bed size of -32768 x
            // -19661 inches. A negative or absurd bed would then propagate into the data
            // source's ICAP_PHYSICALWIDTH and into every remote application's page setup.
            double physicalWidth = PlausibleDimension(session.GetFix32Current(Twain.ICAP_PHYSICALWIDTH), 8.5);
            double physicalHeight = PlausibleDimension(session.GetFix32Current(Twain.ICAP_PHYSICALHEIGHT), 14.0);

            return new ScannerCapsResponseMessage(
                ScannerId: scannerId,
                Found: true,
                Resolutions: resolutions,
                PixelTypes: pixelTypes,
                PaperSizes: paperSizes,
                Features: features,
                BrightnessMin: brightnessMin,
                BrightnessMax: brightnessMax,
                ContrastMin: contrastMin,
                ContrastMax: contrastMax,
                MaxWidthThousandthsInch: (int)(physicalWidth * 1000),
                MaxHeightThousandthsInch: (int)(physicalHeight * 1000));
        }
        finally
        {
            session.CloseSource();
        }
    }

    public void Scan(string scannerId, ScanSettings settings, IScanSink sink,
                     CancellationToken cancellationToken)
    {
        settings.Validate();
        string productName = StripPrefix(scannerId);

        using TwainDsm? dsm = TwainDsm.TryLoad(out _diagnostic);
        if (dsm is null)
            throw new ScanException(ScanErrorCode.DriverFault, _diagnostic);

        using var window = new MessageWindow();
        using var session = new TwainSession(dsm, window.Handle);

        if (!session.OpenDsm())
            throw new ScanException(ScanErrorCode.DriverFault, "The TWAIN data source manager refused to open.");

        if (!session.OpenSource(productName))
            throw ScanException.NotFound(scannerId);

        try
        {
            session.ApplySettings(settings);
            session.Enable(settings.ShowScannerUi);

            try
            {
                session.PumpUntilTransferReady(sink, settings, cancellationToken);
            }
            finally
            {
                session.Disable();
            }
        }
        finally
        {
            session.CloseSource();
        }
    }

    /// <summary>
    /// Accepts a reported bed dimension only if a real scanner could have it. The widest
    /// production scanners are around 48 inches; anything outside 0.5-60 is a driver bug,
    /// not a device, and the documented default is used instead.
    /// </summary>
    private static double PlausibleDimension(double? reported, double fallback)
        => reported is { } value && value is >= 0.5 and <= 60.0 ? value : fallback;

    private static string StripPrefix(string scannerId)
        => scannerId.StartsWith(IdPrefix, StringComparison.Ordinal) ? scannerId[IdPrefix.Length..] : scannerId;

    private static ScannerCapsResponseMessage NotFound(string scannerId)
        => new(scannerId, false, Array.Empty<int>(), Array.Empty<PixelType>(),
               Array.Empty<PaperSize>(), ScannerFeatures.None, 0, 0, 0, 0, 0, 0);

    public void Dispose() { }
}

/// <summary>
/// A hidden top-level window to act as the TWAIN parent.
///
/// TWAIN requires a real HWND at MSG_OPENDSM, and data sources parent their own dialogs to
/// it. The stock "STATIC" class is used so no window class has to be registered and
/// unregistered; the window is never given WS_VISIBLE, so it never appears.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class MessageWindow : IDisposable
{
    private const int WS_POPUP = unchecked((int)0x80000000);
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    public MessageWindow()
    {
        Handle = CreateWindowExW(WS_EX_TOOLWINDOW, "STATIC", "RemoteScanner", WS_POPUP,
                                 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

        if (Handle == IntPtr.Zero)
            throw new ScanException(ScanErrorCode.DriverFault,
                "Could not create the window TWAIN requires as a parent.");
    }

    public IntPtr Handle { get; private set; }

    public void Dispose()
    {
        if (Handle != IntPtr.Zero)
        {
            DestroyWindow(Handle);
            Handle = IntPtr.Zero;
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(
        int exStyle, string className, string windowName, int style,
        int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr hWnd);
}
