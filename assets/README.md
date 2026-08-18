# ScanBridge visual identity

## Files

| File | What it is |
|---|---|
| `scanbridge.svg` | Primary mark, 512×512, drawn full-bleed on a rounded tile that carries its own background. White ink on Bridge Navy. This is the 256px-and-up artwork; the smaller `.ico` frames are deliberately not this drawing. |
| `scanbridge-mono.svg` | The same shape with no tile, stroked in `currentColor`, so it takes the colour of the text it sits in. For a README header, a print, a XAML `Fill` binding — anywhere the host supplies its own background. Not for the tray; see below. |
| `scanbridge.ico` | Windows icon, 7 frames (16, 24, 32, 48, 64, 128, 256 px), 32-bit BGRA with real alpha. This is what an `.exe`, a shortcut, or a `NotifyIcon` in the tray actually loads — SVG is not a native Windows icon format, so this file, not the SVGs above, is what most users see day to day. |
| `social-preview.png` | 1280×640 card for GitHub's **Settings → General → Social preview**. A repository has no icon of its own; this is the only image it owns, and it is what appears when the link is pasted into Slack, X or LinkedIn. |
| `tools/icogen/` | Source for the program that generates both `scanbridge.ico` and `social-preview.png`. Not part of `ScanBridge.sln`; see "Regenerating" below. |

## The mark

A capture frame, a bridge, and a page.

The four corner brackets say "scan" without drawing a scanner, which at icon size is a grey
box that could equally be a printer. The span inside them says what the product does: the
scanner is on one side, the application is on the other, and neither side is drawn as more
important than the other. The three lines beneath the deck are what is carried across.

A suspension bridge rather than a flat slab because the silhouette has to survive being
small: a deck, two towers and a dipping cable read as *bridge* in one glance, where a plain
crossbar reads as a minus sign.

## Colour

| Name | Hex | Used for |
|---|---|---|
| Bridge Navy | `#214A8C` | The tile, in `scanbridge.svg` and in every frame of `scanbridge.ico`. |
| Paper White | `#FFFFFF` | All ink inside the tile — brackets, bridge, page lines. |
| Span Teal | `#2FD3C7` | One rule on the social card, under the wordmark. The only place an accent appears; the mark itself is two colours. |
| Card Navy | `#0F2545` | The social card's ground. Deeper than the tile on purpose, so the tile reads as a tile instead of dissolving into the background. |

**Why a tile and not a bare glyph.** A tray icon has no say in what it is drawn against — a
light taskbar, a dark one, or an accent colour the user picked — and an `.ico` cannot carry
a `prefers-color-scheme` rule the way an SVG in a browser can. A bare glyph has to survive
all three backgrounds with one baked-in colour. A tile does not have to: it brings its own
background, so the only contrast that matters is ink against tile, and that one is fixed.

| Measured | Ratio |
|---|---|
| White ink on Bridge Navy (what you actually read) | **8.67:1** |
| Bridge Navy tile against a white taskbar | 8.67:1 |
| Bridge Navy tile against a `#202020` taskbar | 1.88:1 |

That last number is the point. The tile sits close to the luminance of a dark taskbar, which
would be fatal for a bare glyph and is irrelevant for a tile, because the tile is not the
thing being read. Its rounded corners separate it from the background where luminance does
not.

`scanbridge-mono.svg` gives all of that up deliberately: it inherits `currentColor` and so
can guarantee nothing about its background. Correct for a document, which sets both. Wrong
for a tray, which sets neither.

## Geometry

Everything is drawn in a 512-unit square. `scanbridge.svg`, `scanbridge-mono.svg` and
`tools/icogen/Program.cs` encode the same coordinates, and `Program.cs` scales that
512-unit space to whatever frame it is rendering, so the drawing and the SVG cannot drift
apart.

- Tile: `512×512`, corner radius `92`.
- Capture brackets: stroke `16`, round caps, `40`-long arms off a `16`-radius corner, at
  `(112,128)`, `(400,128)`, `(112,384)`, `(400,384)`.
- Deck: `M112 273 H400`, stroke `16`.
- Cables: one path from `(118,253)`, dipping between the towers and rising to `(394,253)`,
  stroke `16`.
