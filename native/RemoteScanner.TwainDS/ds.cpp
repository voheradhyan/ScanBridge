// ds.cpp — RemoteScanner virtual TWAIN Data Source.
//
// This DLL is installed as C:\Windows\twain_32\RemoteScanner\RemoteScanner.ds (and the
// twain_64 equivalent). Any TWAIN application on the RDP server enumerates it as a normal
// scanner named "Remote Scanner (<client PC>)". Every TWAIN operation is proxied over a
// named pipe to the per-session agent, which forwards it down the RDP dynamic virtual
// channel to the user's local PC where the physical scanner lives.
//
// Threading model
// ---------------
// The manager calls DS_Entry synchronously on the host application's thread. Acquisition runs on
// a worker thread we own, so the host's message loop is never blocked and its COM apartment
// is never touched. The two communicate through a small bounded queue: the worker decodes a
// page and parks it; the host thread transfers it out. At most one decoded page and one
// in-flight page exist at a time, which is what keeps a 200-page job off the heap.

#include <windows.h>
#include <sddl.h>
#include <string>
#include <vector>
#include <deque>
#include <thread>
#include <mutex>
#include <condition_variable>
#include <atomic>

#include "../include/rs_twain.h"
#include "../include/rs_protocol.h"
#include "../include/rs_pipe.h"
#include "../include/rs_log.h"
#include "caps.h"
#include "decode.h"

using namespace rs;

namespace {

constexpr TW_UINT16 kProtocolMajor = 2;
constexpr TW_UINT16 kProtocolMinor = 4;
constexpr uint32_t  kStreamId = 1;

HINSTANCE g_instance = nullptr;

// ---------------------------------------------------------------- utilities

std::string wideToUtf8(const std::wstring& value) {
    if (value.empty()) return {};
    int needed = WideCharToMultiByte(CP_UTF8, 0, value.c_str(), static_cast<int>(value.size()),
                                     nullptr, 0, nullptr, nullptr);
    std::string out(static_cast<size_t>(needed), '\0');
    WideCharToMultiByte(CP_UTF8, 0, value.c_str(), static_cast<int>(value.size()),
                        out.data(), needed, nullptr, nullptr);
    return out;
}

/// Copies into a TW_STR32 (32 usable characters plus a terminator), truncating safely.
void setStr32(TW_STR32 dest, const std::string& source) {
    ZeroMemory(dest, sizeof(TW_STR32));
    const size_t limit = sizeof(TW_STR32) - 2;
    memcpy(dest, source.c_str(), (std::min)(source.size(), limit));
}

uint32_t currentSessionId() {
    DWORD sessionId = 0;
    ProcessIdToSessionId(GetCurrentProcessId(), &sessionId);
    return sessionId;
}

/// SID of the user this process runs as, in string form — half of the session pipe name.
std::wstring currentUserSid() {
    HANDLE token = nullptr;
    if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token)) return L"";

    DWORD size = 0;
    GetTokenInformation(token, TokenUser, nullptr, 0, &size);
    std::vector<uint8_t> buffer(size);
    std::wstring result;

    if (size && GetTokenInformation(token, TokenUser, buffer.data(), size, &size)) {
        auto* user = reinterpret_cast<TOKEN_USER*>(buffer.data());
        LPWSTR text = nullptr;
        if (ConvertSidToStringSidW(user->User.Sid, &text)) {
            result = text;
            LocalFree(text);
        }
    }
    CloseHandle(token);
    return result;
}

std::wstring sessionRegistryKey() {
    return L"Software\\RemoteScanner\\Session\\" + std::to_wstring(currentSessionId());
}

std::wstring readRegistryString(const std::wstring& subKey, const std::wstring& name,
                                const std::wstring& fallback) {
    HKEY key{};
    if (RegOpenKeyExW(HKEY_CURRENT_USER, subKey.c_str(), 0, KEY_READ, &key) != ERROR_SUCCESS)
        return fallback;

    wchar_t buffer[256]{};
    DWORD size = sizeof(buffer);
    DWORD type = 0;
    std::wstring result = fallback;
    if (RegQueryValueExW(key, name.c_str(), nullptr, &type, reinterpret_cast<LPBYTE>(buffer), &size)
            == ERROR_SUCCESS && type == REG_SZ) {
        result = buffer;
    }
    RegCloseKey(key);
    return result;
}

/// The name shown in the application's scanner list. The session agent writes the client's
/// machine name here when it connects, so the user sees which PC the scanner is on.
std::string productName() {
    std::wstring client = readRegistryString(sessionRegistryKey(), L"ClientName", L"");
    if (client.empty()) return "Remote Scanner";
    std::string name = "Remote Scanner (" + wideToUtf8(client) + ")";
    if (name.size() > sizeof(TW_STR32) - 2) name = name.substr(0, sizeof(TW_STR32) - 2);
    return name;
}

/// Emits a baseline uncompressed TIFF for DAT_IMAGEFILEXFER.
///
/// Written by hand rather than through WIC because file transfer is serviced on the host
/// application's thread, and initialising COM there would disturb an apartment we do not own.
/// The page has already been compressed for the wire, so nothing is gained by re-encoding.
template <typename Put>
void writeUncompressedTiff(Put&& put, const DecodedPage& page) {
    const uint16_t samples = page.bitsPerPixel == 24 ? 3 : 1;
    const uint16_t bitsPerSample = page.bitsPerPixel == 24 ? 8 : static_cast<uint16_t>(page.bitsPerPixel);
    // TIFF rows are byte-packed; DIB rows are padded to 4 bytes, so they get repacked below.
    const uint32_t tiffStride = static_cast<uint32_t>((page.width * page.bitsPerPixel + 7) / 8);
    const uint32_t imageBytes = tiffStride * static_cast<uint32_t>(page.height);

    constexpr uint16_t kEntryCount = 13;
    const uint32_t ifdOffset = 8;
    const uint32_t ifdSize = 2u + kEntryCount * 12u + 4u;
    const uint32_t extraOffset = ifdOffset + ifdSize;

    // Values too large for an IFD entry's inline 4 bytes live in this block.
    const uint32_t bitsOffset = extraOffset;
    const uint32_t bitsSize = samples == 3 ? 6u : 0u;
    const uint32_t xResOffset = bitsOffset + bitsSize;
    const uint32_t yResOffset = xResOffset + 8;
    const uint32_t imageOffset = yResOffset + 8;

    std::vector<uint8_t> head;
    head.reserve(imageOffset);

    auto push16 = [&](uint16_t v) { head.insert(head.end(), reinterpret_cast<uint8_t*>(&v),
                                                reinterpret_cast<uint8_t*>(&v) + 2); };
    auto push32 = [&](uint32_t v) { head.insert(head.end(), reinterpret_cast<uint8_t*>(&v),
                                                reinterpret_cast<uint8_t*>(&v) + 4); };

    head.push_back('I'); head.push_back('I');
    push16(42);
    push32(ifdOffset);

    push16(kEntryCount);

    constexpr uint16_t kShort = 3, kLong = 4, kRational = 5;
    auto entry = [&](uint16_t tag, uint16_t type, uint32_t count, uint32_t value) {
        push16(tag);
        push16(type);
        push32(count);
        // A single SHORT occupies the first two bytes of the value field, not the last.
        if (type == kShort && count == 1) { push16(static_cast<uint16_t>(value)); push16(0); }
        else push32(value);
    };

    entry(256, kLong, 1, static_cast<uint32_t>(page.width));              // ImageWidth
    entry(257, kLong, 1, static_cast<uint32_t>(page.height));             // ImageLength
    if (samples == 3) entry(258, kShort, 3, bitsOffset);                  // BitsPerSample
    else              entry(258, kShort, 1, bitsPerSample);
    entry(259, kShort, 1, 1);                                             // Compression: none
    // Our 1-bit and 8-bit palettes both map index 0 to black.
    entry(262, kShort, 1, samples == 3 ? 2u : 1u);                        // PhotometricInterpretation
    entry(273, kLong, 1, imageOffset);                                    // StripOffsets
    entry(277, kShort, 1, samples);                                       // SamplesPerPixel
    entry(278, kLong, 1, static_cast<uint32_t>(page.height));             // RowsPerStrip
    entry(279, kLong, 1, imageBytes);                                     // StripByteCounts
    entry(282, kRational, 1, xResOffset);                                 // XResolution
    entry(283, kRational, 1, yResOffset);                                 // YResolution
    entry(284, kShort, 1, 1);                                             // PlanarConfiguration
    entry(296, kShort, 1, 2);                                             // ResolutionUnit: inch

    push32(0);   // no further IFDs

    if (samples == 3) { push16(8); push16(8); push16(8); }
    push32(static_cast<uint32_t>(page.dpiX)); push32(1);
    push32(static_cast<uint32_t>(page.dpiY)); push32(1);

    put(head.data(), head.size());

    // DIB rows are bottom-up and padded; TIFF wants top-down and tight. 24bpp DIBs are BGR
    // while TIFF RGB is R,G,B, so colour samples are swapped as the row is repacked.
    const int32_t dibStride = page.stride();
    std::vector<uint8_t> row(tiffStride);
    for (int32_t y = 0; y < page.height; ++y) {
        const uint8_t* source = page.bits.data() + static_cast<size_t>(page.height - 1 - y) * dibStride;
        if (samples == 3) {
            for (int32_t x = 0; x < page.width; ++x) {
                row[static_cast<size_t>(x) * 3 + 0] = source[static_cast<size_t>(x) * 3 + 2];
                row[static_cast<size_t>(x) * 3 + 1] = source[static_cast<size_t>(x) * 3 + 1];
                row[static_cast<size_t>(x) * 3 + 2] = source[static_cast<size_t>(x) * 3 + 0];
            }
        } else {
            memcpy(row.data(), source, tiffStride);
        }
        put(row.data(), row.size());
    }
}

// -------------------------------------------------------------- data source

class DataSource {
public:
    TW_UINT16 dispatch(pTW_IDENTITY origin, TW_UINT32 dg, TW_UINT16 dat, TW_UINT16 msg, TW_MEMREF data);

private:
    // ---- state helpers
    TW_UINT16 fail(TW_UINT16 conditionCode, const std::string& detail = {}) {
        conditionCode_ = conditionCode;
        if (!detail.empty()) {
            statusText_ = detail;
            RS_LOG_ERROR("%s (TWCC %u)", detail.c_str(), conditionCode);
        }
        return TWRC_FAILURE;
    }

    TW_UINT16 sequenceError() { return fail(TWCC_SEQERROR); }

    TW_UINT16 succeed() { conditionCode_ = TWCC_SUCCESS; return TWRC_SUCCESS; }

    bool inState(int low, int high) const { return state_ >= low && state_ <= high; }

