// Drives a real TWAIN Data Source Manager and lists every data source it reports.
//
// This is the view a scanning application has. Our own log can show that a DSM loaded
// ScanBridge.ds, and a direct call can show the data source answers correctly, and yet
// the application still lists nothing - which means the disagreement is inside the manager.
// This prints what the manager actually returns, which is the only way to see that.
//
// Tries each DSM in turn so the output also shows WHICH manager an application would get:
//   TWAINDSM.dll   the TWAIN 2.x manager, shipped by applications and some scanner vendors
//   twain_32.dll   the legacy manager Windows itself ships (32-bit only)

#include <windows.h>
#include <cstdio>
#include <shlwapi.h>
#include "../include/rs_twain.h"

namespace {

typedef TW_UINT16(FAR PASCAL* DsmEntryProc)(pTW_IDENTITY, pTW_IDENTITY,
                                            TW_UINT32, TW_UINT16, TW_UINT16, TW_MEMREF);

const char* returnCodeName(TW_UINT16 rc) {
    switch (rc) {
        case TWRC_SUCCESS:     return "TWRC_SUCCESS";
        case TWRC_FAILURE:     return "TWRC_FAILURE";
        case TWRC_CHECKSTATUS: return "TWRC_CHECKSTATUS";
        case TWRC_CANCEL:      return "TWRC_CANCEL";
        case TWRC_ENDOFLIST:   return "TWRC_ENDOFLIST (the manager found no data sources)";
        default:               return "(other)";
    }
}

TW_IDENTITY makeApplicationIdentity() {
    TW_IDENTITY app{};
    app.Id = 0;
    app.ProtocolMajor = 2;
    app.ProtocolMinor = 4;
    // DF_APP2 tells a 2.x manager we understand DAT_ENTRYPOINT. Without it, a 2.x DSM
    // behaves as a 1.x one, which is itself worth being able to observe.
    app.SupportedGroups = DG_CONTROL | DG_IMAGE | DF_APP2;
    app.Version.MajorNum = 1;
    app.Version.MinorNum = 0;
    app.Version.Language = TWLG_ENGLISH_USA;
    app.Version.Country = TWCY_USA;
    lstrcpynA(app.Version.Info, "1.0", sizeof(app.Version.Info));
    lstrcpynA(app.Manufacturer, "ScanBridge", sizeof(app.Manufacturer));
    lstrcpynA(app.ProductFamily, "Diagnostics", sizeof(app.ProductFamily));
    lstrcpynA(app.ProductName, "dsmenum", sizeof(app.ProductName));
    return app;
}

bool tryManager(const char* dsmName) {
    std::printf("\n-- %s ------------------------------------------------\n", dsmName);

    HMODULE module = LoadLibraryA(dsmName);
    if (!module) {
        const DWORD error = GetLastError();
        switch (error) {
            case ERROR_MOD_NOT_FOUND:
            case ERROR_FILE_NOT_FOUND:
                std::printf("   not present\n");
                break;
            case ERROR_BAD_EXE_FORMAT:
                // A manager of the other bitness. Expected and harmless - it simply belongs
                // to applications of the other kind.
                std::printf("   wrong bitness for this process - skipping\n");
                break;
            default:
                std::printf("   could not load (GetLastError = %lu)\n", error);
                break;
        }
        return false;
    }

    char resolved[MAX_PATH]{};
    GetModuleFileNameA(module, resolved, MAX_PATH);
    std::printf("   loaded: %s\n", resolved);

    auto entry = reinterpret_cast<DsmEntryProc>(GetProcAddress(module, "DSM_Entry"));
    if (!entry) {
        std::printf("   no DSM_Entry export - unusable\n");
        FreeLibrary(module);
        return false;
    }

    TW_IDENTITY app = makeApplicationIdentity();

    // A DSM needs a window handle to open. A hidden one is enough for enumeration.
    HWND window = CreateWindowExA(0, "STATIC", "dsmenum", 0, 0, 0, 0, 0,
                                  nullptr, nullptr, nullptr, nullptr);
    TW_UINT16 rc = entry(&app, nullptr, DG_CONTROL, DAT_PARENT, MSG_OPENDSM, &window);
    if (rc != TWRC_SUCCESS) {
        std::printf("   MSG_OPENDSM failed (rc=%u)\n", rc);
        if (window) DestroyWindow(window);
        FreeLibrary(module);
        return false;
    }
    std::printf("   manager opened. app.SupportedGroups now 0x%08lX%s\n",
                static_cast<unsigned long>(app.SupportedGroups),
                (app.SupportedGroups & DF_DSM2) ? "  (TWAIN 2.x manager)" : "  (TWAIN 1.x manager)");

    std::printf("\n   data sources reported:\n");

    int count = 0;
    bool sawScanBridge = false;
    TW_IDENTITY source{};

    rc = entry(&app, nullptr, DG_CONTROL, DAT_IDENTITY, MSG_GETFIRST, &source);

    // An empty list and a refused call look identical from the outside, so say which it was.
    if (rc != TWRC_SUCCESS) {
        std::printf("     MSG_GETFIRST returned %u (%s)\n", rc, returnCodeName(rc));
        if (rc == TWRC_FAILURE) {
            TW_STATUS status{};
            if (entry(&app, nullptr, DG_CONTROL, DAT_STATUS, MSG_GET, &status) == TWRC_SUCCESS)
                std::printf("     condition code %u\n", status.ConditionCode);
        }
    }

    while (rc == TWRC_SUCCESS) {
        ++count;
        std::printf("     %d. \"%s\"\n", count, source.ProductName);
        std::printf("        by %s, protocol %u.%u, groups 0x%08lX\n",
                    source.Manufacturer, source.ProtocolMajor, source.ProtocolMinor,
                    static_cast<unsigned long>(source.SupportedGroups));

        if (lstrcmpiA(source.Manufacturer, "ScanBridge") == 0) sawScanBridge = true;

        ZeroMemory(&source, sizeof(source));
        rc = entry(&app, nullptr, DG_CONTROL, DAT_IDENTITY, MSG_GETNEXT, &source);
    }

    if (count == 0) std::printf("     (none)\n");

    std::printf("\n   >>> %d data source(s); ScanBridge %s <<<\n",
                count, sawScanBridge ? "FOUND" : "NOT FOUND");

    entry(&app, nullptr, DG_CONTROL, DAT_PARENT, MSG_CLOSEDSM, &window);
    if (window) DestroyWindow(window);
    FreeLibrary(module);
    return true;
}

}  // namespace

