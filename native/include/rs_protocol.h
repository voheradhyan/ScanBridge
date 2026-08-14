// rs_protocol.h — C++ mirror of ScanBridge.Protocol (Wire.cs / Codec.cs / Messages.cs).
//
// Header-only so both the data source and the DVC plugin can consume it without a shared
// library. Layouts here MUST match the C# side byte for byte; the round-trip test in
// tests/Unit/ProtocolInteropTests exists to catch drift.

#ifndef RS_PROTOCOL_H
#define RS_PROTOCOL_H

#include <windows.h>
#include <cstdint>
#include <cstring>
#include <string>
#include <vector>
#include <stdexcept>

namespace rs {

// ------------------------------------------------------------------ constants

constexpr uint8_t  kMagic = 0x52;            // 'R'
constexpr uint8_t  kVersion = 0x01;
constexpr size_t   kHeaderSize = 12;
constexpr uint32_t kMaxPayload = 32 * 1024;
constexpr uint32_t kMaxFrameSize = static_cast<uint32_t>(kHeaderSize) + kMaxPayload;
constexpr int      kDefaultCreditWindow = 64;
constexpr wchar_t  kDvcChannelNameW[] = L"ScanBridge";
constexpr char     kDvcChannelNameA[] = "ScanBridge";
constexpr wchar_t  kAgentPipeName[] = L"ScanBridge.Agent";
constexpr uint32_t kHeartbeatIntervalMs = 15000;
constexpr uint32_t kHeartbeatTimeoutMs = 45000;
constexpr size_t   kNonceLength = 32;
constexpr size_t   kMacLength = 32;

enum class MsgType : uint16_t {
    Hello = 0x01,
    HelloAck = 0x02,
    Authenticate = 0x03,
    AuthResult = 0x04,
    ScannerEnumRequest = 0x10,
    ScannerEnumResponse = 0x11,
    ScannerCapsRequest = 0x12,
    ScannerCapsResponse = 0x13,
    ScanRequest = 0x20,
    ScanStart = 0x21,
    ScanPageBegin = 0x22,
    ScanPageData = 0x23,
    ScanPageEnd = 0x24,
    ScanProgress = 0x25,
    ScanComplete = 0x26,
    ScanCancel = 0x27,
    ScanError = 0x28,
    FlowCredit = 0x30,
    Heartbeat = 0x40,
    Disconnect = 0x41,
};

enum class PeerRole : uint8_t {
    Unknown = 0, TwainDataSource = 1, SessionAgent = 2, DvcPlugin = 3, LocalAgent = 4,
};

enum class PeerCaps : uint32_t {
    None = 0, Duplex = 1u << 0, Adf = 1u << 1, Jpeg = 1u << 2, CcittG4 = 1u << 3,
    Png = 1u << 4, Raw = 1u << 5, FlowControl = 1u << 6, Cancellation = 1u << 7,
};

enum class ScannerIface : uint8_t { Unknown = 0, Twain = 1, Wia = 2 };
enum class ScannerState : uint8_t { Unknown = 0, Ready = 1, Busy = 2, Offline = 3, Error = 4 };

enum class ScannerFeature : uint32_t {
    None = 0, Flatbed = 1u << 0, Feeder = 1u << 1, Duplex = 1u << 2, Color = 1u << 3,
    Grayscale = 1u << 4, BlackWhite = 1u << 5, AutoPageSize = 1u << 6, AutoDeskew = 1u << 7,
    Brightness = 1u << 8, Contrast = 1u << 9, BlankPageRemoval = 1u << 10,
};

enum class PixType : uint8_t { BlackWhite = 0, Grayscale = 1, Rgb = 2 };
enum class PageSource : uint8_t { Auto = 0, Flatbed = 1, Feeder = 2 };
enum class PageSide : uint8_t { Front = 0, Back = 1 };
enum class PaperSize : uint8_t {
    Auto = 0, A4 = 1, A3 = 2, A5 = 3, Letter = 4, Legal = 5, Executive = 6,
    B4 = 7, B5 = 8, Custom = 255,
};
enum class PageOrientation : uint8_t { Portrait = 0, Landscape = 1 };
enum class PageEncoding : uint8_t { RawBgr = 0, Jpeg = 1, CcittG4Tiff = 2, Png = 3 };

enum class ScanError : uint16_t {
    None = 0, Unknown = 1, ScannerNotFound = 2, ScannerBusy = 3, ScannerDisconnected = 4,
    PaperJam = 5, FeederEmpty = 6, DoubleFeed = 7, CoverOpen = 8, DriverFault = 9,
    UserCancelled = 10, UnsupportedSetting = 11, TransportFailure = 12,
    AuthenticationFailed = 13, ProtocolViolation = 14, Timeout = 15, OutOfMemory = 16,
    NoLocalAgent = 17, SessionLost = 18,
};

enum class AuthStatus : uint8_t { Ok = 0, BadCredentials = 1, VersionMismatch = 2, NotConfigured = 3 };
enum class DisconnectReason : uint8_t { Normal = 0, ProtocolError = 1, Timeout = 2, Shutdown = 3, SessionEnded = 4 };

inline uint32_t operator|(PeerCaps a, PeerCaps b) {
    return static_cast<uint32_t>(a) | static_cast<uint32_t>(b);
}

class ProtocolError : public std::runtime_error {
public:
    explicit ProtocolError(const std::string& what) : std::runtime_error(what) {}
};

// -------------------------------------------------------------------- writer

class PayloadWriter {
public:
    void u8(uint8_t v) { buf_.push_back(v); }
    void boolean(bool v) { u8(v ? 1 : 0); }

