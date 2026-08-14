# Remote Scanner — setup guide

Scanner plugged into your own PC becomes usable by programs running on the server
through Remote Desktop.

Two folders, produced by the build:

| Folder | Goes to | Size |
|---|---|---|
| `build\server` | the Windows Server, once | 73 MB |
| `build\client` | every PC that has a scanner | 300 MB |

Do the server first. A PC installed before the server has nothing to talk to.

---

## Part 1 — The server (once, needs an administrator)

1. Copy the whole **`build\server`** folder onto the server itself.
   Not a network drive — copy it to the server's own disk, e.g. `C:\RemoteScanner`.

2. Right-click **`INSTALL-ON-SERVER.bat`** → **Run as administrator**.

3. If Windows shows a blue *"Windows protected your PC"* box:
   **More info** → **Run anyway**.

4. Wait for the green **DONE**.

5. Double-click **`CHECK-SERVER.bat`**. It should say **SERVER LOOKS READY**.

Nothing to open on the firewall. It rides the Remote Desktop connection you already have.

---

## Part 2 — Your PC, and each colleague's PC

1. **Close Remote Desktop Connection completely.** The installer replaces a file that
   Remote Desktop holds open while it runs.

2. Copy the whole **`build\client`** folder to that PC.

3. Double-click **`INSTALL-ON-MY-PC.bat`**. No administrator needed.

4. When it finishes, **Remote Scanner** appears in the Start Menu and starts automatically
   with Windows. A small icon sits near the clock.

5. Open it and check your scanner is listed under **Detected scanners**.

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
   Remote Scanner (YOUR-PC-NAME)
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

1. Right-click **`ALLOW-DIRECT-CONNECTION.bat`** → **Run as administrator**.
   Allows incoming TCP 47214, from your local network only, to Remote Scanner only.
   (The installer offers this too; this is here for when it was declined.)

2. Open **Remote Scanner** and click **Pairing Code**. It copies to the clipboard.

**In your Remote Desktop session**

3. Run **`PAIR-WITH-MY-PC.bat`** and paste the code.

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

### 2. Check the server

On the server, double-click **`CHECK-SERVER.bat`**.

Read the section **"Last failure reported by the driver"**. It names the cause in plain
words and says what to do — it distinguishes five faults that all look identical from
inside the scanning program.

### 3. Check the driver is visible

On the server, double-click **`CHECK-TWAIN.bat`**.

**Part 2** is the answer. It loads a real TWAIN manager and asks it what it can find.
PASS means the driver is fine and the problem is elsewhere.

### 4. Prove scanning works without Remote Desktop

Copy the **`build\server`** folder to the PC **that has the scanner**, and double-click
**`SELF-TEST.bat`** there.

It runs everything except the Remote Desktop hop and scans a real page. This splits the
problem in half:

- **PASSED** — scanning works; the fault is the Remote Desktop connection.
- **FAILED** — ignore Remote Desktop entirely; the fault is on that PC.

(Needs NAPS2 installed on that PC — it borrows NAPS2's TWAIN manager. NAPS2 is free:
<https://www.naps2.com>)

### 5. Send the logs

Double-click **`COLLECT-LOGS.bat`** on **both** machines and send both zip files.

They land on the Desktop. No detailed-logging step to switch on first — the record of
every message crossing the connection is written by default.

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

- **A PC:** double-click **`UNINSTALL.bat`** in the client folder.
- **The server:** double-click **`UNINSTALL-SERVER.bat`**. It asks for administrator
  rights itself — no right-clicking, no PowerShell.

Everyone must sign out of the server and back in afterwards, or scanning programs keep
listing a scanner that is no longer there.

Close any scanning programs on the server first. Files still in use cannot be deleted,
and the removal then only finishes after a restart.
