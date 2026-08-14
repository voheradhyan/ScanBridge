# Troubleshooting

Work top to bottom. Each section narrows the fault to one hop in the chain:

```
remote app → RemoteScanner.ds → SessionAgent → [RDP DVC] → DvcPlugin → Agent → ScanHost → scanner
```

Logs for every component: `%LocalAppData%\RemoteScanner\logs\` on both machines.
Native components log there too (`twainds-<pid>.log`, `dvcplugin-<pid>.log`).

---

## Split the problem in half first: the loopback self-test

Everything above is one chain, and when it fails the natural question — is scanning broken, or
is RDP redirection broken? — cannot be answered by looking at the whole chain at once.

The loopback self-test removes the RDP hop and runs everything else on the PC with the
scanner:

```
data source → session pipe → session agent → local pipe → tray agent → ScanHost → scanner
```

Double-click **`SELF-TEST.bat`** in the client folder, or run it by hand:

```bash
RemoteScanner.SessionAgent.exe --loopback
```

then, in another window:

```bash
x64\dsmprobe.exe "C:\Program Files\NAPS2\lib\_win64\twaindsm.dll" x64\RemoteScanner.ds --scan out.bmp
```

Every component except the transport is the production one on its real code path. If this
passes, scanning works and the fault is in RDP redirection — the virtual channel, the
`mstsc.exe` plugin, or the add-in registration. If it fails, do not look at RDP at all; the
fault is somewhere in this list and the logs will say where.

`--loopback` refuses to run inside a remote session, where it would quietly scan the server's
own hardware instead of the user's and look like a success.

---

## "Remote Scanner" does not appear in the application's scanner list

Run the probe first. It answers this question directly instead of by elimination:

```bash
x64\dsmprobe.exe "C:\Program Files\NAPS2\lib\_win64\twaindsm.dll" x64\RemoteScanner.ds
```

`dsmprobe` loads a real TWAIN manager, redirects its search path to a scratch folder it
controls, drops the data source in, and reports what the manager lists. It needs no
administrator rights and does not touch the installed copy, so it separates "the data source
is broken" from "the data source is not installed where the manager looks" in one run.

If the probe passes and the application still shows nothing, the fault is installation or
bitness, below. If the probe fails, the data source itself is at fault and nothing about the
installation will help.

**Check which bitness the application is.** Task Manager → Details → the *Platform* column,
or look for `*32` beside the process name. Then confirm the matching file exists:

| Application | File that must exist |
|---|---|
| 32-bit (most ERP, older DMS) | `C:\Windows\twain_32\RemoteScanner\RemoteScanner.ds` |
| 64-bit (Acrobat, ABBYY) | `C:\Windows\twain_64\RemoteScanner\RemoteScanner.ds` |

If one is missing, re-run `Install-Server.ps1` and read its output — it warns when a bitness
is absent from the payload.

**Restart the application.** TWAIN applications enumerate data sources at startup.

**Windows Fax and Scan / the Windows Scan app will never show it.** Those are WIA-only. This
is a designed-in limitation, not a fault — see `01-FEASIBILITY.md` §4. Use any TWAIN
application instead.

### Listed twice

An installer before this one also copied the data source to `C:\Windows\twain_32\` and
`twain_64\` directly, one level above the `RemoteScanner\` sub-folder. A TWAIN 2.x manager
scans both levels and lists it once per copy. Re-running `Install-Server.ps1` deletes the
stray copy; if it reports the file is in use, close every scanning application and run it
again.

### Why the entry point matters (a note for anyone modifying `ds.cpp`)

A TWAIN data source must export **`DS_Entry`** — five arguments, no `pDest`. `DSM_Entry` is
what a data source *manager* exports for applications to call, and it takes six. A manager
resolves `GetProcAddress(module, "DS_Entry")`, and when that returns null it unloads the
library and moves on **without reporting anything to the application**.

The failure is silent and total: the file loads, its `DllMain` runs, it logs that it loaded,
it answers direct calls correctly, and no application ever lists it. Every test that calls the
data source directly still passes, because such a test resolves whatever name it was told to.
`dsmprobe` exists because only a real manager makes this visible.

---

## "Remote Scanner" appears but selecting it fails

The data source loaded but could not reach the session agent. `twainds-*.log` on the server
will show why.

### `cannot reach the RemoteScanner agent`

The session agent is not running in your session.

```bash
sc query RemoteScanner
```

If it is not running, start it. If it is, check `sessionagent-*.log`. The most common entry:

```
Session N is not a remote session; nothing to redirect.
```

meaning the service attached to a console session — normal, ignore it.

### `shared secret not present; is the RemoteScanner agent running in this session?`

The session agent has not published its key yet. It writes
`HKCU\Software\RemoteScanner\Session\<sessionId>` at startup. Sign out and back in.

### `no scanner is available on the local PC`

The chain reached your PC but the agent reported nothing. Go to *No scanners detected* below.

---

## Nothing happens at all — no channel, no errors

`sessionagent-*.log` repeats:

```
Virtual channel unavailable (WTSVirtualChannelOpenEx('RemoteScanner') failed.
  There is no RDP session, or the client is not running the RemoteScanner plugin.)
