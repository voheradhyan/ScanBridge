# RemoteScanner

Scanner redirection over RDP. A scanner physically attached to your Windows PC becomes usable
by applications running inside a Remote Desktop session on a Windows Server — with no step
where you scan locally, save a PDF and upload it.

Functionally comparable to TSScan, built from scratch.

```
[ Physical scanner ]
        │ TWAIN / WIA
[ ScanHost.exe ]  ──── isolated per job, built x86 + x64
        │ named pipe (ACL: one SID, HMAC)
[ RemoteScanner.Client ]  ──── tray agent on your PC
        │ named pipe
[ RemoteScanner.DvcPlugin.dll ]  ──── loaded inside mstsc.exe
        │
        │  RDP Dynamic Virtual Channel "RemoteScanner"  (inside the existing :3389)
        │
[ RemoteScanner.SessionAgent.exe ]  ──── one per RDP session, runs as the user
        │ named pipe (ACL: one SID, HMAC)
[ RemoteScanner.ds ]  ──── virtual TWAIN Data Source, x86 + x64
        │ TWAIN
[ Acrobat / ERP / DMS / ABBYY / NAPS2 ]
```

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
| Install / uninstall / build scripts | Built |
| **Discovery by a real TWAIN manager** | **Verified**, x86 and x64, against an unmodified `twaindsm.dll` |
| **Full scan, everything but the RDP hop** | **Verified** — real page off real hardware via `SELF-TEST.bat` |
| End-to-end scan through a live RDP session | **Verified** 14 Aug 2026 — NAPS2 on a Windows Server 2019 RDS host, scanner on the user's Windows 11 laptop, page and colours correct |

Everything compiles clean at `/W4` (native) and `TreatWarningsAsErrors` (managed).

## Verification

Two things here cannot be established by testing the components against themselves, so both
are checked against something real.

**Discovery.** `installer\Build-All.ps1` finishes by running `dsmprobe`, which loads a genuine
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

Everything except the transport is the production component on its real code path. It is also
the fastest way to split a fault in half: if the self-test passes, scanning works and the
problem is RDP redirection.

## Quick start

```bash
powershell -ExecutionPolicy Bypass -File installer\Build-All.ps1
```

Then on the **server**, elevated:

```bash
powershell -ExecutionPolicy Bypass -File build\server\Install-Server.ps1
```

And on the **PC with the scanner**, *not* elevated:

```bash
powershell -ExecutionPolicy Bypass -File build\client\Install-Client.ps1
```

Connect with `mstsc.exe`, open any TWAIN application, pick
**`Remote Scanner (YOUR-PC-NAME)`**.

Check the local scanner stack at any time, without RDP:

```bash
build\client\RemoteScanner.Agent.exe --enumerate-once
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
  RemoteScanner.Protocol/     wire format, framing, flow control, HMAC handshake
  RemoteScanner.Common/       DPAPI secrets, SDDL-locked pipes, Serilog, config
  RemoteScanner.Rdp/          WTS virtual channel + session interop
  RemoteScanner.Scanner/      TWAIN and WIA backends, DIB → JPEG/PNG codec
  RemoteScanner.ScanHost/     sacrificial acquisition process (x86 + x64)
  RemoteScanner.Agent/        local agent: protocol endpoint, ScanHost orchestration
  RemoteScanner.Client.UI/    WPF tray application
  RemoteScanner.SessionAgent/ server side: DVC ⇄ data source relay
  RemoteScanner.Service/      LocalSystem service, spawns session agents

native/
  include/                    TWAIN ABI, protocol mirror, pipe + crypto, logger
  RemoteScanner.TwainDS/      the virtual TWAIN Data Source
  RemoteScanner.DvcPlugin/    the mstsc.exe plugin
  tools/
    dsmprobe.cpp              drives a REAL TWAIN manager against our data source
    dsmenum.cpp               lists what every manager on a machine reports
    dstest.cpp                loads a .ds the way a manager does, step by step
  build.cmd                   builds both, both bitnesses

installer/                    Build-All, Install/Uninstall for client and server
tests/Unit/                   protocol, validation, CRC, auth, flow control
docs/                         feasibility, architecture, setup, troubleshooting, security
```

## Documentation

| | |
|---|---|
| [01-FEASIBILITY.md](docs/01-FEASIBILITY.md) | How this class of product works, what is and is not possible, and why |
| [02-ARCHITECTURE.md](docs/02-ARCHITECTURE.md) | Components, data path, protocol, streaming, reconnect |
| [03-SETUP.md](docs/03-SETUP.md) | Build, install, configure, hardware test checklist |
| [04-TROUBLESHOOTING.md](docs/04-TROUBLESHOOTING.md) | Fault isolation, hop by hop |
| [05-SECURITY.md](docs/05-SECURITY.md) | Threat model, authentication, least privilege, known limitations |

## Before production

Authenticode-sign `RemoteScanner.ds` and `RemoteScanner.DvcPlugin.dll`. They load into
third-party processes (Acrobat, `mstsc.exe`), which is exactly where antivirus is most
suspicious of unsigned DLLs.
