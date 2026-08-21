# Orientation

For whoever picks this up next, human or otherwise, with no memory of building it.

Read [README.md](../README.md) first for what the product is and how to build it. This document
is the other half: what went wrong, how each fault was actually found, and which of those
lessons are now enforced by the build rather than left to memory. It exists because every
expensive bug in this project had the same shape, and knowing that shape is worth more than
knowing any individual fix.

---

## 1. The shape every bad bug had

**Every layer reported itself healthy and nothing worked.**

That is not a figure of speech. It happened five times:

| What was broken | What it looked like |
|---|---|
| The data source exported `DSM_Entry` instead of `DS_Entry` | Loads, initialises, logs that it loaded, answers every direct call correctly — and is invisible to every scanning application. The manager does `GetProcAddress`, gets null, unloads it, says nothing. |
| A write queued behind a parked read on the same synchronous pipe handle | Both ends healthy, message accepted, message never delivered. |
| `FileStream` over an RDP channel handle | Every write reported success and carried nothing. |
| Six wrong `DAT_` constants | 67 unit tests, a real-manager discovery gate, and end-to-end scans in three transfer mechanisms all passed. |
| DIB rows handed to memory transfer unconverted | Correct size, correct geometry, correct return codes, and a blue photograph of a copper page. |

In each case the components agreed with each other perfectly. The disagreement was always with
something outside: a real manager, a real application, a real byte order.

**So the rule this project runs on: verify against an outside implementation, never against
yourself.** Three build gates exist only for that, and each was added after the corresponding
fault escaped everything else.

---

## 2. The two-day bug, in full, because it is the archetype

NAPS2 reported `TWAIN error: CapUnsupported` immediately after every scan that had plainly
worked — the scanner ran, the page transferred, then an error dialog.

The cause was that `rs_twain.h` had every image `DAT_` constant from `0x0103` upward off by
one. `DAT_IMAGEMEMXFER` — plain memory transfer, what NAPS2 uses — was defined as `0x0104`,
which is really `DAT_IMAGENATIVEXFER`.

Why nothing caught it: the data source manager forwards `DG`/`DAT`/`MSG` untouched, and
`dsmprobe` includes the same header as the data source. Both sides used `0x0104` for "memory
transfer", agreed completely, and disagreed with every real application in the world.

It then produced **two entirely different symptoms from one mistake**, which is why it took so
long:

1. First, nothing was implemented at `0x0103`, so it fell to the unimplemented-DAT branch and
   returned `TWCC_CAPUNSUPPORTED`. Hence the dialog.
2. Then `DAT_IMAGEMEMFILEXFER` was implemented — at the same wrong number — so the call reached
   *that*, which handed back the bytes of a BMP **file** into buffers the application was
   filling with raw scanlines. Every call succeeded, no error appeared, and NAPS2 had nothing
   it could turn into an image. The second symptom looked exactly like a regression caused by
   fixing the first.

It was found by reading a log from the user's machine. Not by reasoning, not by testing.

**Now enforced:** `installer\ConstantCheck` runs before anything compiles and checks all 308
constants in `rs_twain.h` and `TwainTypes.cs` against the enums inside NAPS2's `NTwain.dll`.
Re-introducing the original bug fails the build — verified by re-introducing it.

---

## 3. Things that are true and non-obvious

Each of these cost time to learn. None can be deduced from the code alone.

- **`DS_Entry`, not `DSM_Entry`.** Applications call `DSM_Entry` on the manager (six arguments);
  the manager calls `DS_Entry` on a data source (five). Exporting the wrong one produces a
  component that passes every test written against it directly and is invisible to every real
  manager. Undecorated in the `.def`, or x86 exports `_DS_Entry@20` and no manager finds it.

- **TWAIN structures are `#pragma pack(2)`.** Default packing shifts every field and produces
  garbage that almost works.

- **Memory transfer of `TWPT_RGB` is R,G,B. A Windows DIB is B,G,R.** That byte order is the
  definition of the pixel type, not a platform detail. Native transfer hands over a DIB, so BGR
  is correct there; memory transfer must convert. A probe that writes received bytes into a BMP
  cannot see the difference, because a BMP is BGR too — two mistakes that cancel.

- **The four transfer mechanisms share almost no code.** Passing one says nothing about the
  others. `dsmprobe` drives all four for that reason. Native transfer had never once run until
  the constants were fixed, and it had been landing in the memory-transfer handler.

- **A dynamic virtual channel name is not limited to 7 characters.** That limit is for static
  channels. `ScanBridge` is 10 and works.

- **`Assembly.Location` is empty in a single-file app.** It silently yields a 1601 date rather
  than throwing.

