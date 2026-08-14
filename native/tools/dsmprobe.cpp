// Proves a data source is visible to a REAL TWAIN Data Source Manager, without installing
// anything and without administrator rights.
//
// Why this exists
// ---------------
// Every other test we can write asks the data source questions directly, which only ever
// confirms that our own idea of the protocol is self-consistent. It was exactly that kind of
// test which certified a data source that exported DSM_Entry instead of DS_Entry: the file
// loaded, answered DAT_IDENTITY correctly, and was invisible to every real manager, because
// no real manager was ever involved in the test.
//
// So this tool involves one. It loads an unmodified TWAINDSM.dll - the same binary that ships
// inside NAPS2 and other scanning applications - and asks it, as an application would, what
// data sources exist.
//
// The catch is that a DSM only scans %WINDIR%\twain_32 and %WINDIR%\twain_64, and writing
// there needs administrator rights. Rather than require them, the manager's own call to
// GetWindowsDirectory is redirected: its import table is patched so it receives a scratch
// directory instead of C:\Windows. Everything after that is the manager's genuine, unpatched
// logic - its directory walk, its LoadLibrary, its GetProcAddress("DS_Entry"), its identity
// call and its accept/reject decision. That is the behaviour under test, and it is not
// simulated here in any part.
//
// Usage
//   dsmprobe.exe <twaindsm.dll> <ScanBridge.ds> [--toplevel] [--open]
//                                                  [--scan [out.bmp]] [--timeout <seconds>]
//
//     --toplevel  also place a copy directly in twain_NN\, as the legacy layout wants, to
//                 show whether a 2.x manager then lists the source twice
//     --open      additionally issue MSG_OPENDS, which makes the data source attempt to reach
//                 its session agent; expected to fail when there is no agent to reach
//     --memory    transfer the page through DAT_IMAGEMEMXFER instead of a file, which is what
//                 most scanning applications do by default
//     --scan      go all the way: acquire one page unattended and write it as a BMP. Implies
//                 --open. Needs a session agent and a tray agent to be running, and a scanner
//                 switched on at the far end.
//
// Exit code 0 only if the manager listed a source manufactured by ScanBridge.

#include <windows.h>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <vector>
#include <algorithm>
#include "../include/rs_twain.h"

namespace {

constexpr int kBits = static_cast<int>(sizeof(void*) * 8);

char g_fakeWindowsDirectory[MAX_PATH]{};

// ------------------------------------------------------------------- redirection

UINT WINAPI fakeGetWindowsDirectoryA(LPSTR buffer, UINT size) {
    const UINT length = static_cast<UINT>(lstrlenA(g_fakeWindowsDirectory));
    if (!buffer || size < length + 1) return length + 1;   // the documented contract
    lstrcpynA(buffer, g_fakeWindowsDirectory, size);
    return length;
}

UINT WINAPI fakeGetWindowsDirectoryW(LPWSTR buffer, UINT size) {
    wchar_t wide[MAX_PATH]{};
    const int length = MultiByteToWideChar(CP_ACP, 0, g_fakeWindowsDirectory, -1,
                                           wide, MAX_PATH) - 1;
    if (length < 0) return 0;
    if (!buffer || size < static_cast<UINT>(length) + 1) return length + 1;
    lstrcpynW(buffer, wide, size);
    return static_cast<UINT>(length);
}

/// Replaces every import-table slot in `module` that currently points at `target`.
///
/// Matching on the resolved address rather than on the imported name is deliberate: the same
/// function arrives under several names (kernel32, kernelbase, and the api-ms-win-core-*
/// api-sets all expose GetWindowsDirectoryA) and a binary may bind to any of them. The
/// address after loading is the same in every case, so one sweep covers them all - and it
/// also works for modules with bound imports, where the name thunks are gone.
int patchImports(HMODULE module, void* target, void* replacement) {
    if (!target) return 0;

    auto* dos = reinterpret_cast<PIMAGE_DOS_HEADER>(module);
    if (dos->e_magic != IMAGE_DOS_SIGNATURE) return 0;

    auto* nt = reinterpret_cast<PIMAGE_NT_HEADERS>(
        reinterpret_cast<BYTE*>(module) + dos->e_lfanew);
    if (nt->Signature != IMAGE_NT_SIGNATURE) return 0;

    const IMAGE_DATA_DIRECTORY& directory =
        nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_IMPORT];
    if (!directory.VirtualAddress) return 0;

    auto* descriptor = reinterpret_cast<PIMAGE_IMPORT_DESCRIPTOR>(
        reinterpret_cast<BYTE*>(module) + directory.VirtualAddress);

    int patched = 0;
    for (; descriptor->Name; ++descriptor) {
        auto* thunk = reinterpret_cast<PIMAGE_THUNK_DATA>(
            reinterpret_cast<BYTE*>(module) + descriptor->FirstThunk);

        for (; thunk->u1.Function; ++thunk) {
            if (reinterpret_cast<void*>(thunk->u1.Function) != target) continue;

            DWORD previous = 0;
            if (!VirtualProtect(&thunk->u1.Function, sizeof(ULONG_PTR),
                                PAGE_READWRITE, &previous)) {
                continue;
            }
            thunk->u1.Function = reinterpret_cast<ULONG_PTR>(replacement);
            VirtualProtect(&thunk->u1.Function, sizeof(ULONG_PTR), previous, &previous);
            ++patched;
        }
    }
    return patched;
}

/// Patches one function wherever it came from. Each provider is resolved separately because
/// an api-set can forward somewhere other than kernel32, giving a different address in the
/// import table than the one kernel32 hands out.
int redirect(HMODULE module, const char* name, void* replacement) {
    static const char* providers[] = {
        "kernel32.dll",
        "kernelbase.dll",
        "api-ms-win-core-sysinfo-l1-1-0.dll",
        "api-ms-win-core-sysinfo-l1-2-0.dll",
    };

    void* seen[8]{};
    int seenCount = 0;
    int patched = 0;

    for (const char* provider : providers) {
        HMODULE host = GetModuleHandleA(provider);
        if (!host) host = LoadLibraryA(provider);
        if (!host) continue;

        void* address = reinterpret_cast<void*>(GetProcAddress(host, name));
        if (!address) continue;

        bool duplicate = false;
        for (int i = 0; i < seenCount; ++i) {
            if (seen[i] == address) { duplicate = true; break; }
        }
        if (duplicate) continue;
        if (seenCount < 8) seen[seenCount++] = address;

        patched += patchImports(module, address, replacement);
    }
    return patched;
}

