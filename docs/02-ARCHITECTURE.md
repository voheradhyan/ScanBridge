# Phase 2 — Architecture

## 1. End-to-end data path

```
╔══════════════════════════ LOCAL PC (Windows 10/11) ══════════════════════════╗
║                                                                              ║
║   [ Physical Scanner ]  USB / WSD / network                                  ║
║            │                                                                 ║
║            │  vendor driver                                                  ║
║            ▼                                                                 ║
║   [ TWAIN DSM (TWAINDSM.dll) ]        [ WIA 2.0 (IWiaDevMgr2) ]              ║
║            └───────────────┬───────────────────┘                             ║
║                            ▼                                                 ║
║   ┌────────────────────────────────────────────┐                             ║
║   │ ScanHost  (isolated child, one per job)    │  ← driver crash lands here  ║
║   │   • STA thread + Win32 message pump        │    and only here            ║
║   │   • TWAIN state machine 1..7               │                             ║
║   │   • strip-wise memory transfer             │                             ║
║   └───────────────────┬────────────────────────┘                             ║
║                       │ anonymous pipe (parent/child)                        ║
║   x64 is a role of ScanBridge-Client.exe (--scan-host); x86 ships as      ║
║   a separate file, because a driver's bitness decides the process that       ║
║   can load it, and there is no in-process bitness bridge                     ║
║                       │                                                      ║
║                       ▼                                                      ║
║   ┌────────────────────────────────────────────┐                             ║
║   │ ScanBridge-Client.exe  (tray, WPF)      │                             ║
║   │   • scanner registry     • per-page encode │                             ║
║   │   • encode JPEG/G4/PNG   • config, logs    │                             ║
║   │   • diagnostics          • RDP watcher     │                             ║
║   └───────────────────┬────────────────────────┘                             ║
║                       │ named pipe  \\.\pipe\ScanBridge.Agent             ║
║                       │ (ACL: current user only)                             ║
║                       ▼                                                      ║
║   ┌────────────────────────────────────────────┐                             ║
║   │ mstsc.exe                                  │                             ║
║   │   └─ ScanBridge.DvcPlugin.dll  (C++)    │  ← IWTSPlugin, in-process   ║
║   └───────────────────┬────────────────────────┘                             ║
╚═══════════════════════╪══════════════════════════════════════════════════════╝
                        │
                        │   RDP  ::  Dynamic Virtual Channel  "ScanBridge"
                        │   (MS-RDPEDYC, priority MED, inside existing :3389)
                        │
╔═══════════════════════╪══════════════════ WINDOWS SERVER 2019/2022/2025 ═════╗
║                       ▼                                                      ║
║   ┌────────────────────────────────────────────┐                             ║
║   │ ScanBridge-Server.exe --session-agent   │   one per RDP session,       ║
║   │   • WTSVirtualChannelOpenEx(DYNAMIC)       │   runs AS THE USER           ║
║   │   • overlapped read/write                  │                             ║
║   │   • auth handshake, session isolation      │                             ║
║   └───────────────────┬────────────────────────┘                             ║
║            ▲          │ named pipe                                           ║
║            │          │ \\.\pipe\ScanBridge.Session.<SID>.<SessionId>     ║
║   spawned by          │ (ACL: that user's SID only)                          ║
║            │          ▼                                                      ║
║   ┌────────────────┐  ┌────────────────────────────────────────────┐         ║
║   │ ScanBridge  │  │ Host app: Acrobat / ERP / DMS / ABBYY      │         ║
║   │ -Server.exe    │  │    └─ TWAIN DSM                            │         ║
║   │ (LocalSystem,  │  │         └─ ScanBridge.ds  (C++ x86/x64) │         ║
║   │  session 0)    │  │              "ScanBridge (DESKTOP-X)"  │         ║
║   │ WTS session    │  └────────────────────────────────────────────┘         ║
║   │ notifications  │                                                         ║
║   └────────────────┘                                                         ║
╚══════════════════════════════════════════════════════════════════════════════╝
```

`ScanBridge-Client.exe` and `ScanBridge-Server.exe` are what get deployed — each a
single self-contained file that installs itself (`--install`) and carries its native payload
embedded. The per-session agent and the 64-bit ScanHost are roles of those files, selected by
a command-line switch, rather than separate executables; `ScanBridge.ds` and
`ScanBridge.DvcPlugin.dll` are the two components that stay separate files regardless,
because each loads into a process this product does not own (the host application, and
`mstsc.exe`) rather than running on its own.