    // ---- operation handlers
    TW_UINT16 onOpenDs(pTW_IDENTITY origin);
    TW_UINT16 onCloseDs();
    TW_UINT16 onEnableDs(pTW_USERINTERFACE ui, bool uiOnly);
    TW_UINT16 onDisableDs();
    TW_UINT16 onProcessEvent(pTW_EVENT event);
    TW_UINT16 onCapability(TW_UINT16 msg, pTW_CAPABILITY cap);
    TW_UINT16 onPendingXfers(TW_UINT16 msg, pTW_PENDINGXFERS pending);
    TW_UINT16 onImageInfo(pTW_IMAGEINFO info);
    TW_UINT16 onImageLayout(TW_UINT16 msg, pTW_IMAGELAYOUT layout);
    TW_UINT16 onExtImageInfo(pTW_EXTIMAGEINFO info);
    TW_UINT16 unhandledOperation(TW_UINT32 dg, TW_UINT16 dat, TW_UINT16 msg);
    TW_UINT16 onNativeXfer(TW_MEMREF data);
    TW_UINT16 onMemoryXfer(pTW_IMAGEMEMXFER xfer);
    TW_UINT16 onFileXfer();
    TW_UINT16 onMemoryFileXfer(pTW_IMAGEMEMXFER xfer);
    TW_UINT16 onSetupFileXfer(TW_UINT16 msg, pTW_SETUPFILEXFER setup);
    void encodeCurrentPage(uint32_t format, std::vector<uint8_t>& out) const;

    // ---- capability plumbing
    void buildCapabilities(const ScannerCaps& caps);
    TW_UINT16 capabilityGet(TW_UINT16 msg, pTW_CAPABILITY cap);
    TW_UINT16 capabilitySet(pTW_CAPABILITY cap);

    // ---- transport
    bool connectToAgent();
    ScanSettings settingsFromCapabilities() const;
    void workerLoop();
    void stopWorker(bool sendCancel);
    bool waitForPage();
    void notifyApplication(TW_UINT16 message);

    // ---- worker/host shared state
    std::thread worker_;
    std::atomic<bool> cancelRequested_{ false };
    std::atomic<bool> jobRunning_{ false };
    std::atomic<bool> jobFailed_{ false };
    ScanError jobError_ = ScanError::None;
    std::string jobErrorText_;

    std::mutex queueMutex_;
    std::condition_variable queueSignal_;
    std::deque<DecodedPage> readyPages_;
    static constexpr size_t kMaxQueuedPages = 2;

    // ---- host-thread state
    int state_ = 3;
    TW_UINT16 conditionCode_ = TWCC_SUCCESS;
    std::string statusText_;

    TW_IDENTITY appIdentity_{};
    bool haveAppIdentity_ = false;

    PipeClient pipe_;
    CapabilityStore caps_;
    DsmMemory memory_;
    std::string scannerId_;
    ScannerCaps scannerCaps_;

    // A page size the application set through DAT_IMAGELAYOUT, forwarded to the scanner as a
    // custom page size. Cleared by MSG_RESET and by closing the source.
    bool haveLayoutRegion_ = false;
    int32_t layoutWidthThou_ = 0;
    int32_t layoutHeightThou_ = 0;

    DecodedPage currentPage_;
    bool havePage_ = false;
    uint32_t memXferRow_ = 0;          // next scanline for strip-wise memory transfer

    // The current page encoded as a complete image file, drained a buffer at a time by
    // DAT_IMAGEMEMFILEXFER. Empty between pages; built on the first call for each one.
    std::vector<uint8_t> memFile_;
    size_t memFileOffset_ = 0;
    uint32_t jobId_ = 0;
    int pagesTransferred_ = 0;

    // Written by the acquisition worker, read by the application's thread in DAT_EVENT.
    // Atomic because those are genuinely different threads: a plain member could be cached in
    // a register across the application's polling loop and the page would never be collected.
    std::atomic<TW_UINT16> pendingMessage_{ MSG_NULL };
    TW_USERINTERFACE userInterface_{};
    TW_SETUPFILEXFER fileXfer_{ "", TWFF_BMP, 0 };

    // TWAIN 2.x applications register a callback instead of pumping DAT_EVENT.
    TW_CALLBACK2 callback_{};
    bool haveCallback_ = false;

public:
    DsmMemory& memory() { return memory_; }
    void setEntryPoints(pTW_ENTRYPOINT entry) {
        memory_.allocate = entry->DSM_MemAllocate;
        memory_.freeMem = entry->DSM_MemFree;
        memory_.lock = entry->DSM_MemLock;
        memory_.unlock = entry->DSM_MemUnlock;
        RS_LOG_DEBUG("DSM 2.x entry points installed");
    }
    TW_UINT16 conditionCode() const { return conditionCode_; }
    const std::string& statusText() const { return statusText_; }
};

DataSource g_ds;

// -------------------------------------------------------------- capabilities

void DataSource::buildCapabilities(const ScannerCaps& caps) {
    caps_.clear();

    // Pixel types the physical scanner actually reports. Nothing is invented here: if the
    // hardware cannot do colour, the application is never offered colour.
    std::vector<uint32_t> pixelTypes;
    for (PixType type : caps.pixelTypes) {
        switch (type) {
            case PixType::BlackWhite: pixelTypes.push_back(TWPT_BW); break;
            case PixType::Grayscale:  pixelTypes.push_back(TWPT_GRAY); break;
            case PixType::Rgb:        pixelTypes.push_back(TWPT_RGB); break;
        }
    }
    if (pixelTypes.empty()) pixelTypes = { TWPT_BW, TWPT_GRAY, TWPT_RGB };
    const uint32_t defaultPixelType =
        std::find(pixelTypes.begin(), pixelTypes.end(), static_cast<uint32_t>(TWPT_RGB)) != pixelTypes.end()
            ? TWPT_RGB : pixelTypes.front();
    caps_.defineEnum(ICAP_PIXELTYPE, TWTY_UINT16, pixelTypes, defaultPixelType);

    std::vector<uint32_t> resolutions;
    for (int32_t dpi : caps.resolutions) resolutions.push_back(packFix32(static_cast<double>(dpi)));
    if (resolutions.empty())
        for (int dpi : { 100, 150, 200, 300, 400, 600 }) resolutions.push_back(packFix32(dpi));
    const uint32_t defaultDpi = packFix32(300.0);
    const uint32_t chosenDpi =
        std::find(resolutions.begin(), resolutions.end(), defaultDpi) != resolutions.end()
            ? defaultDpi : resolutions.front();
    caps_.defineEnum(ICAP_XRESOLUTION, TWTY_FIX32, resolutions, chosenDpi);
    caps_.defineEnum(ICAP_YRESOLUTION, TWTY_FIX32, resolutions, chosenDpi);

    std::vector<uint32_t> paperSizes{ TWSS_NONE };
    for (PaperSize size : caps.paperSizes) {
        switch (size) {
            case PaperSize::A4:        paperSizes.push_back(TWSS_A4LETTER); break;
            case PaperSize::A3:        paperSizes.push_back(TWSS_A3); break;
            case PaperSize::A5:        paperSizes.push_back(TWSS_A5); break;
            case PaperSize::Letter:    paperSizes.push_back(TWSS_USLETTER); break;
            case PaperSize::Legal:     paperSizes.push_back(TWSS_USLEGAL); break;
            case PaperSize::Executive: paperSizes.push_back(TWSS_USEXECUTIVE); break;
            case PaperSize::B4:        paperSizes.push_back(TWSS_B4); break;
            case PaperSize::B5:        paperSizes.push_back(TWSS_B5LETTER); break;
            default: break;
        }
    }
    caps_.defineEnum(ICAP_SUPPORTEDSIZES, TWTY_UINT16, paperSizes, TWSS_NONE);

    const bool hasFeeder = (caps.features & static_cast<uint32_t>(ScannerFeature::Feeder)) != 0;
    const bool hasDuplex = (caps.features & static_cast<uint32_t>(ScannerFeature::Duplex)) != 0;

    caps_.defineBool(CAP_FEEDERENABLED, hasFeeder, !hasFeeder);
    caps_.defineBool(CAP_FEEDERLOADED, false, /*readOnly*/ true);
    caps_.defineBool(CAP_PAPERDETECTABLE, hasFeeder, true);
    caps_.defineBool(CAP_AUTOFEED, hasFeeder, !hasFeeder);
    caps_.defineBool(CAP_AUTOSCAN, hasFeeder, !hasFeeder);
    caps_.defineSingle(CAP_DUPLEX, TWTY_UINT16, hasDuplex ? 1u : 0u, /*readOnly*/ true);
    caps_.defineBool(CAP_DUPLEXENABLED, false, !hasDuplex);

    // -1 means "scan until the feeder is empty"; TWAIN encodes it as 0xFFFF in an INT16.
    caps_.defineRange(CAP_XFERCOUNT, TWTY_INT16, static_cast<uint32_t>(0xFFFF), 9999, 1,
                      static_cast<uint32_t>(0xFFFF));

    caps_.defineSingle(CAP_UICONTROLLABLE, TWTY_BOOL, 1, true);
    caps_.defineSingle(CAP_DEVICEONLINE, TWTY_BOOL, 1, true);
    caps_.defineBool(CAP_INDICATORS, true);
    caps_.defineSingle(CAP_ENABLEDSUIONLY, TWTY_BOOL, 0, true);
    caps_.defineBool(CAP_JOBCONTROL, false, true);
    // No vendor settings blob to hand back: the physical scanner's own driver holds that, on
    // the other end of the link. Answering "false" is a real answer; leaving the capability
    // undefined makes an application asking about it collect a capability error instead.
    caps_.defineSingle(CAP_CUSTOMDSDATA, TWTY_BOOL, 0, true);

    // Transfer mechanisms. All four are implemented; native remains the default because that
    // is what most applications assume. TWSX_MEMFILE belongs here because applications do use
    // it - NAPS2 does - and an application that means to use a mechanism will call for it
    // whether or not the source advertised it, so leaving it off the list bought nothing and
    // cost a transfer that failed after the page had already been scanned.
    caps_.defineEnum(ICAP_XFERMECH, TWTY_UINT16,
                     { TWSX_NATIVE, TWSX_MEMORY, TWSX_FILE, TWSX_MEMFILE }, TWSX_NATIVE);
    caps_.defineEnum(ICAP_UNITS, TWTY_UINT16, { TWUN_INCHES, TWUN_PIXELS }, TWUN_INCHES);
    caps_.defineEnum(ICAP_COMPRESSION, TWTY_UINT16, { TWCP_NONE }, TWCP_NONE);
    caps_.defineEnum(ICAP_IMAGEFILEFORMAT, TWTY_UINT16, { TWFF_BMP, TWFF_TIFF }, TWFF_BMP);
    caps_.defineEnum(ICAP_ORIENTATION, TWTY_UINT16, { TWOR_PORTRAIT, TWOR_LANDSCAPE }, TWOR_PORTRAIT);
    caps_.defineEnum(ICAP_BITORDER, TWTY_UINT16, { TWBO_MSBFIRST }, TWBO_MSBFIRST);
    caps_.defineEnum(ICAP_PIXELFLAVOR, TWTY_UINT16, { TWPF_CHOCOLATE }, TWPF_CHOCOLATE);
    caps_.defineEnum(ICAP_PLANARCHUNKY, TWTY_UINT16, { TWPC_CHUNKY }, TWPC_CHUNKY);
    caps_.defineEnum(ICAP_BITDEPTH, TWTY_UINT16, { 1, 8, 24 }, 24);

    if (caps.features & static_cast<uint32_t>(ScannerFeature::Brightness)) {
        caps_.defineRange(ICAP_BRIGHTNESS, TWTY_FIX32,
                          packFix32(caps.brightnessMin), packFix32(caps.brightnessMax),
                          packFix32(1.0), packFix32(0.0));
    }
    if (caps.features & static_cast<uint32_t>(ScannerFeature::Contrast)) {
        caps_.defineRange(ICAP_CONTRAST, TWTY_FIX32,
                          packFix32(caps.contrastMin), packFix32(caps.contrastMax),
                          packFix32(1.0), packFix32(0.0));
    }
    if (caps.features & static_cast<uint32_t>(ScannerFeature::AutoDeskew))
        caps_.defineBool(ICAP_AUTOMATICDESKEW, false);
    if (caps.features & static_cast<uint32_t>(ScannerFeature::AutoPageSize))
        caps_.defineBool(ICAP_AUTOSIZE, false);
    if (caps.features & static_cast<uint32_t>(ScannerFeature::BlankPageRemoval))
        caps_.defineBool(ICAP_AUTODISCARDBLANKPAGES, false);

    caps_.defineSingle(ICAP_PHYSICALWIDTH, TWTY_FIX32,
                       packFix32(caps.maxWidthThou / 1000.0), true);
    caps_.defineSingle(ICAP_PHYSICALHEIGHT, TWTY_FIX32,
                       packFix32(caps.maxHeightThou / 1000.0), true);
    caps_.defineSingle(ICAP_XNATIVERESOLUTION, TWTY_FIX32, chosenDpi, true);
    caps_.defineSingle(ICAP_YNATIVERESOLUTION, TWTY_FIX32, chosenDpi, true);
    caps_.defineBool(ICAP_UNDEFINEDIMAGESIZE, false, true);

    RS_LOG_INFO("published %zu capabilities for scanner '%s'",
                caps_.supportedList().size(), caps.scannerId.c_str());
}