    void u16(uint16_t v) { raw(&v, 2); }
    void i16(int16_t v) { raw(&v, 2); }
    void u32(uint32_t v) { raw(&v, 4); }
    void i32(int32_t v) { raw(&v, 4); }
    void u64(uint64_t v) { raw(&v, 8); }
    void i64(int64_t v) { raw(&v, 8); }

    // 3-decimal fixed point, matching PayloadWriter.WriteFixed on the managed side.
    void fixed3(double v) { i32(static_cast<int32_t>(v * 1000.0 + (v >= 0 ? 0.5 : -0.5))); }

    void bytes(const void* data, size_t len) { raw(data, len); }

    void str(const std::string& utf8) {
        if (utf8.size() > 0xFFFF) throw ProtocolError("string too long");
        u16(static_cast<uint16_t>(utf8.size()));
        raw(utf8.data(), utf8.size());
    }

    void blob(const void* data, size_t len) {
        if (len > kMaxPayload) throw ProtocolError("blob too large");
        i32(static_cast<int32_t>(len));
        raw(data, len);
    }

    const std::vector<uint8_t>& data() const { return buf_; }
    size_t size() const { return buf_.size(); }
    void clear() { buf_.clear(); }

private:
    // x86/x64 are little-endian, which is what the wire format specifies, so a raw copy
    // is correct here. Any future big-endian port must byte-swap in this one place.
    void raw(const void* p, size_t n) {
        const uint8_t* b = static_cast<const uint8_t*>(p);
        buf_.insert(buf_.end(), b, b + n);
    }

    std::vector<uint8_t> buf_;
};

// -------------------------------------------------------------------- reader

class PayloadReader {
public:
    PayloadReader(const uint8_t* data, size_t len) : p_(data), end_(data + len) {}
    explicit PayloadReader(const std::vector<uint8_t>& v) : PayloadReader(v.data(), v.size()) {}

    size_t remaining() const { return static_cast<size_t>(end_ - p_); }

    uint8_t  u8() { return *take(1); }
    bool     boolean() { return u8() != 0; }
    uint16_t u16() { uint16_t v; std::memcpy(&v, take(2), 2); return v; }
    int16_t  i16() { int16_t  v; std::memcpy(&v, take(2), 2); return v; }
    uint32_t u32() { uint32_t v; std::memcpy(&v, take(4), 4); return v; }
    int32_t  i32() { int32_t  v; std::memcpy(&v, take(4), 4); return v; }
    uint64_t u64() { uint64_t v; std::memcpy(&v, take(8), 8); return v; }
    int64_t  i64() { int64_t  v; std::memcpy(&v, take(8), 8); return v; }
    double   fixed3() { return i32() / 1000.0; }

    std::string str() {
        uint16_t len = u16();
        const uint8_t* s = take(len);
        return std::string(reinterpret_cast<const char*>(s), len);
    }

