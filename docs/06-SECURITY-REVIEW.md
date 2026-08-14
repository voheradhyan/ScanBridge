# Security review — 14 Aug 2026

A pass over the whole product ahead of publishing it, covering the network transport, the pipe
hops and their ACLs, key storage, wire parsing, process launching, and the two DLLs that load
into third-party processes.

Each finding is fixed, accepted with a reason, or scheduled with a date-free but specific plan.
Nothing is left as "should probably look at this".

## What is being protected, and from whom

The product moves scanned business documents from a PC to an application running in somebody's
Remote Desktop session. Three attackers are worth naming:

1. **Another user on the same RDS host.** Realistic: these are multi-user machines. They can run
   code, enumerate pipes, and read anything the filesystem lets them.
2. **Another process in the same session, running as the same user.** Inside the trust boundary
   by definition — it can already read that user's registry, DPAPI blobs and screen. Defending
   against it is not possible and not attempted; this is stated so the boundary is explicit
   rather than assumed.
3. **Anything that can reach the direct-connection port on the PC's network.** The only hop with
   no operating system standing in between.

## Fixed

### 1. The direct-connection port accepted callers from any address · medium

`LanListener` bound `IPAddress.Any` and served whatever connected. The documented restriction to
the local subnet lived only in the installer's firewall rule — outside the program, in something
a user can widen, an administrator can replace, or a "allow this app through the firewall"
prompt can answer generously on somebody's behalf.

Callers are now checked against the private and link-local ranges (plus loopback, which the
self-test uses) before anything else happens, and refused with an explanation. The firewall rule
remains the primary control; this states the same restriction where it cannot be edited by
accident.

### 2. Unauthenticated connections were unbounded · medium

Every accepted socket got a task, a buffer and up to ten seconds of patience, none of which
required the caller to have proved anything. A remote attacker could hold thousands open.

There is now a ceiling of eight connections in flight *before* authentication; the slot is
released the moment a caller authenticates, so real sessions are not capped by it. The genuine
number of Remote Desktop sessions scanning to one PC is one, occasionally a few.

### 3. The PC's machine name was disclosed before authentication · low, privacy

`HELLO_ACK` carried `Environment.MachineName` and was sent to anyone who opened the socket and
said hello. Machine names are very often people's names.

The name is now empty in `HELLO_ACK` and travels in `AUTH_RESULT`, only when the answer is yes.

### 4. The answering end never proved it held the key · low–medium

Only the dialling end authenticated. Anything that answered on the address — an impostor, a
stale port forward, a PC whose pairing key had been reset — completed the handshake, and the
caller learned that it had failed only when encrypted records started failing several frames
later. It could never read or forge traffic, since the record keys derive from a secret it does
not hold, but the failure was late and unexplained.

The responder now returns an HMAC over both nonces under `ChannelAuth.ResponderLabel` — a label
that existed in the code, unused, since the beginning — and the caller verifies it. The field is
appended after the existing ones, and the native reader in `rs_pipe.h` stops after the detail
string, so the change is compatible with the pipe hops.

**Tests:** `tests/Unit/LanHandshakeTests.cs` covers the agreeing case, a caller with the wrong
key, an impostor that answers "yes" while holding no key, and the absence of the machine name
before authentication. That last pair are the two that would otherwise regress silently.

## Accepted, with reasons

### 5. The pipe hop accepts a caller whose SID matches even if the MAC does not

`AgentHost.CallerIsThisUser` impersonates the pipe client and compares SIDs, and a match is
accepted even when the shared secret disagrees. This looks like a weakening and is not.

The pipe is created with `D:P(A;;GA;;;SY)(A;;GA;;;<user SID>)` and `PIPE_REJECT_REMOTE_CLIENTS`,
so the kernel has already refused everyone but that user and LocalSystem before a byte is read.
The secret was only ever standing in for "is this the same user", and any process that could
read the secret could also pass the SID check — it is the same trust boundary, checked directly
instead of by proxy. It exists because components running as the same user intermittently read
different keys from the same registry value, a fault that was never explained.

Confirmed unreachable from the network path: the fallback requires a `NamedPipeServerStream`,
and the direct transport authenticates before it ever reaches `AuthenticateAsync`.

### 6. DPAPI blobs carry no extra entropy

`CryptProtectData` is called with a description and no optional entropy, so any process running
as that user can unprotect the key. That is attacker 2 above, which is inside the boundary by
definition — such a process could equally read the plaintext out of the agent's memory.

### 7. Pipe traffic is not encrypted after the handshake

Deliberate. The pipes are single-machine, ACLed to one SID, and reject remote clients; the
network hop, which has none of those protections, is encrypted with AES-256-GCM.

### 8. The TWAIN data source writes to a path the application chooses

That is what `DAT_SETUPFILEXFER` is. The data source runs inside the calling application's
process and its user's token, so it can write nothing the application could not write itself.
Relative path segments are rejected to avoid surprising traversal through a path an application
built by concatenation.

## Scheduled

### 9. Every user on the RDS host can read every other user's logs · medium–low

`Install-Server.ps1` grants `BUILTIN\Users` Modify over `%ProgramData%\RemoteScanner`,
recursively, so that session agents running as different users can write there. The consequence
is that any user on a Session Host can read all of it: machine names, user SIDs, session ids,
scanner models, link timings. No document content — pages are never written to a log at any
level — but on a multi-user host this is more than a user needs to know about their colleagues.

Fix in the packaging phase, where the layout is being rebuilt anyway: per-session components log
under the user's own profile, and only the machine-wide service keeps a shared location.

### 10. The two DLLs that load into third-party processes are unsigned

`RemoteScanner.ds` loads into every scanning application, and `RemoteScanner.DvcPlugin.dll` into
`mstsc.exe`. Unsigned DLLs in those positions are exactly what endpoint protection is most
suspicious of, and signing is also what makes tampering detectable. Outstanding since before
this review; it matters more once distribution is a single downloadable executable.

### 11. A pairing key cannot be revoked from the interface

The key is created on first use and lives until the registry value is removed by hand. A "reset
pairing" action belongs next to the "show pairing code" button, and lands with the interface
work.

## Checked, nothing to report

- **Wire parsing.** `PayloadReader` bounds every read; `FrameCodec.ParseHeader` rejects any frame
  declaring more than 32 KB before a byte is allocated; `ReadBlob` and `ReadCount` reject absurd
  lengths. A hostile peer cannot drive an allocation from a length field.
- **MAC comparison.** `CryptographicOperations.FixedTimeEquals` throughout — no `SequenceEqual`
  on a secret anywhere in the tree.
- **Nonces and keys.** `RandomNumberGenerator` for both. Keys are 32 bytes; pairing seeds are 160
  bits, hashed to the key, so the code a user types is never itself the key.
- **Record layer.** AES-256-GCM, one key per direction, counter nonces, length authenticated as
  associated data. Twelve tests cover tampering, truncation, replay, reordering, a wrong secret,
  and that no plaintext appears on the wire.
- **Pipe creation.** Explicit SDDL, protected DACL, `FILE_FLAG_FIRST_PIPE_INSTANCE` against name
  squatting, remote clients rejected.
- **Process launching.** `ScanHostRunner` starts a fixed path from the install directory with
  `UseShellExecute = false` and a random 128-bit pipe name. No part of the command line comes
  from a peer.
- **Secret handling in memory.** Plaintext buffers are zeroed after use on both sides of the
  DPAPI wrapper.