Direction of control is **right-to-left**: the remote application initiates
(`MSG_ENABLEDS`), and the request travels back down to the physical scanner. Image data then
flows left-to-right.

## 2. Component responsibilities

| Component | Lang | Runs as | Responsibility |
|---|---|---|---|
| `ScanBridge.TwainDS.ds` | C++ | inside host app | Real TWAIN Data Source. Full state machine, capability negotiation, native/memory/file transfer. Translates TWAIN → protocol. |
| `ScanBridge.DvcPlugin.dll` | C++ | inside `mstsc.exe` | Owns the DVC listener. Bridges DVC ⇄ local agent pipe. Pure byte pump; no parsing beyond framing. |
| `ScanBridge-Server.exe --session-agent` | C# | user, in-session | Server end of DVC. Serves the pipe the `.ds` connects to. Multiplexes several concurrent `.ds` clients onto one channel. A role of the server executable, not a separate file. |
| `ScanBridge-Server.exe` (no args, run by SCM) | C# | LocalSystem | Watches WTS session connect/disconnect, spawns/reaps a session-agent process per session. No network listener. |
| `ScanBridge-Client.exe` (no args) | C# | user, local PC | Tray UI, scanner enumeration, config, job orchestration, diagnostics, logs. |
| `ScanBridge-Client.exe --scan-host` | C# | user, local PC | Sacrificial acquisition process for the 64-bit case, one per scan job. A role of the client executable. |
| `ScanHost.exe` (x86) | C# | user, local PC | The 32-bit sacrificial acquisition process. A separate file: a 64-bit process cannot load a 32-bit-only scanner driver, so the bitness that has to differ from the client executable cannot be a role of it. |
| `ScanBridge.Protocol` | C# + C++ hdr | library | Wire format, shared by all. |

Both executables are also their own installer (`--install`, `--uninstall`) and can report
what they carry (`--extract <folder>`, needs no administrator rights and installs nothing).
See `docs/03-SETUP.md` for the full command-line reference.

## 3. Why a separate ScanHost process

Vendor TWAIN drivers are the least reliable code in the chain — they leak, they show modal
dialogs, they call `ExitProcess`, some are 32-bit only. Running acquisition in the tray
process would mean a bad Canon driver takes down the whole redirection agent. ScanHost exists
in **both bitnesses** — x64 as the client executable's `--scan-host` role, x86 as a separate
file — and the agent launches whichever matches the selected DS, so 32-bit-only scanner
drivers work on a 64-bit PC. If it dies, the agent reports `SCAN_ERROR / DriverFault` and
stays up.

## 4. Session isolation & authentication

- Each RDP session gets its own session-agent process, own DVC instance, own pipe. Session A
  cannot address session B's channel: `WTSVirtualChannelOpenEx(WTS_CURRENT_SESSION, …)` is
  scoped by the kernel.
- Pipe from `.ds` → session agent is ACLed with an explicit SDDL that grants only the
  interactive user's SID and denies everything else; `PIPE_REJECT_REMOTE_CLIENTS` is set.
- Pipe from `mstsc` plugin → local agent is ACLed to the logged-on user's SID.
- **Handshake:** `HELLO` carries protocol version + a per-boot random 32-byte channel nonce.
  `AUTHENTICATE` proves possession of a **shared secret derived per-session**:
  `HMAC-SHA256(psk, clientNonce ‖ serverNonce ‖ sessionId)`. The PSK is generated by the local
  agent at install time, stored DPAPI-protected under the user profile, and is *never
  transmitted*. A mismatched or absent PSK fails closed.
- Because the DVC is already inside the RDP tunnel, the data is protected by RDP's own TLS.
  The HMAC is there to stop a hostile process **in the same session** from impersonating the
  endpoint, which TLS does not address.
- There is no spool, and never was one that anything wrote to. A page exists only as the
  in-memory buffer described in §5, for the time between arriving over the channel and being
  handed to the host application — there is nothing at rest on the server to protect.

## 5. Streaming, backpressure, memory

The rule from §11 of the brief — never hold a whole job in RAM — is enforced structurally:

- The `.ds` requests `ICAP_XFERMECH = TWSX_MEMORY` from the real scanner and the local side
  pushes **strips**, not pages.
- Each page becomes an **encoded** page (JPEG q85 for colour/grey, CCITT G4 for bitonal,
  Deflate-PNG when lossless is asked for) *before* it crosses the wire. A 600 dpi A4 colour
  page is ~100 MB raw, ~3–5 MB as JPEG.
- Wire frames cap at **32 KiB payload**, so a page is many `SCAN_PAGE_DATA` frames.
- **Credit-based backpressure:** the receiver advertises a window (default 64 frames = 2 MiB
  in flight). The sender blocks when credit is exhausted and resumes on `FLOW_CREDIT`. This
  prevents a fast scanner from ballooning RDP's send queue and freezing the session's mouse.
- On the server, each page is decoded straight into memory; the `.ds` hands the host app one
  page at a time via `DAT_PENDINGXFERS`, so even a 200-page job costs one page of memory. There
  is no spool directory and never was one that anything wrote to — a page never touches server
  disk at any point between arriving over the DVC and being handed to the host application.

## 6. Reconnect semantics

| Event | Behaviour |
|---|---|
| RDP session disconnects mid-scan | The session agent sees a channel error → `.ds` returns `TWRC_FAILURE/TWCC_OPERATIONERROR` for the pending transfer, job aborted cleanly, host app sees a normal scan failure (never a crash). Local agent stops the physical scanner. |
| RDP reconnects | Service gets `WTS_SESSION_LOGON`/`WTS_REMOTE_CONNECT`, respawns/re-attaches a session agent, DVC re-listens, plugin reconnects, scanners re-registered automatically. |
| Local agent restarts | Plugin retries the pipe with exponential backoff (1s → 30s cap), forever. |
| Scanner unplugged | Enumeration refresh marks it `Offline`; a pending job fails with `ScannerDisconnected`. |
| Multiple simultaneous RDP sessions | Independent by construction — one plugin instance per `mstsc.exe`, one agent pipe connection per plugin, one session agent per server session. The local agent tracks each as a distinct `RdpLink` and lets the user bind a different scanner to each. |

## 7. Protocol (v1)

Frame:

```
 0        1        2        4                8               12
+--------+--------+--------+----------------+----------------+
| MAGIC  | VER    | TYPE   | STREAM_ID (u32)| LENGTH  (u32)  |  payload (LENGTH bytes)
| 0x52   | 0x01   | u16 LE | LE             | LE, <= 32768   |
+--------+--------+--------+----------------+----------------+
```

`STREAM_ID` multiplexes concurrent `.ds` clients over one DVC. Payload for control messages is
**MessagePack-free, explicit little-endian structs** (see `Protocol/Messages.cs` and the mirror
`protocol.h`) so the C++ and C# sides cannot drift.

Message types:

| Type | Dir | Payload |
|---|---|---|
| `HELLO` (0x01) | both | version, role, machine name, 32-byte nonce |
| `HELLO_ACK` (0x02) | both | negotiated version, peer nonce, capabilities bitmask |
| `AUTHENTICATE` (0x03) | ds→agent | 32-byte HMAC |
| `AUTH_RESULT` (0x04) | agent→ds | status |
| `SCANNER_ENUM_REQ` (0x10) | ds→agent | — |
| `SCANNER_ENUM_RESP` (0x11) | agent→ds | count + `[id, name, vendor, iface, status, flags]` |
| `SCANNER_CAPS_REQ` (0x12) | ds→agent | scanner id |
| `SCANNER_CAPS_RESP` (0x13) | agent→ds | dpi list, pixel types, sizes, duplex/ADF/dust flags, ranges |
| `SCAN_REQUEST` (0x20) | ds→agent | scanner id + full `ScanSettings` |
| `SCAN_START` (0x21) | agent→ds | job id |
| `SCAN_PAGE_BEGIN` (0x22) | agent→ds | job, page#, side, w, h, dpi, pixeltype, encoding, byte length |
| `SCAN_PAGE_DATA` (0x23) | agent→ds | job, page#, offset, bytes |
| `SCAN_PAGE_END` (0x24) | agent→ds | job, page#, crc32 |
| `SCAN_PROGRESS` (0x25) | agent→ds | job, pages done, bytes, bytes/s |
| `SCAN_COMPLETE` (0x26) | agent→ds | job, total pages |
| `SCAN_CANCEL` (0x27) | ds→agent | job |
| `SCAN_ERROR` (0x28) | agent→ds | job, code, utf8 message |
| `FLOW_CREDIT` (0x30) | ds→agent | frames granted |
| `HEARTBEAT` (0x40) | both | tick |
| `DISCONNECT` (0x41) | both | reason |

