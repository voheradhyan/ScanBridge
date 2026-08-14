# Phase 1 — Technical Feasibility (honest assessment)

Scope: make a scanner physically attached to a **local Windows PC** usable by applications
running inside an **RDP session on a Windows Server**, with no manual save/copy/upload step.

This document states what is genuinely achievable, what is not, and why. Section 27 of the
brief ("do not create a fake demonstration") is treated as the governing rule.

---

## 1. How TSScan-class products actually work

There is no Microsoft-provided scanner redirection in RDP. RDP redirects drives, printers,
smart cards, ports, audio, clipboard and (via RemoteFX USB) *some* USB devices. Scanners are
deliberately excluded — RemoteFX USB redirection requires VDI-class policy, works only against
Win7/8/10 Enterprise VDI hosts, not Session Host, and mass-storage/imaging classes are
blocked. So every product in this space (TSScan, ScanRedirector, ThinPrint, FabulaTech) does
the same four things:

1. A **client-side plugin loaded inside `mstsc.exe`** that owns an RDP **Dynamic Virtual
   Channel (DVC)**.
2. A **local agent** that drives the real scanner through TWAIN/WIA and feeds the plugin.
3. A **server-side per-session agent** that opens the other end of the same DVC.
4. A **virtual TWAIN Data Source installed on the server** that appears in every TWAIN
   application's scanner list and proxies every TWAIN call over the channel.

The scanner never leaves the client. Only image data and TWAIN capability negotiation cross
the wire. That is the architecture used here.

---

## 2. RDP virtual channels — the mechanism

Two generations exist.

**Static Virtual Channels (SVC)** — legacy. Name limited to 7 chars + NUL, fixed 1600-byte
chunks (`CHANNEL_CHUNK_LENGTH`), max 31 channels per connection, client side registered via
`VirtualChannelEntry` in a DLL. Flow control is poor and there is a hard channel budget shared
with the rest of the RDP stack. **Rejected.**

**Dynamic Virtual Channels (DVC)** — MS-RDPEDYC, available since Windows 7 / Server 2008 R2.
Channels are created on demand, names up to 31 chars, the stack handles chunking and
multiplexing, and there is no practical channel-count limit. **Selected.**

### Client side (inside `mstsc.exe`)

`mstsc.exe` enumerates DVC plugins from the registry at connect time:

```
HKCU\Software\Microsoft\Terminal Server Client\Default\AddIns\RemoteScanner
    Name = REG_SZ  C:\Program Files\RemoteScanner\RemoteScanner.DvcPlugin.dll
```

The DLL must export `VirtualChannelGetInstance` and hand back an object implementing
`IWTSPlugin` (`tsvirtualchannels.h`). The RDP client then calls
`IWTSPlugin::Initialize` → the plugin calls `IWTSVirtualChannelManager::CreateListener` with
the channel name and an `IWTSListenerCallback`. When the server opens the channel, the client
gets `OnNewChannelConnection` and receives an `IWTSVirtualChannel` for writes plus supplies an
`IWTSVirtualChannelCallback` for reads.

This is an **in-process COM object loaded into `mstsc.exe`**. Consequences are covered in §5.

### Server side (inside the user's session)

A process running **in the target session** (not session 0) calls:

```c
HANDLE h = WTSVirtualChannelOpenEx(WTS_CURRENT_SESSION, "RemoteScanner",
                                   WTS_CHANNEL_OPTION_DYNAMIC);
```

then `WTSVirtualChannelRead` / `WTSVirtualChannelWrite`. Optionally
`WTS_CHANNEL_OPTION_DYNAMIC_PRI_MED` to pick a priority band so bulk image data does not
starve input/graphics. We use MED — image transfer should never make the session feel laggy.

`WTSVirtualChannelQuery(h, WTSVirtualFileHandle, ...)` yields a real file `HANDLE`, which lets
us do **overlapped (async) I/O** instead of blocking reads. That matters for cancellation and
for not pinning a thread per session.

---

## 3. TWAIN — how it actually works

