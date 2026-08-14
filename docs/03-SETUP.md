# Setup

## What you need

| | Requirement |
|---|---|
| **Client PC** (scanner attached) | Windows 10 1809+ or Windows 11 (x64). **Connects with `mstsc.exe`.** Nothing else to pre-install — `ScanBridge-Client.exe` is self-contained and carries its own .NET runtime. |
| **Server** | Windows Server 2019 / 2022 / 2025, or Windows 10/11 for a single session (x64). Administrator rights to install. Also self-contained; no .NET runtime needs to be present beforehand. |
| **Scanner** | Any device with a working TWAIN or WIA driver on the client PC. |

> **The client must use `mstsc.exe`.** The Microsoft Store "Windows App" / "Remote Desktop"
> (MSRDC) does not load RDP add-ins, and neither do FreeRDP, Royal TS or Jump Desktop. There
> is no workaround — this is how those clients are built. RemoteApp and RDWeb launches use
> `mstsc.exe` underneath and work normally.

## 1. Build

On a machine with Visual Studio Build Tools 2022 (C++ workload + Windows SDK) and the .NET 8
SDK:

```bash
powershell -ExecutionPolicy Bypass -File installer\Build-All.ps1
```

This runs the TWAIN constant check against a real TWAIN library first (see
`docs/02-ARCHITECTURE.md` § Build-time verification), builds the native data source and DVC
plugin for both bitnesses, builds and tests the managed solution, and finishes by packing two
self-contained, single-file executables into `build\dist\`:

```
build\dist\ScanBridge-Server.exe   34 MB — the service, the per-session agent
                                       (a role of the same file), and both bitnesses
                                       of ScanBridge.ds
build\dist\ScanBridge-Client.exe  104 MB — the tray agent, the RDP add-in, the
                                       64-bit ScanHost (a role of the same file), and
                                       the 32-bit ScanHost, which cannot be a role of a
                                       64-bit process because a scanner driver's bitness
                                       decides the bitness of the process that loads it
```

Nothing else needs to be copied alongside either file — each carries its payload embedded
and writes it out on install. This replaced a folder of roughly 200 files plus PowerShell
installer scripts: one file that installs itself cannot end up with a mismatched half, and
there is no script that can be run against the wrong payload or the wrong build.

`--extract <folder>` on either executable writes out the files it carries, with a SHA-256
hash for each, without installing anything or needing administrator rights. `Build-All.ps1`
runs this against both executables as its last step and fails the build if either comes back
empty — a build assembled without the native components produces an installer that looks
identical and otherwise only fails at the last step, on someone else's machine.

Copy `ScanBridge-Client.exe` to the PC with the scanner and `ScanBridge-Server.exe` to
the RDS host.

## 2. Install the server component

On the Windows Server, from an **elevated** Command Prompt or PowerShell:

```bash
ScanBridge-Server.exe --install
```

Run without administrator rights, it refuses and explains why rather than doing a partial
install: this step writes a Windows service and files under `C:\Windows\twain_32` and
`twain_64`, both of which need elevation.

This installs `ScanBridge.ds` into **both** `C:\Windows\twain_32\ScanBridge\` and
`C:\Windows\twain_64\ScanBridge\`, registers the `ScanBridge` service (LocalSystem,
automatic start), and creates the event log source. `--to <folder>` overrides the default
install location (`C:\Program Files\ScanBridge`); the TWAIN folders are fixed by the TWAIN
specification and are not affected by it.

Both bitnesses are installed deliberately: TWAIN has no in-process bitness bridge, so a
32-bit ERP can only load the 32-bit data source and 64-bit Acrobat can only load the 64-bit
one. Installing one would leave half your applications with no scanner.

No firewall rule is needed. Nothing listens on the network.

Sign out and back in afterwards — the service starts a session agent when a session is
*created*, and a session that was already open before the install never gets one.

## 3. Install the client

On the PC with the scanner, from a **normal (not elevated)** Command Prompt or PowerShell:

```bash
ScanBridge-Client.exe --install
```

**Run this elevated and it refuses outright**, rather than appearing to succeed in the wrong
place. Everything it installs belongs to one user: the RDP add-in is registered under `HKCU`,
which is where `mstsc.exe` reads it from; the shared secret it authenticates with is
protected by that user's own DPAPI key; and the tray application has to run as the person
whose scanner it is. An elevated install would put all of that in the administrator's
account, where the intended user cannot see any of it, and `mstsc.exe` running as that user
would never find the add-in.

`--install` copies the executable to `%LocalAppData%\Programs\ScanBridge` (or `--to
<folder>`), extracts the RDP add-in for both bitnesses and the 32-bit ScanHost, registers the
add-in, adds a startup entry, warns about any group policy that would block add-ins, and
starts the tray agent.

## 4. Use it

1. Make sure the tray agent is running on your PC and lists your scanner. It starts itself
   after `--install`, and again at every sign-in after that.
2. Connect to the server with **`mstsc.exe`**. (Connect *after* the agent is running; the
   plugin is loaded at connect time.)
3. In the remote session open any TWAIN application — Acrobat, your ERP, NAPS2, IrfanView.
4. Choose **File → Import / Acquire → Select Source**.
5. Pick **`ScanBridge (YOUR-PC-NAME)`**.
6. Scan. The pages appear in the remote application.

There is no step where you save a file locally and upload it — pages are decoded to their
final encoding in memory and handed straight to the host application; nothing is written to
disk on the server at any point.

## Verifying before you involve RDP

The fastest way to separate a scanner problem from a redirection problem:

```bash
ScanBridge-Client.exe --enumerate-once
```

This drives the whole local scanner stack — both ScanHost bitnesses, TWAIN and WIA — with no
RDP in the picture, and prints what each one can see:

```
x64 host: 2 scanner(s)
    Contoso IJ-48200W [...]  [Wia]  Microsoft
        dpi        : 150, 200, 300, 600
        colour     : BlackWhite, Grayscale, Rgb
        features   : Flatbed, Color, Grayscale, BlackWhite
        bed (in)   : 8.5 x 14
