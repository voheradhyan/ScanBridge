# What this runs on

Two lists: what has actually been run, and what the code is written to support but nobody has
tried. The second list is not a promise. It is written down so that a bug report from one of
those environments is recognised as "first time anyone has been there" rather than argued with.

Only one hardware combination has ever executed a full scan, so the honest summary is: this is
known to work in one place, and designed to work in many.

## Verified

| | |
|---|---|
| Client OS | Windows 11, consumer edition |
| Server OS | Windows Server 2019 (build 17763), RDS Session Host, multi-user |
| Connection | `mstsc.exe` over a LAN |
| Scanner | Consumer multifunction inkjet, WIA only, flatbed |
| Application | NAPS2, **32-bit**, memory transfer |
| Transports | RDP dynamic virtual channel; loopback (self-test). |
| Transfer mechanisms | native, memory, memory-file and file, both bitnesses, against real hardware |
| Installation | Both installers run on their own machine: the server's elevated on the 2019 host — service registered, session agent started, a scan completed through it — and the client's unelevated on the Windows 11 laptop |

## Supported by design, never exercised

Each row says what would most likely break first, so a failure can be recognised quickly.

| Environment | Written to work because | Watch for |
|---|---|---|
| Windows 10 / 8.1 client | No API newer than Windows 8.1 is used; the DVC plugin API is unchanged since Windows 7 | Add-in registration under a different Remote Desktop client build |
| Windows Server 2016 / 2022 / 2025 | Only WTS APIs stable across all of them | Session enumeration differences on 2025 |
| A non-RDS host (Windows 10/11 with Remote Desktop enabled) | Session logic keys off "is this a remote session", not off RDS | Single-session hosts number sessions differently |
| **TWAIN-only scanners** | Full TWAIN backend, x86 and x64 hosts | **This path has never run.** It carried three wrong constants until 14 Aug 2026 precisely because the only scanner on hand was WIA. Treat the first TWAIN scan on real hardware as unproven. |
| Sheet-fed and duplex scanners | Feeder, duplex and page-count capabilities are negotiated from what the device reports | `CAP_XFERCOUNT` handling with a real ADF; ending a multi-page job |
| Several scanners on one PC | Enumeration collapses WIA/TWAIN duplicates and honours a chosen default | The WIA-to-TWAIN shim's `WIA-` name prefix is assumed; a localised Windows may name it differently |
| Several users scanning at once on one host | Every pipe, key and agent is scoped by SID and session id | Contention for one client PC from two sessions |
| 64-bit scanning applications | Both bitnesses of the data source are installed | 64-bit TWAIN needs `TWAINDSM.dll`, which many machines lack |
| Non-Latin machine names | The scanner name is truncated on a UTF-8 character boundary, not a byte | Applications that assume ASCII in `TW_STR32` |
| The encrypted TCP fallback | Same protocol, AES-256-GCM, pairing key | Verified between two processes; **never between two machines** |

## Known not to work, by design

| | Why |
|---|---|
| Microsoft Store "Windows App" / MSRDC, FreeRDP, Royal TS, Jump Desktop | They do not load RDP add-ins. There is no workaround; `mstsc.exe`, RemoteApp and RDWeb are fine. |
| Windows Fax and Scan, the Windows Scan app | WIA-only. A virtual WIA device needs a Microsoft-attested driver signature (EV certificate and a Partner Center account). Every TWAIN application works. TSScan has the same boundary. |
| macOS or Linux clients | The client half is Windows-specific: DPAPI, WTS, named pipes, and an `mstsc.exe` add-in. |
| Scanning *to* a server-side scanner | Backwards from this product's purpose. |

## Environment assumptions the code deliberately does not make

Worth stating, because each was checked rather than assumed:

- **No hard-coded paths.** Locations come from `AppPaths`, resolved from environment folders.
  `InstallDirectory` resolves through `ProgramW6432` so that a 32-bit component and a 64-bit one
  agree on one answer rather than differing by their own bitness.
- **No hard-coded account names.** The installer grants rights by SID (`*S-1-5-32-545`), not by
  the name "Users", which is localised on a German or French Windows.
- **No OS version gates.** Nothing branches on a build number.
- **No culture-sensitive parsing.** The wire protocol is binary; the only culture-aware
  formatting is timestamps shown in the interface.
- **No assumption of exactly one session, one scanner or one link.** All three are collections.
