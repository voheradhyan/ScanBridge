using System.Runtime.InteropServices;

namespace ScanBridge.Scanner.Twain;

// TWAIN structures are 2-byte packed on Windows. Pack = 2 on every one of these is
// load-bearing: with the default 8-byte packing every field after the first shifts and the
// driver reads garbage — a failure mode that looks like a flaky scanner, not a bug.
//
// These mirror native/include/rs_twain.h exactly. The two must stay in step.

[StructLayout(LayoutKind.Sequential, Pack = 2)]
internal struct TW_FIX32
{
    public short Whole;
    public ushort Frac;

    public static TW_FIX32 FromDouble(double value)
    {
        bool negative = value < 0;
        int scaled = (int)(value * 65536.0 + (negative ? -0.5 : 0.5));
        return new TW_FIX32 { Whole = (short)(scaled >> 16), Frac = (ushort)(scaled & 0xFFFF) };
    }

    public readonly double ToDouble() => Whole + Frac / 65536.0;

    /// <summary>The 32-bit pattern a TW_ONEVALUE carries for a TWTY_FIX32 item.</summary>
    public readonly uint ToRaw() => ((uint)(ushort)Whole << 16) | Frac;

    public static TW_FIX32 FromRaw(uint raw)
        => new() { Whole = (short)(raw >> 16), Frac = (ushort)(raw & 0xFFFF) };
}

[StructLayout(LayoutKind.Sequential, Pack = 2, CharSet = CharSet.Ansi)]
internal struct TW_VERSION
{
    public ushort MajorNum;
    public ushort MinorNum;
    public ushort Language;
    public ushort Country;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 34)] public string Info;
}

[StructLayout(LayoutKind.Sequential, Pack = 2, CharSet = CharSet.Ansi)]
internal struct TW_IDENTITY
{
    public uint Id;
    public TW_VERSION Version;
    public ushort ProtocolMajor;
    public ushort ProtocolMinor;
    public uint SupportedGroups;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 34)] public string Manufacturer;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 34)] public string ProductFamily;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 34)] public string ProductName;
}

[StructLayout(LayoutKind.Sequential, Pack = 2)]
internal struct TW_STATUS
{
    public ushort ConditionCode;
    public ushort Data;
}

[StructLayout(LayoutKind.Sequential, Pack = 2)]
internal struct TW_CAPABILITY
{
    public ushort Cap;
    public ushort ConType;
    public IntPtr hContainer;
}

[StructLayout(LayoutKind.Sequential, Pack = 2)]
internal struct TW_ONEVALUE
{
    public ushort ItemType;
    public uint Item;
}

[StructLayout(LayoutKind.Sequential, Pack = 2)]
internal struct TW_ENUMERATION
{
    public ushort ItemType;
    public uint NumItems;
    public uint CurrentIndex;
    public uint DefaultIndex;
    // ItemList follows immediately; read manually at the offset below.
}

[StructLayout(LayoutKind.Sequential, Pack = 2)]
internal struct TW_ARRAY
{
    public ushort ItemType;
    public uint NumItems;
}

[StructLayout(LayoutKind.Sequential, Pack = 2)]
internal struct TW_RANGE
{
    public ushort ItemType;
    public uint MinValue;
    public uint MaxValue;
    public uint StepSize;
    public uint DefaultValue;
    public uint CurrentValue;
}

[StructLayout(LayoutKind.Sequential, Pack = 2)]
internal struct TW_USERINTERFACE
{
    public ushort ShowUI;      // TW_BOOL is 16-bit
    public ushort ModalUI;
    public IntPtr hParent;
}

[StructLayout(LayoutKind.Sequential, Pack = 2)]
internal struct TW_EVENT
{
    public IntPtr pEvent;
    public ushort TWMessage;
}

[StructLayout(LayoutKind.Sequential, Pack = 2)]
internal struct TW_PENDINGXFERS
{
    public ushort Count;
    public uint EOJ;
}

[StructLayout(LayoutKind.Sequential, Pack = 2)]
internal struct TW_SETUPMEMXFER
{
    public uint MinBufSize;
    public uint MaxBufSize;
    public uint Preferred;
}

[StructLayout(LayoutKind.Sequential, Pack = 2)]
internal struct TW_MEMORY
{
    public uint Flags;
    public uint Length;
    public IntPtr TheMem;
}