// ------------------------------------------------------------------ scratch tree

bool ensureDirectory(const char* path) {
    if (CreateDirectoryA(path, nullptr)) return true;
    return GetLastError() == ERROR_ALREADY_EXISTS;
}

/// Removes the scratch tree. Called on the way out so repeated runs do not leave a trail of
/// abandoned twain_NN folders in %TEMP%; failure is ignored, since the manager may still hold
/// the .ds mapped and a leftover folder is harmless either way.
void removeScratchTree(const char* root) {
    char twain[MAX_PATH]{};
    char vendor[MAX_PATH]{};
    char file[MAX_PATH]{};

    wsprintfA(twain, "%s\\twain_%d", root, kBits);
    wsprintfA(vendor, "%s\\ScanBridge", twain);

    wsprintfA(file, "%s\\ScanBridge.ds", vendor);
    DeleteFileA(file);
    wsprintfA(file, "%s\\ScanBridge.ds", twain);
    DeleteFileA(file);

    RemoveDirectoryA(vendor);
    RemoveDirectoryA(twain);
    RemoveDirectoryA(root);
}

/// Builds <root>\twain_NN\ScanBridge\ScanBridge.ds, the layout a 2.x manager expects.
bool buildScratchTree(const char* root, const char* dataSource, bool alsoTopLevel) {
    char twain[MAX_PATH]{};
    char vendor[MAX_PATH]{};
    wsprintfA(twain, "%s\\twain_%d", root, kBits);
    wsprintfA(vendor, "%s\\ScanBridge", twain);

    if (!ensureDirectory(root) || !ensureDirectory(twain) || !ensureDirectory(vendor)) {
        std::printf("   could not create the scratch tree under %s (error %lu)\n",
                    root, GetLastError());
        return false;
    }

    char destination[MAX_PATH]{};
    wsprintfA(destination, "%s\\ScanBridge.ds", vendor);
    if (!CopyFileA(dataSource, destination, FALSE)) {
        std::printf("   could not copy %s -> %s (error %lu)\n",
                    dataSource, destination, GetLastError());
        return false;
    }
    std::printf("   placed %s\n", destination);

    if (alsoTopLevel) {
        char legacy[MAX_PATH]{};
        wsprintfA(legacy, "%s\\ScanBridge.ds", twain);
        if (CopyFileA(dataSource, legacy, FALSE)) {
            std::printf("   placed %s\n", legacy);
        }
    }
    return true;
}

// ----------------------------------------------------------------------- driving

const char* returnCodeName(TW_UINT16 rc) {
    switch (rc) {
        case TWRC_SUCCESS:     return "TWRC_SUCCESS";
        case TWRC_FAILURE:     return "TWRC_FAILURE";
        case TWRC_CHECKSTATUS: return "TWRC_CHECKSTATUS";
        case TWRC_CANCEL:      return "TWRC_CANCEL";
        case TWRC_ENDOFLIST:   return "TWRC_ENDOFLIST (the manager listed nothing further)";
        case TWRC_XFERDONE:    return "TWRC_XFERDONE";
        case TWRC_DSEVENT:     return "TWRC_DSEVENT";
        case TWRC_NOTDSEVENT:  return "TWRC_NOTDSEVENT";
        case TWRC_BUSY:        return "TWRC_BUSY";
        default:               return "(other)";
    }
}

// ------------------------------------------------------------------- acquisition

// Not in rs_twain.h because the data source never needs them: only an application handing a
// buffer to DAT_IMAGEMEMXFER does.
constexpr TW_UINT16 kTwmfAppOwns = 0x0001;
constexpr TW_UINT16 kTwmfPointer = 0x0010;

/// Sets a one-value capability. Returns the return code so a refusal is visible rather than
/// silently leaving the source on a different transfer mechanism than we are about to drive.
TW_UINT16 setCapability(DSMENTRYPROC entry, pTW_IDENTITY app, pTW_IDENTITY ds,
                        TW_UINT16 capability, TW_UINT16 type, TW_UINT32 value) {
    TW_CAPABILITY cap{};
    cap.Cap = capability;
    cap.ConType = TWON_ONEVALUE;
    cap.hContainer = GlobalAlloc(GHND, sizeof(TW_ONEVALUE));
    if (!cap.hContainer) return TWRC_FAILURE;

    auto* one = static_cast<pTW_ONEVALUE>(GlobalLock(cap.hContainer));
    one->ItemType = type;
    one->Item = value;
    GlobalUnlock(cap.hContainer);

    TW_UINT16 rc = entry(app, ds, DG_CONTROL, DAT_CAPABILITY, MSG_SET, &cap);
    GlobalFree(cap.hContainer);
    return rc;
}

/// Pumps the message loop until the source says a page is ready.
///
/// A TWAIN source signals asynchronously: MSG_ENABLEDS returns immediately and the page
/// arrives later, announced through DAT_EVENT. Every real scanning application has this loop;
/// without it the source would be told to scan and never asked for the result.
TW_UINT16 waitForTransfer(DSMENTRYPROC entry, pTW_IDENTITY app, pTW_IDENTITY ds, int timeoutSeconds) {
    const ULONGLONG deadline = GetTickCount64() + static_cast<ULONGLONG>(timeoutSeconds) * 1000;

    while (GetTickCount64() < deadline) {
        MSG message{};
        if (!PeekMessageA(&message, nullptr, 0, 0, PM_REMOVE)) {
            Sleep(20);
            continue;
        }

        TW_EVENT event{};
        event.pEvent = &message;
        event.TWMessage = MSG_NULL;

        TW_UINT16 rc = entry(app, ds, DG_CONTROL, DAT_EVENT, MSG_PROCESSEVENT, &event);

        if (rc == TWRC_NOTDSEVENT) {
            TranslateMessage(&message);
            DispatchMessageA(&message);
        }

        switch (event.TWMessage) {
            case MSG_XFERREADY:   return MSG_XFERREADY;
            case MSG_CLOSEDSREQ:  return MSG_CLOSEDSREQ;
            default:              break;
        }
    }
    return MSG_NULL;
}