// Where the legacy manager will look for data sources.
//
// twain_32.dll builds its search path from GetWindowsDirectory(), and on a Terminal Server
// that is NOT always C:\Windows: the OS can hand each user a private Windows directory for
// old-application compatibility. When that happens the manager scans a folder no installer
// ever writes to, finds nothing at all - not even Microsoft's own wiatwain.ds - and every
// application on the server reports "no scanners". Printing both answers makes that visible
// instead of leaving it as a mystery.
void reportWindowsDirectories() {
    char perUser[MAX_PATH]{};
    char systemWide[MAX_PATH]{};
    GetWindowsDirectoryA(perUser, MAX_PATH);
    GetSystemWindowsDirectoryA(systemWide, MAX_PATH);

    std::printf("\n-- where the manager will look ---------------------------------\n");
    std::printf("   GetWindowsDirectory()       : %s\n", perUser);
    std::printf("   GetSystemWindowsDirectory() : %s\n", systemWide);

    if (lstrcmpiA(perUser, systemWide) != 0) {
        std::printf("\n   *** THESE DIFFER ***\n");
        std::printf("   This is a Terminal Server handing out a private Windows directory.\n");
        std::printf("   The manager is scanning the first path, but drivers are installed\n");
        std::printf("   into the second, so it finds nothing.\n");
    } else {
        std::printf("   (same - the usual case)\n");
    }

    // List what is actually in each candidate folder, so a wrong path is obvious.
    for (int which = 0; which < 2; ++which) {
        const char* base = which == 0 ? perUser : systemWide;
        if (which == 1 && lstrcmpiA(perUser, systemWide) == 0) break;

        char pattern[MAX_PATH]{};
        wsprintfA(pattern, "%s\\twain_%d\\*.ds", base, static_cast<int>(sizeof(void*) * 8) == 64 ? 64 : 32);
        std::printf("\n   %s\n", pattern);

        WIN32_FIND_DATAA find{};
        HANDLE handle = FindFirstFileA(pattern, &find);
        if (handle == INVALID_HANDLE_VALUE) {
            std::printf("      (no .ds files there)\n");
            continue;
        }
        do {
            std::printf("      %s\n", find.cFileName);
        } while (FindNextFileA(handle, &find));
        FindClose(handle);
    }
}

