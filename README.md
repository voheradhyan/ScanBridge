<p align="center">
  <img src="assets/scanbridge.svg" alt="" width="112" height="112">
</p>

# ScanBridge

Scanner redirection over RDP. A scanner physically attached to your Windows PC becomes usable
by applications running inside a Remote Desktop session on a Windows Server — with no step
where you scan locally, save a PDF and upload it.

Functionally comparable to TSScan, built from scratch.

```
[ Physical scanner ]
        │ TWAIN / WIA
[ ScanHost ]  ──── isolated per job; x64 is a role of ScanBridge-Client.exe,
        │           x86 ships as a separate file (a driver's bitness picks the host)
        │ named pipe (ACL: one SID, HMAC)
[ ScanBridge-Client.exe ]  ──── tray agent on your PC
        │ named pipe
[ ScanBridge.DvcPlugin.dll ]  ──── loaded inside mstsc.exe
        │
        │  RDP Dynamic Virtual Channel "ScanBridge"  (inside the existing :3389)
        │
[ ScanBridge-Server.exe --session-agent ]  ──── one per RDP session, runs as the user
        │ named pipe (ACL: one SID, HMAC)
[ ScanBridge.ds ]  ──── virtual TWAIN Data Source, x86 + x64
        │ TWAIN
[ Acrobat / ERP / DMS / ABBYY / NAPS2 ]
```

