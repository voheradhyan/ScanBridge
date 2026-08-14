# Contributing

## Before you start

There is one contributor and one machine this has ever been tested on (see
`docs/07-COMPATIBILITY.md`). That is not a warning to stay away — it is the reason changes
need to say what was actually run, not what should theoretically work. See "What to say in a
pull request" below.

## Building

```bash
powershell -ExecutionPolicy Bypass -File installer\Build-All.ps1
```

Requires Visual Studio Build Tools 2022 (C++ workload + Windows SDK) and the .NET 8 SDK. It
does the following, in this order, and stops at the first failure:

1. **TWAIN constant check** (`installer\ConstantCheck`) — before anything else compiles.
2. **Native build**, both bitnesses — the virtual TWAIN Data Source and the RDP DVC plugin.
3. **Managed build** — `ScanBridge.sln`.
4. **Unit tests** — protocol, validation, CRC, auth, flow control, the LAN handshake.
5. **Native transport tests** (`pipetest.exe`, both bitnesses).
6. **Packing** — two single-file installers, `ScanBridge-Server.exe` and
   `ScanBridge-Client.exe`, into `build\dist\`, each checked afterwards with `--extract` to
   confirm the payload actually landed inside the file rather than merely being intended to.
7. **Discovery gate** — `dsmprobe` against a real `twaindsm.dll`, if one is installed.

Steps 1 and 7 are described below because they are not ordinary tests: they exist specifically
to check something no amount of testing against our own code can check.

`-SkipNative` and `-SkipTests` are there for iterating on managed code only; do not treat a
build run with either flag set as equivalent to a full run before opening a pull request.

## Why the build gates exist

**The TWAIN constant check.** `rs_twain.h` (native) and `TwainTypes.cs` (managed) each define
the numeric value of every `DG_`/`DAT_`/`MSG_`/`CAP_`/… constant in the TWAIN specification.
The data source manager forwards those numbers untouched between application and data source —
it does not interpret them. That means a data source and a test that both compile against the
same wrong header agree with each other perfectly and disagree with every real TWAIN
application. This is the one class of bug the repository cannot catch by testing itself: no
unit test, no discovery gate, no end-to-end scan against our own stack will ever notice that a
constant is wrong, because everything here shares the same source of truth for what the
constant means.

Six wrong `DAT_` values shipped past 67 unit tests, the discovery gate, and end-to-end scans
in three of the four transfer mechanisms before this check existed, and cost two days to find
by reading a log from someone else's machine. `installer\ConstantCheck` now checks every
constant in both files against the enums compiled into NAPS2's `NTwain.dll` — a real TWAIN
implementation maintained outside this repository — and fails the build on any mismatch. If
you add or change a TWAIN constant, this is what will catch a typo, not the rest of the suite.

**The discovery gate.** A data source can load, initialise, answer every direct call
correctly, and pass every unit test while being invisible to every real scanning application —
that happened here when the data source exported `DSM_Entry` instead of `DS_Entry`. Nothing
that asserts against the data source directly can detect it, because the fault is in how a
*manager* resolves the file, not in how the file answers once found. `dsmprobe` loads a
genuine `twaindsm.dll`, points its search path at a scratch folder holding the freshly built
`.ds`, and asks the manager what it sees. The build fails if the manager does not list it.

If you don't have NAPS2 installed, `ConstantCheck` prints a warning and exits 0 (the check is
skipped, not failed); the discovery gate does the same when no `twaindsm.dll` is found. A build
that passes on a machine without either has not actually verified either gate — see the CI
section below, which is exactly that situation.

## Hardware tests CI cannot run

CI has no scanner attached to it. Nothing here substitutes for running the real thing before a
change touching the scanner backends, the protocol, or either native component ships.

**`SELF-TEST.bat`**, run on the PC with the scanner, replaces the RDP hop with a direct
loopback connection and drives the full production path on real hardware:

```
data source → session pipe → session agent → local pipe → tray agent → ScanHost → scanner
```

It is also the fastest way to split a bug report in half: if the self-test passes, scanning
itself works and the fault is in RDP redirection; if it fails, the fault is somewhere in the
list above and RDP is not worth looking at yet.

**`dsmprobe`** drives all four TWAIN transfer mechanisms against real hardware individually,
not just whichever one a particular application happens to use — this matters because the
mechanism NAPS2 actually uses (memory transfer) is precisely where the six wrong constants
mentioned above were hiding, unexercised by everything else in the suite at the time:

```bash
x64\dsmprobe.exe <path-to-twaindsm.dll> x64\ScanBridge.ds --scan out.bmp --native
x64\dsmprobe.exe <path-to-twaindsm.dll> x64\ScanBridge.ds --scan out.bmp --memory
x64\dsmprobe.exe <path-to-twaindsm.dll> x64\ScanBridge.ds --scan out.bmp --memfile
x64\dsmprobe.exe <path-to-twaindsm.dll> x64\ScanBridge.ds --scan out.bmp
```

(the last form, with no mechanism flag, is file transfer). Run all four, on both bitnesses,
against whatever scanner you have, before changing anything in the state machine or the
transfer paths. The full hardware checklist — 22 scenarios covering ADF, duplex, cancellation,
disconnects, and multi-session use — is in `docs/03-SETUP.md`.

## House rules

These are visible in the existing code and reviews will apply them:

- **Comments explain why, not what.** The code already says what it does; a comment earns its
  place by saying something the code cannot — a constraint, a history, a trade-off, a fault it
  is working around.
- **A test that cannot fail is worth nothing.** If you cannot describe the specific way a test
  would fail on the old code, it is not testing the thing you think it is.
- **Anything asserted should be measured.** "Fixed", "verified", "should work now" are claims,
  not evidence. Say what you ran and what it showed. `docs/06-SECURITY-REVIEW.md` and the
  status table in `README.md` are written in this style on purpose — match it.

A few conventions that follow from the above and show up throughout the tree: constant
identifiers are checked against an outside reference rather than trusted (see above); rights
are granted by SID, never by a localised account name like "Users"; MAC/HMAC comparisons use
`CryptographicOperations.FixedTimeEquals` or a constant-time loop, never `SequenceEqual` or
`memcmp`; nothing branches on an OS build number. If a change needs to deviate from one of
these, say why in the pull request rather than leaving it to be noticed in review.

## If you change the wire protocol

`Wire.cs` and `native/include/rs_protocol.h` are two hand-written mirrors of the same format.
Nothing generates one from the other. If you change one, change the other in the same commit,
bump `Wire.Version` on any incompatible change, and update the round-trip tests in
`tests/Unit/ProtocolTests.cs` — they pin the C# side; the C++ side has no equivalent, so review
both files together.

## What to say in a pull request

The template asks for this directly, but the short version: say which of the build gates you
ran and what they reported, and say what you tested on real hardware versus what you are
asserting should work by inspection. "Builds clean, gates pass, not tested on hardware" is a
completely acceptable and honest thing to write. A claim with no measurement behind it is not.