/// Pulls a page through DAT_IMAGEMEMXFER, the way most scanning applications do.
///
/// Worth testing separately from file transfer, and not a variation on it: memory transfer is
/// the only path where the application supplies the buffer and the source has to honour a
/// stride, a row count and a running offset. Nothing in file transfer exercises any of that,
/// so a source can write perfect BMPs and still hand back skewed or truncated images here.
/// NAPS2 and most other applications use this mechanism by default.
///
/// The rows are written out as a BMP so the result can be looked at rather than trusted.
bool transferViaMemory(DSMENTRYPROC entry, pTW_IDENTITY app, pTW_IDENTITY ds,
                       const char* outputPath) {
    TW_IMAGEINFO info{};
    if (entry(app, ds, DG_IMAGE, DAT_IMAGEINFO, MSG_GET, &info) != TWRC_SUCCESS) {
        std::printf("     DAT_IMAGEINFO failed\n");
        return false;
    }
    std::printf("     image: %ld x %ld, %u bits/pixel, %u samples\n",
                static_cast<long>(info.ImageWidth), static_cast<long>(info.ImageLength),
                info.BitsPerPixel, info.SamplesPerPixel);

    TW_SETUPMEMXFER setup{};
    if (entry(app, ds, DG_CONTROL, DAT_SETUPMEMXFER, MSG_GET, &setup) != TWRC_SUCCESS) {
        std::printf("     DAT_SETUPMEMXFER failed\n");
        return false;
    }
    std::printf("     buffer sizes: min %lu, preferred %lu, max %lu\n",
                static_cast<unsigned long>(setup.MinBufSize),
                static_cast<unsigned long>(setup.Preferred),
                static_cast<unsigned long>(setup.MaxBufSize));

    const TW_UINT32 bufferSize = setup.Preferred ? setup.Preferred : setup.MinBufSize;
    std::vector<uint8_t> buffer(bufferSize);
    std::vector<uint8_t> page;

    TW_UINT32 stride = 0;
    TW_UINT32 rows = 0;
    TW_UINT16 rc = TWRC_SUCCESS;
    int blocks = 0;

    while (rc == TWRC_SUCCESS) {
        TW_IMAGEMEMXFER xfer{};
        xfer.Compression = TWCP_NONE;
        xfer.Memory.Flags = kTwmfAppOwns | kTwmfPointer;
        xfer.Memory.Length = bufferSize;
        xfer.Memory.TheMem = buffer.data();

        rc = entry(app, ds, DG_IMAGE, DAT_IMAGEMEMXFER, MSG_GET, &xfer);
        if (rc != TWRC_SUCCESS && rc != TWRC_XFERDONE) {
            std::printf("     DAT_IMAGEMEMXFER -> %u (%s) after %d block(s)\n",
                        rc, returnCodeName(rc), blocks);
            return false;
        }

        ++blocks;
        stride = xfer.BytesPerRow;
        rows += xfer.Rows;
        page.insert(page.end(), buffer.begin(), buffer.begin() + xfer.BytesWritten);

        if (rc == TWRC_XFERDONE) break;
    }

    std::printf("     %d block(s), %lu rows, %lu bytes/row, %zu bytes total\n",
                blocks, static_cast<unsigned long>(rows),
                static_cast<unsigned long>(stride), page.size());

    // The source must deliver exactly the image it described. A short or over-long transfer is
    // the failure this whole path exists to catch.
    const TW_UINT32 expected = stride * static_cast<TW_UINT32>(info.ImageLength);
    if (rows != static_cast<TW_UINT32>(info.ImageLength) || page.size() != expected) {
        std::printf("     WRONG: expected %lu rows and %lu bytes\n",
                    static_cast<unsigned long>(info.ImageLength),
                    static_cast<unsigned long>(expected));
        return false;
    }

    // Memory transfer is top-down; a BMP is bottom-up, so rows are written in reverse.
    //
    // The pixels are also converted, and that conversion is the point rather than a detail.
    // TWAIN delivers TWPT_RGB as R,G,B; a BMP stores B,G,R. Writing the received bytes
    // straight into a BMP - which this tool used to do - cancels a channel swap in the source
    // against the same swap here, so an image with its reds and blues exchanged is written out
    // looking perfectly correct and every check passes. That is exactly what happened: the
    // probe was happy while NAPS2 rendered copper tubing bright blue.
    if (info.BitsPerPixel == 24) {
        for (size_t i = 0; i + 2 < page.size(); i += 3) std::swap(page[i], page[i + 2]);
    }

    BITMAPFILEHEADER file{};
    BITMAPINFOHEADER header{};
    header.biSize = sizeof(header);
    header.biWidth = static_cast<LONG>(info.ImageWidth);
    header.biHeight = static_cast<LONG>(info.ImageLength);
    header.biPlanes = 1;
    header.biBitCount = info.BitsPerPixel;
    header.biCompression = BI_RGB;
    header.biSizeImage = static_cast<DWORD>(page.size());

    file.bfType = 0x4D42;
    file.bfOffBits = sizeof(file) + sizeof(header);
    file.bfSize = file.bfOffBits + header.biSizeImage;

    HANDLE out = CreateFileA(outputPath, GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS,
                             FILE_ATTRIBUTE_NORMAL, nullptr);
    if (out == INVALID_HANDLE_VALUE) {
        std::printf("     could not create %s\n", outputPath);
        return false;
    }

    DWORD written = 0;
    WriteFile(out, &file, sizeof(file), &written, nullptr);
    WriteFile(out, &header, sizeof(header), &written, nullptr);
    for (TW_UINT32 row = 0; row < rows; ++row) {
        WriteFile(out, page.data() + static_cast<size_t>(rows - 1 - row) * stride,
                  stride, &written, nullptr);
    }
    CloseHandle(out);
    return true;
}

/// Which of the four transfer mechanisms this run exercises. They share almost no code inside
/// the data source, so passing one does not imply another passes — the reason this is an
/// explicit choice rather than a boolean.
enum class Mechanism { File, Memory, MemFile, Native };