Two files are what you actually deploy — see [What ships](#what-ships) below. The session
agent and the 64-bit ScanHost are roles of those two executables, chosen by a command-line
switch, rather than separate files; `ScanBridge.ds` and `ScanBridge.DvcPlugin.dll`
stay separate because they load into a third-party process (the host application, `mstsc.exe`)
rather than running on their own.

## Status

| Component | State |
|---|---|
| Virtual TWAIN Data Source (C++, x86 + x64) | Built. Full state machine 3→7, capability negotiation, native/memory/file transfer |
| RDP DVC plugin (C++, x86 + x64) | Built. `IWTSPlugin` / `IWTSListenerCallback` / `IWTSVirtualChannelCallback` |
| Protocol (C# + C++ mirror) | Built, 29 unit tests green |
| WIA backend | Built, **verified against real hardware** |
| TWAIN backend | Built, **verified against real hardware** (enumeration + capability negotiation) |
| ScanHost (x64 + self-contained x86) | Built, verified |
| Local agent | Built, verified end to end |
| Session agent + relay | Built, WTS interop verified at runtime |
| Windows service | Built |
| WPF tray UI | Built |
| Installers — setup window with folder choice, shortcuts, start-with-Windows; `--install` / `--uninstall` still there for unattended use | Built, run on both machines |
| In-memory scan preview | Built — a test scan is shown and never written to disk |
| **Discovery by a real TWAIN manager** | **Verified**, x86 and x64, against an unmodified `twaindsm.dll` |
| **Full scan, everything but the RDP hop** | **Verified** — real page off real hardware via `SELF-TEST.bat` |
| End-to-end scan through a live RDP session | **Verified** 21 Aug 2026 — NAPS2 on a Windows Server 2019 RDS host, scanner on the user's Windows 11 laptop, page and colours correct. It first worked on 14 Aug 2026, but under the product's former name; renaming it moved the add-in registration and the channel name, so that run was repeated afterwards rather than inherited. |

Everything compiles clean at `/W4` (native) and `TreatWarningsAsErrors` (managed).

## What ships

Two self-contained executables, produced by `installer\Build-All.ps1` and left in
`build\dist\`:

| | Size | Runs as |
|---|---|---|
| `ScanBridge-Server.exe` | 69 MB | administrator |
| `ScanBridge-Client.exe` | 104 MB | your own account — never elevated |

The server was 34 MB until it grew a setup window. It is a service and a command-line
installer, and could have stayed that size by keeping `--install` as the only way in — but the
person running it is an administrator standing at a server they may not install software on
often, and handing them a console switch to get the install directory right is a worse trade
than 35 MB of disk.

Each carries its own payload — the native DLLs, both bitnesses of the TWAIN data source or
the RDP add-in, the 32-bit ScanHost — embedded in the file. There is nothing else to copy
alongside them. This replaced a folder of roughly 200 files plus PowerShell installer
scripts; one file that installs itself cannot end up with a mismatched half, and a script
can no longer be run against the wrong payload.

```
ScanBridge-Server.exe --install [--to <folder>]  install the service and both data sources
ScanBridge-Server.exe --uninstall                remove all of it
ScanBridge-Server.exe --pair=<code>               pair this session with a PC, for the
                                                      direct-connection fallback
ScanBridge-Server.exe --console                   run the service in the foreground
ScanBridge-Server.exe --extract <folder>          write out the carried files and their hashes

ScanBridge-Client.exe --install [--to <folder>]  install for the current user and start it
ScanBridge-Client.exe --uninstall                remove it
ScanBridge-Client.exe --enumerate-once           list the scanners this PC can see, then exit
ScanBridge-Client.exe --pairing-code             print the code that pairs this PC with a session
ScanBridge-Client.exe --extract <folder>         write out the carried files and their hashes
```

Running either with `--help` prints this from the binary itself. Running the client with no
arguments starts the tray application (after `--install` has been run once, so it knows
where its own payload landed).

**The client refuses to run elevated**, and says why: everything it installs belongs to one
user — the RDP add-in is registered under `HKCU`, the key it authenticates with is protected
by that user's own DPAPI key, and the tray application has to run as the person whose
scanner it is. An elevated install writes all of that into the administrator's account,
where the user it was meant for cannot see any of it, and it fails in the most confusing way
possible: by appearing to succeed.

**`--extract`** needs no administrator rights and installs nothing. It writes the files
carried inside the executable to a folder you choose, alongside a SHA-256 hash for each one.
It exists because "does this installer actually contain its payload" is otherwise
unanswerable from outside the file — a build assembled without the native components
produces an installer that looks identical and only fails at the last step, on someone
else's machine. `Build-All.ps1` runs `--extract` against both executables as its last step
and fails the build if either comes back empty.

## Verification

Three things here cannot be established by testing the components against themselves, so all
three are checked against something real.

**TWAIN constants.** `installer\ConstantCheck` runs first in `Build-All.ps1`, before anything
else compiles, and checks every constant in `native/include/rs_twain.h` and `TwainTypes.cs`
against the enums compiled into NAPS2's `NTwain.dll` — a real TWAIN implementation outside this
repository. A wrong `DAT_` value cannot be caught by any test that shares our own header: the
data source manager forwards `DG`/`DAT`/`MSG` untouched, so a data source and a test that both
define the same wrong number agree with each other perfectly and disagree with every real
application. That mistake shipped past 67 unit tests, the real-manager discovery gate below, and
end-to-end scans in three of the four transfer mechanisms, and cost two days to find. It is now
a build gate rather than something to remember.

**Discovery.** `installer\Build-All.ps1` then runs `dsmprobe`, which loads a genuine
`twaindsm.dll`, patches its import table so `GetWindowsDirectory` returns a scratch folder,
drops the freshly built data source in, and asks the manager what it found. The build fails if
the manager does not list it. This exists because a data source can load, initialise, answer
every direct call correctly, pass every unit test, and still be invisible to every scanning
application on the machine — see the note at the end of `docs/04-TROUBLESHOOTING.md`.

**Scanning.** `SELF-TEST.bat`, run on the PC with the scanner, replaces the RDP hop with a
direct connection to the tray agent and scans a real page through the real driver:

```
data source → session pipe → session agent → local pipe → tray agent → ScanHost → scanner
```

Everything except the transport is the production component on its real code path, and
`dsmprobe` drives all four TWAIN transfer mechanisms (native, memory, memory-file, file)
against real hardware, not just the one a given test application happens to use. It is also the
fastest way to split a fault in half: if the self-test passes, scanning works and the problem is
RDP redirection.

## Quick start

```bash
powershell -ExecutionPolicy Bypass -File installer\Build-All.ps1
```

leaves `ScanBridge-Server.exe` and `ScanBridge-Client.exe` in `build\dist\`. Then, on
the **server**, from an elevated prompt:

```bash
build\dist\ScanBridge-Server.exe --install
```

And on the **PC with the scanner**, from an ordinary, *not* elevated prompt:

```bash
build\dist\ScanBridge-Client.exe --install
```

Connect with `mstsc.exe`, open any TWAIN application, pick
**`ScanBridge (YOUR-PC-NAME)`**.

Check the local scanner stack at any time, without RDP:

```bash
build\dist\ScanBridge-Client.exe --enumerate-once
```

## Two things to know before you deploy

**The client must connect with `mstsc.exe`.** The Microsoft Store "Windows App" / MSRDC does
not load RDP add-ins, and neither do FreeRDP, Royal TS or Jump Desktop. RemoteApp and RDWeb
use `mstsc.exe` underneath and work fine.

**Windows Fax and Scan and the Windows Scan app will not see the scanner.** They are WIA-only,
and a virtual WIA device needs a Microsoft-attested driver signature (EV certificate +
Partner Center account). Every TWAIN application — Acrobat, ERP, document management, ABBYY,
NAPS2, IrfanView — works. TSScan has the same boundary.

Both are explained in `docs/01-FEASIBILITY.md`.

## Layout

```
src/
  ScanBridge.Protocol/     wire format, framing, flow control, HMAC handshake
  ScanBridge.Common/       DPAPI secrets, SDDL-locked pipes, Serilog, config
  ScanBridge.Rdp/          WTS virtual channel + session interop
  ScanBridge.Scanner/      TWAIN and WIA backends, DIB → JPEG/PNG codec
  ScanBridge.ScanHost/     sacrificial acquisition process (x86 + x64)
  ScanBridge.Agent/        local agent: protocol endpoint, ScanHost orchestration
  ScanBridge.Client/    WPF tray application; publishes as ScanBridge-Client.exe,
                               with the x64 ScanHost and the RDP add-in embedded inside it
  ScanBridge.SessionAgent/ server side: DVC ⇄ data source relay; runs as a role
                               (--session-agent) of ScanBridge-Server.exe, not its own file
  ScanBridge.Server/      LocalSystem service, spawns session agents; publishes as
                               ScanBridge-Server.exe, with both data sources embedded

native/
  include/                    TWAIN ABI, protocol mirror, pipe + crypto, logger
  ScanBridge.TwainDS/      the virtual TWAIN Data Source
  ScanBridge.DvcPlugin/    the mstsc.exe plugin
  tools/
    dsmprobe.cpp              drives a REAL TWAIN manager against our data source
    dsmenum.cpp               lists what every manager on a machine reports
    dstest.cpp                loads a .ds the way a manager does, step by step
  build.cmd                   builds both, both bitnesses

installer/
  Build-All.ps1              builds everything and packs build\dist\ScanBridge-Server.exe
                              and ScanBridge-Client.exe
  ConstantCheck/              checks TWAIN constants against NAPS2's NTwain.dll — runs first
tests/Unit/                  protocol, validation, CRC, auth, flow control
docs/                        feasibility, architecture, setup, troubleshooting, security
```

## Documentation

| | |
|---|---|
| [01-FEASIBILITY.md](docs/01-FEASIBILITY.md) | How this class of product works, what is and is not possible, and why |
| [02-ARCHITECTURE.md](docs/02-ARCHITECTURE.md) | Components, data path, protocol, streaming, reconnect |
| [03-SETUP.md](docs/03-SETUP.md) | Build, install, configure, hardware test checklist |
| [04-TROUBLESHOOTING.md](docs/04-TROUBLESHOOTING.md) | Fault isolation, hop by hop |
| [05-SECURITY.md](docs/05-SECURITY.md) | Threat model, authentication, least privilege, known limitations |
| [06-SECURITY-REVIEW.md](docs/06-SECURITY-REVIEW.md) | Findings from the review before publication: fixed, accepted, scheduled |
| [07-COMPATIBILITY.md](docs/07-COMPATIBILITY.md) | What has actually been run, and what is only supported by design |
| [08-ORIENTATION.md](docs/08-ORIENTATION.md) | **Start here if you are picking this up cold.** What went wrong, how each fault was found, and which lessons the build now enforces |

## Before production

Authenticode-sign `ScanBridge.ds` and `ScanBridge.DvcPlugin.dll`. They load into
third-party processes (Acrobat, `mstsc.exe`), which is exactly where antivirus is most
suspicious of unsigned DLLs.
