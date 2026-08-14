# ScanBridge — setup guide

Scanner plugged into your own PC becomes usable by programs running on the server
through Remote Desktop.

Two files, produced by the build, and nothing else to copy alongside them — each carries
its own payload:

| File | Goes to | Size |
|---|---|---|
| `ScanBridge-Server.exe` | the Windows Server, once | 34 MB |
| `ScanBridge-Client.exe` | every PC that has a scanner | 104 MB |

Do the server first. A PC installed before the server has nothing to talk to.

---

## Part 1 — The server (once, needs an administrator)

1. Copy **`ScanBridge-Server.exe`** onto the server itself.
   Not a network drive — copy it to the server's own disk, e.g. `C:\ScanBridge`.

2. Open an **administrator** Command Prompt or PowerShell window (Start → type `cmd` →
   **Run as administrator**) and run:

   ```
   C:\ScanBridge\ScanBridge-Server.exe --install
   ```

   If Windows shows a blue *"Windows protected your PC"* box: **More info** → **Run anyway**.

3. Wait for **Installed.** Everything else it prints is a record of what it just did — where
   it put each data source, and that the service is running.

4. Sign out of the server and back in. The service starts a session agent when a session is
   *created*; a session that was already open before the install never gets one.

Nothing to open on the firewall. It rides the Remote Desktop connection you already have.

Running it without `--install` (or without administrator rights) does nothing but print
usage and explain what it needs — it never half-installs.

---

## Part 2 — Your PC, and each colleague's PC

1. **Close Remote Desktop Connection completely.** The installer replaces a file that
   Remote Desktop holds open while it runs.

2. Copy **`ScanBridge-Client.exe`** to that PC — anywhere is fine, e.g. the Desktop or
   Downloads.

3. Open an **ordinary** Command Prompt or PowerShell window — **not** "Run as
   administrator" — and run:

   ```
   ScanBridge-Client.exe --install
   ```

   This has to be the plain, non-elevated way. If you run it elevated it refuses outright
   and explains why: everything it installs — the Remote Desktop add-in, the key it
   authenticates with — belongs to your own Windows account. Installed from an
   administrator prompt, all of that lands in the administrator's account instead, and
   scanning would never work for you even though the install appeared to succeed.

4. It copies itself to `%LocalAppData%\Programs\ScanBridge`, registers the Remote
   Desktop add-in for your account, sets itself to start with Windows, and opens
   automatically. A small icon appears near the clock.

5. Check your scanner is listed under **Detected scanners**. If you need to reopen the
   window later, run `ScanBridge-Client.exe` again (from where it installed itself, or
   the copy you downloaded) — it brings the running one to the front rather than starting a
   second copy.

### If the PC has more than one scanner

The remote session gets exactly one. Choose which:

- select the scanner in the list
- click **Use for Remote**

The **Used remotely** column shows the chosen one. Click **Test Scan** first — the
*Status* column says "Ready" for any scanner Windows knows about, including a wireless
one that is switched off, so a test scan is the only honest check.

---

## Part 3 — Use it

1. Connect to the server with **Remote Desktop Connection** (`mstsc`).

   **It must be this app.** The Microsoft Store "Windows App", FreeRDP, Royal TS and
   Jump Desktop cannot carry scanners and never will — they do not load RDP add-ins.

2. Open your scanning program on the server.

3. Choose the TWAIN driver named:

   ```
   ScanBridge (YOUR-PC-NAME)
   ```

4. Scan. The page comes off the scanner on your desk.

---

## If scanning still does not work: pair the two machines

Normally the scanner is carried by the Remote Desktop connection itself and there is nothing
else to do. But that channel can open and then carry no data at all — some Remote Desktop
clients and some group policies cause it, and it looks exactly like a healthy connection from
both ends.

When that happens the server connects to your PC over the network instead. Two one-time steps:

**On the PC with the scanner**

1. Allow the server to reach you: from an **administrator** Command Prompt or PowerShell,
   run (one line, adjust the path if you installed elsewhere):

   ```
   netsh advfirewall firewall add rule name="ScanBridge (direct connection)" dir=in ^
       action=allow protocol=TCP localport=47214 remoteip=LocalSubnet ^
       program="%LocalAppData%\Programs\ScanBridge\ScanBridge.Client.exe" enable=yes
   ```

   This allows incoming TCP 47214, from your local network only, to ScanBridge only.
   Anything that connects still has to prove it holds your pairing code before a single byte
   of a document moves, and everything after that is encrypted separately from RDP.

2. Get your pairing code:

   ```
   ScanBridge-Client.exe --pairing-code
   ```

