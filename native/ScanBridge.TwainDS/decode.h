// decode.h — turns an encoded page from the wire into a Windows DIB.
//
// Pages cross the RDP channel as JPEG, PNG or CCITT-G4 TIFF. TWAIN applications want a DIB
// (native transfer) or raw scanlines (memory transfer), so something has to decode. We use
// WIC, which ships with Windows and handles all three, rather than linking a codec.
//
// Decoding happens on the data source's worker thread, never on the host application's UI
// thread, so the host's COM apartment is left untouched.

#ifndef RS_TWAINDS_DECODE_H
#define RS_TWAINDS_DECODE_H

#include <windows.h>
#include <wincodec.h>
#include <vector>
#include <stdexcept>

#include "../include/rs_protocol.h"
#include "../include/rs_log.h"

#pragma comment(lib, "windowscodecs.lib")
#pragma comment(lib, "ole32.lib")

namespace rs {

/// A decoded page held as a bottom-up, 4-byte-row-aligned DIB body plus its geometry.
/// Exactly one of these exists at a time, which is what keeps a 200-page job off the heap.
class DecodeError : public std::runtime_error {
public:
    explicit DecodeError(const std::string& what) : std::runtime_error(what) {}
};

struct DecodedPage {
    int32_t width = 0;
    int32_t height = 0;
    int32_t bitsPerPixel = 24;      // 1, 8 or 24
    int32_t dpiX = 300;
    int32_t dpiY = 300;
    PixType pixelType = PixType::Rgb;
    PageSide side = PageSide::Front;
    int32_t pageNumber = 0;
    std::vector<uint8_t> bits;      // stride * height, bottom-up

    /// Bytes per row, computed in int64_t.
    ///
    /// It used to be int32_t: ((width * bitsPerPixel + 31) / 32) * 4. At 24 bits per pixel any
    /// width above roughly 89 million overflows that product - signed overflow, so undefined
    /// behaviour before the wrong number is even used. Every caller then went on to check the
    /// resulting size, which is the wrong order: the check ran after the arithmetic that had
    /// already gone wrong, and a width chosen to wrap the product to a small positive value
    /// sailed through it.
    ///
    /// int64_t cannot overflow for any width this type will accept, so the checks below are now
    /// checking a number that means what it says.
    int64_t stride() const {
        return ((static_cast<int64_t>(width) * bitsPerPixel + 31) / 32) * 4;
    }

    /// Geometry a decoded page is allowed to have, whatever produced it.
    ///
    /// PageHeader::validate() bounds what a *peer* may declare on the wire, and for a long time
    /// that looked like enough. It was not: it only guards the uncompressed path, because
    /// fromRaw() is the only place those declared numbers become the page's geometry. Every real
    /// scan is JPEG, and there the width and height come from the codec's own metadata - a JPEG
    /// SOF marker or a PNG IHDR - which the wire format never sees and validate() never touches.
    /// The bound has to live where the geometry is set, not where one of its two sources is
    /// parsed.
    void validate() const {
        if (width <= 0 || height <= 0)
            throw DecodeError("decoded page has a non-positive width or height");

        // Matches PageHeader::validate(). A 600 dpi scan of A0 is about 28,000 pixels on its
        // long edge, so this is generous and still finite.
        constexpr int32_t kMaxPageEdge = 200000;
        if (width > kMaxPageEdge || height > kMaxPageEdge)
            throw DecodeError("decoded page has an implausible width or height");

        constexpr int64_t kMaxImageBytes = 512LL * 1024 * 1024;
        if (stride() * static_cast<int64_t>(height) > kMaxImageBytes)
            throw DecodeError("decoded page size is implausible");
    }

    size_t paletteEntries() const {
        switch (bitsPerPixel) {
            case 1: return 2;
            case 8: return 256;
            default: return 0;
        }
    }

    /// Size of BITMAPINFOHEADER + palette + bits — the exact size of a native-transfer HGLOBAL.
    size_t dibSize() const {
        return sizeof(BITMAPINFOHEADER) + paletteEntries() * sizeof(RGBQUAD) + bits.size();
    }
};

class PageDecoder {
public:
    /// Must be called once on the thread that will do the decoding.
    void initialize() {
        HRESULT hr = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
        // RPC_E_CHANGED_MODE means the thread already belongs to an apartment we did not
        // create. That is fine — WIC works either way; we simply must not uninitialise it.
        ownsCom_ = SUCCEEDED(hr);

        hr = CoCreateInstance(CLSID_WICImagingFactory, nullptr, CLSCTX_INPROC_SERVER,
                              IID_PPV_ARGS(&factory_));
        if (FAILED(hr)) throw DecodeError("WIC imaging factory unavailable");
    }

