# Troubleshooting

Work top to bottom. Each section narrows the fault to one hop in the chain:

```
remote app → ScanBridge.ds → session agent → [RDP DVC] → DvcPlugin → tray agent → ScanHost → scanner
```

Logs follow what a component runs as, not what language it is written in. Anything running
as a signed-in user — the tray agent, the session agent, the `.ds` inside the scanning
application, the plugin inside `mstsc.exe` — logs to `%LocalAppData%\ScanBridge\logs\` on
its own machine (`twainds-<pid>.log`, `dvcplugin-<pid>.log`, `sessionagent-*.log`,
`agent-*.log`). Only the machine-wide service, on the server, logs to
`%ProgramData%\ScanBridge\logs\` — and it is the only thing that writes there. Check both
locations; a fault on the server can be in either one depending on which piece is at fault.

---

## Split the problem in half first: the loopback self-test

Everything above is one chain, and when it fails the natural question — is scanning broken, or
is RDP redirection broken? — cannot be answered by looking at the whole chain at once.

The loopback self-test removes the RDP hop and runs everything else on the PC with the
scanner:

```
data source → session pipe → session agent → local pipe → tray agent → ScanHost → scanner
```

Double-click **`SELF-TEST.bat`**, or run it by hand — the session agent is a role of the
server executable, not its own file:

```bash
ScanBridge-Server.exe --session-agent --loopback
```

then, in another window:

```bash
x64\dsmprobe.exe "C:\Program Files\NAPS2\lib\_win64\twaindsm.dll" x64\ScanBridge.ds --scan out.bmp
```

Every component except the transport is the production one on its real code path. If this
passes, scanning works and the fault is in RDP redirection — the virtual channel, the
`mstsc.exe` plugin, or the add-in registration. If it fails, do not look at RDP at all; the
fault is somewhere in this list and the logs will say where.

`--loopback` refuses to run inside a remote session, where it would quietly scan the server's
own hardware instead of the user's and look like a success.

---

## "ScanBridge" does not appear in the application's scanner list

Run the probe first. It answers this question directly instead of by elimination:

```bash
x64\dsmprobe.exe "C:\Program Files\NAPS2\lib\_win64\twaindsm.dll" x64\ScanBridge.ds
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
| 32-bit (most ERP, older DMS) | `C:\Windows\twain_32\ScanBridge\ScanBridge.ds` |
| 64-bit (Acrobat, ABBYY) | `C:\Windows\twain_64\ScanBridge\ScanBridge.ds` |

If one is missing, re-run `ScanBridge-Server.exe --install` and read its output — it warns
when a bitness is absent from the payload.

**Restart the application.** TWAIN applications enumerate data sources at startup.

**Windows Fax and Scan / the Windows Scan app will never show it.** Those are WIA-only. This
is a designed-in limitation, not a fault — see `01-FEASIBILITY.md` §4. Use any TWAIN
application instead.

### Listed twice