TW_UINT16 DataSource::capabilityGet(TW_UINT16 msg, pTW_CAPABILITY cap) {
    ContainerBuilder builder(memory_);

    // CAP_SUPPORTEDCAPS is synthesised from the store so it can never disagree with it.
    if (cap->Cap == CAP_SUPPORTEDCAPS) {
        if (msg != MSG_GET && msg != MSG_GETCURRENT && msg != MSG_GETDEFAULT)
            return fail(TWCC_CAPBADOPERATION);
        cap->ConType = TWON_ARRAY;
        cap->hContainer = builder.array(TWTY_UINT16, caps_.supportedList());
        return cap->hContainer ? succeed() : fail(TWCC_LOWMEMORY);
    }

    Capability* entry = caps_.find(cap->Cap);

    if (msg == MSG_QUERYSUPPORT) {
        // "Which operations does this capability support?" is answerable for a capability we
        // do not have: none of them. Answering zero is what the question asks for, and it is
        // how an application discovers the capability is absent without being handed a
        // failure it then shows the user. Failing here is how a scan that worked ends with
        // "TWAIN error: CapUnsupported" on screen.
        cap->ConType = TWON_ONEVALUE;
        cap->hContainer = builder.oneValue(TWTY_INT32, entry ? entry->querySupport() : 0u);
        return cap->hContainer ? succeed() : fail(TWCC_LOWMEMORY);
    }

    if (!entry) return fail(TWCC_CAPUNSUPPORTED);

    if (msg == MSG_GETCURRENT || msg == MSG_GETDEFAULT) {
        cap->ConType = TWON_ONEVALUE;
        cap->hContainer = builder.oneValue(entry->type,
                                           msg == MSG_GETCURRENT ? entry->current : entry->defaultValue);
        return cap->hContainer ? succeed() : fail(TWCC_LOWMEMORY);
    }

    if (msg != MSG_GET) return fail(TWCC_CAPBADOPERATION);

    if (entry->isRange) {
        cap->ConType = TWON_RANGE;
        cap->hContainer = builder.range(entry->type, entry->rangeMin, entry->rangeMax,
                                        entry->rangeStep, entry->defaultValue, entry->current);
    } else if (entry->values.size() == 1) {
        cap->ConType = TWON_ONEVALUE;
        cap->hContainer = builder.oneValue(entry->type, entry->current);
    } else {
        cap->ConType = TWON_ENUMERATION;
        cap->hContainer = builder.enumeration(entry->type, entry->values,
                                              entry->currentIndex(), entry->defaultIndex());
    }
    return cap->hContainer ? succeed() : fail(TWCC_LOWMEMORY);
}

/// Names the handful of capabilities worth reading in a log by name rather than by number.
/// Everything else is printed as hex, which is what `rs_twain.h` lists it as.
const char* capabilityName(TW_UINT16 id) {
    switch (id) {
        case ICAP_XFERMECH:        return "ICAP_XFERMECH";
        case ICAP_IMAGEFILEFORMAT: return "ICAP_IMAGEFILEFORMAT";
        case ICAP_PIXELTYPE:       return "ICAP_PIXELTYPE";
        case ICAP_COMPRESSION:     return "ICAP_COMPRESSION";
        case ICAP_UNITS:           return "ICAP_UNITS";
        case ICAP_BITDEPTH:        return "ICAP_BITDEPTH";
        case ICAP_XRESOLUTION:     return "ICAP_XRESOLUTION";
        case ICAP_YRESOLUTION:     return "ICAP_YRESOLUTION";
        case ICAP_SUPPORTEDSIZES:  return "ICAP_SUPPORTEDSIZES";
        case CAP_XFERCOUNT:        return "CAP_XFERCOUNT";
        case CAP_FEEDERENABLED:    return "CAP_FEEDERENABLED";
        case CAP_DUPLEXENABLED:    return "CAP_DUPLEXENABLED";
        case CAP_AUTOFEED:         return "CAP_AUTOFEED";
        case CAP_AUTOSCAN:         return "CAP_AUTOSCAN";
        case CAP_INDICATORS:       return "CAP_INDICATORS";
        default:                   return "";
    }
}

TW_UINT16 DataSource::capabilitySet(pTW_CAPABILITY cap) {
    Capability* entry = caps_.find(cap->Cap);
    if (!entry) return fail(TWCC_CAPUNSUPPORTED);
    if (entry->readOnly) return fail(TWCC_CAPBADOPERATION);
    if (!cap->hContainer) return fail(TWCC_BADVALUE);

    auto* raw = static_cast<uint8_t*>(memory_.acquire(cap->hContainer));
    if (!raw) return fail(TWCC_LOWMEMORY);

    uint32_t requested = 0;
    bool parsed = false;

    switch (cap->ConType) {
        case TWON_ONEVALUE: {
            auto* container = reinterpret_cast<pTW_ONEVALUE>(raw);
            requested = readItem(raw + offsetof(TW_ONEVALUE, Item), container->ItemType);
            parsed = true;
            break;
        }
        case TWON_ENUMERATION: {
            auto* container = reinterpret_cast<pTW_ENUMERATION>(raw);
            if (container->NumItems > 0 && container->CurrentIndex < container->NumItems) {
                const size_t unit = itemSize(container->ItemType);
                requested = readItem(raw + offsetof(TW_ENUMERATION, ItemList)
                                         + unit * container->CurrentIndex,
                                     container->ItemType);
                parsed = true;
            }
            break;
        }
        case TWON_RANGE: {
            auto* container = reinterpret_cast<pTW_RANGE>(raw);
            requested = container->CurrentValue;
            parsed = true;
            break;
        }
        case TWON_ARRAY: {
            auto* container = reinterpret_cast<pTW_ARRAY>(raw);
            if (container->NumItems > 0) {
                requested = readItem(raw + offsetof(TW_ARRAY, ItemList), container->ItemType);
                parsed = true;
            }
            break;
        }
        default:
            break;
    }

    memory_.relinquish(cap->hContainer);

    if (!parsed) return fail(TWCC_BADVALUE);

    if (!entry->allows(requested)) {
        // Logged at warning level, and deliberately loudly.
        //
        // An application that asks for a value the source does not offer does not necessarily
        // change its mind when refused. NAPS2 asked for a transfer mechanism we did not list,
        // was told BADVALUE, and used it anyway - which is how a scan that ran perfectly ended
        // in an error dialog. Whatever the application asked for here is what it is about to
        // assume it got, so a refusal is a lead, not a non-event.
        RS_LOG_WARN("application asked for %s%s0x%04X = %u, which is not offered",
                    capabilityName(cap->Cap), *capabilityName(cap->Cap) ? " / " : "",
                    cap->Cap, requested);

        // TWAIN's contract: clamp to the nearest legal value and report CHECKSTATUS rather
        // than failing outright, so applications that guess a value still get a usable scan.
        if (entry->isRange) {
            uint32_t clamped = requested;
            if (entry->type == TWTY_FIX32) {
                double v = unpackFix32(requested);
                double lo = unpackFix32(entry->rangeMin), hi = unpackFix32(entry->rangeMax);
                clamped = packFix32(v < lo ? lo : (v > hi ? hi : v));
            } else {
                clamped = requested < entry->rangeMin ? entry->rangeMin
                        : (requested > entry->rangeMax ? entry->rangeMax : requested);
            }
            entry->current = clamped;
            entry->modified = true;
            conditionCode_ = TWCC_BADVALUE;
            return TWRC_CHECKSTATUS;
        }
        return fail(TWCC_BADVALUE);
    }

    entry->current = requested;
    entry->modified = true;

    RS_LOG_INFO("application set %s%s0x%04X = %u",
                capabilityName(cap->Cap), *capabilityName(cap->Cap) ? " / " : "",
                cap->Cap, requested);

    // X and Y resolution move together unless an application deliberately splits them;
    // most scanners are square-resolution and applications expect the pairing.
    if (cap->Cap == ICAP_XRESOLUTION) {
        if (Capability* y = caps_.find(ICAP_YRESOLUTION); y && !y->modified) y->current = requested;
    } else if (cap->Cap == ICAP_YRESOLUTION) {
        if (Capability* x = caps_.find(ICAP_XRESOLUTION); x && !x->modified) x->current = requested;
    }

    return succeed();
}