- **`Application.Shutdown(code)` during `OnStartup` leaves the process exit code at -1**
  regardless of what you pass. Use `Environment.Exit` where a script reads `errorlevel`.

- **`SpecialFolder.ProgramFiles` answers according to the calling process's bitness.** A 32-bit
  component asking where the install went is told "Program Files (x86)". Use `ProgramW6432`.

- **A `.bat` file with LF line endings is mis-parsed by `cmd.exe`.** `setlocal` becomes
  `'tlocal' is not recognized`, from a script nobody changed. `.gitattributes` pins CRLF.

- **Only `mstsc.exe` loads RDP add-ins.** Not the Store "Windows App", not FreeRDP, not Royal
  TS, not Jump Desktop. There is no workaround; RemoteApp and RDWeb use `mstsc.exe` underneath
  and are fine.

---

## 4. One fault that was never explained

Components running as the same user, reading the same registry value, at the same moment,
intermittently got different shared secrets. Measured, not inferred: the stored blob's SHA-256
was identical before and after, exactly one `Software\ScanBridge` key existed across all loaded
hives, and the 32- and 64-bit views agreed.

A theory that was wrong and should not be inherited: that this was `mstsc.exe`'s cached
`HKEY_CURRENT_USER`. It is documented Windows behaviour and it fitted the plugin evidence — but
the tray agent, a plain desktop application that never impersonates, then did the same thing.

Something to know before re-measuring it, which was not known when it was written: a large part
of the evidence above was gathered through an assistant's tooling, and that tooling ran inside
an MSIX container. A containerised process gets a private, virtualised `HKEY_CURRENT_USER` and
`%LOCALAPPDATA%`. Registry reads made that way describe the container and not the machine — the
same divergence, from a cause with nothing to do with this product. That is a confirmed property
of the tooling; it is *not* established that it explains the original fault, and the tray-agent
observation may well be real. But anyone reproducing this section must run the reads from an
ordinary shell on the machine itself, or they will measure the wrong hive and believe whatever
the container happens to hold. The same mistake cost hours on the add-in registration later, for
exactly this reason.

Two mitigations are in place, and scanning is unaffected either way: secrets are read from
`HKEY_USERS\<SID>` opened by name, and the local pipe hop verifies the caller's SID directly
rather than leaning on the secret. If the keys ever diverge again the agent log says so; treat
that as a lead, not an outage.

---

## 5. What has never been tested

Being honest about this is more useful than a confident summary. See
[07-COMPATIBILITY.md](07-COMPATIBILITY.md) for the full matrix.

- **TWAIN-only scanners.** The whole managed TWAIN backend. It carried three wrong constants
  until 14 Aug 2026 precisely because the only scanner on hand was WIA and this path never ran.
- **The encrypted TCP fallback between two machines.** Verified between two processes only.
- **Sheet-fed and duplex hardware, multiple simultaneous sessions, non-Latin machine names in
  the wild.**

---

## 6. How to work on it

```powershell
installer\Build-All.ps1
```

Runs, and fails on: the constant check, native C++ for both bitnesses, the solution, 71 unit
tests, native transport tests in both bitnesses, a discovery gate that loads a real
`twaindsm.dll` and requires it to list our data source, and a payload check that extracts what
each installer carries.

What that does **not** cover is anything involving a scanner. For that:

```powershell
# One page through the whole stack except the RDP hop, on the PC with the scanner.
build\server\SELF-TEST.bat

# Or drive the driver directly, one mechanism at a time.
build\server\x64\dsmprobe.exe "C:\Program Files\NAPS2\lib\_win64\twaindsm.dll" `
    build\server\x64\ScanBridge.ds --scan "$env:TEMP\out.bmp" --memory
```

`installer\TEST-WITH-NAPS2-LOCALLY.ps1` reproduces a real NAPS2 scan on one machine with no
server and no RDP hop. Anything that is not the transport should be chased there first: it
turns a copy, an install and a sign-out per attempt into seconds.

### House rules, visible in the code

- Comments say **why**, not what. If a line looks wrong and is right, that is the line that
  needs the comment.
- **A test that cannot fail is worth nothing.** `dsmprobe`'s post-transfer checks include a
  control case that *must* still return `TWCC_CAPUNSUPPORTED`, because otherwise a passing run
  would prove only that the probe cannot see failures. The same rule caught a fault in the CI
  workflow before it ever ran: two commands under one `run:` block are gated only by the last
  one's exit code, so `pipetest` x64 could fail and the build stay green. One command per step
  now, for that reason.
- **Measure, do not assert.** Every claim in these documents that could be checked, was. Where
  something is believed rather than measured, it says so.
- Do not ship a theory. Reproduce, fix, then re-run the gates.