TWAIN is a 1992-era C ABI, still the only universal scanning interface on Windows.

- **Application** links the **DSM** (Data Source Manager): legacy `twain_32.dll` (32-bit only,
  shipped in Windows) or the modern open-source `TWAINDSM.dll` (both bitnesses).
- **DSM** enumerates **Data Sources** — DLLs with a `.ds` extension exporting a single entry
  point:

  ```c
  TW_UINT16 FAR PASCAL DS_Entry(pTW_IDENTITY pOrigin,
                                TW_UINT32 DG, TW_UINT16 DAT, TW_UINT16 MSG,
                                TW_MEMREF pData);
  ```

  Note the name and the arity. The manager exports `DSM_Entry` for applications to call and
  takes a sixth argument, `pDest`, naming the data source meant; a data source exports
  `DS_Entry` and needs no such argument, being always its own destination. A `.ds` exporting
  `DSM_Entry` is loaded, fails the manager's `GetProcAddress("DS_Entry")`, and is discarded
  with no error surfaced to the application.

- Search paths are bitness-split:
  - 32-bit DSM → `C:\Windows\twain_32\<Vendor>\<name>.ds`
  - 64-bit DSM → `C:\Windows\twain_64\<Vendor>\<name>.ds`

- The DS runs a **7-state machine**: 1 unloaded, 2 loaded, 3 DSM open, 4 DS open,
  5 enabled/ready, 6 transfer ready, 7 transferring. Illegal transitions must return
  `TWRC_FAILURE` + `TWCC_SEQERROR` — real applications probe this, so it cannot be faked.
- Capability negotiation is via `DAT_CAPABILITY` with `MSG_GET`, `MSG_GETCURRENT`,
  `MSG_GETDEFAULT`, `MSG_SET`, `MSG_RESET`, `MSG_QUERYSUPPORT` over containers
  (`TW_ONEVALUE`, `TW_ENUMERATION`, `TW_RANGE`, `TW_ARRAY`).
- Three transfer mechanisms (`ICAP_XFERMECH`): **Native** (a single HGLOBAL DIB — simple but
  needs the whole page in RAM), **Memory** (`DAT_IMAGEMEMXFER`, strip-by-strip — the one that
  scales), **File** (`DAT_IMAGEFILEXFER`).

**Verdict: fully feasible.** A virtual DS is an ordinary DLL. No signing, no driver, no kernel
component. It is just a file in `twain_32`/`twain_64`. This is the single reason the whole
project is possible.

---

## 4. WIA — and the honest "no"

WIA is the other Windows imaging stack. A **virtual WIA scanner is not feasible for a
self-hosted build**, and the brief asked to be told rather than shown a fake.

To make a device appear in WIA you must supply a **WIA user-mode minidriver**: a COM object
implementing `IWiaMiniDrv`, packaged with an INF as an `Image` class device, installed through
PnP. On 64-bit Windows 10/11 and Server 2019+, **kernel-mode-adjacent and PnP driver packages
must carry a Microsoft-issued signature**. Since Windows 10 1607 that means the package must
be submitted to the Microsoft Hardware Dev Center and **attestation-signed**, which requires:

- a Partner Center hardware account, and
- an EV code-signing certificate (hardware token, ~US$300–450/yr, identity vetting).

Test-signing works only on a machine booted with `bcdedit /set testsigning on` — unacceptable
on a production server. Additionally, a fake PnP device needs a root-enumerated or software
device node, which further tightens signing requirements.

**Consequence — stated plainly:**

| Remote application | Interface it uses | Works with this design |
|---|---|---|
| Adobe Acrobat (Pro/Standard) | TWAIN and WIA | **Yes** (select the TWAIN device) |
| ERP / accounting (SAP B1, Dynamics, Tally, Busy) | TWAIN | **Yes** |
| Document management (DocuWare, M-Files, Laserfiche) | TWAIN | **Yes** |
| IrfanView, XnView, PaperPort, ABBYY, NAPS2 | TWAIN | **Yes** |
| Microsoft Office 2013+ | *no scan support at all* | N/A — MS removed it after Office 2010 |
| **Windows Fax and Scan** | WIA only | **No** |
| **Windows Scan (Store app)** | WIA only | **No** |