// Finds TWAINDSM.dll copies that applications have installed alongside themselves.
//
// This is the manager that matters. Scanning applications ship their own rather than relying
// on Windows, so testing only the system one answers a question nobody was asking: what an
// application actually sees depends on the copy sitting next to its .exe.
// Recursive but depth-limited. Applications do not all put the manager beside the .exe -
// some nest it under lib\ or a per-architecture folder - so a one-level look misses them,
// while an unbounded walk of the whole disk would take minutes.
void findManagers(const char* directory, int depth, int& tested) {
    if (depth > 3) return;

    char pattern[MAX_PATH]{};
    wsprintfA(pattern, "%s\\*", directory);

    WIN32_FIND_DATAA find{};
    HANDLE handle = FindFirstFileA(pattern, &find);
    if (handle == INVALID_HANDLE_VALUE) return;

    do {
        if (find.cFileName[0] == '.') continue;

        char child[MAX_PATH]{};
        wsprintfA(child, "%s\\%s", directory, find.cFileName);

        if (find.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) {
            findManagers(child, depth + 1, tested);
        } else if (lstrcmpiA(find.cFileName, "twaindsm.dll") == 0) {
            ++tested;
            tryManager(child);
        }
    } while (FindNextFileA(handle, &find));

    FindClose(handle);
}

/// Reports scanning applications that are installed, so "no manager found" can be told apart
/// from "no application installed" - two very different situations.
void listScanningApplications() {
    const char* names[] = { "NAPS2", "Acrobat", "ABBYY", "Scan", "Twain", "Kofax", "Dynamsoft" };
    const char* roots[] = { "C:\\Program Files", "C:\\Program Files (x86)" };

    std::printf("\n   scanning-looking applications installed:\n");
    bool found = false;

    for (const char* root : roots) {
        char pattern[MAX_PATH]{};
        wsprintfA(pattern, "%s\\*", root);

        WIN32_FIND_DATAA find{};
        HANDLE handle = FindFirstFileA(pattern, &find);
        if (handle == INVALID_HANDLE_VALUE) continue;

        do {
            if (!(find.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY)) continue;
            if (find.cFileName[0] == '.') continue;

            for (const char* name : names) {
                if (StrStrIA(find.cFileName, name)) {
                    std::printf("      %s\\%s\n", root, find.cFileName);
                    found = true;
                    break;
                }
            }
        } while (FindNextFileA(handle, &find));

        FindClose(handle);
    }

    if (!found) std::printf("      (none recognised)\n");
}

void tryApplicationManagers(int& tested) {
    char localAppData[MAX_PATH]{};
    GetEnvironmentVariableA("LOCALAPPDATA", localAppData, MAX_PATH);

    char perUserPrograms[MAX_PATH]{};
    if (localAppData[0]) wsprintfA(perUserPrograms, "%s\\Programs", localAppData);

    const char* roots[] = {
        "C:\\Program Files",
        "C:\\Program Files (x86)",
        perUserPrograms,        // some installers default to a per-user location
    };

    for (const char* root : roots) {
        if (!root || !root[0]) continue;
        if (GetFileAttributesA(root) == INVALID_FILE_ATTRIBUTES) continue;
        findManagers(root, 0, tested);
    }
}

int main(int argc, char** argv) {
    std::printf("\n=== TWAIN manager enumeration (%d-bit process) ===\n",
                static_cast<int>(sizeof(void*) * 8));
    std::printf("\nThis is the list a scanning application of this bitness would see.\n");

    // An explicit path wins, so any manager can be pointed at directly.
    if (argc > 1) {
        std::printf("\nTesting the manager given on the command line.\n");
        tryManager(argv[1]);
        std::printf("\n");
        return 0;
    }

    reportWindowsDirectories();

    bool any = false;
    any |= tryManager("TWAINDSM.dll");
    if (sizeof(void*) == 4) any |= tryManager("twain_32.dll");

    // The important one: managers shipped inside scanning applications.
    int applicationManagers = 0;
    tryApplicationManagers(applicationManagers);

    if (applicationManagers == 0) {
        std::printf("\n-- application-supplied managers --------------------------------\n");
        std::printf("   No twaindsm.dll found under Program Files, Program Files (x86)\n");
        std::printf("   or the per-user Programs folder (searched 3 levels deep).\n");
        listScanningApplications();
        std::printf("\n   If an application IS listed above, it keeps its TWAIN manager\n");
        std::printf("   elsewhere or uses a different one. Point this tool straight at it:\n");
        std::printf("      dsmenum.exe \"C:\\path\\to\\twaindsm.dll\"\n");
    }

    if (!any && applicationManagers == 0) {
        std::printf("\nNo TWAIN manager could be loaded at all. An application of this\n");
        std::printf("bitness can only scan if it ships its own TWAINDSM.dll.\n");
    }

    std::printf("\n");
    return 0;
}