TW_UINT16 DataSource::onCapability(TW_UINT16 msg, pTW_CAPABILITY cap) {
    // MSG_RESETALL carries no capability, so it is answered before the null check.
    if (msg == MSG_RESETALL) {
        if (state_ != 4) return sequenceError();
        caps_.resetAll();
        haveLayoutRegion_ = false;
        RS_LOG_DEBUG("all capabilities reset to their defaults");
        return succeed();
    }

    if (!cap) return fail(TWCC_BADVALUE);

    switch (msg) {
        case MSG_GET:
        case MSG_GETCURRENT:
        case MSG_GETDEFAULT:
        case MSG_QUERYSUPPORT:
            // Reads are legal from state 4 up; writes are not.
            if (!inState(4, 7)) return sequenceError();
            return capabilityGet(msg, cap);

        case MSG_SET:
        case MSG_SETCONSTRAINT:
            if (state_ != 4) return sequenceError();
            return capabilitySet(cap);

        case MSG_RESET: {
            if (state_ != 4) return sequenceError();
            Capability* entry = caps_.find(cap->Cap);
            if (!entry) {
                // "Put this capability back to its default." For one we do not have, that is
                // the state we are already in, so there is nothing to refuse and nothing to
                // do - the same reasoning that applies to DAT_IMAGELAYOUT MSG_RESET below.
                // Applications reset capabilities in bulk when a job ends, and failing one of
                // them turns a completed scan into an error dialog.
                RS_LOG_DEBUG("MSG_RESET on unsupported capability 0x%04X: already default",
                             cap->Cap);
                cap->ConType = TWON_ONEVALUE;
                cap->hContainer = nullptr;
                return succeed();
            }
            if (entry->readOnly) return fail(TWCC_CAPBADOPERATION);
            entry->current = entry->defaultValue;
            entry->modified = false;
            return capabilityGet(MSG_GETCURRENT, cap);
        }

        default:
            return fail(TWCC_CAPBADOPERATION);
    }
}

// ------------------------------------------------------------------ transport

bool DataSource::connectToAgent() {
    try {
        const uint32_t sessionId = currentSessionId();
        const std::wstring sid = currentUserSid();
        if (sid.empty()) {
            fail(TWCC_OPERATIONERROR, "could not determine the session user SID");
            return false;
        }

        const std::wstring pipeName =
            L"RemoteScanner.Session." + sid + L"." + std::to_wstring(sessionId);

        // On a clean thread for the same reason the DVC plugin does it: this runs inside a
        // third-party scanning application, and HKEY_CURRENT_USER and DPAPI both follow the
        // thread token. An application that happens to be impersonating would read a valid
        // secret belonging to somebody else and be refused with no useful error.
        std::vector<uint8_t> secret =
            loadProtectedSecretOnCleanThread(sessionRegistryKey(), L"Secret");

        RS_LOG_INFO("shared secret loaded: key %s (calling thread impersonating: %s)",
                    secretFingerprint(secret).c_str(),
                    threadIsImpersonating() ? "yes" : "no");

        pipe_.connect(pipeName, 5000);

        const uint32_t capabilities =
            static_cast<uint32_t>(PeerCaps::Jpeg) | static_cast<uint32_t>(PeerCaps::Png) |
            static_cast<uint32_t>(PeerCaps::CcittG4) | static_cast<uint32_t>(PeerCaps::Raw) |
            static_cast<uint32_t>(PeerCaps::FlowControl) | static_cast<uint32_t>(PeerCaps::Cancellation);

        performHandshake(pipe_, PeerRole::TwainDataSource, capabilities, secret, sessionId, kStreamId);
        SecureZeroMemory(secret.data(), secret.size());

        // Ask which scanners the client PC is offering. The agent puts the user's chosen
        // default first, so index 0 is the one this data source represents.
        pipe_.sendFrame(MsgType::ScannerEnumRequest, kStreamId, std::vector<uint8_t>{});
        Frame response = pipe_.readFrame(15000);
        if (response.type != MsgType::ScannerEnumResponse) {
            fail(TWCC_OPERATIONERROR, "unexpected reply to scanner enumeration");
            return false;
        }

        PayloadReader reader = response.reader();
        const int32_t count = reader.count(256);
        if (count == 0) {
            fail(TWCC_CHECKDEVICEONLINE, "no scanner is available on the local PC");
            return false;
        }
        ScannerInfo first = ScannerInfo::read(reader);
        scannerId_ = first.id;
        RS_LOG_INFO("bound to local scanner '%s' (%s)", first.name.c_str(), first.vendor.c_str());

        PayloadWriter capsRequest;
        capsRequest.str(scannerId_);
        pipe_.sendFrame(MsgType::ScannerCapsRequest, kStreamId, capsRequest);

        Frame capsFrame = pipe_.readFrame(15000);
        if (capsFrame.type != MsgType::ScannerCapsResponse) {
            fail(TWCC_OPERATIONERROR, "unexpected reply to capability query");
            return false;
        }
        PayloadReader capsReader = capsFrame.reader();
        scannerCaps_ = ScannerCaps::read(capsReader);
        if (!scannerCaps_.found) {
            fail(TWCC_CHECKDEVICEONLINE, "the local scanner is no longer available");
            return false;
        }

        buildCapabilities(scannerCaps_);
        return true;
    }
    catch (const PipeError& ex) {
        // TWCC_CHECKDEVICEONLINE, not TWCC_MAXCONNECTIONS: applications render the former as
        // "the scanner is switched off or disconnected", which is exactly what has happened
        // from the user's point of view - the client PC is not sharing its scanner. The
        // latter renders as "too many connections", sending them to look for a second
        // application holding the device open, which does not exist.
        fail(TWCC_CHECKDEVICEONLINE,
             std::string("cannot reach the RemoteScanner agent: ") + ex.what());
        return false;
    }
    catch (const std::exception& ex) {
        fail(TWCC_OPERATIONERROR, std::string("connect failed: ") + ex.what());
        return false;
    }
}

ScanSettings DataSource::settingsFromCapabilities() const {
    ScanSettings settings;
    settings.resolution = static_cast<int32_t>(caps_.currentFix32(ICAP_XRESOLUTION, 300.0) + 0.5);

    switch (caps_.current(ICAP_PIXELTYPE, TWPT_RGB)) {
        case TWPT_BW:   settings.pixelType = PixType::BlackWhite; break;
        case TWPT_GRAY: settings.pixelType = PixType::Grayscale; break;
        default:        settings.pixelType = PixType::Rgb; break;
    }

    const bool feeder = caps_.currentBool(CAP_FEEDERENABLED, false);
    settings.source = feeder ? PageSource::Feeder : PageSource::Flatbed;
    settings.duplex = caps_.currentBool(CAP_DUPLEXENABLED, false);

    switch (caps_.current(ICAP_SUPPORTEDSIZES, TWSS_NONE)) {
        case TWSS_A4LETTER:    settings.paperSize = PaperSize::A4; break;
        case TWSS_A3:          settings.paperSize = PaperSize::A3; break;
        case TWSS_A5:          settings.paperSize = PaperSize::A5; break;
        case TWSS_USLETTER:    settings.paperSize = PaperSize::Letter; break;
        case TWSS_USLEGAL:     settings.paperSize = PaperSize::Legal; break;
        case TWSS_USEXECUTIVE: settings.paperSize = PaperSize::Executive; break;
        case TWSS_B4:          settings.paperSize = PaperSize::B4; break;
        case TWSS_B5LETTER:    settings.paperSize = PaperSize::B5; break;
        default:               settings.paperSize = PaperSize::Auto; break;
    }

    // An explicit layout beats the paper-size capability: the application set it later and
    // more specifically, and it is the one the user actually chose in the scanning program.
    if (haveLayoutRegion_ && layoutWidthThou_ > 0 && layoutHeightThou_ > 0) {
        settings.paperSize = PaperSize::Custom;
        settings.customWidthThou = layoutWidthThou_;
        settings.customHeightThou = layoutHeightThou_;
    }

    settings.orientation = caps_.current(ICAP_ORIENTATION, TWOR_PORTRAIT) == TWOR_LANDSCAPE
        ? PageOrientation::Landscape : PageOrientation::Portrait;
    settings.brightness = caps_.currentFix32(ICAP_BRIGHTNESS, 0.0);
    settings.contrast = caps_.currentFix32(ICAP_CONTRAST, 0.0);
    settings.autoDeskew = caps_.currentBool(ICAP_AUTOMATICDESKEW, false);
    settings.autoPageSize = caps_.currentBool(ICAP_AUTOSIZE, false);
    settings.blankPageRemoval = caps_.currentBool(ICAP_AUTODISCARDBLANKPAGES, false);
    settings.showScannerUi = userInterface_.ShowUI != 0;

    // CAP_XFERCOUNT is an INT16 where -1 means "everything in the feeder".
    const int16_t xferCount = static_cast<int16_t>(caps_.current(CAP_XFERCOUNT, 0xFFFF));
    settings.pageLimit = xferCount < 0 ? 0 : xferCount;

    // Bitonal pages compress far better as CCITT G4 than as JPEG, and JPEG artefacts on
    // 1-bit text are unacceptable; everything else goes out as JPEG.
    settings.encoding = settings.pixelType == PixType::BlackWhite
        ? PageEncoding::CcittG4Tiff : PageEncoding::Jpeg;
    settings.jpegQuality = 85;

    return settings;
}

