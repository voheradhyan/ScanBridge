using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using RemoteScanner.Protocol;

namespace RemoteScanner.Scanner;

/// <summary>
/// Scanner access through Windows Image Acquisition.
///
/// Uses the WIA automation layer (wiaaut.dll, ProgID "WIA.DeviceManager") through late
/// binding rather than the raw IWiaDevMgr2 COM interfaces. That is a deliberate trade: the
/// automation layer is present on every Windows 10/11 client, handles the transfer plumbing,
/// and can hand back an already-encoded JPEG or PNG — which is exactly the form we want to
/// put on the wire. Declaring the raw interfaces would be several hundred lines of interop
/// for no functional gain here.
///
/// WIA matters because a large number of cheap MFPs and every WSD network scanner ship a WIA
/// driver and no TWAIN driver at all.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WiaBackend : IScannerBackend
{
    // WIA format GUIDs, as the automation layer expects them (string form).
    private const string FormatBmp = "{B96B3CAB-0728-11D3-9D7B-0000F81EF32E}";
    private const string FormatJpeg = "{B96B3CAE-0728-11D3-9D7B-0000F81EF32E}";
    private const string FormatPng = "{B96B3CAF-0728-11D3-9D7B-0000F81EF32E}";

    // Item properties.
    private const int WIA_IPA_DATATYPE = 4103;
    private const int WIA_IPA_DEPTH = 4104;
    private const int WIA_IPA_PIXELS_PER_LINE = 4108;
    private const int WIA_IPA_NUMBER_OF_LINES = 4109;
    private const int WIA_IPS_XRES = 6147;
    private const int WIA_IPS_YRES = 6148;
    private const int WIA_IPS_XPOS = 6149;
    private const int WIA_IPS_YPOS = 6150;
    private const int WIA_IPS_XEXTENT = 6151;
    private const int WIA_IPS_YEXTENT = 6152;
    private const int WIA_IPS_BRIGHTNESS = 6154;
    private const int WIA_IPS_CONTRAST = 6155;

    // Device properties.
    private const int WIA_DPS_HORIZONTAL_BED_SIZE = 3074;   // thousandths of an inch
    private const int WIA_DPS_VERTICAL_BED_SIZE = 3075;
    private const int WIA_DPS_DOCUMENT_HANDLING_CAPABILITIES = 3086;
    private const int WIA_DPS_DOCUMENT_HANDLING_STATUS = 3087;
    private const int WIA_DPS_DOCUMENT_HANDLING_SELECT = 3088;
    private const int WIA_DPS_PAGES = 3096;

    // Document handling bits.
    private const int FEEDER = 0x001;
    private const int FLATBED = 0x002;
    private const int DUPLEX = 0x004;
    private const int FEED_READY = 0x001;

    // WIA_IPA_DATATYPE values.
    private const int DATATYPE_THRESHOLD = 0;
    private const int DATATYPE_GRAYSCALE = 2;
    private const int DATATYPE_COLOR = 3;

    // HRESULTs the driver stack raises. Mapping them is what turns "COMException 0x80210003"
    // into "the document feeder is empty".
    private const int WIA_ERROR_GENERAL = unchecked((int)0x80210001);
    private const int WIA_ERROR_PAPER_JAM = unchecked((int)0x80210002);
    private const int WIA_ERROR_PAPER_EMPTY = unchecked((int)0x80210003);
    private const int WIA_ERROR_PAPER_PROBLEM = unchecked((int)0x80210004);
    private const int WIA_ERROR_OFFLINE = unchecked((int)0x80210005);
    private const int WIA_ERROR_BUSY = unchecked((int)0x80210006);
    private const int WIA_ERROR_WARMING_UP = unchecked((int)0x80210007);
    private const int WIA_ERROR_USER_INTERVENTION = unchecked((int)0x80210008);
    private const int WIA_ERROR_ITEM_DELETED = unchecked((int)0x80210009);
    private const int WIA_ERROR_DEVICE_LOCKED = unchecked((int)0x8021000A);
    private const int WIA_ERROR_COVER_OPEN = unchecked((int)0x80210016);

    /// <summary>The "wia:" prefix keeps ids unambiguous when merged with the TWAIN list.</summary>
    public const string IdPrefix = "wia:";

    public ScannerInterface Interface => ScannerInterface.Wia;

    public IReadOnlyList<ScannerInfo> Enumerate()
    {
        var scanners = new List<ScannerInfo>();
        dynamic? manager = null;

        try
        {
            manager = CreateDeviceManager();
            dynamic infos = manager.DeviceInfos;

            for (int i = 1; i <= (int)infos.Count; i++)
            {
                dynamic info = infos[i];
                try
                {
                    // WiaDeviceType: 1 == Scanner. Cameras and video devices are not ours.
                    if ((int)info.Type != 1) continue;

                    string deviceId = (string)info.DeviceID;
                    scanners.Add(new ScannerInfo(
                        Id: IdPrefix + deviceId,
                        Name: ReadProperty(info.Properties, "Name") ?? "WIA Scanner",
                        Vendor: ReadProperty(info.Properties, "Manufacturer") ?? string.Empty,
                        Interface: ScannerInterface.Wia,
                        Status: ScannerStatus.Ready,
                        Features: ScannerFeatures.Flatbed | ScannerFeatures.Color
                                  | ScannerFeatures.Grayscale | ScannerFeatures.BlackWhite,
                        Is32BitOnly: false));
                }
                finally
                {
                    Release(info);
                }
            }
        }
        catch (COMException)
        {
            // The WIA service is stopped or absent (common on Server Core). An empty list
            // is the right answer; the TWAIN backend may still have devices.
        }
        finally
        {
            Release(manager);
        }

        return scanners;
    }

    public ScannerCapsResponseMessage GetCapabilities(string scannerId)
    {
        dynamic? manager = null;
        dynamic? device = null;

        try
        {
            manager = CreateDeviceManager();
            device = ConnectDevice(manager, StripPrefix(scannerId));
            if (device is null) return NotFound(scannerId);

            dynamic item = device.Items[1];
            try
            {
                int handling = ReadInt(device.Properties, WIA_DPS_DOCUMENT_HANDLING_CAPABILITIES, FLATBED);

                var features = ScannerFeatures.None;
                if ((handling & FLATBED) != 0) features |= ScannerFeatures.Flatbed;
                if ((handling & FEEDER) != 0) features |= ScannerFeatures.Feeder;
                if ((handling & DUPLEX) != 0) features |= ScannerFeatures.Duplex;

                var pixelTypes = new List<PixelType>();
                foreach (int dataType in ReadSubTypeValues(item.Properties, WIA_IPA_DATATYPE))
                {
                    switch (dataType)
                    {
                        case DATATYPE_THRESHOLD: pixelTypes.Add(PixelType.BlackWhite); break;
                        case DATATYPE_GRAYSCALE: pixelTypes.Add(PixelType.Grayscale); break;
                        case DATATYPE_COLOR: pixelTypes.Add(PixelType.Rgb); break;
                    }
                }
                if (pixelTypes.Count == 0)
                    pixelTypes.AddRange(new[] { PixelType.BlackWhite, PixelType.Grayscale, PixelType.Rgb });

                if (pixelTypes.Contains(PixelType.Rgb)) features |= ScannerFeatures.Color;
                if (pixelTypes.Contains(PixelType.Grayscale)) features |= ScannerFeatures.Grayscale;
                if (pixelTypes.Contains(PixelType.BlackWhite)) features |= ScannerFeatures.BlackWhite;

                // Binding through a statically typed local keeps these calls out of the
                // dynamic binder, which cannot deconstruct or infer their result types.
                object itemProperties = item.Properties;

                int[] resolutions = ReadResolutions(itemProperties);

                PropertyRange brightness = ReadRange(itemProperties, WIA_IPS_BRIGHTNESS);
                PropertyRange contrast = ReadRange(itemProperties, WIA_IPS_CONTRAST);

                if (brightness.IsRange) features |= ScannerFeatures.Brightness;
                if (contrast.IsRange) features |= ScannerFeatures.Contrast;

                int bedWidth = ReadInt(device.Properties, WIA_DPS_HORIZONTAL_BED_SIZE, 8500);
                int bedHeight = ReadInt(device.Properties, WIA_DPS_VERTICAL_BED_SIZE, 14000);

                return new ScannerCapsResponseMessage(
                    ScannerId: scannerId,
                    Found: true,
                    Resolutions: resolutions,
                    PixelTypes: pixelTypes,
                    // WIA does not expose a paper-size enumeration; the scan region is set
                    // in inches instead, so the sizes we honour are the ones we can compute.
                    PaperSizes: new[]
                    {
                        PaperSize.Auto, PaperSize.A4, PaperSize.A5, PaperSize.Letter, PaperSize.Legal,
                    },
                    Features: features,
                    BrightnessMin: brightness.Min,
                    BrightnessMax: brightness.Max,
                    ContrastMin: contrast.Min,
                    ContrastMax: contrast.Max,
                    MaxWidthThousandthsInch: bedWidth,
                    MaxHeightThousandthsInch: bedHeight);
            }
            finally
            {
                Release(item);
            }
        }
        catch (COMException ex)
        {
            throw Translate(ex, scannerId);
        }
        finally
        {
            Release(device);
            Release(manager);
        }
    }

    public void Scan(string scannerId, ScanSettings settings, IScanSink sink,
                     CancellationToken cancellationToken)
    {
        settings.Validate();

        dynamic? manager = null;
        dynamic? device = null;

        try
        {
            manager = CreateDeviceManager();
            device = ConnectDevice(manager, StripPrefix(scannerId));
            if (device is null) throw ScanException.NotFound(scannerId);

            int handling = ReadInt(device.Properties, WIA_DPS_DOCUMENT_HANDLING_CAPABILITIES, FLATBED);
            bool useFeeder = settings.Source switch
            {
                PageSource.Feeder => true,
                PageSource.Flatbed => false,
                // Auto: prefer the feeder when one exists and has paper in it.
                _ => (handling & FEEDER) != 0 && FeederHasPaper(device),
            };

            if (useFeeder && (handling & FEEDER) == 0)
                throw new ScanException(ScanErrorCode.UnsupportedSetting,
                    "This scanner has no document feeder.");

            bool duplex = settings.Duplex && (handling & DUPLEX) != 0;
            if (settings.Duplex && !duplex)
                throw new ScanException(ScanErrorCode.UnsupportedSetting,
                    "This scanner cannot scan both sides.");

            int select = useFeeder ? FEEDER : FLATBED;
            if (duplex) select |= DUPLEX;
            TrySetInt(device.Properties, WIA_DPS_DOCUMENT_HANDLING_SELECT, select);

            long totalBytes = 0;
            int pageNumber = 0;
            int pageLimit = settings.PageLimit == ScanSettings.UnlimitedPages ? int.MaxValue : settings.PageLimit;

            while (pageNumber < pageLimit)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // The flatbed produces exactly one image; only the feeder loops.
                if (pageNumber > 0 && !useFeeder) break;
                if (pageNumber > 0 && !FeederHasPaper(device)) break;

                dynamic item = device.Items[1];
                byte[] data;
                int width, height;

                try
                {
                    ApplyItemSettings(item, settings);

                    string format = ChooseFormat(settings.PixelType);
                    dynamic image;
                    try
                    {
                        image = item.Transfer(format);
                    }
                    catch (COMException ex) when (ex.HResult == WIA_ERROR_PAPER_EMPTY)
                    {
                        // Normal end of an ADF run, not a failure — unless nothing scanned.
                        if (pageNumber == 0)
                            throw new ScanException(ScanErrorCode.FeederEmpty,
                                "The document feeder is empty.");
                        break;
                    }

                    try
                    {
                        width = (int)image.Width;
                        height = (int)image.Height;
                        data = ExtractBytes(image);
                    }
                    finally
                    {
                        Release(image);
                    }
                }
                catch (COMException ex)
                {
                    throw Translate(ex, scannerId);
                }
                finally
                {
                    Release(item);
                }

                pageNumber++;
                totalBytes += data.Length;

                sink.Page(new ScannedPage(
                    PageNumber: pageNumber,
                    // WIA duplex delivers front and back as consecutive images.
                    Side: duplex && pageNumber % 2 == 0 ? PageSide.Back : PageSide.Front,
                    WidthPixels: width,
                    HeightPixels: height,
                    DpiX: settings.Resolution,
                    DpiY: settings.Resolution,
                    PixelType: settings.PixelType,
                    Encoding: settings.PixelType == PixelType.Rgb ? PageEncoding.Jpeg : PageEncoding.Png,
                    Data: data));

                sink.Progress(pageNumber, totalBytes);
            }

            if (pageNumber == 0)
                throw new ScanException(ScanErrorCode.FeederEmpty, "No pages were scanned.");
        }
        catch (OperationCanceledException)
        {
            throw ScanException.Cancelled();
        }
        finally
        {
            Release(device);
            Release(manager);
        }
    }

    // ------------------------------------------------------------------ helpers

    private static dynamic CreateDeviceManager()
    {
        Type? type = Type.GetTypeFromProgID("WIA.DeviceManager");
        if (type is null)
            throw new ScanException(ScanErrorCode.DriverFault,
                "Windows Image Acquisition is not available on this PC.");

        return Activator.CreateInstance(type)
            ?? throw new ScanException(ScanErrorCode.DriverFault, "Could not start the WIA device manager.");
    }

    private static dynamic? ConnectDevice(dynamic manager, string deviceId)
    {
        dynamic infos = manager.DeviceInfos;
        for (int i = 1; i <= (int)infos.Count; i++)
        {
            dynamic info = infos[i];
            try
            {
                if (string.Equals((string)info.DeviceID, deviceId, StringComparison.OrdinalIgnoreCase))
                    return info.Connect();
            }
            finally
            {
                Release(info);
            }
        }
        return null;
    }

    private static string StripPrefix(string scannerId)
        => scannerId.StartsWith(IdPrefix, StringComparison.Ordinal) ? scannerId[IdPrefix.Length..] : scannerId;

    private static ScannerCapsResponseMessage NotFound(string scannerId)
        => new(scannerId, false, Array.Empty<int>(), Array.Empty<PixelType>(),
               Array.Empty<PaperSize>(), ScannerFeatures.None, 0, 0, 0, 0, 0, 0);

    private static void ApplyItemSettings(dynamic item, ScanSettings settings)
    {
        TrySetInt(item.Properties, WIA_IPA_DATATYPE, settings.PixelType switch
        {
            PixelType.BlackWhite => DATATYPE_THRESHOLD,
            PixelType.Grayscale => DATATYPE_GRAYSCALE,
            _ => DATATYPE_COLOR,
        });

        TrySetInt(item.Properties, WIA_IPA_DEPTH, settings.PixelType switch
        {
            PixelType.BlackWhite => 1,
            PixelType.Grayscale => 8,
            _ => 24,
        });

        TrySetInt(item.Properties, WIA_IPS_XRES, settings.Resolution);
        TrySetInt(item.Properties, WIA_IPS_YRES, settings.Resolution);

        // The scan region has to be recomputed whenever resolution changes: WIA expresses
        // extents in pixels, so the same paper size is a different number at every dpi.
        (int widthThou, int heightThou) = PaperSizeInThousandths(settings);
        if (widthThou > 0 && heightThou > 0)
        {
            TrySetInt(item.Properties, WIA_IPS_XPOS, 0);
            TrySetInt(item.Properties, WIA_IPS_YPOS, 0);
            TrySetInt(item.Properties, WIA_IPS_XEXTENT, widthThou * settings.Resolution / 1000);
            TrySetInt(item.Properties, WIA_IPS_YEXTENT, heightThou * settings.Resolution / 1000);
        }

        if (settings.Brightness != 0) TrySetInt(item.Properties, WIA_IPS_BRIGHTNESS, (int)settings.Brightness);
        if (settings.Contrast != 0) TrySetInt(item.Properties, WIA_IPS_CONTRAST, (int)settings.Contrast);
    }

    /// <summary>Paper dimensions in thousandths of an inch, portrait unless asked otherwise.</summary>
    private static (int Width, int Height) PaperSizeInThousandths(ScanSettings settings)
    {
        (int w, int h) = settings.PaperSize switch
        {
            PaperSize.A4 => (8268, 11693),
            PaperSize.A3 => (11693, 16535),
            PaperSize.A5 => (5827, 8268),
            PaperSize.Letter => (8500, 11000),
            PaperSize.Legal => (8500, 14000),
            PaperSize.Executive => (7250, 10500),
            PaperSize.B4 => (9843, 13898),
            PaperSize.B5 => (6929, 9843),
            PaperSize.Custom => (settings.CustomWidthThousandthsInch, settings.CustomHeightThousandthsInch),
            _ => (0, 0),     // Auto: leave the driver's own default region alone
        };

        return settings.Orientation == PageOrientation.Landscape ? (h, w) : (w, h);
    }

    private static string ChooseFormat(PixelType pixelType)
        // JPEG for photographic content; PNG for grey and bitonal, where JPEG artefacts on
        // text are unacceptable and lossless costs little.
        => pixelType == PixelType.Rgb ? FormatJpeg : FormatPng;

    private static byte[] ExtractBytes(dynamic image)
    {
        // BinaryData comes back as a VARIANT array of bytes.
        object raw = image.FileData.BinaryData;
        return raw as byte[]
            ?? throw new ScanException(ScanErrorCode.DriverFault, "The scanner returned no image data.");
    }

    private static bool FeederHasPaper(dynamic device)
    {
        try
        {
            int status = ReadInt(device.Properties, WIA_DPS_DOCUMENT_HANDLING_STATUS, 0);
            return (status & FEED_READY) != 0;
        }
        catch (COMException)
        {
            // Not every driver implements the status property. Assuming paper is present is
            // the safe default: the transfer itself will report PAPER_EMPTY if it is not.
            return true;
        }
    }

    private static string? ReadProperty(dynamic properties, string name)
    {
        try { return properties[name].Value?.ToString(); }
        catch (COMException) { return null; }
        catch (ArgumentException) { return null; }
    }

    private static int ReadInt(dynamic properties, int propertyId, int fallback)
    {
        try { return Convert.ToInt32(properties[propertyId].Value); }
        catch (COMException) { return fallback; }
        catch (ArgumentException) { return fallback; }
        catch (InvalidCastException) { return fallback; }
    }

    private static void TrySetInt(dynamic properties, int propertyId, int value)
    {
        try { properties[propertyId].Value = value; }
        catch (COMException) { /* driver refused it; the scan proceeds with its default */ }
        catch (ArgumentException) { /* property not present on this device */ }
    }

    /// <summary>A WIA property that exposes a continuous range rather than a value list.</summary>
    private readonly record struct PropertyRange(double Min, double Max, bool IsRange);

    private static int[] ReadSubTypeValues(object properties, int propertyId)
    {
        try
        {
            dynamic property = ((dynamic)properties)[propertyId];
            // SubType 2 == list of legal values; 1 == range; 0 == free-form.
            if ((int)property.SubType != 2) return Array.Empty<int>();

            dynamic values = property.SubTypeValues;
            var result = new List<int>();
            for (int i = 1; i <= (int)values.Count; i++) result.Add(Convert.ToInt32(values[i]));
            return result.ToArray();
        }
        catch (COMException) { return Array.Empty<int>(); }
        catch (ArgumentException) { return Array.Empty<int>(); }
    }

    private static int[] ReadResolutions(object properties)
    {
        int[] listed = ReadSubTypeValues(properties, WIA_IPS_XRES);
        if (listed.Length > 0) return listed.Distinct().OrderBy(dpi => dpi).ToArray();

        PropertyRange range = ReadRange(properties, WIA_IPS_XRES);
        if (range.IsRange)
        {
            int[] within = StandardResolutions.WithinRange((int)range.Min, (int)range.Max);
            if (within.Length > 0) return within;
        }

        return new[] { 150, 200, 300, 600 };
    }

    private static PropertyRange ReadRange(object properties, int propertyId)
    {
        try
        {
            dynamic property = ((dynamic)properties)[propertyId];
            // SubType 1 == range, 2 == list of legal values, 0 == free-form.
            if ((int)property.SubType != 1) return default;
            return new PropertyRange(
                Convert.ToDouble(property.SubTypeMin), Convert.ToDouble(property.SubTypeMax), true);
        }
        catch (COMException) { return default; }
        catch (ArgumentException) { return default; }
    }

    /// <summary>Turns a driver HRESULT into something a user can act on.</summary>
    private static ScanException Translate(COMException ex, string scannerId) => ex.HResult switch
    {
        WIA_ERROR_PAPER_EMPTY => new ScanException(ScanErrorCode.FeederEmpty,
            "The document feeder is empty.", ex),
        WIA_ERROR_PAPER_JAM => new ScanException(ScanErrorCode.PaperJam,
            "There is a paper jam. Clear it and try again.", ex),
        WIA_ERROR_PAPER_PROBLEM => new ScanException(ScanErrorCode.DoubleFeed,
            "The scanner reported a paper feed problem.", ex),
        WIA_ERROR_COVER_OPEN => new ScanException(ScanErrorCode.CoverOpen,
            "The scanner cover is open.", ex),
        WIA_ERROR_BUSY or WIA_ERROR_DEVICE_LOCKED => new ScanException(ScanErrorCode.ScannerBusy,
            "The scanner is currently being used by another application.", ex),
        WIA_ERROR_OFFLINE or WIA_ERROR_ITEM_DELETED => new ScanException(ScanErrorCode.ScannerDisconnected,
            $"Scanner '{scannerId}' is offline or was disconnected.", ex),
        WIA_ERROR_WARMING_UP => new ScanException(ScanErrorCode.ScannerBusy,
            "The scanner is still warming up.", ex),
        WIA_ERROR_USER_INTERVENTION => new ScanException(ScanErrorCode.DriverFault,
            "The scanner needs attention at the device.", ex),
        WIA_ERROR_GENERAL => new ScanException(ScanErrorCode.DriverFault,
            "The scanner driver reported an error.", ex),
        _ => new ScanException(ScanErrorCode.DriverFault,
            $"WIA error 0x{ex.HResult:X8}: {ex.Message}", ex),
    };

    /// <summary>
    /// WIA automation objects are RCWs over COM. Releasing them promptly matters: a lingering
    /// device reference keeps the scanner locked against other applications.
    /// </summary>
    private static void Release(object? comObject)
    {
        if (comObject is not null && Marshal.IsComObject(comObject))
        {
            try { Marshal.FinalReleaseComObject(comObject); }
            catch (ArgumentException) { /* already released */ }
        }
    }

    public void Dispose() { }
}