/// Pulls a page through DAT_IMAGENATIVEXFER — a DIB handed over as a single global handle.
///
/// The default mechanism for most applications, and it went untested here for the whole
/// project while the constants were wrong, so this call was arriving at the memory-transfer
/// handler and being read as a TW_IMAGEMEMXFER. Nothing crashed only by luck.
bool transferViaNative(DSMENTRYPROC entry, pTW_IDENTITY app, pTW_IDENTITY ds,
                       const char* outputPath) {
    TW_IMAGEINFO info{};
    if (entry(app, ds, DG_IMAGE, DAT_IMAGEINFO, MSG_GET, &info) == TWRC_SUCCESS) {
        std::printf("     image: %ld x %ld, %u bits/pixel, %u samples\n",
                    static_cast<long>(info.ImageWidth), static_cast<long>(info.ImageLength),
                    info.BitsPerPixel, info.SamplesPerPixel);
    }

    TW_HANDLE handle = nullptr;
    TW_UINT16 rc = entry(app, ds, DG_IMAGE, DAT_IMAGENATIVEXFER, MSG_GET, &handle);
    std::printf("     DAT_IMAGENATIVEXFER -> %u (%s)\n", rc, returnCodeName(rc));
    if (rc != TWRC_XFERDONE || !handle) return false;

    // What comes back is a DIB - BITMAPINFOHEADER then palette then pixels - with no file
    // header, so one is written in front of it to make an openable BMP.
    auto* dib = static_cast<const uint8_t*>(GlobalLock(static_cast<HGLOBAL>(handle)));
    if (!dib) { GlobalFree(static_cast<HGLOBAL>(handle)); return false; }

    const SIZE_T dibSize = GlobalSize(static_cast<HGLOBAL>(handle));
    BITMAPINFOHEADER header{};
    memcpy(&header, dib, sizeof(header));

    if (header.biSize != sizeof(BITMAPINFOHEADER) || header.biWidth != info.ImageWidth) {
        std::printf("     WRONG: the handle does not begin with a matching BITMAPINFOHEADER\n");
        GlobalUnlock(static_cast<HGLOBAL>(handle));
        GlobalFree(static_cast<HGLOBAL>(handle));
        return false;
    }

    const DWORD paletteEntries = header.biBitCount <= 8 ? (1u << header.biBitCount) : 0u;
    BITMAPFILEHEADER file{};
    file.bfType = 0x4D42;
    file.bfOffBits = static_cast<DWORD>(sizeof(file) + header.biSize + paletteEntries * sizeof(RGBQUAD));
    file.bfSize = static_cast<DWORD>(sizeof(file) + dibSize);

    HANDLE out = CreateFileA(outputPath, GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS,
                             FILE_ATTRIBUTE_NORMAL, nullptr);
    bool ok = out != INVALID_HANDLE_VALUE;
    if (ok) {
        DWORD written = 0;
        WriteFile(out, &file, sizeof(file), &written, nullptr);
        WriteFile(out, dib, static_cast<DWORD>(dibSize), &written, nullptr);
        CloseHandle(out);
    }

    std::printf("     %llu bytes, %ld x %ld, %u bpp\n",
                static_cast<unsigned long long>(dibSize),
                static_cast<long>(header.biWidth), static_cast<long>(header.biHeight),
                header.biBitCount);

    GlobalUnlock(static_cast<HGLOBAL>(handle));
    GlobalFree(static_cast<HGLOBAL>(handle));
    return ok;
}

/// Pulls a page through DAT_IMAGEMEMFILEXFER — the mechanism NAPS2 uses.
///
/// Not a variation on either neighbour, which is why it gets its own path here: the source
/// hands over a complete image *file* through the application's buffer, in as many pieces as
/// the buffer requires, rather than raw scanlines (memory transfer) or a path on disk (file
/// transfer). It went untested for the whole of this project and was the one mechanism the
/// user's application asked for, so the driver refused after every otherwise-successful scan.
bool transferViaMemoryFile(DSMENTRYPROC entry, pTW_IDENTITY app, pTW_IDENTITY ds,
                           const char* outputPath) {
    TW_IMAGEINFO info{};
    if (entry(app, ds, DG_IMAGE, DAT_IMAGEINFO, MSG_GET, &info) == TWRC_SUCCESS) {
        std::printf("     image: %ld x %ld, %u bits/pixel, %u samples\n",
                    static_cast<long>(info.ImageWidth), static_cast<long>(info.ImageLength),
                    info.BitsPerPixel, info.SamplesPerPixel);
    }

    std::vector<TW_UINT8> buffer(64 * 1024);
    std::vector<TW_UINT8> whole;
    int blocks = 0;
    TW_UINT16 rc = TWRC_SUCCESS;

    while (rc == TWRC_SUCCESS) {
        TW_IMAGEMEMXFER xfer{};
        xfer.Compression = TWCP_NONE;
        xfer.Memory.Flags = kTwmfAppOwns | kTwmfPointer;
        xfer.Memory.Length = static_cast<TW_UINT32>(buffer.size());
        xfer.Memory.TheMem = buffer.data();

        rc = entry(app, ds, DG_IMAGE, DAT_IMAGEMEMFILEXFER, MSG_GET, &xfer);
        if (rc != TWRC_SUCCESS && rc != TWRC_XFERDONE) {
            std::printf("     DAT_IMAGEMEMFILEXFER -> %u (%s) after %d block(s)\n",
                        rc, returnCodeName(rc), blocks);
            return false;
        }

        if (xfer.BytesWritten > buffer.size()) {
            std::printf("     WRONG: the source claims %lu bytes in a %zu byte buffer\n",
                        static_cast<unsigned long>(xfer.BytesWritten), buffer.size());
            return false;
        }

        ++blocks;
        whole.insert(whole.end(), buffer.begin(), buffer.begin() + xfer.BytesWritten);

        if (rc == TWRC_XFERDONE) break;
    }

    std::printf("     %d block(s), %zu bytes total\n", blocks, whole.size());

    if (whole.size() < 3) {
        std::printf("     WRONG: nothing usable was transferred\n");
        return false;
    }

    // The bytes are a whole image file, so what arrives must actually be one. A BMP starts
    // 'BM'; a little-endian TIFF starts 'II'. Checking is the difference between "the calls
    // returned success" and "the application has a picture".
    const bool looksLikeBmp = whole[0] == 'B' && whole[1] == 'M';
    const bool looksLikeTiff = whole[0] == 'I' && whole[1] == 'I' && whole[2] == 42;
    std::printf("     file signature: %c%c (%s)\n", whole[0], whole[1],
                looksLikeBmp ? "BMP" : (looksLikeTiff ? "TIFF" : "UNRECOGNISED"));
    if (!looksLikeBmp && !looksLikeTiff) return false;

    HANDLE out = CreateFileA(outputPath, GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS,
                             FILE_ATTRIBUTE_NORMAL, nullptr);
    if (out == INVALID_HANDLE_VALUE) {
        std::printf("     could not create %s\n", outputPath);
        return false;
    }
    DWORD written = 0;
    WriteFile(out, whole.data(), static_cast<DWORD>(whole.size()), &written, nullptr);
    CloseHandle(out);
    return written == whole.size();
}