**In your Remote Desktop session**

3. Run, pasting the code you were given:

   ```
   ScanBridge-Server.exe --pair=<code>
   ```

4. Sign out of the server, sign back in, and scan.

The pairing code is a password: anybody who has it can use that scanner. It is stored for
your account only, so other people signed in to the same server cannot use it.

### Which one is being used

The server's `sessionagent-*.log` says so directly:

```
Channel to the client PC confirmed: heartbeat answered in 15 ms.
Direct connection in use for session 30; the RDP virtual channel is not carrying data.
```

The first is the Remote Desktop channel — the normal path, and the better one, because it
needs no ports and rides the encryption Remote Desktop already provides. The second is the
direct connection, which is encrypted separately with the pairing key.

---

## If it does not work

Work through these in order. Stop when one of them fixes it.

### 1. Sign out of the server and back in

**Sign out** — Start menu → your name → Sign out. Not the X, not "disconnect".

The server starts the scanner service for a session when that session is *created*. A
session that was already open before the install never got one. This is the single most
common cause.

### 2. Check the scanner locally

On the PC with the scanner, from any Command Prompt:

```
ScanBridge-Client.exe --enumerate-once
```

This drives the whole local scanner stack with no RDP in the picture and prints what it
sees. If the scanner is not listed here, the problem has nothing to do with Remote Desktop —
fix the driver first.

### 3. Check the driver is visible on the server

`CHECK-TWAIN.bat` and `CHECK-SERVER.bat`, and the deeper self-test in step 4, are part of a
separate diagnostics toolkit that comes with the full build rather than the two installers —
ask whoever gave you ScanBridge for it, or see `docs/04-TROUBLESHOOTING.md` if you built
it yourself. `CHECK-TWAIN.bat` loads a real TWAIN manager on the server and asks it what it
can find; **PASS** means the driver is fine and the problem is elsewhere, **FAIL** means the
driver itself is at fault.

### 4. Prove scanning works without Remote Desktop

`SELF-TEST.bat`, from the same diagnostics toolkit, run on the PC **that has the scanner**,
runs everything except the Remote Desktop hop and scans a real page. This splits the problem
in half:

- **PASSED** — scanning works; the fault is the Remote Desktop connection.
- **FAILED** — ignore Remote Desktop entirely; the fault is on that PC.

(Needs NAPS2 installed on that PC — it borrows NAPS2's TWAIN manager. NAPS2 is free:
<https://www.naps2.com>)

### 5. Send the logs

Anything running as a signed-in user — the tray application on your PC, the session agent
and the driver on the server, the plugin inside `mstsc.exe` — logs to its own
`%LocalAppData%\ScanBridge\logs`. Only the machine-wide service on the server logs to
`%ProgramData%\ScanBridge\logs`. `COLLECT-LOGS.bat`, from the diagnostics toolkit,
gathers both locations into one zip on the Desktop; without it, zip the two folders above by
hand on each machine and send both.

No detailed-logging step to switch on first — the record of every message crossing the
connection is written by default.

The two bundles together say which side lost the request:

```
server:  -> client PC: "ScannerEnumRequest"     (the server sent it)
your PC: server -> agent: message 0x10          (your PC received it)
```

First line present and second missing → lost in transit.
Neither → it never left the server.

---

## Things that are normal, not faults

**Windows Fax and Scan and the Windows Scan app never show it.** They are WIA-only. A
virtual WIA device needs a Microsoft-signed driver. Every TWAIN program works — Acrobat,
NAPS2, ABBYY, ERP and document-management software. TSScan has exactly the same boundary.

**All your scanners show "Wia" in the Interface column.** Correct. Windows also
re-presents every WIA scanner as a fake TWAIN entry named `WIA-<name>`; those duplicates
are hidden because they are wrappers around the same device, and the poorer of the two.

**`CHECK-TWAIN.bat` says "condition code 3" for `twain_32.dll`.** Expected on a server.
The `twain_32.dll` Windows ships stopped being a real TWAIN manager years ago; it only
re-lists WIA devices and never reads driver files. Real scanning programs carry their own.

---

## Removing it

- **A PC:** open an ordinary Command Prompt where you installed it and run
  `ScanBridge-Client.exe --uninstall` — not elevated, same as the install.
- **The server:** from an administrator Command Prompt, run
  `ScanBridge-Server.exe --uninstall`.

Everyone must sign out of the server and back in afterwards, or scanning programs keep
listing a scanner that is no longer there.

Close any scanning programs on the server first. Files still in use cannot be deleted,
and the removal then only finishes after a restart.