void DataSource::workerLoop() {
    PageDecoder decoder;
    ScanError error = ScanError::None;
    std::string errorText;

    try {
        decoder.initialize();

        PayloadWriter request;
        request.str(scannerId_);
        settingsFromCapabilities().write(request);
        pipe_.sendFrame(MsgType::ScanRequest, kStreamId, request);

        std::vector<uint8_t> pageBuffer;
        PageHeader header;
        Crc32 crc;
        bool receiving = false;
        int creditsConsumed = 0;

        const PixType target = settingsFromCapabilities().pixelType;

        while (!cancelRequested_.load()) {
            Frame frame = pipe_.readFrame(120000);

            switch (frame.type) {
                case MsgType::ScanStart: {
                    PayloadReader r = frame.reader();
                    jobId_ = r.u32();
                    RS_LOG_INFO("scan job %u started", jobId_);
                    break;
                }

                case MsgType::ScanPageBegin: {
                    PayloadReader r = frame.reader();
                    header = PageHeader::read(r);
                    if (header.encodedLength < 0 || header.encodedLength > (256 * 1024 * 1024))
                        throw ProtocolError("page declares an implausible encoded length");
                    pageBuffer.clear();
                    pageBuffer.reserve(static_cast<size_t>(header.encodedLength));
                    crc = Crc32();
                    receiving = true;
                    break;
                }

                case MsgType::ScanPageData: {
                    if (!receiving) break;
                    PayloadReader r = frame.reader();
                    r.u32();                       // job id
                    r.i32();                       // page number
                    r.i64();                       // offset (sequential; kept for resync tooling)
                    std::vector<uint8_t> chunk = r.blob();
                    crc.append(chunk.data(), chunk.size());
                    pageBuffer.insert(pageBuffer.end(), chunk.begin(), chunk.end());

                    // Return flow-control credit so the sender may continue.
                    if (++creditsConsumed >= kDefaultCreditWindow / 2) {
                        PayloadWriter credit;
                        credit.u32(jobId_);
                        credit.i32(creditsConsumed);
                        pipe_.sendFrame(MsgType::FlowCredit, kStreamId, credit);
                        creditsConsumed = 0;
                    }
                    break;
                }

                case MsgType::ScanPageEnd: {
                    if (!receiving) break;
                    PayloadReader r = frame.reader();
                    r.u32();
                    const int32_t pageNumber = r.i32();
                    const uint32_t expected = r.u32();
                    receiving = false;

                    if (crc.finish() != expected) {
                        error = ScanError::TransportFailure;
                        errorText = "page " + std::to_string(pageNumber) + " failed its integrity check";
                        goto finished;
                    }

                    DecodedPage page = decoder.decode(pageBuffer, header, target);
                    pageBuffer.clear();
                    pageBuffer.shrink_to_fit();

                    {
                        std::unique_lock<std::mutex> lock(queueMutex_);
                        queueSignal_.wait(lock, [this] {
                            return readyPages_.size() < kMaxQueuedPages || cancelRequested_.load();
                        });
                        if (cancelRequested_.load()) goto finished;
                        readyPages_.push_back(std::move(page));
                    }
                    queueSignal_.notify_all();

                    // The first completed page is what moves the application to state 6.
                    if (pageNumber == 1) notifyApplication(MSG_XFERREADY);
                    break;
                }

                case MsgType::ScanComplete: {
                    RS_LOG_INFO("scan job %u complete", jobId_);
                    goto finished;
                }

                case MsgType::ScanError: {
                    PayloadReader r = frame.reader();
                    r.u32();
                    error = static_cast<ScanError>(r.u16());
                    errorText = r.str();
                    RS_LOG_ERROR("scan job failed: %s", errorText.c_str());
                    goto finished;
                }

                case MsgType::ScanProgress:
                case MsgType::Heartbeat:
                    break;

                case MsgType::Disconnect: {
                    error = ScanError::SessionLost;
                    errorText = "the agent closed the connection";
                    goto finished;
                }

                default:
                    break;
            }
        }
    }
    catch (const std::exception& ex) {
        error = ScanError::TransportFailure;
        errorText = ex.what();
        RS_LOG_ERROR("worker aborted: %s", errorText.c_str());
    }

finished:
    decoder.shutdown();

    if (error != ScanError::None) {
        jobError_ = error;
        jobErrorText_ = errorText;
        jobFailed_.store(true);
    }
    jobRunning_.store(false);
    queueSignal_.notify_all();

    // If nothing was ever delivered the application is still sitting in state 5 waiting for
    // MSG_XFERREADY; wake it so it can observe the failure instead of hanging.
    if (readyPages_.empty() && !havePage_)
        notifyApplication(error != ScanError::None ? MSG_CLOSEDSREQ : MSG_XFERREADY);
}

void DataSource::notifyApplication(TW_UINT16 message) {
    // Published before the wake-up below, so an application that reaches DAT_EVENT as a
    // result of it cannot find the message not yet there. The atomic is what orders the two:
    // this runs on the acquisition worker while the application reads on its own thread.
    pendingMessage_.store(message, std::memory_order_release);

    // Wake the application's message loop.
    //
    // A TWAIN 1.x application learns nothing by itself: it blocks in GetMessage and only
    // calls DG_CONTROL/DAT_EVENT/MSG_PROCESSEVENT for messages that arrive. If the source
    // merely sets a flag, an application whose window is idle never pumps another message,
    // never asks, and waits for a page that is already sitting here — the scan completes on
    // the client PC and the application hangs anyway. Posting to the window the application
    // handed us in TW_USERINTERFACE.hParent is what every hardware source does, and WM_NULL
    // is enough: it carries no meaning, is ignored by every window procedure, and its only
    // job is to make GetMessage return so the application asks us what happened.
    // hParent is a TW_HANDLE in the TWAIN structures; on Windows it is the application's HWND.
    auto parent = static_cast<HWND>(userInterface_.hParent);
    if (parent && IsWindow(parent)) PostMessageA(parent, WM_NULL, 0, 0);

    // TWAIN 2.x applications register a callback and never pump DAT_EVENT. Invoking it is
    // how they learn a page is ready; 1.x applications poll MSG_PROCESSEVENT instead and
    // pick the pending message up there.
    if (haveCallback_ && callback_.CallBackProc) {
        using CallbackProc = TW_UINT16(PASCAL*)(pTW_IDENTITY, pTW_IDENTITY, TW_UINT32,
                                                TW_UINT16, TW_UINT16, TW_MEMREF);
        auto proc = reinterpret_cast<CallbackProc>(callback_.CallBackProc);
        __try {
            proc(nullptr, &appIdentity_, DG_CONTROL, DAT_NULL, message,
                 reinterpret_cast<TW_MEMREF>(callback_.RefCon));
        }
        __except (EXCEPTION_EXECUTE_HANDLER) {
            // A faulting application callback must not take the host process down with it.
            RS_LOG_ERROR("application callback raised an exception; falling back to DAT_EVENT");
            haveCallback_ = false;
        }
    }
}

void DataSource::stopWorker(bool sendCancel) {
    if (sendCancel && jobRunning_.load()) {
        try {
            PayloadWriter cancel;
            cancel.u32(jobId_);
            pipe_.sendFrame(MsgType::ScanCancel, kStreamId, cancel);
        } catch (...) { /* the channel may already be gone; nothing useful to do */ }
    }

    cancelRequested_.store(true);
    queueSignal_.notify_all();

    if (worker_.joinable()) worker_.join();

    cancelRequested_.store(false);
    jobRunning_.store(false);

    std::lock_guard<std::mutex> lock(queueMutex_);
    readyPages_.clear();
}

bool DataSource::waitForPage() {
    std::unique_lock<std::mutex> lock(queueMutex_);
    queueSignal_.wait(lock, [this] {
        return !readyPages_.empty() || !jobRunning_.load() || cancelRequested_.load();
    });

    if (readyPages_.empty()) return false;

    currentPage_ = std::move(readyPages_.front());
    readyPages_.pop_front();
    havePage_ = true;
    memXferRow_ = 0;
    memFile_.clear();
    memFile_.shrink_to_fit();
    memFileOffset_ = 0;
    lock.unlock();
    queueSignal_.notify_all();
    return true;
}

// ------------------------------------------------------------ state changes

TW_UINT16 DataSource::onOpenDs(pTW_IDENTITY origin) {
    if (state_ != 3) return sequenceError();

    if (origin) {
        appIdentity_ = *origin;
        haveAppIdentity_ = true;
        RS_LOG_INFO("opened by application '%s'", origin->ProductName);
    }

    if (!connectToAgent()) {
        pipe_.close();
        return TWRC_FAILURE;   // connectToAgent already set the condition code
    }

    state_ = 4;
    return succeed();
}

TW_UINT16 DataSource::onCloseDs() {
    if (state_ != 4) return sequenceError();

    stopWorker(true);
    pipe_.close();
    caps_.clear();
    havePage_ = false;
    haveCallback_ = false;
    haveLayoutRegion_ = false;
    pagesTransferred_ = 0;
    state_ = 3;
    return succeed();
}

TW_UINT16 DataSource::onEnableDs(pTW_USERINTERFACE ui, bool uiOnly) {
    if (state_ != 4) return sequenceError();
    if (!ui) return fail(TWCC_BADVALUE);

    // We deliberately do not advertise CAP_ENABLEDSUIONLY: there is no settings-only dialog
    // to show on the server, and pretending otherwise would leave the application waiting
    // for a window that never appears.
    if (uiOnly) return fail(TWCC_CAPBADOPERATION, "MSG_ENABLEDSUIONLY is not supported");

    userInterface_ = *ui;
    pendingMessage_.store(MSG_NULL, std::memory_order_relaxed);
    pagesTransferred_ = 0;
    havePage_ = false;
    jobFailed_.store(false);
    jobError_ = ScanError::None;
    jobErrorText_.clear();
    cancelRequested_.store(false);
    jobRunning_.store(true);

    // ShowUI==TRUE means the *physical* scanner's own driver dialog is shown, on the user's
    // local PC where that scanner actually is. There is no meaningful way to render a
    // vendor's dialog inside the remote session, and this is what the user expects anyway.
    worker_ = std::thread([this] { workerLoop(); });

    state_ = 5;

    // The settings the application has actually chosen, at the moment they stop changing.
    // Transfer mechanism and file format decide how the page is handed back and in what
    // format - the two things an application can disagree with the source about while every
    // individual call still reports success, leaving the user with no image and no error.
    const uint32_t mech = caps_.current(ICAP_XFERMECH, TWSX_NATIVE);
    const uint32_t format = caps_.current(ICAP_IMAGEFILEFORMAT, TWFF_BMP);
    const char* mechName = mech == TWSX_NATIVE ? "native"
                         : mech == TWSX_MEMORY ? "memory"
                         : mech == TWSX_FILE ? "file"
                         : mech == TWSX_MEMFILE ? "memory-file" : "unknown";
    const char* formatName = format == TWFF_BMP ? "BMP"
                           : format == TWFF_TIFF ? "TIFF"
                           : format == TWFF_JFIF ? "JPEG"
                           : format == TWFF_PNG ? "PNG" : "unknown";

    RS_LOG_INFO("enabled (ShowUI=%d): transfer=%s (%u), file format=%s (%u), pixel type=%u, %d dpi",
                userInterface_.ShowUI ? 1 : 0, mechName, mech, formatName, format,
                caps_.current(ICAP_PIXELTYPE, TWPT_RGB),
                static_cast<int>(caps_.currentFix32(ICAP_XRESOLUTION, 300.0) + 0.5));
    return succeed();
}

TW_UINT16 DataSource::onDisableDs() {
    if (state_ != 5) return sequenceError();

    stopWorker(true);
    havePage_ = false;
    state_ = 4;
    return succeed();
}

TW_UINT16 DataSource::onProcessEvent(pTW_EVENT event) {
    if (!inState(5, 7)) return sequenceError();
    if (!event) return fail(TWCC_BADVALUE);

    // Taken and cleared in one step: two threads could otherwise both see the same message
    // and the second would wait on a page the first already collected.
    const TW_UINT16 pending = pendingMessage_.exchange(MSG_NULL, std::memory_order_acquire);

    if (pending != MSG_NULL) {
        event->TWMessage = pending;

        if (event->TWMessage == MSG_XFERREADY && state_ == 5) {
            if (waitForPage()) {
                state_ = 6;
            } else {
                // The job ended without producing anything; tell the application to close
                // rather than leaving it waiting for an image that will never arrive.
                event->TWMessage = MSG_CLOSEDSREQ;
            }
        }
        return TWRC_DSEVENT;
    }

    // We own no windows inside the host, so every message belongs to the application.
    event->TWMessage = MSG_NULL;
    return TWRC_NOTDSEVENT;
}