/// Reports one operation and, when it failed, the condition code the application would show.
///
/// Reading DAT_STATUS is not incidental: it is exactly what a scanning application does with a
/// TWRC_FAILURE, and it is where "TWAIN error: CapUnsupported" comes from. TWCC_CAPUNSUPPORTED
/// is called out by name because a source that answers it here has produced that dialog.
bool reportOperation(DSMENTRYPROC entry, pTW_IDENTITY app, pTW_IDENTITY ds,
                     const char* what, TW_UINT16 rc) {
    if (rc == TWRC_SUCCESS || rc == TWRC_XFERDONE || rc == TWRC_CHECKSTATUS) {
        std::printf("       %-34s -> %u (%s)\n", what, rc, returnCodeName(rc));
        return true;
    }

    TW_STATUS status{};
    TW_UINT16 condition = 0xFFFF;
    if (entry(app, ds, DG_CONTROL, DAT_STATUS, MSG_GET, &status) == TWRC_SUCCESS)
        condition = status.ConditionCode;

    std::printf("       %-34s -> %u (%s), condition code %u%s\n", what, rc, returnCodeName(rc),
                condition,
                condition == TWCC_CAPUNSUPPORTED ? "  <-- CapUnsupported, the user sees this" : "");

    return condition != TWCC_CAPUNSUPPORTED;
}

/// Everything an application may ask for once the page is in its hands, in state 7.
///
/// This is the exact window the reported fault lives in: the scan succeeds, the page arrives,
/// and then one of these asks produces an error dialog on top of an image that is already
/// correct. Driving them here makes that measurable without a server, an RDP session or NAPS2.
bool probePostTransfer(DSMENTRYPROC entry, pTW_IDENTITY app, pTW_IDENTITY ds) {
    std::printf("     post-transfer operations (state 7):\n");
    bool clean = true;

    // An application asking for per-page extras. The InfoID is deliberately one we do not
    // produce: the operation must still succeed and mark that single item unavailable.
    TW_EXTIMAGEINFO extended{};
    extended.NumInfos = 1;
    extended.Info[0].InfoID = 0x1201;
    clean &= reportOperation(entry, app, ds, "DAT_EXTIMAGEINFO MSG_GET",
                             entry(app, ds, DG_IMAGE, DAT_EXTIMAGEINFO, MSG_GET, &extended));
    if (extended.Info[0].ReturnCode != TWRC_INFONOTSUPPORTED) {
        std::printf("       (item ReturnCode %u, expected %u)\n",
                    extended.Info[0].ReturnCode, TWRC_INFONOTSUPPORTED);
    }

    TW_IMAGELAYOUT layout{};
    clean &= reportOperation(entry, app, ds, "DAT_IMAGELAYOUT MSG_GET",
                             entry(app, ds, DG_IMAGE, DAT_IMAGELAYOUT, MSG_GET, &layout));

    // "What can you do with this capability?" for one the source does not have. The answer is
    // "nothing", not a failure - an application uses this to discover absence.
    TW_CAPABILITY query{};
    query.Cap = 0x1130;                       // not in our table, by design
    query.ConType = TWON_DONTCARE16;
    clean &= reportOperation(entry, app, ds, "MSG_QUERYSUPPORT, unknown cap",
                             entry(app, ds, DG_CONTROL, DAT_CAPABILITY, MSG_QUERYSUPPORT, &query));
    if (query.hContainer) GlobalFree(query.hContainer);

    TW_CAPABILITY custom{};
    custom.Cap = CAP_CUSTOMDSDATA;
    custom.ConType = TWON_DONTCARE16;
    clean &= reportOperation(entry, app, ds, "CAP_CUSTOMDSDATA MSG_GET",
                             entry(app, ds, DG_CONTROL, DAT_CAPABILITY, MSG_GET, &custom));
    if (custom.hContainer) GlobalFree(custom.hContainer);

    // Control case, and the reason the four lines above can be believed.
    //
    // Reading the *value* of a capability the source does not have must still answer
    // TWCC_CAPUNSUPPORTED - there is no value to give, and inventing one would be worse. So
    // this operation is expected to produce exactly the condition code the others are being
    // checked for. If it does not, the checks above passed because this probe cannot see that
    // code at all, and they mean nothing.
    TW_CAPABILITY absent{};
    absent.Cap = 0x1130;
    absent.ConType = TWON_DONTCARE16;
    TW_UINT16 absentRc = entry(app, ds, DG_CONTROL, DAT_CAPABILITY, MSG_GET, &absent);
    if (absent.hContainer) GlobalFree(absent.hContainer);

    TW_STATUS status{};
    TW_UINT16 condition = 0xFFFF;
    if (entry(app, ds, DG_CONTROL, DAT_STATUS, MSG_GET, &status) == TWRC_SUCCESS)
        condition = status.ConditionCode;

    const bool detectorWorks = absentRc == TWRC_FAILURE && condition == TWCC_CAPUNSUPPORTED;
    std::printf("       %-34s -> %u (%s), condition code %u  [control: %s]\n",
                "MSG_GET, unknown cap", absentRc, returnCodeName(absentRc), condition,
                detectorWorks ? "this probe does detect CapUnsupported"
                              : "BROKEN - the checks above prove nothing");
    clean &= detectorWorks;

    return clean;
}