    std::vector<uint8_t> blob() {
        int32_t len = i32();
        if (len < 0 || static_cast<uint32_t>(len) > kMaxPayload) throw ProtocolError("blob length out of range");
        const uint8_t* s = take(static_cast<size_t>(len));
        return std::vector<uint8_t>(s, s + len);
    }

    std::vector<uint8_t> fixedBytes(size_t n) {
        const uint8_t* s = take(n);
        return std::vector<uint8_t>(s, s + n);
    }

    // Validates a loop count before the caller reserves anything.
    int32_t count(int32_t max) {
        int32_t v = i32();
        if (v < 0 || v > max) throw ProtocolError("element count out of range");
        return v;
    }

private:
    const uint8_t* take(size_t n) {
        if (remaining() < n) throw ProtocolError("truncated payload");
        const uint8_t* r = p_;
        p_ += n;
        return r;
    }

    const uint8_t* p_;
    const uint8_t* end_;
};

// --------------------------------------------------------------------- frame

struct Frame {
    MsgType type = MsgType::Heartbeat;
    uint32_t streamId = 0;
    std::vector<uint8_t> payload;

    PayloadReader reader() const { return PayloadReader(payload); }
};

inline void writeHeader(uint8_t* dst, MsgType type, uint32_t streamId, uint32_t payloadLen) {
    if (payloadLen > kMaxPayload) throw ProtocolError("payload exceeds frame limit");
    dst[0] = kMagic;
    dst[1] = kVersion;
    uint16_t t = static_cast<uint16_t>(type);
    std::memcpy(dst + 2, &t, 2);
    std::memcpy(dst + 4, &streamId, 4);
    std::memcpy(dst + 8, &payloadLen, 4);
}

inline uint32_t parseHeader(const uint8_t* hdr, MsgType& type, uint32_t& streamId) {
    if (hdr[0] != kMagic) throw ProtocolError("bad frame magic; stream out of sync");
    if (hdr[1] != kVersion) throw ProtocolError("unsupported protocol version");
    uint16_t t;
    std::memcpy(&t, hdr + 2, 2);
    std::memcpy(&streamId, hdr + 4, 4);
    uint32_t len;
    std::memcpy(&len, hdr + 8, 4);
    if (len > kMaxPayload) throw ProtocolError("frame payload above limit");
    type = static_cast<MsgType>(t);
    return len;
}

inline std::vector<uint8_t> encode(MsgType type, uint32_t streamId, const std::vector<uint8_t>& payload) {
    std::vector<uint8_t> out(kHeaderSize + payload.size());
    writeHeader(out.data(), type, streamId, static_cast<uint32_t>(payload.size()));
    if (!payload.empty()) std::memcpy(out.data() + kHeaderSize, payload.data(), payload.size());
    return out;
}

inline std::vector<uint8_t> encode(MsgType type, uint32_t streamId, const PayloadWriter& w) {
    return encode(type, streamId, w.data());
}

// ------------------------------------------------------------------- helpers

struct ScannerInfo {
    std::string id, name, vendor;
    ScannerIface iface = ScannerIface::Unknown;
    ScannerState state = ScannerState::Unknown;
    uint32_t features = 0;
    bool is32BitOnly = false;

    static ScannerInfo read(PayloadReader& r) {
        ScannerInfo s;
        s.id = r.str();
        s.name = r.str();
        s.vendor = r.str();
        s.iface = static_cast<ScannerIface>(r.u8());
        s.state = static_cast<ScannerState>(r.u8());
        s.features = r.u32();
        s.is32BitOnly = r.boolean();
        return s;
    }
};

struct ScannerCaps {
    std::string scannerId;
    bool found = false;
    std::vector<int32_t> resolutions;
    std::vector<PixType> pixelTypes;
    std::vector<PaperSize> paperSizes;
    uint32_t features = 0;
    double brightnessMin = 0, brightnessMax = 0, contrastMin = 0, contrastMax = 0;
    int32_t maxWidthThou = 8500, maxHeightThou = 14000;

