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
║   │ ScanHost.exe  (x86 + x64, isolated child)  │  ← driver crash lands here  ║
║   │   • STA thread + Win32 message pump        │    and only here            ║
║   │   • TWAIN state machine 1..7               │                             ║
║   │   • strip-wise memory transfer             │                             ║
║   └───────────────────┬────────────────────────┘                             ║
║                       │ anonymous pipe (parent/child)                        ║
║                       ▼                                                      ║
║   ┌────────────────────────────────────────────┐                             ║
║   │ RemoteScanner.Agent.exe  (tray, WPF)       │                             ║
║   │   • scanner registry     • job spooler     │                             ║
║   │   • encode JPEG/G4/PNG   • config, logs    │                             ║
║   │   • diagnostics          • RDP watcher     │                             ║
║   └───────────────────┬────────────────────────┘                             ║
║                       │ named pipe  \\.\pipe\RemoteScanner.Agent             ║
║                       │ (ACL: current user only)                             ║
║                       ▼                                                      ║
║   ┌────────────────────────────────────────────┐                             ║
║   │ mstsc.exe                                  │                             ║
║   │   └─ RemoteScanner.DvcPlugin.dll  (C++)    │  ← IWTSPlugin, in-process   ║
║   └───────────────────┬────────────────────────┘                             ║
╚═══════════════════════╪══════════════════════════════════════════════════════╝
                        │
                        │   RDP  ::  Dynamic Virtual Channel  "RemoteScanner"
                        │   (MS-RDPEDYC, priority MED, inside existing :3389)
                        │