/// What an application does while tidying up, back in state 4 after MSG_DISABLEDS.
bool probeAfterDisable(DSMENTRYPROC entry, pTW_IDENTITY app, pTW_IDENTITY ds) {
    std::printf("     clean-up operations (state 4):\n");
    bool clean = true;

    TW_CAPABILITY reset{};
    reset.Cap = 0x1130;                       // again, one we do not have
    reset.ConType = TWON_DONTCARE16;
    clean &= reportOperation(entry, app, ds, "MSG_RESET, unknown cap",
                             entry(app, ds, DG_CONTROL, DAT_CAPABILITY, MSG_RESET, &reset));
    if (reset.hContainer) GlobalFree(reset.hContainer);

    clean &= reportOperation(entry, app, ds, "DAT_CAPABILITY MSG_RESETALL",
                             entry(app, ds, DG_CONTROL, DAT_CAPABILITY, MSG_RESETALL, nullptr));

    TW_IMAGELAYOUT layout{};
    layout.DocumentNumber = 1;
    layout.PageNumber = 1;
    layout.FrameNumber = 1;
    clean &= reportOperation(entry, app, ds, "DAT_IMAGELAYOUT MSG_RESET",
                             entry(app, ds, DG_IMAGE, DAT_IMAGELAYOUT, MSG_RESET, &layout));

    return clean;
}

/// Drives a complete acquisition: enable, wait, transfer one page to a BMP, end cleanly.
bool acquire(DSMENTRYPROC entry, pTW_IDENTITY app, pTW_IDENTITY ds,
             HWND window, const char* outputPath, int timeoutSeconds, Mechanism mechanism,
             TW_UINT32 fileFormat) {
    const char* mechanismName = mechanism == Mechanism::Memory ? "memory"
                              : mechanism == Mechanism::MemFile ? "memory-file"
                              : mechanism == Mechanism::Native ? "native" : "file";
    const char* capName = mechanism == Mechanism::Memory ? "TWSX_MEMORY"
                        : mechanism == Mechanism::MemFile ? "TWSX_MEMFILE"
                        : mechanism == Mechanism::Native ? "TWSX_NATIVE" : "TWSX_FILE";
    const TW_UINT32 capValue = mechanism == Mechanism::Memory ? TWSX_MEMORY
                             : mechanism == Mechanism::MemFile ? TWSX_MEMFILE
                             : mechanism == Mechanism::Native ? TWSX_NATIVE : TWSX_FILE;

    std::printf("\n   acquiring a page (%s transfer):\n", mechanismName);

    TW_UINT16 rc = setCapability(entry, app, ds, ICAP_XFERMECH, TWTY_UINT16, capValue);
    std::printf("     ICAP_XFERMECH = %s -> %u (%s)\n", capName, rc, returnCodeName(rc));
    if (rc != TWRC_SUCCESS) return false;

    // File and memory-file transfer both emit whatever ICAP_IMAGEFILEFORMAT currently says, so
    // the format is part of what is under test - a source can hand over a perfectly sized file
    // that no application can read.
    if (fileFormat != TWFF_BMP) {
        rc = setCapability(entry, app, ds, ICAP_IMAGEFILEFORMAT, TWTY_UINT16, fileFormat);
        std::printf("     ICAP_IMAGEFILEFORMAT = %s -> %u (%s)\n",
                    fileFormat == TWFF_TIFF ? "TWFF_TIFF" : "(other)", rc, returnCodeName(rc));
        if (rc != TWRC_SUCCESS) return false;
    }

    if (mechanism == Mechanism::File) {
        TW_SETUPFILEXFER setup{};
        lstrcpynA(setup.FileName, outputPath, sizeof(setup.FileName));
        setup.Format = TWFF_BMP;
        setup.VRefNum = 0;
        rc = entry(app, ds, DG_CONTROL, DAT_SETUPFILEXFER, MSG_SET, &setup);
        std::printf("     output file set -> %u (%s)\n", rc, returnCodeName(rc));
        if (rc != TWRC_SUCCESS) return false;
    }

    // ShowUI FALSE is what makes this unattended, and is exactly what a server-side
    // application does when it scans without prompting the user.
    TW_USERINTERFACE ui{};
    ui.ShowUI = FALSE;
    ui.ModalUI = FALSE;
    ui.hParent = window;

    rc = entry(app, ds, DG_CONTROL, DAT_USERINTERFACE, MSG_ENABLEDS, &ui);
    std::printf("     MSG_ENABLEDS -> %u (%s)\n", rc, returnCodeName(rc));
    if (rc != TWRC_SUCCESS) return false;

    std::printf("     waiting for the scanner (up to %d s)...\n", timeoutSeconds);
    TW_UINT16 signalled = waitForTransfer(entry, app, ds, timeoutSeconds);

    bool transferred = false;
    bool postTransferClean = true;

    if (signalled == MSG_XFERREADY) {
        if (mechanism == Mechanism::Memory) {
            transferred = transferViaMemory(entry, app, ds, outputPath);
        } else if (mechanism == Mechanism::MemFile) {
            transferred = transferViaMemoryFile(entry, app, ds, outputPath);
        } else if (mechanism == Mechanism::Native) {
            transferred = transferViaNative(entry, app, ds, outputPath);
        } else {
            TW_IMAGEINFO info{};
            if (entry(app, ds, DG_IMAGE, DAT_IMAGEINFO, MSG_GET, &info) == TWRC_SUCCESS) {
                std::printf("     image: %ld x %ld, %u bits/pixel, %u samples\n",
                            static_cast<long>(info.ImageWidth), static_cast<long>(info.ImageLength),
                            info.BitsPerPixel, info.SamplesPerPixel);
            }

            rc = entry(app, ds, DG_IMAGE, DAT_IMAGEFILEXFER, MSG_GET, nullptr);
            std::printf("     DAT_IMAGEFILEXFER -> %u (%s)\n", rc, returnCodeName(rc));
            transferred = rc == TWRC_XFERDONE;
        }

        // Before ending the transfer, while the source is still in state 7 holding the page -
        // which is where an application asks its follow-up questions.
        if (transferred) postTransferClean = probePostTransfer(entry, app, ds);

        TW_PENDINGXFERS pending{};
        entry(app, ds, DG_CONTROL, DAT_PENDINGXFERS, MSG_ENDXFER, &pending);
        std::printf("     pages still pending: %u\n", pending.Count);

        // Discard anything left in the feeder so MSG_DISABLEDS is legal from state 5.
        entry(app, ds, DG_CONTROL, DAT_PENDINGXFERS, MSG_RESET, &pending);
    }
    else if (signalled == MSG_CLOSEDSREQ) {
        std::printf("     the source asked to close - the scan failed on the far side.\n");
    }
    else {
        std::printf("     TIMED OUT - no page arrived.\n");
    }

    entry(app, ds, DG_CONTROL, DAT_USERINTERFACE, MSG_DISABLEDS, &ui);

    if (transferred && !probeAfterDisable(entry, app, ds)) postTransferClean = false;

    if (!transferred) return false;

    // A page that arrived intact but left an error on the application's screen is a failure
    // here. That combination is the whole fault: the image is right and the user is told the
    // scan went wrong.
    if (!postTransferClean) {
        std::printf("     FAIL: the page transferred, but an operation after it returned\n");
        std::printf("           TWCC_CAPUNSUPPORTED - that is the dialog the user sees.\n");
        return false;
    }

    WIN32_FILE_ATTRIBUTE_DATA attributes{};
    if (GetFileAttributesExA(outputPath, GetFileExInfoStandard, &attributes)) {
        std::printf("     wrote %s (%lu bytes)\n", outputPath, attributes.nFileSizeLow);
        return attributes.nFileSizeLow > 0;
    }

    std::printf("     the source reported success but no file was written.\n");
    return false;
}