    void shutdown() {
        if (factory_) { factory_->Release(); factory_ = nullptr; }
        if (ownsCom_) { CoUninitialize(); ownsCom_ = false; }
    }

    ~PageDecoder() { shutdown(); }

    /// Decodes one encoded page. `target` selects the DIB depth the application asked for.
    DecodedPage decode(const std::vector<uint8_t>& encoded, const PageHeader& header, PixType target) {
        if (header.encoding == PageEncoding::RawBgr)
            return fromRaw(encoded, header, target);

        if (!factory_) throw DecodeError("decoder not initialised");

        IWICStream* stream = nullptr;
        IWICBitmapDecoder* decoder = nullptr;
        IWICBitmapFrameDecode* frame = nullptr;
        IWICFormatConverter* converter = nullptr;

        DecodedPage page;
        try {
            HRESULT hr = factory_->CreateStream(&stream);
            if (FAILED(hr)) throw DecodeError("WIC CreateStream failed");

            hr = stream->InitializeFromMemory(const_cast<BYTE*>(encoded.data()),
                                              static_cast<DWORD>(encoded.size()));
            if (FAILED(hr)) throw DecodeError("WIC InitializeFromMemory failed");

            hr = factory_->CreateDecoderFromStream(stream, nullptr, WICDecodeMetadataCacheOnDemand, &decoder);
            if (FAILED(hr)) throw DecodeError("no WIC decoder for this page encoding");

            hr = decoder->GetFrame(0, &frame);
            if (FAILED(hr)) throw DecodeError("WIC GetFrame failed");

            UINT w = 0, h = 0;
            frame->GetSize(&w, &h);

            // UINT to int32_t: a PNG IHDR may declare up to 2^31-1, which would arrive here
            // negative. Rejected before it is stored rather than after it has been used.
            if (w == 0 || h == 0 || w > static_cast<UINT>(INT32_MAX) || h > static_cast<UINT>(INT32_MAX))
                throw DecodeError("the encoded page declares an implausible size");

            page.width = static_cast<int32_t>(w);
            page.height = static_cast<int32_t>(h);

            double dpiX = 0, dpiY = 0;
            if (SUCCEEDED(frame->GetResolution(&dpiX, &dpiY)) && dpiX > 1 && dpiY > 1) {
                page.dpiX = static_cast<int32_t>(dpiX + 0.5);
                page.dpiY = static_cast<int32_t>(dpiY + 0.5);
            } else {
                // Some encoders omit resolution; the sender told us what it scanned at.
                page.dpiX = header.dpiX;
                page.dpiY = header.dpiY;
            }

            WICPixelFormatGUID destFormat;
            switch (target) {
                case PixType::BlackWhite: destFormat = GUID_WICPixelFormatBlackWhite; page.bitsPerPixel = 1; break;
                case PixType::Grayscale:  destFormat = GUID_WICPixelFormat8bppGray;   page.bitsPerPixel = 8; break;
                default:                  destFormat = GUID_WICPixelFormat24bppBGR;   page.bitsPerPixel = 24; break;
            }
            page.pixelType = target;
            page.side = header.side;
            page.pageNumber = header.pageNumber;

            hr = factory_->CreateFormatConverter(&converter);
            if (FAILED(hr)) throw DecodeError("WIC CreateFormatConverter failed");

            hr = converter->Initialize(frame, destFormat, WICBitmapDitherTypeErrorDiffusion,
                                       nullptr, 0.0, WICBitmapPaletteTypeMedianCut);
            if (FAILED(hr)) throw DecodeError("WIC pixel format conversion unsupported");

            // Now that bitsPerPixel is known, bound the whole geometry. This is the check that
            // was missing: everything above this line came out of the image file itself.
            page.validate();

            copyBottomUp(converter, page);
        }
        catch (...) {
            if (converter) converter->Release();
            if (frame) frame->Release();
            if (decoder) decoder->Release();
            if (stream) stream->Release();
            throw;
        }

        converter->Release();
        frame->Release();
        decoder->Release();
        stream->Release();

        RS_LOG_DEBUG("decoded page %d: %dx%d @%ddpi %dbpp (%zu bytes)",
                     page.pageNumber, page.width, page.height, page.dpiX, page.bitsPerPixel, page.bits.size());
        return page;
    }