TW_UINT16 DataSource::onPendingXfers(TW_UINT16 msg, pTW_PENDINGXFERS pending) {
    if (!pending) return fail(TWCC_BADVALUE);

    switch (msg) {
        case MSG_GET: {
            if (!inState(4, 7)) return sequenceError();
            bool more = havePage_;
            if (!more) {
                std::lock_guard<std::mutex> lock(queueMutex_);
                more = !readyPages_.empty() || jobRunning_.load();
            }
            // 0xFFFF is TWAIN's "more available, count unknown" — correct for an ADF whose
            // page count nobody knows until the tray runs out.
            pending->Count = more ? static_cast<TW_UINT16>(0xFFFF) : 0;
            return succeed();
        }

        case MSG_ENDXFER: {
            if (!inState(6, 7)) return sequenceError();

            havePage_ = false;
            currentPage_ = DecodedPage{};
            memXferRow_ = 0;
            memFile_.clear();
            memFile_.shrink_to_fit();
            memFileOffset_ = 0;

            if (waitForPage()) {
                pending->Count = static_cast<TW_UINT16>(0xFFFF);
                state_ = 6;
            } else {
                pending->Count = 0;
                state_ = 5;
                if (jobFailed_.load() && jobError_ != ScanError::UserCancelled) {
                    RS_LOG_WARN("job ended with error: %s", jobErrorText_.c_str());
                }
            }
            return succeed();
        }

        case MSG_RESET: {
            if (state_ != 6) return sequenceError();
            stopWorker(true);
            havePage_ = false;
            pending->Count = 0;
            state_ = 5;
            return succeed();
        }

        case MSG_STOPFEEDER: {
            if (state_ != 6) return sequenceError();
            // Let the pages already scanned drain, but stop pulling new sheets.
            try {
                PayloadWriter cancel;
                cancel.u32(jobId_);
                pipe_.sendFrame(MsgType::ScanCancel, kStreamId, cancel);
            } catch (...) { /* channel already gone */ }
            pending->Count = static_cast<TW_UINT16>(0xFFFF);
            return succeed();
        }

        default:
            return fail(TWCC_BADPROTOCOL);
    }
}

// ---------------------------------------------------------------- image data

TW_UINT16 DataSource::onImageInfo(pTW_IMAGEINFO info) {
    if (!inState(6, 7)) return sequenceError();
    if (!info) return fail(TWCC_BADVALUE);
    if (!havePage_) return fail(TWCC_SEQERROR);

    ZeroMemory(info, sizeof(TW_IMAGEINFO));
    info->XResolution = toFix32(currentPage_.dpiX);
    info->YResolution = toFix32(currentPage_.dpiY);
    info->ImageWidth = currentPage_.width;
    info->ImageLength = currentPage_.height;
    info->Compression = TWCP_NONE;
    info->Planar = 0;

    switch (currentPage_.pixelType) {
        case PixType::BlackWhite:
            info->SamplesPerPixel = 1;
            info->BitsPerSample[0] = 1;
            info->BitsPerPixel = 1;
            info->PixelType = TWPT_BW;
            break;
        case PixType::Grayscale:
            info->SamplesPerPixel = 1;
            info->BitsPerSample[0] = 8;
            info->BitsPerPixel = 8;
            info->PixelType = TWPT_GRAY;
            break;
        default:
            info->SamplesPerPixel = 3;
            info->BitsPerSample[0] = 8;
            info->BitsPerSample[1] = 8;
            info->BitsPerSample[2] = 8;
            info->BitsPerPixel = 24;
            info->PixelType = TWPT_RGB;
            break;
    }
    return succeed();
}

TW_UINT16 DataSource::onImageLayout(TW_UINT16 msg, pTW_IMAGELAYOUT layout) {
    if (!layout) return fail(TWCC_BADVALUE);

    switch (msg) {
        case MSG_GET:
        case MSG_GETDEFAULT:
        case MSG_GETCURRENT: {
            if (!inState(4, 7)) return sequenceError();
            ZeroMemory(layout, sizeof(TW_IMAGELAYOUT));
            layout->Frame.Left = toFix32(0);
            layout->Frame.Top = toFix32(0);
            if (havePage_ && currentPage_.dpiX > 0 && currentPage_.dpiY > 0) {
                layout->Frame.Right = toFix32(static_cast<double>(currentPage_.width) / currentPage_.dpiX);
                layout->Frame.Bottom = toFix32(static_cast<double>(currentPage_.height) / currentPage_.dpiY);
            } else {
                layout->Frame.Right = toFix32(scannerCaps_.maxWidthThou / 1000.0);
                layout->Frame.Bottom = toFix32(scannerCaps_.maxHeightThou / 1000.0);
            }
            layout->DocumentNumber = 1;
            layout->PageNumber = static_cast<TW_UINT32>(pagesTransferred_ + 1);
            layout->FrameNumber = 1;
            return succeed();
        }

        case MSG_RESET:
            // "Go back to the default", and the default is the whole page — which is exactly
            // what this source does when no region has been set. Refusing it was simply wrong:
            // the state the application asked for was the state it was already in, and the
            // refusal surfaced to the user as "TWAIN error: CapUnsupported" after a scan that
            // had in fact worked.
            if (state_ != 4) return sequenceError();
            haveLayoutRegion_ = false;
            RS_LOG_DEBUG("image layout reset to the full page");
            return succeed();

        case MSG_SET: {
            if (state_ != 4) return sequenceError();

            const double left = fromFix32(layout->Frame.Left);
            const double top = fromFix32(layout->Frame.Top);
            const double right = fromFix32(layout->Frame.Right);
            const double bottom = fromFix32(layout->Frame.Bottom);

            if (right <= left || bottom <= top) return fail(TWCC_BADVALUE);

            // A frame that starts at the corner is a page size, and a page size is something
            // the scanner on the other end can genuinely apply — it goes across as a custom
            // page size. An offset crop is not expressible in the protocol, and pretending to
            // honour it would hand back a full page while the application believed it had
            // asked for a region. Declining is the honest answer for that case only.
            const double cornerTolerance = 0.01;   // inches; a quarter of a millimetre
            if (left > cornerTolerance || top > cornerTolerance) {
                RS_LOG_INFO("declining an offset crop (%.2f,%.2f)-(%.2f,%.2f) inches: only a "
                            "page size can be applied by the scanner",
                            left, top, right, bottom);
                return fail(TWCC_CAPUNSUPPORTED);
            }

            haveLayoutRegion_ = true;
            layoutWidthThou_ = static_cast<int32_t>((right - left) * 1000.0 + 0.5);
            layoutHeightThou_ = static_cast<int32_t>((bottom - top) * 1000.0 + 0.5);

            RS_LOG_INFO("image layout set to %d x %d thousandths of an inch",
                        layoutWidthThou_, layoutHeightThou_);
            return succeed();
        }

        default:
            return fail(TWCC_BADPROTOCOL);
    }
}

/// DG_IMAGE / DAT_EXTIMAGEINFO / MSG_GET — per-page extras (bar codes, patch codes, page
/// side, deskew angles) that an application asks for once a page has been transferred.
///
/// We carry none of them: the protocol moves pixels and page geometry, and nothing on the
/// far side produces this information. That is not a reason to fail the operation. TWAIN
/// gives every requested item its own ReturnCode precisely so a source can say "I have
/// nothing for that one" item by item while the operation itself succeeds — which is what an
/// application asking after a completed transfer needs to hear. Refusing outright is how a
/// scan that has already worked ends in "TWAIN error: CapUnsupported": NAPS2 asks after the
/// last byte of the page has arrived, and the refusal lands on the user's screen instead of
/// the image.
TW_UINT16 DataSource::onExtImageInfo(pTW_EXTIMAGEINFO info) {
    // State 7 is where the specification puts this; state 6 is accepted because some
    // applications ask before starting the transfer and refusing gains nothing.
    if (!inState(6, 7)) return sequenceError();
    if (!info) return fail(TWCC_BADVALUE);

    // The count comes from the application and indexes the array walked below, so it is
    // bounded before use: a wild value here would read off the end of somebody else's buffer.
    constexpr TW_UINT32 kMaxInfos = 256;
    if (info->NumInfos > kMaxInfos)
        return fail(TWCC_BADVALUE, "implausible extended image info request");

    for (TW_UINT32 i = 0; i < info->NumInfos; ++i) {
        TW_INFO& item = info->Info[i];
        item.ItemType = TWTY_UINT32;
        item.NumItems = 0;
        item.Item = 0;
        item.ReturnCode = TWRC_INFONOTSUPPORTED;
    }

    RS_LOG_DEBUG("extended image info: %lu item(s) requested, none available",
                 static_cast<unsigned long>(info->NumInfos));
    return succeed();
}

/// An operation this source does not implement.
///
/// TWCC_BADPROTOCOL, not TWCC_CAPUNSUPPORTED: the latter means "that capability does not
/// exist", and applications render it as a capability error even when no capability was
/// involved — which is how an unimplemented DAT after a transfer reaches the user as
/// "TWAIN error: CapUnsupported" moments after a scan that worked. The triplet is logged
/// because the next unimplemented operation someone hits should be identifiable from the
/// log alone, without a debugger.
TW_UINT16 DataSource::unhandledOperation(TW_UINT32 dg, TW_UINT16 dat, TW_UINT16 msg) {
    char detail[128]{};
    wsprintfA(detail, "unimplemented operation dg=0x%04lX dat=0x%04X msg=0x%04X",
              static_cast<unsigned long>(dg), dat, msg);
    return fail(TWCC_BADPROTOCOL, detail);
}

TW_UINT16 DataSource::onNativeXfer(TW_MEMREF data) {
    if (state_ != 6) return sequenceError();
    if (!data) return fail(TWCC_BADVALUE);
    if (!havePage_) return fail(TWCC_SEQERROR);

    const size_t size = currentPage_.dibSize();
    TW_HANDLE handle = memory_.alloc(static_cast<TW_UINT32>(size));
    if (!handle) return fail(TWCC_LOWMEMORY);

    auto* bits = static_cast<uint8_t*>(memory_.acquire(handle));
    if (!bits) {
        memory_.release(handle);
        return fail(TWCC_LOWMEMORY);
    }

    PageDecoder::writeDib(currentPage_, bits);
    memory_.relinquish(handle);

    // TWAIN declares this parameter as pTW_UINT32, but on 64-bit builds the DSM and every
    // real application pass a pointer to a pointer-sized handle. Writing a TW_HANDLE is
    // what the TWAIN reference data source does and what interoperates in practice.
    *static_cast<TW_HANDLE*>(data) = handle;

    state_ = 7;
    ++pagesTransferred_;
    RS_LOG_DEBUG("native transfer of page %d (%zu bytes)", currentPage_.pageNumber, size);
    return TWRC_XFERDONE;
}