[StructLayout(LayoutKind.Sequential, Pack = 2)]
internal struct TW_IMAGEMEMXFER
{
    public ushort Compression;
    public uint BytesPerRow;
    public uint Columns;
    public uint Rows;
    public uint XOffset;
    public uint YOffset;
    public uint BytesWritten;
    public TW_MEMORY Memory;
}

[StructLayout(LayoutKind.Sequential, Pack = 2)]
internal struct TW_IMAGEINFO
{
    public TW_FIX32 XResolution;
    public TW_FIX32 YResolution;
    public int ImageWidth;
    public int ImageLength;
    public short SamplesPerPixel;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)] public short[] BitsPerSample;
    public short BitsPerPixel;
    public ushort Planar;
    public short PixelType;
    public ushort Compression;
}

[StructLayout(LayoutKind.Sequential, Pack = 2)]
internal struct TW_ENTRYPOINT
{
    public uint Size;
    public IntPtr DSM_Entry;
    public IntPtr DSM_MemAllocate;
    public IntPtr DSM_MemFree;
    public IntPtr DSM_MemLock;
    public IntPtr DSM_MemUnlock;
}

internal static class Twain
{
    public const uint DG_CONTROL = 0x0001;
    public const uint DG_IMAGE = 0x0002;
    public const uint DF_DSM2 = 0x10000000;
    public const uint DF_APP2 = 0x20000000;

    public const ushort DAT_CAPABILITY = 0x0001;
    public const ushort DAT_EVENT = 0x0002;
    public const ushort DAT_IDENTITY = 0x0003;
    public const ushort DAT_PARENT = 0x0004;
    public const ushort DAT_PENDINGXFERS = 0x0005;
    public const ushort DAT_SETUPMEMXFER = 0x0006;
    public const ushort DAT_STATUS = 0x0008;
    public const ushort DAT_USERINTERFACE = 0x0009;
    public const ushort DAT_IMAGEINFO = 0x0101;
    // Corrected 14 Aug 2026. These three were each one step off (0x0104/0x0105/0x0401), which
    // is DAT_IMAGENATIVEXFER, DAT_IMAGEFILEXFER and DAT_ICCPROFILE respectively. This side
    // drives real vendor TWAIN drivers on the user's PC, so acquiring from a TWAIN-only
    // scanner asked the driver for the wrong operation. It was invisible here because the
    // scanner on hand is a WIA device and never went down this path.
    public const ushort DAT_IMAGEMEMXFER = 0x0103;
    public const ushort DAT_IMAGENATIVEXFER = 0x0104;
    public const ushort DAT_IMAGEFILEXFER = 0x0105;
    public const ushort DAT_ENTRYPOINT = 0x0403;

    public const ushort MSG_GET = 0x0001;
    public const ushort MSG_GETCURRENT = 0x0002;
    public const ushort MSG_GETDEFAULT = 0x0003;
    public const ushort MSG_GETFIRST = 0x0004;
    public const ushort MSG_GETNEXT = 0x0005;
    public const ushort MSG_SET = 0x0006;
    public const ushort MSG_RESET = 0x0007;
    public const ushort MSG_XFERREADY = 0x0101;
    public const ushort MSG_CLOSEDSREQ = 0x0102;
    public const ushort MSG_OPENDSM = 0x0301;
    public const ushort MSG_CLOSEDSM = 0x0302;
    public const ushort MSG_OPENDS = 0x0401;
    public const ushort MSG_CLOSEDS = 0x0402;
    public const ushort MSG_DISABLEDS = 0x0501;
    public const ushort MSG_ENABLEDS = 0x0502;
    public const ushort MSG_PROCESSEVENT = 0x0601;
    public const ushort MSG_ENDXFER = 0x0701;
    public const ushort MSG_STOPFEEDER = 0x0702;

    public const ushort TWRC_SUCCESS = 0;
    public const ushort TWRC_FAILURE = 1;
    public const ushort TWRC_CHECKSTATUS = 2;
    public const ushort TWRC_CANCEL = 3;
    public const ushort TWRC_DSEVENT = 4;
    public const ushort TWRC_NOTDSEVENT = 5;
    public const ushort TWRC_XFERDONE = 6;
    public const ushort TWRC_ENDOFLIST = 7;