The two Windows-bundled scan apps are the honest casualties. Every professional/business
scanning application on that list is TWAIN and is covered. TSScan has the same limitation
unless you buy their WIA add-on; this is inherent to the signing regime, not to our design.

**Note on the reverse direction:** Windows ships a WIA→TWAIN compatibility layer (so WIA
devices show up to TWAIN apps). There is deliberately no TWAIN→WIA layer.

**Where WIA *is* used in this project:** on the **local client**, to talk to real scanners that
ship WIA drivers but no TWAIN driver (common on cheap MFPs and on network scanners Windows
discovers via WSD). That direction is plain COM interop and works perfectly.

---

## 5. What must be native C/C++, and what can be C#

### Must be native C++

**(a) The virtual TWAIN Data Source (`.ds`)** — non-negotiable.
It is loaded **in-process into arbitrary third-party applications** (Acrobat, a 32-bit ERP,
ABBYY). Loading the CLR into a host process you do not own is unacceptable: it can collide
with a CLR the host already loaded, changes process startup behaviour, and cannot work at all
where the host is 32-bit and the machine's .NET layout differs. `DS_Entry` is also a raw C
ABI called on the host's UI thread with the host's message loop. → **C++, built twice
(x86 + x64).** Both bitnesses are mandatory: TWAIN has no in-process bitness bridge, so a
32-bit ERP can only load a 32-bit `.ds`.

**(b) The `mstsc.exe` DVC plugin** — non-negotiable for the same reason.
`mstsc.exe` is a native host, and injecting the CLR into the RDP client risks destabilising
the user's remote session. → **C++ COM in-proc server.** `mstsc.exe` is 64-bit on 64-bit
Windows, so x64 is the required build; an x86 build is produced for completeness.

### Can be C#/.NET 8

- **Local agent** (scanner enumeration, acquisition, encoding, config, tray UI, diagnostics).
  WIA is straightforward COM interop. TWAIN is P/Invoke to `TWAINDSM.dll` — legal and reliable
  from C#, but with two hard requirements: the calling thread must be **STA** and must **pump
  Win32 messages**, because scanner DS UIs are real dialogs that post messages, and
  `MSG_PROCESSEVENT` must see them. We implement a dedicated STA pump thread rather than
  relying on the WPF dispatcher.
- **Server session agent** (WTS API via P/Invoke, session lifetime, pipe server, protocol).
- **Server control service** (session-0 service that spawns the per-session agent).

### Bitness summary

| Component | Bitness | Reason |
|---|---|---|
| `RemoteScanner.TwainDS.ds` | **x86 and x64** | must match each host application |
| `RemoteScanner.DvcPlugin.dll` | **x64** (x86 built too) | must match `mstsc.exe` |
| Local agent / server agent / service | AnyCPU → x64 | own processes |

---

## 6. Windows APIs required

| Purpose | API |
|---|---|
| DVC, client side | `IWTSPlugin`, `IWTSListenerCallback`, `IWTSVirtualChannelCallback`, `IWTSVirtualChannel` (`tsvirtualchannels.h`) |
| DVC, server side | `WTSVirtualChannelOpenEx`, `WTSVirtualChannelQuery(WTSVirtualFileHandle)`, `WTSVirtualChannelRead/Write/Close` |
| Session enumeration & notification | `WTSEnumerateSessionsEx`, `WTSQuerySessionInformation`, `WTSRegisterSessionNotification`, `WTSQueryUserToken` |
| Per-session process launch | `CreateProcessAsUser`, `CreateEnvironmentBlock`, `DuplicateTokenEx` |
| Local RDP session discovery (client PC) | enumerate `mstsc.exe`, `WTSGetActiveConsoleSessionId`, `NtQuerySystemInformation`-free approach via process + registry `HKCU\...\Terminal Server Client\Servers` |
| IPC | Named pipes with explicit SDDL ACLs |
| Scanning (client) | `TWAINDSM.dll` `DSM_Entry`; WIA 2.0 COM (`IWiaDevMgr2`, `IWiaItem2`, `IWiaTransfer`) |
| Imaging encode | `System.Drawing`/`Windows.Graphics.Imaging` for JPEG/PNG; custom TIFF/PDF writer for multi-page |