- Towers: `M178 177V273` and `M334 177V273`, stroke `16` — one weight from cap to deck.
  They were once drawn twice, `16` above the cable junction and `7` below it, which reads
  as a mistake at any size big enough to see it.
- Hangers: `M210 225V273`, `M302 225V273`, `M256 239V273`, stroke `7`. The finest detail
  here, and the first thing dropped as the icon shrinks.
- Page: three rounded bars, `(207,297,98,10)`, `(194,323,124,10)`, `(181,349,150,10)`,
  radius `5`.

### Three levels of detail, because 16 pixels is not a small 256

The full mark is roughly 0.9 mm of ink at 16 px: the hangers are `7/512` of the width,
which is a fifth of one pixel. Drawn faithfully at 16 the whole thing collapses into a blue
square with grey haze in it. So `icogen` drops detail as the frame shrinks and thickens
what remains until it is at least two pixels — the smallest mark that survives
antialiasing. Where the tiers change was decided by rendering each size and looking at it,
not by picking round numbers.

| Frame | What is drawn |
|---|---|
| 64, 128, 256 | Everything: brackets, cables, towers, hangers, page. |
| 48 | Brackets and bridge at heavier weights; hangers and page dropped. |
| 32 | Brackets kept — their arms are far apart and each is a clean two-pixel line — and the bridge reduced to one arch over one deck. |
| 16, 24 | One arch over one deck, nothing else. |

The 16/24 tier was not the first attempt. The first kept the towers and the dipping cables;
rendered at 16 and enlarged to inspect the actual pixels it was a white blob, because the
cable sagged to within 54 units of the deck — 1.7 px — and the ink either side of that gap
merged. Whatever remains at this size has to be separated by at least two pixels of
background, which in a 512-unit space means about 64.

## How this was checked

- **16×16 legibility, the hard constraint.** Every frame is dumped as a PNG, and the small
  ones are also written upscaled 20× with nearest-neighbour scaling (no smoothing) onto
  both a light and a dark backing square, so the actual source pixels — not a viewer's
  downscale of a bigger image — can be inspected directly.
- **The `.ico` is real, not one image pretending to be seven.** After writing the file the
  generator reads its own output back: parses `ICONDIR`, then each of the 7 `ICONDIRENTRY`
  records, then decodes the PNG `IHDR` chunk at each entry's offset and checks its
  dimensions against what the directory claims. All 7 are present, all 32bpp, and the frame
  sizes are all different — 357, 548, 718, 1059, 1280, 2396 and 4703 bytes — which a single
  scaled-and-duplicated image would not produce.
- **The social card cannot overrun its own edge.** Type is measured, not placed by eye: the
  body size is chosen by the longer of the two lines, and the run fails if the widest line
  ends past the right margin. Not hypothetical — the first version set 34 px by eye and the
  second line ran off the right of the card.
- **Card contrast.** White wordmark 15.3:1 on Card Navy, muted subtitle 7.6:1, teal rule
  8.2:1.

## Regenerating

Both artefacts come from `tools/icogen/` (`Program.cs` + `icogen.csproj`, `net8.0-windows`),
deliberately not part of `ScanBridge.sln` — it is a one-off image utility, not product code,
and pulling `System.Drawing.Common` into the main solution would be the wrong trade for
something run maybe once a year.

```powershell
cd assets\tools\icogen
dotnet run -c Release -- ..\..\scanbridge.ico
```

The first argument is the output `.ico`. An optional second is a directory to also dump the
individual PNG frames into, including the 20× nearest-neighbour blow-ups:

```powershell
dotnet run -c Release -- ..\..\scanbridge.ico .\_pngdump
```

Treat the run as failed if it prints anything other than
`OK: 7 entries, sizes 16,24,32,48,64,128,256, all PNG/32bpp, directory size matches embedded IHDR size.`

For the GitHub card:

```powershell
dotnet run -c Release -- --social ..\..\social-preview.png
```

Do **not** write `dotnet run --nologo -- ...`. `dotnet run` forwards `--nologo` to the
program rather than consuming it, so it arrives as the first argument and used to be taken
as the output path — which silently produced a file literally named `--nologo`, committed to
this repository, where it sat unnoticed until somebody listed the directory. The generator
now refuses any output path beginning with `--`.