    /// Writes a full DIB (header + palette + bits) into `dest`, which must hold dibSize() bytes.
    static void writeDib(const DecodedPage& page, uint8_t* dest) {
        auto* info = reinterpret_cast<BITMAPINFOHEADER*>(dest);
        ZeroMemory(info, sizeof(BITMAPINFOHEADER));
        info->biSize = sizeof(BITMAPINFOHEADER);
        info->biWidth = page.width;
        info->biHeight = page.height;            // positive == bottom-up, which is what we store
        info->biPlanes = 1;
        info->biBitCount = static_cast<WORD>(page.bitsPerPixel);
        info->biCompression = BI_RGB;
        info->biSizeImage = static_cast<DWORD>(page.bits.size());
        // TWAIN reports resolution in DPI; BITMAPINFOHEADER wants pixels per metre.
        info->biXPelsPerMeter = static_cast<LONG>(page.dpiX * 39.3700787 + 0.5);
        info->biYPelsPerMeter = static_cast<LONG>(page.dpiY * 39.3700787 + 0.5);
        info->biClrUsed = static_cast<DWORD>(page.paletteEntries());
        info->biClrImportant = info->biClrUsed;

        uint8_t* cursor = dest + sizeof(BITMAPINFOHEADER);

        if (page.bitsPerPixel == 1) {
            auto* palette = reinterpret_cast<RGBQUAD*>(cursor);
            // WIC BlackWhite is 0 = black, 1 = white. TWAIN's default ICAP_PIXELFLAVOR is
            // CHOCOLATE (0 = black), so this palette matches without inverting the bits.
            palette[0] = RGBQUAD{ 0x00, 0x00, 0x00, 0x00 };
            palette[1] = RGBQUAD{ 0xFF, 0xFF, 0xFF, 0x00 };
            cursor += 2 * sizeof(RGBQUAD);
        } else if (page.bitsPerPixel == 8) {
            auto* palette = reinterpret_cast<RGBQUAD*>(cursor);
            for (int i = 0; i < 256; ++i) {
                BYTE v = static_cast<BYTE>(i);
                palette[i] = RGBQUAD{ v, v, v, 0x00 };
            }
            cursor += 256 * sizeof(RGBQUAD);
        }

        if (!page.bits.empty()) memcpy(cursor, page.bits.data(), page.bits.size());
    }

private:
    /// WIC hands back top-down rows; DIBs with a positive biHeight are bottom-up, so the
    /// row order is reversed here once rather than at every transfer.
    static void copyBottomUp(IWICBitmapSource* source, DecodedPage& page) {
        // page.validate() has already bounded these; recomputed in int64_t regardless, so this
        // function is safe to call from anywhere rather than only from a caller that remembered.
        page.validate();

        const int64_t stride = page.stride();
        const size_t total = static_cast<size_t>(stride) * static_cast<size_t>(page.height);
        if (total == 0 || total > (512u * 1024u * 1024u))
            throw DecodeError("decoded page size is implausible");

        std::vector<uint8_t> topDown(total);
        HRESULT hr = source->CopyPixels(nullptr, static_cast<UINT>(stride),
                                        static_cast<UINT>(total), topDown.data());
        if (FAILED(hr)) throw DecodeError("WIC CopyPixels failed");

        page.bits.resize(total);
        for (int32_t y = 0; y < page.height; ++y) {
            memcpy(page.bits.data() + static_cast<size_t>(y) * stride,
                   topDown.data() + static_cast<size_t>(page.height - 1 - y) * static_cast<size_t>(stride),
                   static_cast<size_t>(stride));
        }
    }

    /// Uncompressed pages already arrive in DIB row layout; only geometry needs filling in.
    static DecodedPage fromRaw(const std::vector<uint8_t>& encoded, const PageHeader& header, PixType target) {
        DecodedPage page;
        page.width = header.width;
        page.height = header.height;
        page.dpiX = header.dpiX;
        page.dpiY = header.dpiY;
        page.pixelType = header.pixelType;
        page.side = header.side;
        page.pageNumber = header.pageNumber;
        page.bitsPerPixel = header.pixelType == PixType::BlackWhite ? 1
                          : header.pixelType == PixType::Grayscale ? 8 : 24;

        if (header.pixelType != target)
            throw DecodeError("raw pages must already match the requested pixel type");

        // Same bound as every other path. The numbers here came from a PageHeader that has
        // already been validated on the wire, so this is belt and braces - but the whole reason
        // this fix was needed is that one path was assumed covered by a check living somewhere
        // else, and was not.
        page.validate();

        const size_t expected = static_cast<size_t>(page.stride()) * static_cast<size_t>(page.height);
        if (encoded.size() < expected) throw DecodeError("raw page is shorter than its declared geometry");

        page.bits.assign(encoded.begin(), encoded.begin() + static_cast<ptrdiff_t>(expected));
        return page;
    }

    IWICImagingFactory* factory_ = nullptr;
    bool ownsCom_ = false;
};

}  // namespace rs

#endif  // RS_TWAINDS_DECODE_H