x86 host: 2 scanner(s)
    WIA-Contoso IJ-48200W [...]  [Twain]  Microsoft
        ...
```

If scanners appear here, the driver side is fine and any problem is in redirection. If they
do not, fix the driver first — redirection cannot help.

## Configuration

`%ProgramData%\ScanBridge\config.json`, created on first run. Reachable from the tray
app's **Scanner Settings** button.

| Setting | Meaning |
|---|---|
| `defaultScannerId` | Scanner offered to remote applications first. Empty = first found. |
| `preferredInterface` | `Twain` or `Wia`, when a device offers both. |
| `defaultResolution`, `defaultPixelType`, `defaultPaperSize` | Defaults when the application does not ask. |
| `jpegQuality` | 1–100. 85 is a good balance for documents. |
| `creditWindowFrames` | In-flight 32 KB frames before the sender waits. Lower it if a scan makes the session feel laggy. |
| `logLevel` | `Trace`, `Debug`, `Information`, `Warning`, `Error`. |

Anything a remote application negotiates through TWAIN wins over these — an application that
asks for 600 dpi gets 600 dpi.

Log level is also read from `HKLM\SOFTWARE\ScanBridge\LogLevel` by the native components,
so one setting covers the whole stack.

## Hardware test checklist

Automated tests cannot cover a physical scanner. Work through this with each model you deploy.

| # | Test | Expected |
|---|---|---|
| 1 | Enumerate with `--enumerate-once` | Scanner listed under TWAIN and/or WIA |
| 2 | Flatbed, single page, 300 dpi colour | Page appears in the remote application |
| 3 | Flatbed, 600 dpi colour | Same; note transfer time |
| 4 | Bitonal (black & white) 300 dpi | Sharp text, no JPEG artefacts (PNG/G4 is used) |
| 5 | Greyscale 300 dpi | Correct, not inverted |
| 6 | ADF, 10 pages | All 10 arrive, in order |
| 7 | ADF, 50+ pages | All arrive; agent memory stays flat |
| 8 | Duplex, 10 sheets | 20 pages, front/back interleaved correctly |
| 9 | Cancel mid-scan from the application | Feeder stops, no crash, scanner reusable |
| 10 | Empty feeder | "The document feeder is empty", no crash |
| 11 | Paper jam mid-scan | Jam reported; scanner usable after clearing |
| 12 | Open the cover mid-scan | Reported as cover open |
| 13 | Unplug USB mid-scan | "Scanner disconnected"; agent survives |
| 14 | Disconnect RDP mid-scan | Job aborts cleanly; agent survives |
| 15 | Reconnect RDP | Scanner available again with no restart |
| 16 | Two RDP sessions to two servers | Each sees the scanner independently |
| 17 | Two applications scanning at once | Both work (separate stream ids) |
| 18 | 32-bit application (e.g. older ERP) | Sees `ScanBridge`; scans |
| 19 | 64-bit application (Acrobat) | Sees `ScanBridge`; scans |
| 20 | Restart the server service mid-session | Session agent respawns; scanner returns |
| 21 | Restart the client agent | Plugin reconnects within ~30 s |
| 22 | Uninstall both sides | No `ScanBridge` left in any application's device list |

Record scanner model, driver version, and which interface was used for each run.
