## What changed, and why

<!-- The why matters more than the what here — see CONTRIBUTING.md's house rules. -->

## What was measured

<!--
Say what you actually ran, not what you expect would happen. "Builds clean, gates pass, not
tested on hardware" is a fine and honest answer. A claim with nothing behind it is not.
-->

- [ ] `dotnet build ScanBridge.sln` — clean, no new warnings
- [ ] `dotnet test tests\Unit\ScanBridge.Tests.Unit.csproj` — passes
- [ ] `native\build.cmd both` — builds, if this change touches native code
- [ ] TWAIN constant check ran against a real `NTwain.dll` (not just the CI skip path) — if this touches `rs_twain.h` or `TwainTypes.cs`
- [ ] Discovery gate (`dsmprobe` against a real `twaindsm.dll`) — if this touches the data source
- [ ] `SELF-TEST.bat` on real hardware — if this touches the scanner backends, ScanHost, or the protocol
- [ ] Tested through an actual RDP session — if this touches the DVC plugin, session agent, or reconnect logic

CI only covers the first two, and the native build when the toolchain is available — see the
comments in `.github\workflows\build.yml` for exactly what it does and does not verify. Nothing
with a scanner or a real TWAIN manager involved runs in CI at all.

## Anything accepted rather than fixed

<!--
If you noticed something wrong and deliberately left it — out of scope, low risk, needs a
larger change than this PR should carry — say so here rather than leaving it to be
rediscovered. docs/06-SECURITY-REVIEW.md is written in exactly this style; match it.
-->