TW_UINT16 DataSource::onMemoryXfer(pTW_IMAGEMEMXFER xfer) {
    if (!inState(6, 7)) return sequenceError();
    if (!xfer) return fail(TWCC_BADVALUE);
    if (!havePage_) return fail(TWCC_SEQERROR);

    const uint32_t stride = static_cast<uint32_t>(currentPage_.stride());
    const uint32_t totalRows = static_cast<uint32_t>(currentPage_.height);

    if (memXferRow_ >= totalRows) return fail(TWCC_SEQERROR);
    if (xfer->Memory.TheMem == nullptr || xfer->Memory.Length < stride)
        return fail(TWCC_BADVALUE, "application buffer is smaller than one scanline");

    const uint32_t rowsThatFit = xfer->Memory.Length / stride;
    const uint32_t rows = (std::min)(rowsThatFit, totalRows - memXferRow_);

    // Two conversions happen here, and both are the difference between a correct page and a
    // wrong one that still measures as the right size.
    //
    // Row order: the page is held bottom-up, the way a DIB is, because that is what native
    // transfer hands over unchanged. Memory transfer is defined top-down, so rows come off the
    // far end of the buffer.
    //
    // Channel order: a Windows DIB stores 24-bit pixels as B,G,R. TWAIN memory transfer of
    // TWPT_RGB is R,G,B - that byte order is the definition of the pixel type, not a property
    // of the platform. Copying the DIB row straight through delivers an image whose reds and
    // blues are exchanged: copper (200,120,60) arrives as (60,120,200) and the page looks
    // blue. Nothing detects it by size, geometry or return code, and a probe that writes the
    // bytes back out as a BMP - which is itself BGR - reproduces the mistake and looks right.
    const bool swapRedAndBlue = currentPage_.pixelType == PixType::Rgb && currentPage_.bitsPerPixel == 24;

    auto* dest = static_cast<uint8_t*>(xfer->Memory.TheMem);
    for (uint32_t i = 0; i < rows; ++i) {
        const uint32_t sourceRow = totalRows - 1 - (memXferRow_ + i);
        const uint8_t* source = currentPage_.bits.data() + static_cast<size_t>(sourceRow) * stride;
        uint8_t* target = dest + static_cast<size_t>(i) * stride;

        if (!swapRedAndBlue) {
            memcpy(target, source, stride);
            continue;
        }

        const uint32_t pixels = static_cast<uint32_t>(currentPage_.width);
        for (uint32_t x = 0; x < pixels; ++x) {
            target[x * 3 + 0] = source[x * 3 + 2];
            target[x * 3 + 1] = source[x * 3 + 1];
            target[x * 3 + 2] = source[x * 3 + 0];
        }
        // Whatever padding the stride carries beyond the pixels is copied as-is.
        if (stride > pixels * 3)
            memcpy(target + pixels * 3, source + pixels * 3, stride - pixels * 3);
    }

    xfer->Compression = TWCP_NONE;
    xfer->BytesPerRow = stride;
    xfer->Columns = static_cast<TW_UINT32>(currentPage_.width);
    xfer->Rows = rows;
    xfer->XOffset = 0;
    xfer->YOffset = memXferRow_;
    xfer->BytesWritten = rows * stride;

    memXferRow_ += rows;

    if (memXferRow_ >= totalRows) {
        state_ = 7;
        ++pagesTransferred_;
        return TWRC_XFERDONE;
    }

    state_ = 7;
    return TWRC_SUCCESS;
}

/// Encodes the current page as a complete image file, in memory.
///
/// The same bytes serve DAT_IMAGEFILEXFER, which writes them to a path, and
/// DAT_IMAGEMEMFILEXFER, which hands them to the application in pieces. Sharing the encoder is
/// what stops those two mechanisms drifting into producing different pictures from one page.
void DataSource::encodeCurrentPage(uint32_t format, std::vector<uint8_t>& out) const {
    out.clear();
    auto put = [&out](const void* data, size_t length) {
        const auto* bytes = static_cast<const uint8_t*>(data);
        out.insert(out.end(), bytes, bytes + length);
    };

    if (format == TWFF_BMP) {
        const size_t dibSize = currentPage_.dibSize();
        std::vector<uint8_t> dib(dibSize);
        PageDecoder::writeDib(currentPage_, dib.data());

        BITMAPFILEHEADER header{};
        header.bfType = 0x4D42;                                  // 'BM'
        header.bfSize = static_cast<DWORD>(sizeof(header) + dibSize);
        header.bfOffBits = static_cast<DWORD>(sizeof(header) + sizeof(BITMAPINFOHEADER)
                                              + currentPage_.paletteEntries() * sizeof(RGBQUAD));
        out.reserve(sizeof(header) + dibSize);
        put(&header, sizeof(header));
        put(dib.data(), dibSize);
    } else {
        // Baseline uncompressed TIFF. Enough tags for every reader we care about; the
        // channel already compressed the page, so re-compressing here buys nothing.
        writeUncompressedTiff(put, currentPage_);
    }
}

/// Writes the current page to the path the application set with DAT_SETUPFILEXFER.
/// Only formats we can emit without pulling COM into the host thread are advertised.
TW_UINT16 DataSource::onFileXfer() {
    if (state_ != 6) return sequenceError();
    if (!havePage_) return fail(TWCC_SEQERROR);
    if (fileXfer_.FileName[0] == '\0') return fail(TWCC_BADVALUE, "no output file was set");

    std::vector<uint8_t> encoded;
    encodeCurrentPage(caps_.current(ICAP_IMAGEFILEFORMAT, TWFF_BMP), encoded);

    HANDLE file = CreateFileA(fileXfer_.FileName, GENERIC_WRITE, 0, nullptr,
                              CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE)
        return fail(TWCC_OPERATIONERROR, "cannot create the requested output file");

    DWORD written = 0;
    const bool ok = WriteFile(file, encoded.data(), static_cast<DWORD>(encoded.size()),
                              &written, nullptr) && written == encoded.size();
    CloseHandle(file);
    if (!ok) return fail(TWCC_OPERATIONERROR, "failed writing the output file");

    state_ = 7;
    ++pagesTransferred_;
    return TWRC_XFERDONE;
}

/// DG_IMAGE / DAT_IMAGEMEMFILEXFER / MSG_GET — memory-file transfer.
///
/// This is what NAPS2 asks for, and its absence was the whole reported fault: the page scanned,
/// arrived, decoded, and was then never handed over because the driver refused the one
/// operation the application uses to collect it. The scan looked like it worked because it had,
/// right up to this call.
///
/// It is neither of the two mechanisms it sounds like a blend of. Memory transfer hands over
/// raw scanlines with a stride, strip by strip. This hands over a complete image *file* - the
/// same bytes DAT_IMAGEFILEXFER would put on disk - through the application's buffer instead of
/// a path, in as many pieces as the buffer needs. So the geometry fields are meaningless here
/// and the specification has them left at zero; only BytesWritten describes the piece.
TW_UINT16 DataSource::onMemoryFileXfer(pTW_IMAGEMEMXFER xfer) {
    if (!inState(6, 7)) return sequenceError();
    if (!xfer) return fail(TWCC_BADVALUE);
    if (!havePage_) return fail(TWCC_SEQERROR);
    if (xfer->Memory.TheMem == nullptr || xfer->Memory.Length == 0)
        return fail(TWCC_BADVALUE, "no buffer supplied for the memory-file transfer");

    // Encoded once, on the first call for this page, and then drained across as many calls as
    // the application's buffer size requires.
    if (memFile_.empty()) {
        const uint32_t format = caps_.current(ICAP_IMAGEFILEFORMAT, TWFF_BMP);
        encodeCurrentPage(format, memFile_);
        memFileOffset_ = 0;
        RS_LOG_INFO("memory-file transfer of page %d: %zu bytes as %s, %lu byte buffers",
                    currentPage_.pageNumber, memFile_.size(),
                    format == TWFF_BMP ? "BMP" : "TIFF",
                    static_cast<unsigned long>(xfer->Memory.Length));
        if (memFile_.empty()) return fail(TWCC_OPERATIONERROR, "the page could not be encoded");
    }

    const size_t remaining = memFile_.size() - memFileOffset_;
    const size_t chunk = (std::min)(static_cast<size_t>(xfer->Memory.Length), remaining);
    memcpy(xfer->Memory.TheMem, memFile_.data() + memFileOffset_, chunk);
    memFileOffset_ += chunk;

    xfer->Compression = TWCP_NONE;
    xfer->BytesPerRow = 0;
    xfer->Columns = 0;
    xfer->Rows = 0;
    xfer->XOffset = 0;
    xfer->YOffset = 0;
    xfer->BytesWritten = static_cast<TW_UINT32>(chunk);

    state_ = 7;

    if (memFileOffset_ >= memFile_.size()) {
        ++pagesTransferred_;
        memFile_.clear();
        memFile_.shrink_to_fit();
        memFileOffset_ = 0;
        return TWRC_XFERDONE;
    }
    return TWRC_SUCCESS;
}

TW_UINT16 DataSource::onSetupFileXfer(TW_UINT16 msg, pTW_SETUPFILEXFER setup) {
    if (!inState(4, 6)) return sequenceError();
    if (!setup) return fail(TWCC_BADVALUE);

    switch (msg) {
        case MSG_GET:
        case MSG_GETDEFAULT:
            *setup = fileXfer_;
            return succeed();

        case MSG_SET: {
            // The path arrives from the application, but it is still untrusted input as far
            // as this DLL is concerned; reject anything that is not a plain local path.
            const size_t length = strnlen(setup->FileName, sizeof(setup->FileName));
            if (length == 0 || length >= sizeof(setup->FileName))
                return fail(TWCC_BADVALUE);
            if (strstr(setup->FileName, "..") != nullptr)
                return fail(TWCC_BADVALUE, "relative path segments are not accepted");
            fileXfer_ = *setup;
            return succeed();
        }

        case MSG_RESET:
            ZeroMemory(&fileXfer_, sizeof(fileXfer_));
            fileXfer_.Format = TWFF_BMP;
            return succeed();

        default:
            return fail(TWCC_BADPROTOCOL);
    }
}

// ------------------------------------------------------------------ dispatch

