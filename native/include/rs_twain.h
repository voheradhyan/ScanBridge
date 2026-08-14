// rs_twain.h — TWAIN 2.4 ABI subset required by RemoteScanner's virtual Data Source.
//
// This is an interface definition, not an implementation: the layouts, packing and
// calling convention below are dictated by the TWAIN specification and by every DSM
// and application we must interoperate with. Two details are load-bearing:
//
//   * #pragma pack(2)  — TWAIN structures are 2-byte packed on Windows. Default 8-byte
//     packing silently shifts every field and produces garbage that "almost works".
//   * PASCAL (__stdcall) — TWAIN entry points are __stdcall on both x86 and x64.
//     (On x64 __stdcall collapses to the single native convention, but stating it keeps
//     the x86 build correct, which is the build a 32-bit ERP will load.)
//
// Only the subset the data source actually uses is declared. Anything absent here is
// something we deliberately do not implement.

#ifndef RS_TWAIN_H
#define RS_TWAIN_H

#include <windows.h>

#ifdef __cplusplus
extern "C" {
#endif

#ifdef _MSC_VER
#pragma pack(push, rs_twain_pack)
#pragma pack(2)
#endif

/* ---------------------------------------------------------------- primitives */

typedef unsigned char  TW_UINT8;
typedef unsigned short TW_UINT16;
typedef unsigned long  TW_UINT32;   /* 32 bits on Win32 and Win64 (LLP64) */
typedef signed char    TW_INT8;
typedef short          TW_INT16;
typedef long           TW_INT32;
typedef TW_UINT16      TW_BOOL;

typedef HANDLE         TW_HANDLE;
typedef LPVOID         TW_MEMREF;
typedef UINT_PTR       TW_UINTPTR;

typedef char TW_STR32[34];
typedef char TW_STR64[66];
typedef char TW_STR128[130];
typedef char TW_STR255[256];

typedef struct { TW_INT16 Whole; TW_UINT16 Frac; } TW_FIX32, FAR* pTW_FIX32;

typedef struct { TW_FIX32 Left, Top, Right, Bottom; } TW_FRAME, FAR* pTW_FRAME;

/* ---------------------------------------------------------------- structures */

typedef struct {
    TW_UINT16 MajorNum;
    TW_UINT16 MinorNum;
    TW_UINT16 Language;
    TW_UINT16 Country;
    TW_STR32  Info;
} TW_VERSION, FAR* pTW_VERSION;

typedef struct {
    TW_UINT32  Id;
    TW_VERSION Version;
    TW_UINT16  ProtocolMajor;
    TW_UINT16  ProtocolMinor;
    TW_UINT32  SupportedGroups;
    TW_STR32   Manufacturer;
    TW_STR32   ProductFamily;
    TW_STR32   ProductName;
} TW_IDENTITY, FAR* pTW_IDENTITY;

typedef struct {
    TW_UINT16 ConditionCode;
    TW_UINT16 Data;
} TW_STATUS, FAR* pTW_STATUS;

typedef struct {
    TW_STATUS Status;
    TW_UINT32 Size;
    TW_HANDLE UTF8string;
} TW_STATUSUTF8, FAR* pTW_STATUSUTF8;

typedef struct {
    TW_UINT16 Cap;
    TW_UINT16 ConType;
    TW_HANDLE hContainer;
} TW_CAPABILITY, FAR* pTW_CAPABILITY;

typedef struct {
    TW_UINT16 ItemType;
    TW_UINT32 Item;
} TW_ONEVALUE, FAR* pTW_ONEVALUE;

typedef struct {
    TW_UINT16 ItemType;
    TW_UINT32 NumItems;
    TW_UINT8  ItemList[1];
} TW_ARRAY, FAR* pTW_ARRAY;

typedef struct {
    TW_UINT16 ItemType;
    TW_UINT32 NumItems;
    TW_UINT32 CurrentIndex;
    TW_UINT32 DefaultIndex;
    TW_UINT8  ItemList[1];
} TW_ENUMERATION, FAR* pTW_ENUMERATION;

typedef struct {
    TW_UINT16 ItemType;
    TW_UINT32 MinValue;
    TW_UINT32 MaxValue;
    TW_UINT32 StepSize;
    TW_UINT32 DefaultValue;
    TW_UINT32 CurrentValue;
} TW_RANGE, FAR* pTW_RANGE;

typedef struct {
    TW_BOOL   ShowUI;
    TW_BOOL   ModalUI;
    TW_HANDLE hParent;
} TW_USERINTERFACE, FAR* pTW_USERINTERFACE;

typedef struct {
    TW_MEMREF pEvent;
    TW_UINT16 TWMessage;
} TW_EVENT, FAR* pTW_EVENT;

typedef struct {
    TW_UINT16 Count;
    union { TW_UINT32 EOJ; TW_UINT32 Reserved; } DUMMY;
} TW_PENDINGXFERS, FAR* pTW_PENDINGXFERS;

typedef struct {
    TW_UINT32 MinBufSize;
    TW_UINT32 MaxBufSize;
    TW_UINT32 Preferred;
} TW_SETUPMEMXFER, FAR* pTW_SETUPMEMXFER;

typedef struct {
    TW_STR255 FileName;
    TW_UINT16 Format;
    TW_INT16  VRefNum;
} TW_SETUPFILEXFER, FAR* pTW_SETUPFILEXFER;

typedef struct {
    TW_UINT32 Flags;
    TW_UINT32 Length;
    TW_MEMREF TheMem;
} TW_MEMORY, FAR* pTW_MEMORY;

typedef struct {
    TW_UINT16 Compression;
    TW_UINT32 BytesPerRow;
    TW_UINT32 Columns;
    TW_UINT32 Rows;
    TW_UINT32 XOffset;
    TW_UINT32 YOffset;
    TW_UINT32 BytesWritten;
    TW_MEMORY Memory;
} TW_IMAGEMEMXFER, FAR* pTW_IMAGEMEMXFER;

typedef struct {
    TW_FIX32  XResolution;
    TW_FIX32  YResolution;
    TW_INT32  ImageWidth;
    TW_INT32  ImageLength;
    TW_INT16  SamplesPerPixel;
    TW_INT16  BitsPerSample[8];
    TW_INT16  BitsPerPixel;
    TW_BOOL   Planar;
    TW_INT16  PixelType;
    TW_UINT16 Compression;
} TW_IMAGEINFO, FAR* pTW_IMAGEINFO;

typedef struct {
    TW_FRAME  Frame;
    TW_UINT32 DocumentNumber;
    TW_UINT32 PageNumber;
    TW_UINT32 FrameNumber;
} TW_IMAGELAYOUT, FAR* pTW_IMAGELAYOUT;

/* One requested piece of extended image information.
 *
 * The application fills InfoID and leaves the rest zeroed; the source fills ItemType,
 * NumItems, ReturnCode and Item. A source that has nothing for a given InfoID answers that
 * entry with TWRC_INFONOTSUPPORTED and still returns TWRC_SUCCESS overall - the operation
 * succeeded, it simply carried no data.
 *
 * Item is TW_UINTPTR, not TW_UINT32: it holds either an inline value or a handle, and a
 * handle is pointer-sized. Under #pragma pack(2) it sits at offset 8 in both bitnesses. */
typedef struct {
    TW_UINT16  InfoID;
    TW_UINT16  ItemType;
    TW_UINT16  NumItems;
    TW_UINT16  ReturnCode;
    TW_UINTPTR Item;
} TW_INFO, FAR* pTW_INFO;

typedef struct {
    TW_UINT32 NumInfos;
    TW_INFO   Info[1];
} TW_EXTIMAGEINFO, FAR* pTW_EXTIMAGEINFO;

/* --------------------------------------------------------------- entry points */

/* Two different entry points, and they are not interchangeable.
 *
 *   DSM_Entry  exported by the Data Source *Manager*, called by applications.
 *              Six arguments: pDest names which data source the call is aimed at.
 *   DS_Entry   exported by a Data *Source*, called by the manager.
 *              Five arguments: a source is always its own destination.
 *
 * A .ds that exports DSM_Entry rather than DS_Entry is loaded by the manager, fails the
 * GetProcAddress("DS_Entry") lookup, and is unloaded without any error reaching the
 * application — the scanner just never appears in the device list.
 */

typedef TW_UINT16(FAR PASCAL* DSMENTRYPROC)(
    pTW_IDENTITY pOrigin, pTW_IDENTITY pDest,
    TW_UINT32 DG, TW_UINT16 DAT, TW_UINT16 MSG, TW_MEMREF pData);

typedef TW_UINT16(FAR PASCAL* DSENTRYPROC)(
    pTW_IDENTITY pOrigin,
    TW_UINT32 DG, TW_UINT16 DAT, TW_UINT16 MSG, TW_MEMREF pData);

typedef TW_HANDLE(PASCAL* DSM_MEMALLOCATE)(TW_UINT32 size);
typedef void      (PASCAL* DSM_MEMFREE)(TW_HANDLE handle);
typedef TW_MEMREF(PASCAL* DSM_MEMLOCK)(TW_HANDLE handle);
typedef void      (PASCAL* DSM_MEMUNLOCK)(TW_HANDLE handle);

typedef struct {
    TW_UINT32       Size;
    DSMENTRYPROC    DSM_Entry;
    DSM_MEMALLOCATE DSM_MemAllocate;
    DSM_MEMFREE     DSM_MemFree;
    DSM_MEMLOCK     DSM_MemLock;
    DSM_MEMUNLOCK   DSM_MemUnlock;
} TW_ENTRYPOINT, FAR* pTW_ENTRYPOINT;

typedef struct {
    TW_MEMREF CallBackProc;
    TW_UINTPTR RefCon;
    TW_INT16  Message;
} TW_CALLBACK2, FAR* pTW_CALLBACK2;

#ifdef _MSC_VER
#pragma pack(pop, rs_twain_pack)
#endif

/* -------------------------------------------------------------- data groups */

#define DG_CONTROL 0x0001L
#define DG_IMAGE   0x0002L
#define DG_AUDIO   0x0004L

#define DF_DSM2 0x10000000L
#define DF_APP2 0x20000000L
#define DF_DS2  0x40000000L

/* ------------------------------------------------------------ data arg types */

#define DAT_NULL            0x0000
#define DAT_CAPABILITY      0x0001
#define DAT_EVENT           0x0002
#define DAT_IDENTITY        0x0003
#define DAT_PARENT          0x0004
#define DAT_PENDINGXFERS    0x0005
#define DAT_SETUPMEMXFER    0x0006
#define DAT_SETUPFILEXFER   0x0007
#define DAT_STATUS          0x0008
#define DAT_USERINTERFACE   0x0009
#define DAT_XFERGROUP       0x000a
#define DAT_CUSTOMDSDATA    0x000c
#define DAT_DEVICEEVENT     0x000d
#define DAT_FILESYSTEM      0x000e
#define DAT_PASSTHRU        0x000f
#define DAT_CALLBACK        0x0010
#define DAT_STATUSUTF8      0x0011
#define DAT_CALLBACK2       0x0012
#define DAT_METRICS         0x0013

/* These six were wrong until 14 Aug 2026, and wrong in a way nothing could detect from the
 * inside: DAT_IMAGEMEMXFER was 0x0104 (which is really NATIVEXFER), NATIVEXFER was 0x0105
 * (FILEXFER), FILEXFER was 0x0106 (CIECOLOR), EXTIMAGEINFO was 0x010c (FILTER),
 * IMAGEMEMFILEXFER was 0x0103 (MEMXFER) and ENTRYPOINT was 0x0401 (ICCPROFILE).
 *
 * The manager passes DAT through untouched, so a source and a test tool that share a header
 * agree with each other perfectly while disagreeing with every real application. NAPS2 asked
 * for memory transfer - 0x0103 - and reached whatever this file had put at that number: first
 * nothing, which surfaced as "TWAIN error: CapUnsupported" after each successful scan, then a
 * memory-FILE transfer, which handed a BMP file's bytes back as if they were raw scanlines and
 * produced no image and no error at all.
 *
 * Values below are taken from NTwain's DataArgumentType, the interop layer NAPS2 itself calls
 * through, and are checked against it on every build by installer\Check-TwainConstants.ps1. */
#define DAT_IMAGEINFO        0x0101
#define DAT_IMAGELAYOUT      0x0102
#define DAT_IMAGEMEMXFER     0x0103
#define DAT_IMAGENATIVEXFER  0x0104
#define DAT_IMAGEFILEXFER    0x0105
#define DAT_CIECOLOR         0x0106
#define DAT_GRAYRESPONSE     0x0107
#define DAT_RGBRESPONSE      0x0108
#define DAT_JPEGCOMPRESSION  0x0109
#define DAT_PALETTE8         0x010a
#define DAT_EXTIMAGEINFO     0x010b
#define DAT_FILTER           0x010c

#define DAT_ICCPROFILE       0x0401
#define DAT_IMAGEMEMFILEXFER 0x0402
#define DAT_ENTRYPOINT       0x0403

/* ---------------------------------------------------------------- messages */

#define MSG_NULL            0x0000
#define MSG_GET             0x0001
#define MSG_GETCURRENT      0x0002
#define MSG_GETDEFAULT      0x0003
#define MSG_GETFIRST        0x0004
#define MSG_GETNEXT         0x0005
#define MSG_SET             0x0006
#define MSG_RESET           0x0007
#define MSG_QUERYSUPPORT    0x0008
#define MSG_GETHELP         0x0009
#define MSG_GETLABEL        0x000a
#define MSG_GETLABELENUM    0x000b
#define MSG_SETCONSTRAINT   0x000c

#define MSG_XFERREADY       0x0101
#define MSG_CLOSEDSREQ      0x0102
#define MSG_CLOSEDSOK       0x0103
#define MSG_DEVICEEVENT     0x0104

#define MSG_OPENDSM         0x0301
#define MSG_CLOSEDSM        0x0302

#define MSG_OPENDS          0x0401
#define MSG_CLOSEDS         0x0402
#define MSG_USERSELECT      0x0403

#define MSG_DISABLEDS       0x0501
#define MSG_ENABLEDS        0x0502
#define MSG_ENABLEDSUIONLY  0x0503

#define MSG_PROCESSEVENT    0x0601
#define MSG_ENDXFER         0x0701
#define MSG_STOPFEEDER      0x0702
#define MSG_REGISTER_CALLBACK 0x0902
#define MSG_RESETALL        0x0A01

/* ------------------------------------------------------------ return codes */

#define TWRC_SUCCESS            0
#define TWRC_FAILURE            1
#define TWRC_CHECKSTATUS        2
#define TWRC_CANCEL             3
#define TWRC_DSEVENT            4
#define TWRC_NOTDSEVENT         5
#define TWRC_XFERDONE           6
#define TWRC_ENDOFLIST          7
#define TWRC_INFONOTSUPPORTED   8
#define TWRC_DATANOTAVAILABLE   9
#define TWRC_BUSY              10
#define TWRC_SCANNERLOCKED     11

/* --------------------------------------------------------- condition codes */

#define TWCC_SUCCESS            0
#define TWCC_BUMMER             1
#define TWCC_LOWMEMORY          2
#define TWCC_NODS               3
#define TWCC_MAXCONNECTIONS     4
#define TWCC_OPERATIONERROR     5
#define TWCC_BADCAP             6
#define TWCC_BADPROTOCOL        9
#define TWCC_BADVALUE          10
#define TWCC_SEQERROR          11
#define TWCC_BADDEST           12
#define TWCC_CAPUNSUPPORTED    13
#define TWCC_CAPBADOPERATION   14
#define TWCC_CAPSEQERROR       15
#define TWCC_DENIED            16
#define TWCC_PAPERJAM          20
#define TWCC_PAPERDOUBLEFEED   21
#define TWCC_CHECKDEVICEONLINE 23
#define TWCC_INTERLOCK         24
#define TWCC_NOMEDIA           29

/* ------------------------------------------------------------- item types */

#define TWTY_INT8   0x0000
#define TWTY_INT16  0x0001
#define TWTY_INT32  0x0002
#define TWTY_UINT8  0x0003
#define TWTY_UINT16 0x0004
#define TWTY_UINT32 0x0005
#define TWTY_BOOL   0x0006
#define TWTY_FIX32  0x0007
#define TWTY_FRAME  0x0008
#define TWTY_STR32  0x0009
#define TWTY_STR64  0x000a
#define TWTY_STR128 0x000b
#define TWTY_STR255 0x000c
#define TWTY_HANDLE 0x000f

/* ------------------------------------------------------------- containers */

#define TWON_ARRAY        3
#define TWON_ENUMERATION  4
#define TWON_ONEVALUE     5
#define TWON_RANGE        6
#define TWON_DONTCARE8    0xff
#define TWON_DONTCARE16   0xffff
#define TWON_DONTCARE32   0xffffffff

/* ---------------------------------------------------------- query support */

#define TWQC_GET           0x0001
#define TWQC_SET           0x0002
#define TWQC_GETDEFAULT    0x0004
#define TWQC_GETCURRENT    0x0008
#define TWQC_RESET         0x0010
#define TWQC_SETCONSTRAINT 0x0020

/* ------------------------------------------------------------ capabilities */

#define CAP_XFERCOUNT           0x0001
#define ICAP_COMPRESSION        0x0100
#define ICAP_PIXELTYPE          0x0101
#define ICAP_UNITS              0x0102
#define ICAP_XFERMECH           0x0103

#define CAP_FEEDERENABLED       0x1002
#define CAP_FEEDERLOADED        0x1003
#define CAP_SUPPORTEDCAPS       0x1005
#define CAP_AUTOFEED            0x1007
#define CAP_INDICATORS          0x100b
#define CAP_PAPERDETECTABLE     0x100d
#define CAP_UICONTROLLABLE      0x100e
#define CAP_DEVICEONLINE        0x100f
#define CAP_AUTOSCAN            0x1010
#define CAP_DUPLEX              0x1012
#define CAP_DUPLEXENABLED       0x1013
#define CAP_ENABLEDSUIONLY      0x1014
#define CAP_CUSTOMDSDATA        0x1015
#define CAP_JOBCONTROL          0x1017
#define CAP_SERIALNUMBER        0x1024
#define CAP_SUPPORTEDDATS       0x103e

#define ICAP_BRIGHTNESS         0x1101
#define ICAP_CONTRAST           0x1103
#define ICAP_IMAGEFILEFORMAT    0x110c
#define ICAP_ORIENTATION        0x1110
#define ICAP_PHYSICALWIDTH      0x1111
#define ICAP_PHYSICALHEIGHT     0x1112
#define ICAP_XNATIVERESOLUTION  0x1116
#define ICAP_YNATIVERESOLUTION  0x1117
#define ICAP_XRESOLUTION        0x1118
#define ICAP_YRESOLUTION        0x1119
#define ICAP_BITORDER           0x111c
#define ICAP_PIXELFLAVOR        0x111f
#define ICAP_PLANARCHUNKY       0x1120
#define ICAP_ROTATION           0x1121
#define ICAP_SUPPORTEDSIZES     0x1122
#define ICAP_BITDEPTH           0x112b
#define ICAP_UNDEFINEDIMAGESIZE 0x112d
#define ICAP_AUTODISCARDBLANKPAGES 0x1134
#define ICAP_AUTOMATICDESKEW    0x1151
#define ICAP_AUTOMATICROTATE    0x1152
#define ICAP_JPEGQUALITY        0x1153
#define ICAP_AUTOSIZE           0x1156

/* ------------------------------------------------------------ cap values */

#define TWPT_BW      0
#define TWPT_GRAY    1
#define TWPT_RGB     2

#define TWSX_NATIVE  0
#define TWSX_FILE    1
#define TWSX_MEMORY  2
#define TWSX_MEMFILE 4

#define TWUN_INCHES      0
#define TWUN_CENTIMETERS 1
#define TWUN_PICAS       2
#define TWUN_POINTS      3
#define TWUN_TWIPS       4
#define TWUN_PIXELS      5
#define TWUN_MILLIMETERS 6

#define TWCP_NONE    0
#define TWCP_GROUP4  5
#define TWCP_JPEG    6
#define TWCP_PNG     9

#define TWFF_TIFF      0
#define TWFF_BMP       2
#define TWFF_JFIF      4
#define TWFF_TIFFMULTI 6
#define TWFF_PNG       7
#define TWFF_PDF      10

#define TWSS_NONE        0
#define TWSS_A4LETTER    1
#define TWSS_B5LETTER    2
#define TWSS_USLETTER    3
#define TWSS_USLEGAL     4
#define TWSS_A5          5
#define TWSS_B4          6
#define TWSS_B6          7
#define TWSS_USLEDGER    9
#define TWSS_USEXECUTIVE 10
#define TWSS_A3          11
#define TWSS_B3          12
#define TWSS_A6          13
/* TWAIN 2.x aliases */
#define TWSS_A4  TWSS_A4LETTER
#define TWSS_B5  TWSS_B5LETTER

#define TWOR_PORTRAIT  0
#define TWOR_LANDSCAPE 3

/* Not the order they appear in most documentation: LSB is zero. Reversed here until 14 Aug
 * 2026, which made the source advertise LSB-first while sending MSB-first data - harmless for
 * 8- and 24-bit pages, and a scrambled image for 1-bit black and white. */
#define TWBO_LSBFIRST 0
#define TWBO_MSBFIRST 1

#define TWPF_CHOCOLATE 0
#define TWPF_VANILLA   1

#define TWPC_CHUNKY 0
#define TWPC_PLANAR 1

#define TWLG_ENGLISH_USA 13
#define TWCY_USA          1

#ifdef __cplusplus
}
#endif

#endif /* RS_TWAIN_H */
