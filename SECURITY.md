# Security

## Reporting a vulnerability

Use GitHub's private security advisories for this repository, rather than a public issue: on
the repository's GitHub page, go to **Security → Advisories → Report a vulnerability**. That
gets the report to the author without putting details in front of anyone else until there's a
fix.

This is a one-person, unfunded project with no security team and no SLA. There is no
guarantee of a fix within any particular window, and no guarantee of a bounty — there isn't
one. What you will get is an honest response about whether the report is understood, whether
it will be fixed, and roughly what that depends on. If you need a firmer commitment than that
for your own compliance purposes, say so in the report; it changes how the report gets
prioritised even if it can't change what's promised up front.

Please do not open a public issue for a vulnerability before there is a fix or an agreed
disclosure date.

## Threat model

Full detail is in `docs/05-SECURITY.md` (design) and `docs/06-SECURITY-REVIEW.md` (a review
pass with findings, fixes, and things deliberately accepted). Summary:

**What this protects:** scanned business documents in transit between a user's PC and their
Remote Desktop session, on a server other users are logged into at the same time.

**Three attackers are considered:**

1. **Another user on the same RDS host.** Realistic — these are multi-user machines by
   design. They can run code, enumerate pipes, and read anything the filesystem permits.
2. **Another process in the same session, running as the same user.** *In scope only insofar
   as it's named explicitly as out of scope.* A process running as the same user is inside the
   trust boundary by design — it can already read that user's registry, DPAPI blobs, and
   screen, so defending against it is neither attempted nor possible. This is stated so the
   boundary is explicit rather than assumed.
3. **Anything that can reach the direct-connection port on the client PC's network** — the
   fallback transport used when the RDP channel isn't available. The only hop in the product
   with no operating system access control standing in front of it by default.

**In scope:** the two named pipe hops and their ACLs/HMAC authentication, the direct-connection
LAN listener and its authentication, DPAPI-protected secret storage, wire parsing and bounds
checking, process launching, and the two DLLs described below.

**Out of scope, by design:**

- Another process running as the same signed-in user (attacker 2, above).
- A malicious administrator, or anything running as SYSTEM, on the Session Host. DPAPI's
  user-scoping does not defend against SYSTEM; this is inherent to Windows and true of every
  product in this category.
- A compromised client PC. The scanner is attached to that machine; if it's compromised, so
  are the scans.
- Data at rest: there isn't any in this product's own control. Pages are decoded in memory and
  handed to the calling application through the transfer mechanism it requested; nothing is
  written to disk by this product at any point on either machine.

## Unsigned binaries

`ScanBridge.ds` (loads into every TWAIN scanning application) and `ScanBridge.DvcPlugin.dll`
(loads into `mstsc.exe`) are **not currently Authenticode-signed**. Those are exactly the two
places endpoint protection is most suspicious of an unsigned DLL, and signing is also what
would make tampering with either file detectable. This is a known, tracked limitation, not an
oversight — see `docs/06-SECURITY-REVIEW.md` for how it's tracked.

## Other known limitations

- No pairing-key revocation from the interface yet; the key lives until its registry value is
  removed by hand.
- Verified end to end on exactly one hardware and OS combination (see
  `docs/07-COMPATIBILITY.md`). "Designed to work" and "verified to work" are marked
  differently throughout the documentation on purpose — check which one applies before relying
  on an environment that hasn't been exercised.
