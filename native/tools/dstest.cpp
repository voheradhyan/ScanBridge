// Loads ScanBridge.ds exactly the way a TWAIN Data Source Manager does, and reports
// where it stops.
//
// Written because a real DSM gives no feedback at all: it loads a data source, decides it
// does not like it, and unloads without a word. This reproduces those same three steps with
// the outcome of each one printed, so a failure can be located instead of guessed at.
//
//   1. LoadLibrary on the .ds
//   2. GetProcAddress("DS_Entry")
//   3. DG_CONTROL / DAT_IDENTITY / MSG_GET
//
// Step 2 is the one that matters, and it is worth being precise about which name is being
// looked up. A data source exports DS_Entry (five arguments). DSM_Entry — six arguments — is
// what a data source *manager* exports for applications to call. TWAINDSM.dll resolves
// "DS_Entry" and, finding nothing, unloads the library and moves to the next file without
// telling the application anything; the scanner simply never appears. An earlier version of
// this tool resolved DSM_Entry, so it passed against a data source no manager could use.
//
// Build both bitnesses; a 32-bit data source can only be loaded by a 32-bit process.

#include <windows.h>
#include <cstdio>
#include "../include/rs_twain.h"

namespace {

const char* returnCodeName(TW_UINT16 rc) {
    switch (rc) {
        case TWRC_SUCCESS:     return "TWRC_SUCCESS";
        case TWRC_FAILURE:     return "TWRC_FAILURE";
        case TWRC_CHECKSTATUS: return "TWRC_CHECKSTATUS";
        case TWRC_CANCEL:      return "TWRC_CANCEL";
        case TWRC_ENDOFLIST:   return "TWRC_ENDOFLIST";
        default:               return "(other)";
    }
}

void describeLoadFailure(DWORD error) {
    std::printf("      GetLastError = %lu", error);
    switch (error) {
        case ERROR_MOD_NOT_FOUND:
            std::printf("  (ERROR_MOD_NOT_FOUND - a DLL it depends on is missing)\n");
            break;
        case ERROR_BAD_EXE_FORMAT:
            std::printf("  (ERROR_BAD_EXE_FORMAT - wrong bitness for this process)\n");
            break;
        case ERROR_DLL_INIT_FAILED:
            std::printf("  (ERROR_DLL_INIT_FAILED - DllMain returned FALSE)\n");
            break;
        case ERROR_PROC_NOT_FOUND:
            std::printf("  (ERROR_PROC_NOT_FOUND)\n");
            break;
        default:
            std::printf("\n");
            break;
    }
}

}  // namespace