    public const ushort TWCC_SUCCESS = 0;
    public const ushort TWCC_LOWMEMORY = 2;
    public const ushort TWCC_NODS = 3;
    public const ushort TWCC_MAXCONNECTIONS = 4;
    public const ushort TWCC_OPERATIONERROR = 5;
    public const ushort TWCC_BADVALUE = 10;
    public const ushort TWCC_SEQERROR = 11;
    public const ushort TWCC_CAPUNSUPPORTED = 13;
    public const ushort TWCC_PAPERJAM = 20;
    public const ushort TWCC_PAPERDOUBLEFEED = 21;
    public const ushort TWCC_CHECKDEVICEONLINE = 23;
    public const ushort TWCC_INTERLOCK = 24;
    public const ushort TWCC_NOMEDIA = 29;

    public const ushort TWTY_INT16 = 0x0001;
    public const ushort TWTY_INT32 = 0x0002;
    public const ushort TWTY_UINT16 = 0x0004;
    public const ushort TWTY_UINT32 = 0x0005;
    public const ushort TWTY_BOOL = 0x0006;
    public const ushort TWTY_FIX32 = 0x0007;

    public const ushort TWON_ARRAY = 3;
    public const ushort TWON_ENUMERATION = 4;
    public const ushort TWON_ONEVALUE = 5;
    public const ushort TWON_RANGE = 6;

    public const ushort CAP_XFERCOUNT = 0x0001;
    public const ushort ICAP_PIXELTYPE = 0x0101;
    public const ushort ICAP_UNITS = 0x0102;
    public const ushort ICAP_XFERMECH = 0x0103;
    public const ushort CAP_FEEDERENABLED = 0x1002;
    public const ushort CAP_FEEDERLOADED = 0x1003;
    public const ushort CAP_AUTOFEED = 0x1007;
    public const ushort CAP_INDICATORS = 0x100b;
    public const ushort CAP_PAPERDETECTABLE = 0x100d;
    public const ushort CAP_UICONTROLLABLE = 0x100e;
    public const ushort CAP_DUPLEX = 0x1012;
    public const ushort CAP_DUPLEXENABLED = 0x1013;
    public const ushort ICAP_BRIGHTNESS = 0x1101;
    public const ushort ICAP_CONTRAST = 0x1103;
    public const ushort ICAP_PHYSICALWIDTH = 0x1111;
    public const ushort ICAP_PHYSICALHEIGHT = 0x1112;
    public const ushort ICAP_XRESOLUTION = 0x1118;
    public const ushort ICAP_YRESOLUTION = 0x1119;
    public const ushort ICAP_SUPPORTEDSIZES = 0x1122;
    public const ushort ICAP_AUTODISCARDBLANKPAGES = 0x1134;
    public const ushort ICAP_AUTOMATICDESKEW = 0x1151;
    public const ushort ICAP_AUTOSIZE = 0x1156;

    public const ushort TWPT_BW = 0;
    public const ushort TWPT_GRAY = 1;
    public const ushort TWPT_RGB = 2;

    public const ushort TWSX_NATIVE = 0;
    public const ushort TWSX_MEMORY = 2;

    public const ushort TWUN_INCHES = 0;

    public const ushort TWSS_NONE = 0;
    public const ushort TWSS_A4 = 1;
    public const ushort TWSS_B5 = 2;
    public const ushort TWSS_USLETTER = 3;
    public const ushort TWSS_USLEGAL = 4;
    public const ushort TWSS_A5 = 5;
    public const ushort TWSS_B4 = 6;
    public const ushort TWSS_USEXECUTIVE = 10;
    public const ushort TWSS_A3 = 11;

    public const ushort TWLG_ENGLISH_USA = 13;
    public const ushort TWCY_USA = 1;

    /// <summary>Byte offset of TW_ENUMERATION.ItemList under 2-byte packing.</summary>
    public const int EnumerationItemListOffset = 14;

    /// <summary>Byte offset of TW_ARRAY.ItemList under 2-byte packing.</summary>
    public const int ArrayItemListOffset = 6;

    /// <summary>Byte offset of TW_ONEVALUE.Item under 2-byte packing.</summary>
    public const int OneValueItemOffset = 2;

    public static int ItemSize(ushort itemType) => itemType switch
    {
        0 or 3 => 1,                                  // TWTY_INT8, TWTY_UINT8
        TWTY_INT16 or TWTY_UINT16 or TWTY_BOOL => 2,
        TWTY_INT32 or TWTY_UINT32 or TWTY_FIX32 => 4,
        0x0009 => 34,                                 // TWTY_STR32
        0x000a => 66,                                 // TWTY_STR64
        0x000b => 130,                                // TWTY_STR128
        0x000c => 256,                                // TWTY_STR255
        _ => 0,
    };
}
