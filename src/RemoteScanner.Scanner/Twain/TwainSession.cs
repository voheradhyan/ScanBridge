using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using RemoteScanner.Protocol;

namespace RemoteScanner.Scanner.Twain;

/// <summary>
/// Drives one TWAIN conversation: open the manager, open a source, negotiate capabilities,
/// enable, pump messages, transfer pages, shut down cleanly.
///
/// State transitions follow the TWAIN 2.4 state machine (3 -> 4 -> 5 -> 6 -> 7 and back).
/// Cleanup is unconditional: leaving a source open locks the physical scanner against every
/// other application on the PC, so every path that opens something closes it.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class TwainSession : IDisposable
{
    private readonly TwainDsm _dsm;
    private readonly IntPtr _parent;

    private TW_IDENTITY _app;
    private TW_IDENTITY _source;

    private bool _dsmOpen;
    private bool _sourceOpen;
    private bool _enabled;

    /// <summary>DSM 2.x memory functions, when the manager provided them.</summary>
    private TW_ENTRYPOINT _entryPoints;
    private bool _haveEntryPoints;

    public TwainSession(TwainDsm dsm, IntPtr parent)
    {
        _dsm = dsm;
        _parent = parent;

        _app = new TW_IDENTITY
        {
            Id = 0,
            Version = new TW_VERSION
            {
                MajorNum = 1,
                MinorNum = 0,
                Language = Twain.TWLG_ENGLISH_USA,
                Country = Twain.TWCY_USA,
                Info = "1.0.0",
            },
            ProtocolMajor = 2,
            ProtocolMinor = 4,
            // DF_APP2 tells a 2.x manager we understand DAT_ENTRYPOINT.
            SupportedGroups = Twain.DG_CONTROL | Twain.DG_IMAGE | Twain.DF_APP2,
            Manufacturer = "RemoteScanner",
            ProductFamily = "Remote Scanner",
            ProductName = "RemoteScanner Agent",
        };
    }

    public ushort LastConditionCode { get; private set; }

    // ------------------------------------------------------------------ lifetime

    public bool OpenDsm()
    {
        IntPtr parent = _parent;
        ushort rc = _dsm.Parent(ref _app, IntPtr.Zero, Twain.DG_CONTROL, Twain.DAT_PARENT,
                                Twain.MSG_OPENDSM, ref parent);
        if (rc != Twain.TWRC_SUCCESS) return false;

        _dsmOpen = true;

        // A 2.x manager sets DF_DSM2 in our identity during MSG_OPENDSM. When it does, its
        // allocator must be used for every container it or the source hands back — mixing
        // it with the global heap frees with the wrong allocator.
        if ((_app.SupportedGroups & 0x10000000u) != 0)
        {
            var entry = new TW_ENTRYPOINT { Size = (uint)Marshal.SizeOf<TW_ENTRYPOINT>() };
            if (_dsm.EntryPoint(ref _app, IntPtr.Zero, Twain.DG_CONTROL, Twain.DAT_ENTRYPOINT,
                                Twain.MSG_GET, ref entry) == Twain.TWRC_SUCCESS)
            {
                _entryPoints = entry;
                _haveEntryPoints = entry.DSM_MemLock != IntPtr.Zero;
            }
        }

        return true;
    }

    public IEnumerable<TW_IDENTITY> EnumerateSources()
    {
        var identity = new TW_IDENTITY();
        ushort rc = _dsm.Identity(ref _app, IntPtr.Zero, Twain.DG_CONTROL, Twain.DAT_IDENTITY,
                                  Twain.MSG_GETFIRST, ref identity);

        while (rc == Twain.TWRC_SUCCESS)
        {
            yield return identity;

            identity = new TW_IDENTITY();
            rc = _dsm.Identity(ref _app, IntPtr.Zero, Twain.DG_CONTROL, Twain.DAT_IDENTITY,
                               Twain.MSG_GETNEXT, ref identity);
        }
    }

    public bool OpenSource(string productName)
    {
        foreach (TW_IDENTITY candidate in EnumerateSources())
        {
            if (!string.Equals(candidate.ProductName, productName, StringComparison.OrdinalIgnoreCase))
                continue;

            _source = candidate;
            ushort rc = _dsm.Identity(ref _app, IntPtr.Zero, Twain.DG_CONTROL, Twain.DAT_IDENTITY,
                                      Twain.MSG_OPENDS, ref _source);
            _sourceOpen = rc == Twain.TWRC_SUCCESS;
            return _sourceOpen;
        }

        return false;
    }

    public void CloseSource()
    {
        if (!_sourceOpen) return;
        _dsm.Identity(ref _app, IntPtr.Zero, Twain.DG_CONTROL, Twain.DAT_IDENTITY,
                      Twain.MSG_CLOSEDS, ref _source);
        _sourceOpen = false;
    }

    private void CloseDsm()
    {
        if (!_dsmOpen) return;
        IntPtr parent = _parent;
        _dsm.Parent(ref _app, IntPtr.Zero, Twain.DG_CONTROL, Twain.DAT_PARENT,
                    Twain.MSG_CLOSEDSM, ref parent);
        _dsmOpen = false;
    }

    public void Dispose()
    {
        if (_enabled) Disable();
        CloseSource();
        CloseDsm();
    }

    // -------------------------------------------------------------- capabilities

    private IntPtr Lock(IntPtr handle)
        => _haveEntryPoints
            ? Marshal.GetDelegateForFunctionPointer<MemLock>(_entryPoints.DSM_MemLock)(handle)
            : GlobalLock(handle);

    private void Unlock(IntPtr handle)
    {
        if (_haveEntryPoints)
            Marshal.GetDelegateForFunctionPointer<MemUnlock>(_entryPoints.DSM_MemUnlock)(handle);
        else
            GlobalUnlock(handle);
    }

    private void Free(IntPtr handle)
    {
        if (handle == IntPtr.Zero) return;
        if (_haveEntryPoints)
            Marshal.GetDelegateForFunctionPointer<MemFree>(_entryPoints.DSM_MemFree)(handle);
        else
            GlobalFree(handle);
    }

    private IntPtr Alloc(uint size)
        => _haveEntryPoints
            ? Marshal.GetDelegateForFunctionPointer<MemAlloc>(_entryPoints.DSM_MemAllocate)(size)
            : GlobalAlloc(0x0042 /* GHND */, (UIntPtr)size);

    /// <summary>True when the source answers at all for this capability.</summary>
    public bool SupportsCapability(ushort capability)
    {
        var cap = new TW_CAPABILITY { Cap = capability, ConType = 0, hContainer = IntPtr.Zero };
        ushort rc = _dsm.Capability(ref _app, ref _source, Twain.DG_CONTROL, Twain.DAT_CAPABILITY,
                                    Twain.MSG_GETCURRENT, ref cap);
        if (rc != Twain.TWRC_SUCCESS) return false;
        Free(cap.hContainer);
        return true;
    }

    public uint? GetCapabilityCurrent(ushort capability)
    {
        var cap = new TW_CAPABILITY { Cap = capability, ConType = 0, hContainer = IntPtr.Zero };
        ushort rc = _dsm.Capability(ref _app, ref _source, Twain.DG_CONTROL, Twain.DAT_CAPABILITY,
                                    Twain.MSG_GETCURRENT, ref cap);
        if (rc != Twain.TWRC_SUCCESS || cap.hContainer == IntPtr.Zero) return null;

        try
        {
            IntPtr raw = Lock(cap.hContainer);
            if (raw == IntPtr.Zero) return null;

            try
            {
                return cap.ConType switch
                {
                    Twain.TWON_ONEVALUE => ReadOneValue(raw),
                    Twain.TWON_RANGE => Marshal.PtrToStructure<TW_RANGE>(raw).CurrentValue,
                    Twain.TWON_ENUMERATION => ReadEnumerationCurrent(raw),
                    _ => null,
                };
            }
            finally
            {
                Unlock(cap.hContainer);
            }
        }
        finally
        {
            Free(cap.hContainer);
        }
    }

    public double? GetFix32Current(ushort capability)
        => GetCapabilityCurrent(capability) is { } raw ? TW_FIX32.FromRaw(raw).ToDouble() : null;

    public (double Min, double Max) GetFix32Range(ushort capability)
    {
        var cap = new TW_CAPABILITY { Cap = capability, ConType = 0, hContainer = IntPtr.Zero };
        ushort rc = _dsm.Capability(ref _app, ref _source, Twain.DG_CONTROL, Twain.DAT_CAPABILITY,
                                    Twain.MSG_GET, ref cap);
        if (rc != Twain.TWRC_SUCCESS || cap.hContainer == IntPtr.Zero) return (0, 0);

        try
        {
            IntPtr raw = Lock(cap.hContainer);
            if (raw == IntPtr.Zero) return (0, 0);

            try
            {
                if (cap.ConType != Twain.TWON_RANGE) return (0, 0);
                var range = Marshal.PtrToStructure<TW_RANGE>(raw);
                return (TW_FIX32.FromRaw(range.MinValue).ToDouble(),
                        TW_FIX32.FromRaw(range.MaxValue).ToDouble());
            }
            finally
            {
                Unlock(cap.hContainer);
            }
        }
        finally
        {
            Free(cap.hContainer);
        }
    }

    /// <summary>Every value the source will accept for this capability.</summary>
    public IReadOnlyList<uint> GetCapabilityValues(ushort capability)
    {
        var cap = new TW_CAPABILITY { Cap = capability, ConType = 0, hContainer = IntPtr.Zero };
        ushort rc = _dsm.Capability(ref _app, ref _source, Twain.DG_CONTROL, Twain.DAT_CAPABILITY,
                                    Twain.MSG_GET, ref cap);
        if (rc != Twain.TWRC_SUCCESS || cap.hContainer == IntPtr.Zero) return Array.Empty<uint>();

        try
        {
            IntPtr raw = Lock(cap.hContainer);
            if (raw == IntPtr.Zero) return Array.Empty<uint>();

            try
            {
                switch (cap.ConType)
                {
                    case Twain.TWON_ONEVALUE:
                        return new[] { ReadOneValue(raw) };

                    case Twain.TWON_ENUMERATION:
                    {
                        var header = Marshal.PtrToStructure<TW_ENUMERATION>(raw);
                        return ReadItems(raw + Twain.EnumerationItemListOffset,
                                         header.ItemType, header.NumItems);
                    }

                    case Twain.TWON_ARRAY:
                    {
                        var header = Marshal.PtrToStructure<TW_ARRAY>(raw);
                        return ReadItems(raw + Twain.ArrayItemListOffset,
                                         header.ItemType, header.NumItems);
                    }

                    case Twain.TWON_RANGE:
                    {
                        var range = Marshal.PtrToStructure<TW_RANGE>(raw);
                        return ExpandRange(range);
                    }

                    default:
                        return Array.Empty<uint>();
                }
            }
            finally
            {
                Unlock(cap.hContainer);
            }
        }
        finally
        {
            Free(cap.hContainer);
        }
    }

    /// <summary>
    /// Sets a capability to a single value. Returns false when the source declines, which
    /// is a normal answer — the caller decides whether that is fatal for the job.
    /// </summary>
    public bool SetCapability(ushort capability, ushort itemType, uint value)
    {
        uint size = (uint)(Twain.OneValueItemOffset + Math.Max(Twain.ItemSize(itemType), 4));
        IntPtr container = Alloc(size);
        if (container == IntPtr.Zero) return false;

        try
        {
            IntPtr raw = Lock(container);
            if (raw == IntPtr.Zero) return false;

            try
            {
                Marshal.WriteInt16(raw, 0, (short)itemType);
                switch (Twain.ItemSize(itemType))
                {
                    case 1: Marshal.WriteByte(raw, Twain.OneValueItemOffset, (byte)value); break;
                    case 2: Marshal.WriteInt16(raw, Twain.OneValueItemOffset, (short)value); break;
                    default: Marshal.WriteInt32(raw, Twain.OneValueItemOffset, (int)value); break;
                }
            }
            finally
            {
                Unlock(container);
            }

            var cap = new TW_CAPABILITY
            {
                Cap = capability,
                ConType = Twain.TWON_ONEVALUE,
                hContainer = container,
            };

            ushort rc = _dsm.Capability(ref _app, ref _source, Twain.DG_CONTROL, Twain.DAT_CAPABILITY,
                                        Twain.MSG_SET, ref cap);

            // TWRC_CHECKSTATUS means the source clamped our value to something it can do.
            // That is a success as far as the job is concerned.
            return rc is Twain.TWRC_SUCCESS or Twain.TWRC_CHECKSTATUS;
        }
        finally
        {
            Free(container);
        }
    }

    public bool SetFix32(ushort capability, double value)
        => SetCapability(capability, Twain.TWTY_FIX32, TW_FIX32.FromDouble(value).ToRaw());

    public bool SetBool(ushort capability, bool value)
        => SetCapability(capability, Twain.TWTY_BOOL, value ? 1u : 0u);

    private uint ReadOneValue(IntPtr raw)
    {
        ushort itemType = (ushort)Marshal.ReadInt16(raw, 0);
        return Twain.ItemSize(itemType) switch
        {
            1 => Marshal.ReadByte(raw, Twain.OneValueItemOffset),
            2 => (ushort)Marshal.ReadInt16(raw, Twain.OneValueItemOffset),
            _ => (uint)Marshal.ReadInt32(raw, Twain.OneValueItemOffset),
        };
    }

    private uint ReadEnumerationCurrent(IntPtr raw)
    {
        var header = Marshal.PtrToStructure<TW_ENUMERATION>(raw);
        if (header.NumItems == 0 || header.CurrentIndex >= header.NumItems) return 0;

        int size = Twain.ItemSize(header.ItemType);
        IntPtr item = raw + Twain.EnumerationItemListOffset + (int)(header.CurrentIndex * size);
        return ReadItem(item, header.ItemType);
    }

    private static uint[] ReadItems(IntPtr items, ushort itemType, uint count)
    {
        int size = Twain.ItemSize(itemType);
        if (size == 0 || count == 0 || count > 4096) return Array.Empty<uint>();

        var values = new uint[count];
        for (uint i = 0; i < count; i++) values[i] = ReadItem(items + (int)(i * size), itemType);
        return values;
    }

    private static uint ReadItem(IntPtr address, ushort itemType) => Twain.ItemSize(itemType) switch
    {
        1 => Marshal.ReadByte(address),
        2 => (ushort)Marshal.ReadInt16(address),
        _ => (uint)Marshal.ReadInt32(address),
    };

    /// <summary>
    /// Turns a continuous range into the discrete values we can offer a user. Walking every
    /// step would produce thousands of entries for a 1-step dpi range, so standard
    /// resolutions inside the range are used for FIX32 and the step is honoured otherwise.
    /// </summary>
    private static uint[] ExpandRange(TW_RANGE range)
    {
        if (range.ItemType == Twain.TWTY_FIX32)
        {
            double min = TW_FIX32.FromRaw(range.MinValue).ToDouble();
            double max = TW_FIX32.FromRaw(range.MaxValue).ToDouble();
            return StandardResolutions.WithinRange((int)min, (int)max)
                .Select(dpi => TW_FIX32.FromDouble(dpi).ToRaw())
                .ToArray();
        }

        uint step = Math.Max(range.StepSize, 1);
        var values = new List<uint>();
        for (uint value = range.MinValue; value <= range.MaxValue && values.Count < 256; value += step)
            values.Add(value);
        return values.ToArray();
    }

    // ------------------------------------------------------------------ scanning

    public void ApplySettings(ScanSettings settings)
    {
        // Native transfer: the source hands back a whole DIB in one go. It costs one page of
        // memory, which is the documented ceiling, and it is the mechanism every driver
        // implements correctly. Memory transfer is the fallback for sources that refuse it.
        if (!SetCapability(Twain.ICAP_XFERMECH, Twain.TWTY_UINT16, Twain.TWSX_NATIVE))
            SetCapability(Twain.ICAP_XFERMECH, Twain.TWTY_UINT16, Twain.TWSX_MEMORY);

        SetCapability(Twain.ICAP_UNITS, Twain.TWTY_UINT16, Twain.TWUN_INCHES);

        SetCapability(Twain.ICAP_PIXELTYPE, Twain.TWTY_UINT16, settings.PixelType switch
        {
            PixelType.BlackWhite => Twain.TWPT_BW,
            PixelType.Grayscale => Twain.TWPT_GRAY,
            _ => Twain.TWPT_RGB,
        });

        SetFix32(Twain.ICAP_XRESOLUTION, settings.Resolution);
        SetFix32(Twain.ICAP_YRESOLUTION, settings.Resolution);

        bool feeder = settings.Source == PageSource.Feeder
                      || (settings.Source == PageSource.Auto && SupportsCapability(Twain.CAP_FEEDERENABLED));

        if (SupportsCapability(Twain.CAP_FEEDERENABLED))
        {
            SetBool(Twain.CAP_FEEDERENABLED, feeder);
            if (feeder) SetBool(Twain.CAP_AUTOFEED, true);
        }

        if (settings.Duplex)
        {
            if (!SetBool(Twain.CAP_DUPLEXENABLED, true))
                throw new ScanException(ScanErrorCode.UnsupportedSetting,
                    "This scanner cannot scan both sides.");
        }

        // CAP_XFERCOUNT is an INT16 where -1 means "everything in the feeder".
        SetCapability(Twain.CAP_XFERCOUNT, Twain.TWTY_INT16,
                      settings.PageLimit == ScanSettings.UnlimitedPages
                          ? unchecked((uint)(short)-1)
                          : (uint)settings.PageLimit);

        if (settings.PaperSize != PaperSize.Auto)
        {
            ushort? size = settings.PaperSize switch
            {
                PaperSize.A4 => Twain.TWSS_A4,
                PaperSize.A3 => Twain.TWSS_A3,
                PaperSize.A5 => Twain.TWSS_A5,
                PaperSize.Letter => Twain.TWSS_USLETTER,
                PaperSize.Legal => Twain.TWSS_USLEGAL,
                PaperSize.Executive => Twain.TWSS_USEXECUTIVE,
                PaperSize.B4 => Twain.TWSS_B4,
                PaperSize.B5 => Twain.TWSS_B5,
                _ => null,
            };
            if (size is { } value) SetCapability(Twain.ICAP_SUPPORTEDSIZES, Twain.TWTY_UINT16, value);
        }

        if (settings.Brightness != 0) SetFix32(Twain.ICAP_BRIGHTNESS, settings.Brightness);
        if (settings.Contrast != 0) SetFix32(Twain.ICAP_CONTRAST, settings.Contrast);
        if (settings.AutoDeskew) SetBool(Twain.ICAP_AUTOMATICDESKEW, true);
        if (settings.AutoPageSize) SetBool(Twain.ICAP_AUTOSIZE, true);
        if (settings.BlankPageRemoval) SetBool(Twain.ICAP_AUTODISCARDBLANKPAGES, true);

        // Progress indicators belong on the user's own screen, which is where this runs.
        SetBool(Twain.CAP_INDICATORS, settings.ShowScannerUi);
    }

    public void Enable(bool showUi)
    {
        var ui = new TW_USERINTERFACE
        {
            ShowUI = (ushort)(showUi ? 1 : 0),
            ModalUI = 0,
            hParent = _parent,
        };

        ushort rc = _dsm.UserInterface(ref _app, ref _source, Twain.DG_CONTROL,
                                       Twain.DAT_USERINTERFACE, Twain.MSG_ENABLEDS, ref ui);

        if (rc is not (Twain.TWRC_SUCCESS or Twain.TWRC_CHECKSTATUS))
            throw new ScanException(MapCondition(), "The scanner refused to start scanning.");

        _enabled = true;
    }

    public void Disable()
    {
        if (!_enabled) return;

        var ui = new TW_USERINTERFACE { ShowUI = 0, ModalUI = 0, hParent = _parent };
        _dsm.UserInterface(ref _app, ref _source, Twain.DG_CONTROL, Twain.DAT_USERINTERFACE,
                           Twain.MSG_DISABLEDS, ref ui);
        _enabled = false;
    }

    /// <summary>
    /// Runs the Win32 message loop TWAIN needs, forwarding every message to the source, and
    /// transfers pages as they become ready.
    ///
    /// PeekMessage plus a bounded wait is used rather than GetMessage so cancellation is
    /// honoured promptly: GetMessage would block until the driver happened to post something.
    /// </summary>
    public void PumpUntilTransferReady(IScanSink sink, ScanSettings settings,
                                       CancellationToken cancellationToken)
    {
        IntPtr message = Marshal.AllocHGlobal(Marshal.SizeOf<MSG>());
        int pageNumber = 0;
        long totalBytes = 0;

        try
        {
            while (true)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    StopFeeder();
                    throw ScanException.Cancelled();
                }

                if (!PeekMessageW(message, IntPtr.Zero, 0, 0, PM_REMOVE))
                {
                    // Wake on any new input, or every 50 ms so cancellation stays responsive.
                    MsgWaitForMultipleObjects(0, IntPtr.Zero, false, 50, QS_ALLINPUT);
                    continue;
                }

                var evt = new TW_EVENT { pEvent = message, TWMessage = 0 };
                ushort rc = _dsm.Event(ref _app, ref _source, Twain.DG_CONTROL, Twain.DAT_EVENT,
                                       Twain.MSG_PROCESSEVENT, ref evt);

                if (rc == Twain.TWRC_NOTDSEVENT)
                {
                    // Not the source's message; it belongs to our own hidden window.
                    TranslateMessage(message);
                    DispatchMessageW(message);
                    continue;
                }

                switch (evt.TWMessage)
                {
                    case Twain.MSG_XFERREADY:
                        TransferAll(sink, settings, ref pageNumber, ref totalBytes, cancellationToken);
                        return;

                    case Twain.MSG_CLOSEDSREQ:
                        // The source's own UI was closed before anything was scanned.
                        if (pageNumber == 0) throw ScanException.Cancelled();
                        return;

                    default:
                        break;
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(message);
        }
    }

    private void TransferAll(IScanSink sink, ScanSettings settings, ref int pageNumber,
                             ref long totalBytes, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            IntPtr handle = IntPtr.Zero;
            ushort rc = _dsm.NativeXfer(ref _app, ref _source, Twain.DG_IMAGE,
                                        Twain.DAT_IMAGENATIVEXFER, Twain.MSG_GET, ref handle);

            if (rc == Twain.TWRC_CANCEL) throw ScanException.Cancelled();

            if (rc != Twain.TWRC_XFERDONE)
                throw new ScanException(MapCondition(), "The scanner failed while transferring a page.");

            try
            {
                IntPtr dib = Lock(handle);
                if (dib == IntPtr.Zero)
                    throw new ScanException(ScanErrorCode.OutOfMemory, "Could not read the scanned page.");

                ScannedPage page;
                try
                {
                    DibInfo info = ImageCodec.ReadDibInfo(dib);
                    byte[] encoded = ImageCodec.EncodeDib(dib, info, settings.PreferredEncoding,
                                                          settings.JpegQuality);

                    pageNumber++;
                    totalBytes += encoded.Length;

                    page = new ScannedPage(
                        PageNumber: pageNumber,
                        Side: settings.Duplex && pageNumber % 2 == 0 ? PageSide.Back : PageSide.Front,
                        WidthPixels: info.Width,
                        HeightPixels: info.Height,
                        DpiX: info.DpiX,
                        DpiY: info.DpiY,
                        PixelType: settings.PixelType,
                        Encoding: ImageCodec.EffectiveEncoding(settings.PreferredEncoding, info.BitsPerPixel),
                        Data: encoded);
                }
                finally
                {
                    Unlock(handle);
                }

                // Blocking here is the backpressure: a full channel stops the next sheet.
                sink.Page(page);
                sink.Progress(pageNumber, totalBytes);
            }
            finally
            {
                Free(handle);
            }

            var pending = new TW_PENDINGXFERS();
            _dsm.PendingXfers(ref _app, ref _source, Twain.DG_CONTROL, Twain.DAT_PENDINGXFERS,
                              Twain.MSG_ENDXFER, ref pending);

            if (pending.Count == 0) break;
        }
    }

    private void StopFeeder()
    {
        var pending = new TW_PENDINGXFERS();
        _dsm.PendingXfers(ref _app, ref _source, Twain.DG_CONTROL, Twain.DAT_PENDINGXFERS,
                          Twain.MSG_STOPFEEDER, ref pending);
        _dsm.PendingXfers(ref _app, ref _source, Twain.DG_CONTROL, Twain.DAT_PENDINGXFERS,
                          Twain.MSG_RESET, ref pending);
    }

    /// <summary>Reads the source's condition code and maps it to something a user can act on.</summary>
    private ScanErrorCode MapCondition()
    {
        var status = new TW_STATUS();
        _dsm.Status(ref _app, ref _source, Twain.DG_CONTROL, Twain.DAT_STATUS, Twain.MSG_GET, ref status);
        LastConditionCode = status.ConditionCode;

        return status.ConditionCode switch
        {
            Twain.TWCC_PAPERJAM => ScanErrorCode.PaperJam,
            Twain.TWCC_PAPERDOUBLEFEED => ScanErrorCode.DoubleFeed,
            Twain.TWCC_NOMEDIA => ScanErrorCode.FeederEmpty,
            Twain.TWCC_INTERLOCK => ScanErrorCode.CoverOpen,
            Twain.TWCC_CHECKDEVICEONLINE => ScanErrorCode.ScannerDisconnected,
            Twain.TWCC_MAXCONNECTIONS => ScanErrorCode.ScannerBusy,
            Twain.TWCC_LOWMEMORY => ScanErrorCode.OutOfMemory,
            Twain.TWCC_CAPUNSUPPORTED or Twain.TWCC_BADVALUE => ScanErrorCode.UnsupportedSetting,
            _ => ScanErrorCode.DriverFault,
        };
    }

    // -------------------------------------------------------------------- interop

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate IntPtr MemAlloc(uint size);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void MemFree(IntPtr handle);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate IntPtr MemLock(IntPtr handle);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void MemUnlock(IntPtr handle);

    private const uint PM_REMOVE = 0x0001;
    private const uint QS_ALLINPUT = 0x04FF;

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessageW(IntPtr lpMsg, IntPtr hWnd, uint min, uint max, uint remove);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(IntPtr lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessageW(IntPtr lpMsg);

    [DllImport("user32.dll")]
    private static extern uint MsgWaitForMultipleObjects(
        uint count, IntPtr handles, [MarshalAs(UnmanagedType.Bool)] bool waitAll, uint milliseconds, uint wakeMask);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalLock(IntPtr handle);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(IntPtr handle);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalFree(IntPtr handle);
}