    static ScannerCaps read(PayloadReader& r) {
        ScannerCaps c;
        c.scannerId = r.str();
        c.found = r.boolean();

        int32_t n = r.count(512);
        c.resolutions.reserve(static_cast<size_t>(n));
        for (int32_t i = 0; i < n; ++i) c.resolutions.push_back(r.i32());

        n = r.count(512);
        c.pixelTypes.reserve(static_cast<size_t>(n));
        for (int32_t i = 0; i < n; ++i) c.pixelTypes.push_back(static_cast<PixType>(r.u8()));

        n = r.count(512);
        c.paperSizes.reserve(static_cast<size_t>(n));
        for (int32_t i = 0; i < n; ++i) c.paperSizes.push_back(static_cast<PaperSize>(r.u8()));

        c.features = r.u32();
        c.brightnessMin = r.fixed3();
        c.brightnessMax = r.fixed3();
        c.contrastMin = r.fixed3();
        c.contrastMax = r.fixed3();
        c.maxWidthThou = r.i32();
        c.maxHeightThou = r.i32();
        return c;
    }
};

struct ScanSettings {
    int32_t resolution = 300;
    PixType pixelType = PixType::Rgb;
    PageSource source = PageSource::Auto;
    bool duplex = false;
    PaperSize paperSize = PaperSize::Auto;
    PageOrientation orientation = PageOrientation::Portrait;
    double brightness = 0, contrast = 0;
    int32_t pageLimit = 0;               // 0 == until the feeder empties
    bool autoDeskew = false, autoPageSize = false, blankPageRemoval = false, showScannerUi = false;
    PageEncoding encoding = PageEncoding::Jpeg;
    int32_t jpegQuality = 85;
    int32_t customWidthThou = 0, customHeightThou = 0;

    void write(PayloadWriter& w) const {
        w.i32(resolution);
        w.u8(static_cast<uint8_t>(pixelType));
        w.u8(static_cast<uint8_t>(source));
        w.boolean(duplex);
        w.u8(static_cast<uint8_t>(paperSize));
        w.u8(static_cast<uint8_t>(orientation));
        w.fixed3(brightness);
        w.fixed3(contrast);
        w.i32(pageLimit);
        w.boolean(autoDeskew);
        w.boolean(autoPageSize);
        w.boolean(blankPageRemoval);
        w.boolean(showScannerUi);
        w.u8(static_cast<uint8_t>(encoding));
        w.i32(jpegQuality);
        w.i32(customWidthThou);
        w.i32(customHeightThou);
    }
};

struct PageHeader {
    uint32_t jobId = 0;
    int32_t pageNumber = 0;
    PageSide side = PageSide::Front;
    int32_t width = 0, height = 0, dpiX = 0, dpiY = 0;
    PixType pixelType = PixType::Rgb;
    PageEncoding encoding = PageEncoding::Jpeg;
    int32_t encodedLength = 0;

    static PageHeader read(PayloadReader& r) {
        PageHeader h;
        h.jobId = r.u32();
        h.pageNumber = r.i32();
        h.side = static_cast<PageSide>(r.u8());
        h.width = r.i32();
        h.height = r.i32();
        h.dpiX = r.i32();
        h.dpiY = r.i32();
        h.pixelType = static_cast<PixType>(r.u8());
        h.encoding = static_cast<PageEncoding>(r.u8());
        h.encodedLength = r.i32();
        return h;
    }
};

// CRC-32 (IEEE, reflected), matching Crc32.cs.
class Crc32 {
public:
    Crc32() { state_ = 0xFFFFFFFFu; }

    void append(const uint8_t* data, size_t len) {
        const uint32_t* t = table();
        uint32_t s = state_;
        for (size_t i = 0; i < len; ++i) s = (s >> 8) ^ t[(s ^ data[i]) & 0xFF];
        state_ = s;
    }

    uint32_t finish() const { return state_ ^ 0xFFFFFFFFu; }

private:
    static const uint32_t* table() {
        static uint32_t t[256];
        static bool built = false;
        if (!built) {
            for (uint32_t i = 0; i < 256; ++i) {
                uint32_t v = i;
                for (int b = 0; b < 8; ++b) v = (v & 1) ? (v >> 1) ^ 0xEDB88320u : (v >> 1);
                t[i] = v;
            }
            built = true;
        }
        return t;
    }

    uint32_t state_;
};

}  // namespace rs

#endif  // RS_PROTOCOL_H