```

This means the client side never opened the channel. In order of likelihood:

### 1. You are not using `mstsc.exe`

The Microsoft Store **"Windows App" / "Remote Desktop" (MSRDC)** does not load RDP add-ins.
Neither do FreeRDP, Royal TS, Jump Desktop, or most third-party clients. There is no
workaround. Use `mstsc.exe`.

Check what you are running: on the client, `Get-Process mstsc` should return a process while
you are connected.

### 2. The add-in is not registered

```bash
reg query "HKCU\Software\Microsoft\Terminal Server Client\Default\AddIns\RemoteScanner"
```

Should print a `Name` value pointing at an existing `RemoteScanner.DvcPlugin.dll`. If it is
missing, or points somewhere that no longer exists, re-run `Install-Client.ps1`.

If it points into another user's profile, `Install-Client.ps1` was run elevated. Run it again
**without** elevation, as the account you actually use Remote Desktop with.

### 3. Group policy is blocking add-ins

```bash
reg query "HKLM\Software\Policies\Microsoft\Windows NT\Terminal Services\Client"
```

- `DisableAddIns = 1` — the RDP client will not load any add-in. Clear it.
- `AllowedAddIns` — an allow-list. `RemoteScanner` must be in it.

The tray agent checks both at startup and shows a balloon tip if either blocks us.

### 4. You connected before starting the agent

`mstsc.exe` loads plugins at connect time, and the plugin declines the channel if the agent
is not reachable. Start the agent, then reconnect.

---

## No scanners detected on the client

Run the local-only diagnostic — no RDP involved:

```bash
RemoteScanner.Agent.exe --enumerate-once
```

### Both hosts report 0 scanners

The problem is the driver, not this product. Confirm the scanner works in the vendor's own
software or Windows Fax and Scan. Then check the scanner is powered on and, for network
models, reachable.

### `x86 host: not installed` / `x64 host: not installed`

The payload is incomplete. Re-run `Build-All.ps1` and re-copy `build\client`.

### `x64 host: 0 scanner(s)` but x86 finds them

Normal for a scanner with a 32-bit-only TWAIN driver. That is exactly why ScanHost is built
twice; nothing is wrong.

### `No 64-bit TWAIN DSM found`

`TWAINDSM.dll` is not installed. WIA devices still work. To enable 64-bit TWAIN, place a
64-bit `TWAINDSM.dll` next to `RemoteScanner.ScanHost.exe` in the `x64` folder.

---

## Scanning starts then fails

Look at the error the application shows — it is the real reason, passed through unchanged.

| Message | Cause |
|---|---|
| The document feeder is empty | No paper in the ADF |
| There is a paper jam | Clear it; the scanner is usable afterwards |
| The scanner cover is open | Close it |
| The scanner is currently being used by another application | Something else on the client PC holds the device — often the vendor's own scan utility. Close it. |
| Scanner disconnected | USB unplugged or the device slept. Reconnect. |
| This scanner cannot scan both sides | Duplex was requested on a simplex device |
| The scanner driver reported an error | Vendor driver fault; see `scanhost-*.log` on the client |

A driver crash shows as `DriverFault` and only kills that one job — `ScanHost` is a separate
process precisely so a bad driver cannot take the agent down.

---

## The remote session goes sluggish while scanning

Bulk page data is competing with input and graphics. The channel already runs at medium
priority and paces itself with a credit window, but on a slow link you can tighten it:

`%ProgramData%\RemoteScanner\config.json` → lower `creditWindowFrames` (default 64) to 16 or
32. That is 512 KB–1 MB in flight instead of 2 MB.

Also consider scanning at 200 or 300 dpi rather than 600 — it is roughly a 4× reduction and
is ample for documents.

---

## Scans are slow

A 600 dpi colour A4 page is ~3–5 MB after JPEG. Over a typical WAN that is several seconds
per page, and that is the wire, not the software.

- Drop to 300 dpi for text documents.
- Use black & white for text-only originals — CCITT G4/PNG is dramatically smaller.
- Lower `jpegQuality` from 85 to 70; on documents the difference is hard to see.

---

## Pages are upside down, inverted, or sheared

Report this with the scanner model and driver version. It points at DIB row order or palette
handling for that specific driver. Attach `scanhost-*.log` and note whether the device was
used through TWAIN or WIA (`--enumerate-once` shows which).

---

## After uninstalling, "Remote Scanner" is still listed

The `.ds` was still mapped by a running application when the uninstaller ran. Close every
scanning application, then:

```bash
powershell -ExecutionPolicy Bypass -File Uninstall-Server.ps1
```

Users must sign out and back in for their applications to stop listing it.

---

## Collecting a diagnostic bundle

Tray app → **Diagnostics** writes a report to
`%LocalAppData%\RemoteScanner\logs\diagnostics-<timestamp>.txt` covering the client PC,
ScanHost availability, add-in registration, scanners found, RDP sessions and active links.

Send that plus the `logs` directory from **both** machines. Set
`HKLM\SOFTWARE\RemoteScanner\LogLevel` to `Debug` and reproduce first if the cause is not
obvious. Page content is never written to logs at any level — only sizes and hashes.