Versioning: `HELLO` proposes the highest version the sender knows; `HELLO_ACK` returns
`min(theirs, mine)`. Unknown message types in a known version are skipped by length (forward
compatible). Unknown *version* → `DISCONNECT`.

## 8. Error handling contract

The `.ds` never propagates an internal failure as a crash. Every protocol/transport error maps
to a legal TWAIN return:

| Condition | TWAIN result |
|---|---|
| Channel down / no agent | `TWRC_FAILURE` + `TWCC_MAXCONNECTIONS` at `MSG_OPENDS` |
| Scanner busy on client | `TWRC_FAILURE` + `TWCC_BUSY` |
| Paper jam / ADF empty | `TWRC_FAILURE` + `TWCC_PAPERJAM` / `TWCC_PAPERDOUBLEFEED` |
| User cancelled in agent | `TWRC_CANCEL` |
| Anything else | `TWRC_FAILURE` + `TWCC_OPERATIONERROR`, detail in `DAT_STATUSUTF8` |

Illegal state transitions return `TWCC_SEQERROR` — required, because applications test it.

## 9. Logging

Serilog on the managed side (rolling file, Windows Event Log sink for Error+); the native
components write their own lightweight structured line log because they cannot take a .NET
dependency. Both use the same field names so a diagnostics bundle can merge them.

**Where a component logs follows what it runs as, not what language it is written in.**
Anything running as a signed-in user — the tray agent, the session agent, the `.ds` inside
the host application, the plugin inside `mstsc.exe` — writes to that user's own
`%LocalAppData%\ScanBridge\logs`, because on a multi-user Session Host a shared log
directory would let every user read every other user's machine names, session ids, scanner
models and link timings. Only the machine-wide service, which runs as LocalSystem and belongs
to no user, logs to `%ProgramData%\ScanBridge\logs` — and it is the only thing that writes
there, so `COLLECT-LOGS.bat` and any diagnostics report have to gather both locations to see
the whole picture.

**Page pixel data is never logged**, at any level; only sizes and hashes.

## 10. Build-time verification

Two build gates in `installer\Build-All.ps1` exist because the corresponding fault is
invisible from inside this repository — every component here can agree with itself and still
be wrong.

**TWAIN constants**, checked first, before anything compiles. `rs_twain.h` (native) and
`TwainTypes.cs` (managed) each define the numeric value of every `DG_`/`DAT_`/`MSG_`
constant, and the data source manager forwards those numbers untouched — it does not
interpret them. A data source and a test that both link the same wrong header therefore agree
with each other perfectly and disagree with every real application, and no amount of testing
against ourselves can catch that. `installer\ConstantCheck` checks both files' constants
against the enums compiled into NAPS2's `NTwain.dll`, a real TWAIN implementation outside this
repository. Six wrong `DAT_` values survived 67 unit tests, the discovery gate below, and
end-to-end scans in three of the four transfer mechanisms before this gate existed; they cost
two days to find because the manager passed them through in silent agreement. See
`docs/04-TROUBLESHOOTING.md` for what the failure looked like from inside a scanning
application.

**Discovery**, checked once everything is built. `dsmprobe` loads a genuine `twaindsm.dll`, redirects its search
path to a scratch folder, drops the freshly built `.ds` in, and asks the manager what it
found. A data source can load, initialise, answer every direct call correctly, and pass every
unit test while being invisible to every real scanning application — see the entry-point note
in `docs/04-TROUBLESHOOTING.md`. `dsmprobe` also drives all four TWAIN transfer mechanisms
(native, memory, memory-file, file) end to end against real hardware, not just whichever one
a given test application happens to use — the mechanism a real application (NAPS2) actually
uses had gone unexercised here before, and that is where the six wrong constants above were
hiding.