TW_IDENTITY makeApplicationIdentity() {
    TW_IDENTITY app{};
    app.Id = 0;
    app.ProtocolMajor = 2;
    app.ProtocolMinor = 4;
    app.SupportedGroups = DG_CONTROL | DG_IMAGE | DF_APP2;
    app.Version.MajorNum = 1;
    app.Version.MinorNum = 0;
    app.Version.Language = TWLG_ENGLISH_USA;
    app.Version.Country = TWCY_USA;
    lstrcpynA(app.Version.Info, "1.0", sizeof(app.Version.Info));
    lstrcpynA(app.Manufacturer, "ScanBridge", sizeof(app.Manufacturer));
    lstrcpynA(app.ProductFamily, "Diagnostics", sizeof(app.ProductFamily));
    lstrcpynA(app.ProductName, "dsmprobe", sizeof(app.ProductName));
    return app;
}

}  // namespace

int main(int argc, char** argv) {
    if (argc < 3) {
        std::printf("usage: dsmprobe <twaindsm.dll> <ScanBridge.ds> [--toplevel] [--open]\n"
                    "       [--scan <out.bmp>] [--native | --memory | --memfile]\n"
                    "       [--tiff] [--timeout <seconds>]\n"
                    "\n"
                    "  --scan     acquire a page; file transfer unless a mechanism is given\n"
                    "  --native   DAT_IMAGENATIVEXFER   (one DIB in a global handle)\n"
                    "  --memory   DAT_IMAGEMEMXFER      (raw scanlines, strip by strip) - what\n"
                    "             NAPS2 uses, so it is the one that matters most\n"
                    "  --memfile  DAT_IMAGEMEMFILEXFER  (a whole image file, in buffers)\n"
                    "  --tiff     emit TIFF instead of BMP for the two file-shaped mechanisms\n"
                    "\n"
                    "All four are separate code paths in the data source. Passing one says\n"
                    "nothing about the others.\n");
        return 64;
    }

    const char* managerPath = argv[1];
    const char* dataSourcePath = argv[2];
    bool alsoTopLevel = false;
    bool alsoOpen = false;
    bool alsoScan = false;
    Mechanism mechanism = Mechanism::File;
    TW_UINT32 fileFormat = TWFF_BMP;
    const char* scanOutput = nullptr;
    int scanTimeout = 120;

    for (int i = 3; i < argc; ++i) {
        if (lstrcmpiA(argv[i], "--toplevel") == 0) {
            alsoTopLevel = true;
        } else if (lstrcmpiA(argv[i], "--open") == 0) {
            alsoOpen = true;
        } else if (lstrcmpiA(argv[i], "--scan") == 0) {
            // Scanning requires the source to be open, so --scan implies --open.
            alsoScan = true;
            alsoOpen = true;
            if (i + 1 < argc && argv[i + 1][0] != '-') scanOutput = argv[++i];
        } else if (lstrcmpiA(argv[i], "--memory") == 0) {
            mechanism = Mechanism::Memory;
        } else if (lstrcmpiA(argv[i], "--memfile") == 0) {
            mechanism = Mechanism::MemFile;
        } else if (lstrcmpiA(argv[i], "--native") == 0) {
            mechanism = Mechanism::Native;
        } else if (lstrcmpiA(argv[i], "--tiff") == 0) {
            fileFormat = TWFF_TIFF;
        } else if (lstrcmpiA(argv[i], "--timeout") == 0 && i + 1 < argc) {
            scanTimeout = atoi(argv[++i]);
            if (scanTimeout <= 0) scanTimeout = 120;
        }
    }

    char defaultOutput[MAX_PATH]{};
    if (alsoScan && !scanOutput) {
        char temp[MAX_PATH]{};
        GetTempPathA(MAX_PATH, temp);
        wsprintfA(defaultOutput, "%sScanBridge-probe.bmp", temp);
        scanOutput = defaultOutput;
    }

    std::printf("\n=== real-DSM probe (%d-bit) ===\n\n", kBits);
    std::printf("   manager     : %s\n", managerPath);
    std::printf("   data source : %s\n", dataSourcePath);
    std::printf("   layout      : sub-folder%s\n\n", alsoTopLevel ? " + top-level copy" : "");

    // ---- scratch tree ----------------------------------------------------
    char temp[MAX_PATH]{};
    GetTempPathA(MAX_PATH, temp);
    wsprintfA(g_fakeWindowsDirectory, "%srsprobe%lu", temp, GetCurrentProcessId());

    if (!buildScratchTree(g_fakeWindowsDirectory, dataSourcePath, alsoTopLevel)) return 1;
    std::printf("   posing as windows directory: %s\n\n", g_fakeWindowsDirectory);

    // ---- load the manager and redirect its view of the world -------------
    HMODULE manager = LoadLibraryA(managerPath);
    if (!manager) {
        const DWORD error = GetLastError();
        std::printf("   could not load the manager (error %lu)%s\n", error,
                    error == ERROR_BAD_EXE_FORMAT ? " - wrong bitness for this process" : "");
        return 1;
    }

    const int patched = redirect(manager, "GetWindowsDirectoryA", &fakeGetWindowsDirectoryA) +
                        redirect(manager, "GetWindowsDirectoryW", &fakeGetWindowsDirectoryW);
    std::printf("   redirected %d import slot(s)\n", patched);
    if (patched == 0) {
        // Without the redirect the manager scans the real C:\Windows, so a result here would
        // say nothing about our data source. Better to stop than to report a false negative.
        std::printf("\n   FAILED: this manager does not import GetWindowsDirectory through its\n");
        std::printf("   import table, so its search path could not be redirected. The probe\n");
        std::printf("   cannot draw any conclusion; nothing below would be meaningful.\n\n");
        FreeLibrary(manager);
        removeScratchTree(g_fakeWindowsDirectory);
        return 2;
    }

    auto entry = reinterpret_cast<DSMENTRYPROC>(GetProcAddress(manager, "DSM_Entry"));
    if (!entry) {
        std::printf("   no DSM_Entry export - this is not a data source manager\n");
        FreeLibrary(manager);
        removeScratchTree(g_fakeWindowsDirectory);
        return 2;
    }

    // ---- behave like an application --------------------------------------
    TW_IDENTITY app = makeApplicationIdentity();
    HWND window = CreateWindowExA(0, "STATIC", "dsmprobe", 0, 0, 0, 0, 0,
                                  nullptr, nullptr, nullptr, nullptr);

    TW_UINT16 rc = entry(&app, nullptr, DG_CONTROL, DAT_PARENT, MSG_OPENDSM, &window);
    if (rc != TWRC_SUCCESS) {
        std::printf("   MSG_OPENDSM failed (rc=%u)\n", rc);
        if (window) DestroyWindow(window);
        FreeLibrary(manager);
        removeScratchTree(g_fakeWindowsDirectory);
        return 3;
    }
    std::printf("   manager opened; it reports itself as TWAIN %s\n\n",
                (app.SupportedGroups & DF_DSM2) ? "2.x" : "1.x");

    std::printf("   data sources the manager reports:\n");

    int count = 0;
    int remoteScannerCount = 0;
    TW_IDENTITY ours{};
    TW_IDENTITY source{};

    rc = entry(&app, nullptr, DG_CONTROL, DAT_IDENTITY, MSG_GETFIRST, &source);
    if (rc != TWRC_SUCCESS) std::printf("     MSG_GETFIRST -> %u (%s)\n", rc, returnCodeName(rc));

    while (rc == TWRC_SUCCESS) {
        ++count;
        std::printf("     %d. \"%s\"  by %s, protocol %u.%u, groups 0x%08lX\n",
                    count, source.ProductName, source.Manufacturer,
                    source.ProtocolMajor, source.ProtocolMinor,
                    static_cast<unsigned long>(source.SupportedGroups));

        if (lstrcmpiA(source.Manufacturer, "ScanBridge") == 0) {
            ++remoteScannerCount;
            ours = source;
        }

        ZeroMemory(&source, sizeof(source));
        rc = entry(&app, nullptr, DG_CONTROL, DAT_IDENTITY, MSG_GETNEXT, &source);
    }
    if (count == 0) std::printf("     (none)\n");

    // ---- optionally go one step further ----------------------------------
    //
    // Listing proves discovery. Opening proves the manager will actually hand the source to
    // an application, which is a separate decision and can fail on its own.
    bool scanned = false;

    if (alsoOpen && remoteScannerCount > 0) {
        std::printf("\n   MSG_OPENDS on \"%s\":\n", ours.ProductName);
        TW_UINT16 openRc = entry(&app, nullptr, DG_CONTROL, DAT_IDENTITY, MSG_OPENDS, &ours);
        std::printf("     -> %u (%s)\n", openRc, returnCodeName(openRc));

        if (openRc == TWRC_SUCCESS) {
            std::printf("     opened; assigned Id %u\n", ours.Id);

            if (alsoScan)
                scanned = acquire(entry, &app, &ours, window, scanOutput, scanTimeout,
                                  mechanism, fileFormat);

            entry(&app, nullptr, DG_CONTROL, DAT_IDENTITY, MSG_CLOSEDS, &ours);
        } else {
            TW_STATUS status{};
            if (entry(&app, &ours, DG_CONTROL, DAT_STATUS, MSG_GET, &status) == TWRC_SUCCESS)
                std::printf("     condition code %u\n", status.ConditionCode);
            std::printf("     (expected outside an RDP session: there is no session agent\n");
            std::printf("      to connect to, so the source refuses to open)\n");
        }
    }

    entry(&app, nullptr, DG_CONTROL, DAT_PARENT, MSG_CLOSEDSM, &window);
    if (window) DestroyWindow(window);
    FreeLibrary(manager);
    removeScratchTree(g_fakeWindowsDirectory);

    std::printf("\n   >>> %d source(s) listed; ScanBridge appeared %d time(s) <<<\n",
                count, remoteScannerCount);

    if (remoteScannerCount == 1) {
        if (alsoScan && !scanned) {
            std::printf("\n   FAIL: discovered and opened, but no page was transferred.\n\n");
            return 6;
        }
        std::printf("\n   PASS: a real TWAIN manager discovers the data source%s.\n\n",
                    scanned ? " and a page transferred end to end" : "");
        return 0;
    }
    if (remoteScannerCount > 1) {
        std::printf("\n   FAIL: listed more than once - applications would show duplicates.\n\n");
        return 4;
    }
    std::printf("\n   FAIL: a real TWAIN manager does not discover the data source.\n\n");
    return 5;
}