---

## 7. Server-side RDP configuration required

Good news: **no inbound firewall port, no listener, no GPO change in the default case.**
Custom DVCs ride the existing 3389 connection and are permitted by default.

Things that *can* break it, and must be checked by Diagnostics:

1. **Client-side AddIn policy.** If
   `HKLM\Software\Policies\Microsoft\Windows NT\Terminal Services\Client` has
   `DisableAddIns = 1`, or an `AllowedAddIns` allowlist exists that omits us, `mstsc.exe`
   will not load the plugin. Diagnostics reports this explicitly.
2. **The client must be `mstsc.exe`.** The Microsoft Store "Windows App" / "Remote Desktop"
   (MSRDC) client **does not load classic DVC AddIns**. Neither do most third-party clients
   (FreeRDP, Royal TS, Jump). This is a genuine, unavoidable limitation and is documented in
   the troubleshooting guide. RemoteApp and RDWeb launches *do* use `mstsc.exe` and work.
3. **`fDisableClip`, `fDisableCdm`, etc. do not apply** to custom DVCs — those gate the
   built-in redirectors only.
4. The per-session agent needs to run in the user's session — installed as a service that
   spawns it on session connect (see architecture doc), which needs `SeTcbPrivilege`; the
   controlling service runs as LocalSystem, the spawned agent as the logged-on user.

---

## 8. Windows version compatibility (determined, not assumed)

| OS | Role | Status | Notes |
|---|---|---|---|
| Windows 10 1809+ | client | Supported | DVC AddIns present since Win7 |
| Windows 11 (all) | client | Supported | |
| Windows Server 2019 | server | Supported | `WTSVirtualChannelOpenEx` dynamic since 2008 R2 |
| Windows Server 2022 | server | Supported | |
| Windows Server 2025 | server | Supported | TWAIN DS paths unchanged |
| Windows 10/11 as "server" | server | Supported (1 session) | Single-session RDP |
| Windows Server Core | server | Works headless; no tray UI on server side (none needed) |
| ARM64 Windows | client | x64 emulation works; native ARM64 build not provided |

TWAIN DS directories `C:\Windows\twain_32` and `C:\Windows\twain_64` exist and are searched on
all of the above. Neither is deprecated in Server 2025.

---

## 9. Honest risk register

| Risk | Severity | Mitigation |
|---|---|---|
| MSRDC / Store client can't load AddIns | **High** | Documented; installer detects and warns; user must use `mstsc.exe` |
| No WIA virtual device → Windows Scan/Fax&Scan unsupported | Medium | Documented; all business TWAIN apps covered |
| Host app is 32-bit but only 64-bit DS installed | Medium | Installer always deploys **both** |
| A badly-behaved vendor TWAIN driver hangs the local agent | Medium | Acquisition runs in a **separate child process** (`ScanHost`) so a driver crash cannot kill the agent |
| Large scans exhausting memory | Medium | Memory-mode strip transfer + streaming to disk-backed spool, never whole-job in RAM |
| DVC throughput lower than LAN | Low | Per-page compression; JPEG for colour, CCITT G4 for bitonal |
| Antivirus flagging an unsigned `.ds` in Acrobat | Low | Ship with Authenticode signing hook in the build; documented |

---

## 10. Verdict

Feasible, with one designed-out feature (virtual WIA device) and one environmental constraint
(`mstsc.exe` required on the client). Everything else in the brief — real TWAIN data source,
real DVC transport, real duplex/ADF/multi-page, streaming, no manual file copy — is
implementable exactly as specified, and is implemented in this repository.

Proceed to `02-ARCHITECTURE.md`.