int main(int argc, char** argv) {
    const char* path = argc > 1 ? argv[1] : "ScanBridge.ds";

    std::printf("\n=== ScanBridge data source test (%d-bit process) ===\n\n",
                static_cast<int>(sizeof(void*) * 8));
    std::printf("  target: %s\n\n", path);

    // ---- step 1: load ----------------------------------------------------
    std::printf("  [1] LoadLibrary\n");
    HMODULE module = LoadLibraryA(path);
    if (!module) {
        DWORD error = GetLastError();
        std::printf("      FAILED\n");
        describeLoadFailure(error);
        std::printf("\n  A real DSM would silently skip the data source here.\n\n");
        return 1;
    }
    std::printf("      ok (module at %p)\n\n", static_cast<void*>(module));

    // ---- step 2: find the entry point ------------------------------------
    std::printf("  [2] GetProcAddress(\"DS_Entry\")\n");
    auto entry = reinterpret_cast<DSENTRYPROC>(GetProcAddress(module, "DS_Entry"));
    if (!entry) {
        std::printf("      FAILED - GetLastError = %lu\n", GetLastError());

        // Naming the specific mistake, because the symptom is identical for every cause and
        // this particular one costs days: the file loads, initialises, logs that it loaded,
        // and is discarded.
        if (GetProcAddress(module, "DSM_Entry")) {
            std::printf("\n      This file exports DSM_Entry instead. That is the entry point a\n");
            std::printf("      data source *manager* exports for applications; a data source has\n");
            std::printf("      to export DS_Entry. TWAINDSM.dll logs \"DS_Entry is null\" and\n");
            std::printf("      skips the file, so every application reports no scanners.\n");
        } else {
            std::printf("\n      The export is missing or decorated (x86 __stdcall emits\n");
            std::printf("      _DS_Entry@20 unless the .def undecorates it).\n");
        }
        std::printf("\n");
        FreeLibrary(module);
        return 2;
    }
    std::printf("      ok (entry at %p)\n\n", reinterpret_cast<void*>(entry));

    // ---- step 3: ask who it is -------------------------------------------
    //
    // Asked twice, as a 1.x application and as a 2.x one. The data source is meant to
    // describe itself differently to each, because a 1.x manager will not host a source
    // that claims to be 2.x - so both answers have to be checked, not just the modern one.
    int failures = 0;

    for (int pass = 0; pass < 2; ++pass) {
        const bool asTwain2 = pass == 1;
        std::printf("  [3.%d] DG_CONTROL / DAT_IDENTITY / MSG_GET  (as a TWAIN %s application)\n",
                    pass + 1, asTwain2 ? "2.x" : "1.x");

        TW_IDENTITY application{};
        application.Id = 0;
        application.ProtocolMajor = asTwain2 ? 2 : 1;
        application.ProtocolMinor = asTwain2 ? 4 : 9;
        application.SupportedGroups = DG_CONTROL | DG_IMAGE | (asTwain2 ? DF_APP2 : 0);
        application.Version.MajorNum = 1;
        application.Version.MinorNum = 0;
        application.Version.Language = TWLG_ENGLISH_USA;
        application.Version.Country = TWCY_USA;
        lstrcpynA(application.Version.Info, "1.0", sizeof(application.Version.Info));
        lstrcpynA(application.Manufacturer, "ScanBridge", sizeof(application.Manufacturer));
        lstrcpynA(application.ProductFamily, "Test", sizeof(application.ProductFamily));
        lstrcpynA(application.ProductName, "dstest", sizeof(application.ProductName));

        TW_IDENTITY reported{};
        TW_UINT16 rc = entry(&application, DG_CONTROL, DAT_IDENTITY, MSG_GET, &reported);

        std::printf("        returned %u (%s)\n", rc, returnCodeName(rc));
        if (rc != TWRC_SUCCESS) {
            std::printf("        FAILED - no application of this kind would list it.\n\n");
            ++failures;
            continue;
        }

        std::printf("        ProductName    : \"%s\"\n", reported.ProductName);
        std::printf("        Manufacturer   : \"%s\"\n", reported.Manufacturer);
        std::printf("        Protocol       : %u.%u\n",
                    reported.ProtocolMajor, reported.ProtocolMinor);
        std::printf("        SupportedGroups: 0x%08lX%s\n",
                    static_cast<unsigned long>(reported.SupportedGroups),
                    (reported.SupportedGroups & DF_DS2) ? "  (declares DF_DS2)" : "");

        const bool claims2 = reported.ProtocolMajor >= 2 ||
                             (reported.SupportedGroups & DF_DS2) != 0;
        if (!asTwain2 && claims2) {
            std::printf("        WRONG - claims TWAIN 2.x to a 1.x caller. A legacy manager\n");
            std::printf("        such as twain_32.dll will silently skip it.\n");
            ++failures;
        } else {
            std::printf("        correct for this caller.\n");
        }
        std::printf("\n");
    }

    FreeLibrary(module);

    if (failures == 0) {
        std::printf("  RESULT: the data source loads and identifies correctly to both\n");
        std::printf("  1.x and 2.x applications.\n\n");
        return 0;
    }

    std::printf("  RESULT: %d problem(s) above.\n\n", failures);
    return 3;
}