TW_UINT16 DataSource::dispatch(pTW_IDENTITY origin, TW_UINT32 dg, TW_UINT16 dat,
                               TW_UINT16 msg, TW_MEMREF data) {
    if (dg == DG_CONTROL) {
        switch (dat) {
            case DAT_IDENTITY:
                switch (msg) {
                    case MSG_GET: {
                        auto* identity = static_cast<pTW_IDENTITY>(data);
                        if (!identity) return fail(TWCC_BADVALUE);

                        // The name the application will show in its scanner list. Logged at
                        // INFO because "the driver is installed but does not appear" is the
                        // hardest failure to diagnose from outside, and this one line
                        // separates "the manager never asked us" from "it asked and we
                        // answered" - which are completely different problems.
                        const std::string name = productName();

                        // Announce the protocol level the caller can actually use.
                        //
                        // In practice this answers 1.9, and that is the intended outcome:
                        // TWAINDSM.dll asks this question during its directory scan with a
                        // zeroed origin - measured, it arrives as ProductName "" and
                        // SupportedGroups 0 - so there is no caller to adapt to at the one
                        // moment the answer is recorded, and the conservative reply is the
                        // right one. A source that declares 1.9 is a first-class citizen of a
                        // 2.x manager, which bridges it to 2.x applications; one that declares
                        // 2.4 to a manager that turns out to be 1.x is skipped outright.
                        //
                        // The branch is kept for managers that do pass the application's
                        // identity through. Nothing is lost either way: DsmMemory uses the
                        // global heap when DAT_ENTRYPOINT never arrives, which is the normal
                        // path at 1.9, and the DSM's allocator when it does.
                        const bool callerSpeaksTwain2 =
                            origin != nullptr && (origin->SupportedGroups & DF_APP2) != 0;

                        ZeroMemory(identity, sizeof(TW_IDENTITY));
                        identity->Id = 0;
                        identity->ProtocolMajor = callerSpeaksTwain2 ? kProtocolMajor : 1;
                        identity->ProtocolMinor = callerSpeaksTwain2 ? kProtocolMinor : 9;
                        identity->SupportedGroups =
                            DG_IMAGE | DG_CONTROL | (callerSpeaksTwain2 ? DF_DS2 : 0);
                        identity->Version.MajorNum = 1;
                        identity->Version.MinorNum = 0;
                        identity->Version.Language = TWLG_ENGLISH_USA;
                        identity->Version.Country = TWCY_USA;
                        setStr32(identity->Version.Info, "1.0.0");
                        setStr32(identity->Manufacturer, "RemoteScanner");
                        setStr32(identity->ProductFamily, "Remote Scanner");
                        setStr32(identity->ProductName, name);

                        RS_LOG_INFO("identity requested by \"%s\" (groups 0x%08lX); reporting \"%s\" "
                                    "as TWAIN %u.%u, session %u",
                                    origin && origin->ProductName[0] ? origin->ProductName : "(unnamed)",
                                    static_cast<unsigned long>(origin ? origin->SupportedGroups : 0),
                                    name.c_str(), identity->ProtocolMajor, identity->ProtocolMinor,
                                    currentSessionId());
                        return succeed();
                    }
                    case MSG_OPENDS:  return onOpenDs(origin);
                    case MSG_CLOSEDS: return onCloseDs();
                    default:          return fail(TWCC_BADPROTOCOL);
                }

            case DAT_ENTRYPOINT:
                if (msg != MSG_SET) return fail(TWCC_BADPROTOCOL);
                if (state_ != 3) return sequenceError();
                if (!data) return fail(TWCC_BADVALUE);
                setEntryPoints(static_cast<pTW_ENTRYPOINT>(data));
                return succeed();

            case DAT_CAPABILITY:
                return onCapability(msg, static_cast<pTW_CAPABILITY>(data));

            case DAT_USERINTERFACE:
                switch (msg) {
                    case MSG_ENABLEDS:       return onEnableDs(static_cast<pTW_USERINTERFACE>(data), false);
                    case MSG_ENABLEDSUIONLY: return onEnableDs(static_cast<pTW_USERINTERFACE>(data), true);
                    case MSG_DISABLEDS:      return onDisableDs();
                    default:                 return fail(TWCC_BADPROTOCOL);
                }

            case DAT_EVENT:
                if (msg != MSG_PROCESSEVENT) return fail(TWCC_BADPROTOCOL);
                return onProcessEvent(static_cast<pTW_EVENT>(data));

            case DAT_PENDINGXFERS:
                return onPendingXfers(msg, static_cast<pTW_PENDINGXFERS>(data));

            case DAT_SETUPFILEXFER:
                return onSetupFileXfer(msg, static_cast<pTW_SETUPFILEXFER>(data));

            case DAT_SETUPMEMXFER: {
                if (msg != MSG_GET) return fail(TWCC_BADPROTOCOL);
                if (!inState(4, 6)) return sequenceError();
                auto* setup = static_cast<pTW_SETUPMEMXFER>(data);
                if (!setup) return fail(TWCC_BADVALUE);
                // One scanline minimum; 64 KB preferred keeps strips comfortably inside the
                // application's usual buffer sizes without excessive round trips.
                const uint32_t stride = havePage_ ? static_cast<uint32_t>(currentPage_.stride()) : 4096;
                setup->MinBufSize = stride;
                setup->MaxBufSize = 4u * 1024u * 1024u;
                setup->Preferred = (std::max)(stride, 64u * 1024u);
                return succeed();
            }

            case DAT_XFERGROUP: {
                if (!inState(4, 6)) return sequenceError();
                auto* group = static_cast<TW_UINT32*>(data);
                if (!group) return fail(TWCC_BADVALUE);

                if (msg == MSG_GET) {
                    *group = DG_IMAGE;
                    return succeed();
                }
                // MSG_SET exists so an application can pick between image and audio on a
                // source that does both. This one is images only, so the single legal value
                // is the one we already report.
                if (msg == MSG_SET)
                    return *group == DG_IMAGE ? succeed() : fail(TWCC_BADVALUE);

                return fail(TWCC_BADPROTOCOL);
            }

            case DAT_STATUS: {
                if (msg != MSG_GET) return fail(TWCC_BADPROTOCOL);
                auto* status = static_cast<pTW_STATUS>(data);
                if (!status) return fail(TWCC_BADVALUE);
                status->ConditionCode = conditionCode_;
                status->Data = 0;
                conditionCode_ = TWCC_SUCCESS;      // reading the status clears it
                return TWRC_SUCCESS;
            }

            case DAT_STATUSUTF8: {
                if (msg != MSG_GET) return fail(TWCC_BADPROTOCOL);
                auto* status = static_cast<pTW_STATUSUTF8>(data);
                if (!status) return fail(TWCC_BADVALUE);
                const std::string& text = statusText_;
                TW_HANDLE handle = memory_.alloc(static_cast<TW_UINT32>(text.size() + 1));
                if (!handle) return fail(TWCC_LOWMEMORY);
                if (auto* buffer = static_cast<char*>(memory_.acquire(handle))) {
                    memcpy(buffer, text.c_str(), text.size() + 1);
                    memory_.relinquish(handle);
                }
                status->Size = static_cast<TW_UINT32>(text.size() + 1);
                status->UTF8string = handle;
                return TWRC_SUCCESS;
            }

            case DAT_CALLBACK:
            case DAT_CALLBACK2:
                if (msg != MSG_REGISTER_CALLBACK) return fail(TWCC_BADPROTOCOL);
                if (!inState(4, 7)) return sequenceError();
                if (!data) return fail(TWCC_BADVALUE);
                callback_ = *static_cast<pTW_CALLBACK2>(data);
                haveCallback_ = callback_.CallBackProc != nullptr;
                RS_LOG_DEBUG("application registered a TWAIN callback");
                return succeed();

            default:
                return unhandledOperation(dg, dat, msg);
        }
    }

    if (dg == DG_IMAGE) {
        switch (dat) {
            case DAT_IMAGEINFO:
                if (msg != MSG_GET) return fail(TWCC_BADPROTOCOL);
                return onImageInfo(static_cast<pTW_IMAGEINFO>(data));

            case DAT_IMAGELAYOUT:
                return onImageLayout(msg, static_cast<pTW_IMAGELAYOUT>(data));

            case DAT_IMAGENATIVEXFER:
                if (msg != MSG_GET) return fail(TWCC_BADPROTOCOL);
                return onNativeXfer(data);

            case DAT_IMAGEMEMXFER:
                if (msg != MSG_GET) return fail(TWCC_BADPROTOCOL);
                return onMemoryXfer(static_cast<pTW_IMAGEMEMXFER>(data));

            case DAT_IMAGEFILEXFER:
                if (msg != MSG_GET) return fail(TWCC_BADPROTOCOL);
                return onFileXfer();

            case DAT_IMAGEMEMFILEXFER:
                if (msg != MSG_GET) return fail(TWCC_BADPROTOCOL);
                return onMemoryFileXfer(static_cast<pTW_IMAGEMEMXFER>(data));

            case DAT_EXTIMAGEINFO:
                if (msg != MSG_GET) return fail(TWCC_BADPROTOCOL);
                return onExtImageInfo(static_cast<pTW_EXTIMAGEINFO>(data));

            default:
                return unhandledOperation(dg, dat, msg);
        }
    }

    return unhandledOperation(dg, dat, msg);
}

}  // namespace

// ------------------------------------------------------------------- exports

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID) {
    switch (reason) {
        case DLL_PROCESS_ATTACH:
            g_instance = static_cast<HINSTANCE>(module);
            DisableThreadLibraryCalls(module);
            Log::instance().init(L"twainds");
            RS_LOG_INFO("RemoteScanner data source loaded into pid %lu (%u-bit)",
                        GetCurrentProcessId(), static_cast<unsigned>(sizeof(void*) * 8));
            break;
        case DLL_PROCESS_DETACH:
            RS_LOG_INFO("RemoteScanner data source unloading");
            break;
        default:
            break;
    }
    return TRUE;
}

/// The single entry point every TWAIN Data Source must export.
///
/// The name and arity here are not interchangeable with DSM_Entry, and getting them wrong
/// fails in the most misleading way available:
///
///   * an application calls DSM_Entry, exported by the *manager* — six arguments, because
///     the application must name which source it means (pDest).
///   * the manager calls DS_Entry, exported by *this* file — five arguments, since a source
///     is only ever the destination of a call into itself.
///
/// TWAINDSM.dll does GetProcAddress(module, "DS_Entry") on every .ds it finds and, when that
/// returns null, unloads the library and moves on without surfacing anything to the
/// application. The source loads, initialises, logs that it loaded, and is then discarded in
/// silence — the application simply reports no scanners. Exporting DSM_Entry from a data
/// source therefore produces a component that passes every test you can write against it
/// directly, and is invisible to every real manager.
///
/// PASCAL (__stdcall) is required: undecorated in the .def so x86 does not export this as
/// _DS_Entry@20, which no manager would find.
extern "C" TW_UINT16 FAR PASCAL DS_Entry(pTW_IDENTITY pOrigin, TW_UINT32 DG, TW_UINT16 DAT,
                                         TW_UINT16 MSG, TW_MEMREF pData) {
    // Every call the manager makes, so an enumeration that never reaches DAT_IDENTITY is
    // visible rather than silent. Debug level: this is chatty during a transfer.
    RS_LOG_DEBUG("DS_Entry dg=0x%04X dat=0x%04X msg=0x%04X", DG, DAT, MSG);

    // Nothing may escape into the host application: a C++ exception crossing this boundary
    // would unwind through Acrobat's or an ERP's frames and take the process down.
    try {
        return g_ds.dispatch(pOrigin, DG, DAT, MSG, pData);
    }
    catch (const std::exception& ex) {
        RS_LOG_ERROR("unhandled exception in DS_Entry: %s", ex.what());
        return TWRC_FAILURE;
    }
    catch (...) {
        RS_LOG_ERROR("unhandled non-standard exception in DS_Entry");
        return TWRC_FAILURE;
    }
}