╔═══════════════════════╪══════════════════ WINDOWS SERVER 2019/2022/2025 ═════╗
║                       ▼                                                      ║
║   ┌────────────────────────────────────────────┐                             ║
║   │ RemoteScanner.SessionAgent.exe             │   one per RDP session,       ║
║   │   • WTSVirtualChannelOpenEx(DYNAMIC)       │   runs AS THE USER           ║
║   │   • overlapped read/write                  │                             ║
║   │   • auth handshake, session isolation      │                             ║
║   └───────────────────┬────────────────────────┘                             ║
║            ▲          │ named pipe                                           ║
║            │          │ \\.\pipe\RemoteScanner.Session.<SID>.<SessionId>     ║
║   spawned by          │ (ACL: that user's SID only)                          ║
║            │          ▼                                                      ║
║   ┌────────────────┐  ┌────────────────────────────────────────────┐         ║
║   │ RemoteScanner  │  │ Host app: Acrobat / ERP / DMS / ABBYY      │         ║
║   │ .Service.exe   │  │    └─ TWAIN DSM                            │         ║
║   │ (LocalSystem,  │  │         └─ RemoteScanner.ds  (C++ x86/x64) │         ║
║   │  session 0)    │  │              "Remote Scanner (DESKTOP-X)"  │         ║
║   │ WTS session    │  └────────────────────────────────────────────┘         ║
║   │ notifications  │                                                         ║
║   └────────────────┘                                                         ║
╚══════════════════════════════════════════════════════════════════════════════╝
```

Direction of control is **right-to-left**: the remote application initiates
(`MSG_ENABLEDS`), and the request travels back down to the physical scanner. Image data then
flows left-to-right.

## 2. Component responsibilities

| Component | Lang | Runs as | Responsibility |
|---|---|---|---|
| `RemoteScanner.TwainDS.ds` | C++ | inside host app | Real TWAIN Data Source. Full state machine, capability negotiation, native/memory/file transfer. Translates TWAIN → protocol. |
| `RemoteScanner.DvcPlugin.dll` | C++ | inside `mstsc.exe` | Owns the DVC listener. Bridges DVC ⇄ local agent pipe. Pure byte pump; no parsing beyond framing. |
| `RemoteScanner.SessionAgent.exe` | C# | user, in-session | Server end of DVC. Serves the pipe the `.ds` connects to. Multiplexes several concurrent `.ds` clients onto one channel. |
| `RemoteScanner.Service.exe` | C# | LocalSystem | Watches WTS session connect/disconnect, spawns/reaps SessionAgent per session. No network listener. |
| `RemoteScanner.Agent.exe` | C# | user, local PC | Tray UI, scanner enumeration, config, job orchestration, diagnostics, logs. |
| `ScanHost.exe` | C# | user, local PC | Sacrificial acquisition process. One per scan job. Isolates vendor driver faults. |
| `RemoteScanner.Protocol` | C# + C++ hdr | library | Wire format, shared by all. |

## 3. Why a separate `ScanHost.exe`

Vendor TWAIN drivers are the least reliable code in the chain — they leak, they show modal
dialogs, they call `ExitProcess`, some are 32-bit only. Running acquisition in the tray app
would mean a bad Canon driver takes down the whole redirection service. `ScanHost.exe` is
built **x86 and x64**; the agent launches whichever bitness matches the selected DS, so
32-bit-only scanner drivers work on a 64-bit PC. If it dies, the agent reports
`SCAN_ERROR / DriverFault` and stays up.

## 4. Session isolation & authentication

- Each RDP session gets its own `SessionAgent` process, own DVC instance, own pipe. Session A
  cannot address session B's channel: `WTSVirtualChannelOpenEx(WTS_CURRENT_SESSION, …)` is
  scoped by the kernel.
- Pipe from `.ds` → SessionAgent is ACLed with an explicit SDDL that grants only the
  interactive user's SID and denies everything else; `PIPE_REJECT_REMOTE_CLIENTS` is set.
- Pipe from `mstsc` plugin → local Agent is ACLed to the logged-on user's SID.
- **Handshake:** `HELLO` carries protocol version + a per-boot random 32-byte channel nonce.
  `AUTHENTICATE` proves possession of a **shared secret derived per-session**:
  `HMAC-SHA256(psk, clientNonce ‖ serverNonce ‖ sessionId)`. The PSK is generated by the local
  agent at install time, stored DPAPI-protected under the user profile, and is *never
  transmitted*. A mismatched or absent PSK fails closed.
- Because the DVC is already inside the RDP tunnel, the data is protected by RDP's own TLS.
  The HMAC is there to stop a hostile process **in the same session** from impersonating the
  endpoint, which TLS does not address.
- Payload confidentiality inside the session (page data at rest in the spool) uses AES-256-GCM
  with a per-job key; spool files are opened `FILE_FLAG_DELETE_ON_CLOSE` and wiped on job end.

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
- On the server the page lands in a spool file; the `.ds` hands the host app one page at a
  time via `DAT_PENDINGXFERS`, so even a 200-page job costs one page of RAM.

## 6. Reconnect semantics

| Event | Behaviour |
|---|---|
| RDP session disconnects mid-scan | SessionAgent sees channel error → `.ds` returns `TWRC_FAILURE/TWCC_OPERATIONERROR` for the pending transfer, job aborted cleanly, host app sees a normal scan failure (never a crash). Local agent stops the physical scanner. |
| RDP reconnects | Service gets `WTS_SESSION_LOGON`/`WTS_REMOTE_CONNECT`, respawns/re-attaches SessionAgent, DVC re-listens, plugin reconnects, scanners re-registered automatically. |
| Local agent restarts | Plugin retries the pipe with exponential backoff (1s → 30s cap), forever. |
| Scanner unplugged | Enumeration refresh marks it `Offline`; a pending job fails with `ScannerDisconnected`. |
| Multiple simultaneous RDP sessions | Independent by construction — one plugin instance per `mstsc.exe`, one agent pipe connection per plugin, one SessionAgent per server session. The local agent tracks each as a distinct `RdpLink` and lets the user bind a different scanner to each. |

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

Serilog on managed side (rolling file, `%LocalAppData%\RemoteScanner\logs`, Windows Event Log
sink for Error+). The native components write their own lightweight structured line log
(`%LocalAppData%\RemoteScanner\logs\twainds-<pid>.log`) because they cannot take a .NET
dependency. Both use the same field names so the diagnostics report can merge them.
**Page pixel data is never logged**, at any level; only sizes and hashes.