An installer before this one also copied the data source to `C:\Windows\twain_32\` and
`twain_64\` directly, one level above the `ScanBridge\` sub-folder. A TWAIN 2.x manager
scans both levels and lists it once per copy. Re-running `ScanBridge-Server.exe --install`
deletes the stray copy; if it reports the file is in use, close every scanning application and
run it again.

### Why the entry point matters (a note for anyone modifying `ds.cpp`)

A TWAIN data source must export **`DS_Entry`** — five arguments, no `pDest`. `DSM_Entry` is
what a data source *manager* exports for applications to call, and it takes six. A manager
resolves `GetProcAddress(module, "DS_Entry")`, and when that returns null it unloads the
library and moves on **without reporting anything to the application**.

The failure is silent and total: the file loads, its `DllMain` runs, it logs that it loaded,
it answers direct calls correctly, and no application ever lists it. Every test that calls the
data source directly still passes, because such a test resolves whatever name it was told to.
`dsmprobe` exists because only a real manager makes this visible.

### The same class of fault, for a constant instead of an export

A wrong TWAIN constant is silent the same way, and for the same underlying reason: the data
source manager forwards `DG`/`DAT`/`MSG` numbers through untouched, so if `rs_twain.h` (native)
or `TwainTypes.cs` (managed) has the wrong value for, say, `DAT_IMAGEMEMXFER`, the data source
and any test built against the same header agree with each other and disagree with every real
application. That happened here — six wrong `DAT_` constants, one of which surfaced as
`TWAIN error: CapUnsupported` immediately after an otherwise-successful scan, because the
application's request landed on whatever unrelated operation the wrong number happened to
mean. `installer\ConstantCheck` now checks every constant in both files against NAPS2's
`NTwain.dll` — a real TWAIN implementation — on every build, before anything else compiles. If
you see `CapUnsupported`, or any TWAIN error immediately after a page otherwise transferred
successfully, suspect this class of fault first and check that the constant check gate is
passing on the build in use.

---

## "ScanBridge" appears but selecting it fails

The data source loaded but could not reach the session agent. `twainds-*.log` on the server
will show why.

### `cannot reach the ScanBridge agent`

The session agent is not running in your session.

```bash
sc query ScanBridge
```

If it is not running, start it. If it is, check `sessionagent-*.log`. The most common entry:

```
Session N is not a remote session; nothing to redirect.
```

meaning the service attached to a console session — normal, ignore it.

### `shared secret not present; is the ScanBridge agent running in this session?`

The session agent has not published its key yet. It writes
`HKCU\Software\ScanBridge\Session\<sessionId>` at startup. Sign out and back in.

### `no scanner is available on the local PC`

The chain reached your PC but the agent reported nothing. Go to *No scanners detected* below.

---

## Nothing happens at all — no channel, no errors

`sessionagent-*.log` repeats:

```
Virtual channel unavailable (WTSVirtualChannelOpenEx('ScanBridge') failed.
  There is no RDP session, or the client is not running the ScanBridge plugin.)
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
reg query "HKCU\Software\Microsoft\Terminal Server Client\Default\AddIns\ScanBridge"
```

Should print a `Name` value pointing at an existing `ScanBridge.DvcPlugin.dll` under
`%LocalAppData%\Programs\ScanBridge\`. If it is missing, or points somewhere that no
longer exists, re-run `ScanBridge-Client.exe --install`.

This cannot point into another user's profile: `--install` run elevated is refused outright
rather than registering into the wrong account, so if you got as far as installing, it went
into the account you were signed in as. If a mismatch is somehow present anyway — for example
after moving to a rebuilt client from an older version — run `--install` again as the account
you actually use Remote Desktop with.

### 3. Group policy is blocking add-ins

```bash
reg query "HKLM\Software\Policies\Microsoft\Windows NT\Terminal Services\Client"
```

- `DisableAddIns = 1` — the RDP client will not load any add-in. Clear it.
- `AllowedAddIns` — an allow-list. `ScanBridge` must be in it.

The tray agent checks both at startup and shows a balloon tip if either blocks us.

### 4. You connected before starting the agent

`mstsc.exe` loads plugins at connect time, and the plugin declines the channel if the agent
is not reachable. Start the agent, then reconnect.

---

## No scanners detected on the client

Run the local-only diagnostic — no RDP involved:

```bash
ScanBridge-Client.exe --enumerate-once
```

### Both hosts report 0 scanners

The problem is the driver, not this product. Confirm the scanner works in the vendor's own
software or Windows Fax and Scan. Then check the scanner is powered on and, for network
models, reachable.

### `x86 host: not installed` / `x64 host: not installed`

The payload is incomplete — a build assembled without the native components or the 32-bit
host produces a `ScanBridge-Client.exe` that installs and runs but is missing one side.
Re-run `installer\Build-All.ps1`, or confirm with
`ScanBridge-Client.exe --extract <folder>` that both are actually carried before
reinstalling.

### `x64 host: 0 scanner(s)` but x86 finds them

Normal for a scanner with a 32-bit-only TWAIN driver. That is exactly why ScanHost exists in
both bitnesses; nothing is wrong.

### `No 64-bit TWAIN DSM found`

`TWAINDSM.dll` is not installed. WIA devices still work. The 64-bit role looks for it beside
its own binaries first, then on the default search path — to enable 64-bit TWAIN, place a
64-bit `TWAINDSM.dll` in the same folder as the installed `ScanBridge.Client.exe`
(`%LocalAppData%\Programs\ScanBridge` by default).

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

`%ProgramData%\ScanBridge\config.json` → lower `creditWindowFrames` (default 64) to 16 or
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

## After uninstalling, "ScanBridge" is still listed

The `.ds` was still mapped by a running application when the uninstaller ran. Close every
scanning application, then:

```bash
ScanBridge-Server.exe --uninstall
```

Users must sign out and back in for their applications to stop listing it.

---

## Collecting a diagnostic bundle

Tray app → **Diagnostics** writes a report to
`%LocalAppData%\ScanBridge\logs\diagnostics-<timestamp>.txt` covering the client PC,
ScanHost availability, add-in registration, scanners found, RDP sessions and active links.

Send that plus `%LocalAppData%\ScanBridge\logs\` from **both** machines, and also
`%ProgramData%\ScanBridge\logs\` from the server — that second directory belongs to the
machine-wide service alone, so it is easy to forget and it is where a service-level fault
(session-agent spawn failures, WTS notifications) shows up instead of in the per-user logs.
`COLLECT-LOGS.bat` gathers both locations into one zip automatically.

Set `HKLM\SOFTWARE\ScanBridge\LogLevel` to `Debug` and reproduce first if the cause is not
obvious. Page content is never written to logs at any level — only sizes and hashes.
