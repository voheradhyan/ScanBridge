# Security

This carries business documents, so the threat model is written down rather than assumed.

## What is being protected

Scanned page data, in transit between a user's PC and their RDP session, on a machine
(a Session Host) that other users are logged into at the same time.

## Threat model

| Threat | Addressed by |
|---|---|
| Network eavesdropping between client and server | RDP's own TLS. All traffic rides the existing connection; nothing new is exposed. |
| **Another user's process on the Session Host reading a scan** | Per-session pipes with an explicit DACL granting one SID, plus an HMAC handshake. This is the main threat and the reason most of the design looks the way it does. |
| A hostile process in *the same* session impersonating an endpoint | HMAC-SHA256 proof-of-possession of a per-session pre-shared key. |
| Another process squatting our pipe name to harvest scans | `FILE_FLAG_FIRST_PIPE_INSTANCE` — creation fails if the name already exists. |
| Remote attacker reaching the agent | `PIPE_REJECT_REMOTE_CLIENTS`; no network listener anywhere in the product. |
| A malformed/hostile peer crashing a component | Bounds-checked decoding, element-count limits before allocation, settings validated before any driver sees them. |
| Scanned documents left on disk | Spool is per-session, purged on job end and session end; uninstall removes it. |
| A vendor driver compromising the agent | Acquisition runs in a separate `ScanHost` process. |

## Trust boundaries

```
[remote app] ──in-process──> [RemoteScanner.ds]
                                    │  pipe, ACL = one SID, HMAC authenticated
                                    ▼
                             [SessionAgent]           ← runs AS THE USER, in their session
                                    │  RDP dynamic virtual channel (TLS, kernel session-scoped)
                                    ▼
                             [DvcPlugin in mstsc.exe]
                                    │  pipe, ACL = one SID, HMAC authenticated
                                    ▼
                             [Agent] ──child process──> [ScanHost] ──> scanner
```

The RDP channel itself needs no added authentication — it is already inside the user's
authenticated, encrypted session, and `WTSVirtualChannelOpenEx(WTS_CURRENT_SESSION, …)` is
scoped by the kernel, so session 3 cannot open session 4's channel.

The two pipe hops *do*, because a pipe is reachable by anything running in the same session.

## Authentication

Per-boot random 32-byte pre-shared key, DPAPI-protected under the current user:

- Client PC: `HKCU\Software\RemoteScanner\Secret` — agent ⇄ DVC plugin
- Server: `HKCU\Software\RemoteScanner\Session\<sessionId>\Secret` — session agent ⇄ data source

DPAPI user scope is what makes this work: only the user who wrote the key can unprotect it,
so another user on the same Session Host cannot read it even with the registry path.

Handshake:

```
HELLO         version, role, machine name, 32-byte nonce, capabilities
HELLO_ACK     negotiated version, peer nonce, capabilities
AUTHENTICATE  HMAC-SHA256(psk, label ‖ initiatorNonce ‖ responderNonce ‖ sessionId)
AUTH_RESULT   ok / bad credentials / version mismatch
```

- The key is never transmitted.
- Nonces are fresh per connection, so a captured handshake cannot be replayed.
- The direction label (`.../initiator` vs `.../responder`) stops a response being replayed as
  a request.
- `sessionId` binds the MAC to one session.
- Verification is `CryptographicOperations.FixedTimeEquals` / a constant-time loop in C++.
  Never `SequenceEqual` or `memcmp` on a MAC.
- Failure closes the connection. There is no anonymous fallback.

## Least privilege

| Component | Runs as | Why |
|---|---|---|
| `RemoteScanner.Client` (tray) | Interactive user, **not elevated** | Only needs the user's own scanners and an HKCU registration |
| `ScanHost` | Interactive user | Isolates vendor drivers |
| `RemoteScanner.SessionAgent` | The logged-on user, in their session | Must be in-session for the WTS channel |
| `RemoteScanner.Service` | LocalSystem | Needs `SeTcbPrivilege` for `WTSQueryUserToken` / `CreateProcessAsUser`. Handles no scan data. |
| `RemoteScanner.ds` | Whatever loaded it | In-process by nature; holds no secrets beyond the session key it reads at open |

The service is the only elevated piece and is deliberately kept small: watch session events,
spawn and reap one child per session. It never touches page data.

## Handling of scanned data

- Pages are encoded (JPEG / PNG / CCITT G4) at the source and never written to disk on the
  client.
- On the server, in-flight pages spool to `%ProgramData%\RemoteScanner\spool\<sessionId>\`,
  opened `FILE_FLAG_DELETE_ON_CLOSE`.
- Spool is purged when a job ends, when the session agent exits, and on uninstall.
- Documents are never stored permanently. There is no "keep a copy" path.
- **Page content is never logged, at any level.** Logs carry sizes, page counts, dimensions
  and CRCs only. This is enforced structurally: the native logger has no API that accepts
  pixel data.

## Input validation

Everything crossing a boundary is treated as hostile:

- Frame headers: magic and version checked; payload length rejected above 32 KB before any
  allocation.
- Element counts (`ReadCount`) are bounds-checked *before* a list is reserved, so a corrupt
  peer cannot trigger a large allocation.
- `ScanSettings.Validate()` rejects impossible resolutions, page counts, quality values and
  unknown enum members before any driver sees them.
- Values *from* drivers are validated too. Windows' own WIA-to-TWAIN shim returns
  `TWRC_SUCCESS` with an uninitialised bed size (observed: −32768 × −19661 inches); that is
  range-checked and replaced rather than propagated.
- File paths from `DAT_SETUPFILEXFER` reject `..` segments.
- No component ever executes anything supplied over the channel. The protocol has no
  "run this" message and no extension mechanism that could become one.

## Network exposure

None added. No listening socket, no inbound firewall rule, no port. All traffic is a dynamic
virtual channel inside the existing RDP connection on 3389.

## Known limitations

1. **Binaries are unsigned.** The `.ds` loads into Acrobat and the plugin into `mstsc.exe`;
   both are places antivirus is rightly suspicious of unsigned DLLs. Authenticode-sign both
   before production deployment. The build script has an obvious place to hook that in.
2. **No virtual WIA device.** Not a defect — a WIA driver requires Microsoft attestation
   signing (EV certificate + Partner Center). See `01-FEASIBILITY.md` §4.
3. **A malicious administrator on the Session Host can read anything.** DPAPI user scope does
   not defend against SYSTEM. This is inherent to Windows and true of every product in this
   category.
4. **Compromised client PC = compromised scans.** The scanner is on that machine.

## If you change the protocol

Anything altering `Wire.cs` must be mirrored in `native/include/rs_protocol.h`. The round-trip
tests in `tests/Unit/ProtocolTests.cs` pin the C# side; the C++ side has no equivalent, so
review those two files together and bump `Wire.Version` on any incompatible change. A version
mismatch fails closed with `DISCONNECT`.
