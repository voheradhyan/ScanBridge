# ScanBridge visual identity

## Files

| File | What it is |
|---|---|
| `scanbridge.svg` | Primary mark, two colours, transparent background. Has a `prefers-color-scheme: dark` rule that swaps the pier colour for a light tone, so it holds up in both a light-themed and a dark-themed host document without a second file. |
| `scanbridge-mono.svg` | Single-colour silhouette of the same shape, filled with `currentColor`. For contexts that recolour icons themselves (a tray, a XAML `Fill` binding, a CSS `color` rule) rather than accepting whatever colour the SVG ships with. |
| `scanbridge.ico` | Windows icon, 7 frames (16, 24, 32, 48, 64, 128, 256 px), 32-bit BGRA with real alpha. This is what an `.exe`, a shortcut, or a `NotifyIcon` in the tray actually loads — SVGs are not a native Windows icon format, so this file, not the SVGs above, is what most users will actually see day to day. |
| `tools/icogen/` | Source for the program that generates `scanbridge.ico`. Not built as part of the solution; see "Regenerating the .ico" below. |

## The mark

Two piers stand on either side of a gap; a single deck spans across and rests on
both of them. The piers are the two places involved — the PC with the scanner
attached, and the remote session that wants to use it — drawn identically because
the product doesn't privilege one side over the other. The deck is ScanBridge
itself: the thing that turns two separate places into one reachable one.

Reduced to three flat, rounded rectangles deliberately. No perspective, no
scanner-glass-and-lid drawing, no cloud — those either don't survive being 16
pixels tall or don't say "bridge." The shape is close to the letter Π: two legs
and a crossbar is about as little geometry as "bridge" can be built from, which
is exactly what a tray icon needs.

## Colour

| Name | Hex | Used for |
|---|---|---|
| Ink Steel | `#4A6FA5` | Piers, in `scanbridge.svg`'s default (light) mode, in `scanbridge-mono.svg`'s default fill, and throughout `scanbridge.ico`. |
| Span Teal | `#2FD3C7` | The deck, everywhere. The one accent colour — it is the part of the mark that represents ScanBridge itself, so it stays constant across every variant and every background. |
| Paper | `#E7ECF2` | Pier colour in `scanbridge.svg` only, swapped in under `prefers-color-scheme: dark`. |

Two colours in the mark, plus Paper as a dark-mode substitute for one of them —
within the "two or three colours" the mark needed to stay at.

**Why Steel and not a deeper navy.** The first version of this mark used a near-navy
`#16324F` for the piers. It looks better on paper — 11.8:1 contrast against a white
background — but a Windows tray or taskbar is at least as often dark
(`#202020`-ish) as it is light, and `.ico` files cannot ship a `prefers-color-scheme`
rule the way `scanbridge.svg` can; whatever colour is baked into the icon has to work
on *both* without knowing which one it will land on. Against `#202020`, that navy
measured 1.24:1 — close to invisible. Steel Blue (`#4A6FA5`) was chosen to fix that:
4.61:1 against a light background, 3.19:1 against a near-black one, clearing the
non-text contrast floor (3:1) on both sides instead of maximizing one at the other's
expense. `scanbridge.svg` keeps the option to do better than "works on both" — its
dark-mode swap goes all the way to a near-white `Paper` pier — but the `.ico` and the
mono SVG's default fill don't have that option, so they use the colour that was
actually chosen to survive an unknown background. Span Teal was left alone: it
measures weakly against white by the numbers (1.7:1) but reads clearly in practice
because it's a fully saturated colour against a near-neutral background, not a
luminance-matched one — confirmed by looking at rendered output on both backgrounds
(see "How this was checked" below), not by the ratio alone.

## Geometry

Everything is drawn on a 256×256 canvas, on a 16-unit grid, so key edges land on
whole pixels at every shipped size (16 × 16 = 256):

- Deck: `x=32 y=32 w=192 h=32`, corner radius 16 (radius = half the height, so the
  ends are fully rounded caps).
- Left pier: `x=48 y=48 w=48 h=176`, corner radius 16.
- Right pier: `x=160 y=48 w=48 h=176`, corner radius 16.
- The deck is drawn last (on top), overlapping the top 16 units of each pier, so
  there is no seam between deck and pier at small sizes.

`scanbridge.svg`, `scanbridge-mono.svg`, and `tools/icogen/Program.cs` all encode
these same six numbers. If the mark ever changes, change them in all three.

## How this was checked

- **16×16 legibility (the hard constraint).** `scanbridge.ico`'s 16 px frame was
  extracted, viewed at native size, and separately upscaled 20× with nearest-neighbour
  scaling (no smoothing) on both a light and dark backing square, so the actual source
  pixels — not a browser's downscale of a bigger image — could be inspected directly.
  The two piers and the crossbar stay distinct at both zoom levels and on both
  backgrounds.
- **Light/dark background.** `scanbridge.svg` was opened in a browser with the OS
  colour scheme forced to each of light and dark in turn, confirming the
  `prefers-color-scheme` swap actually fires and that Span Teal reads clearly against
  both.
- **The .ico is real, not one image pretending to be seven.** After writing the file,
  the generator reads its own output back — parses the `ICONDIR` header, then each of
  the 7 `ICONDIRENTRY` records, then decodes the PNG `IHDR` chunk embedded at each
  entry's offset and checks its width/height against what the directory entry claims.
  All 7 entries are present, all 7 are 32 bits per pixel, and all 7 carry distinct
  pixel data whose embedded dimensions match the directory (16, 24, 32, 48, 64, 128,
  256 — not seven copies of one size). Frame byte sizes are also all different (216,
  260, 377, 547, 717, 1387, 2833 bytes), which a single scaled-and-duplicated image
  would not produce. This check runs automatically on every build — see
  "Regenerating the .ico" below to reproduce it.

## Regenerating the .ico

`scanbridge.ico` is generated, not hand-edited. The source lives in
`tools/icogen/` (`Program.cs` + `icogen.csproj`, targeting `net8.0-windows`) but
is deliberately not part of `ScanBridge.sln` — it's a one-off image-generation
utility, not product code, and pulling `System.Drawing.Common` into the main
solution would be the wrong trade for something run maybe once a year.

It draws the mark described above with GDI+ (`System.Drawing`) at 16, 24, 32, 48,
64, 128, and 256 px, PNG-encodes each frame (32bpp, alpha preserved), and writes a
plain `ICONDIR` + `ICONDIRENTRY[]` + concatenated-PNG-blobs file by hand —
`System.Drawing`'s own `Icon` type cannot write a multi-resolution, alpha-capable
`.ico`, so this bypasses it entirely. It then reads that file straight back and
prints every directory entry (size, bit depth, byte offset, embedded PNG
dimensions) so a broken or truncated icon fails loudly instead of shipping quietly.

To rebuild `scanbridge.ico` from a Windows machine with the .NET 8 SDK:

```powershell
cd assets\tools\icogen
dotnet run -c Release -- ..\..\scanbridge.ico
```

The first path argument is the output `.ico` path. An optional second argument is
a directory to also dump the individual PNG frames into, for spot-checking:

```powershell
dotnet run -c Release -- ..\..\scanbridge.ico .\_pngdump
```

Console output ends with a verification block; the run should be treated as
failed if it prints anything other than `OK: 7 entries, sizes
16,24,32,48,64,128,256, all PNG/32bpp, directory size matches embedded IHDR size.`
