// ScanBridge .ico generator.
//
// Renders the ScanBridge mark (two piers + a spanning deck, see assets/scanbridge.svg)
// at each target pixel size with GDI+, then hand-assembles a multi-image Windows icon
// (ICONDIR + ICONDIRENTRY[] + one PNG blob per size) rather than relying on
// System.Drawing's Icon type, which does not support writing multi-resolution, 32bpp
// alpha .ico files. Every frame is stored as a PNG with real alpha, which is valid for
// every size from 16px up under the ICO spec used since Windows Vista, and is the only
// legal encoding for the 256px frame.
//
// After writing the file, reads the ICONDIR header straight back and prints each
// directory entry so the sizes actually present can be checked against what was asked
// for, rather than trusting the encoder.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

internal static class IconGen
{
    // Must match assets/scanbridge.svg and assets/scanbridge-mono.svg. Steel (not a
    // deeper navy) was chosen specifically here: this file cannot adapt itself to a
    // light or dark tray the way the SVG can with prefers-color-scheme, so its ink
    // colour has to hold a workable contrast ratio against both at once. See
    // assets/README.md for the numbers.
    private static readonly Color Navy = Color.FromArgb(0x21, 0x4A, 0x8C);  // tile
    private static readonly Color Steel = Color.FromArgb(0x4A, 0x6F, 0xA5); // Ink Steel
    private static readonly Color Teal = Color.FromArgb(0x2F, 0xD3, 0xC7);  // Span Teal

    private static readonly int[] Sizes = { 16, 24, 32, 48, 64, 128, 256 };

    private static int Main(string[] args)
    {
        string outPath = args.Length > 0 ? args[0] : "scanbridge.ico";
        string pngDumpDir = args.Length > 1 ? args[1] : "";

        var frames = new List<(int size, byte[] png)>();

        foreach (var size in Sizes)
        {
            using var bmp = RenderMark(size);
            using var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Png);
            var bytes = ms.ToArray();
            frames.Add((size, bytes));

            if (!string.IsNullOrEmpty(pngDumpDir))
            {
                Directory.CreateDirectory(pngDumpDir);
                File.WriteAllBytes(Path.Combine(pngDumpDir, $"scanbridge_{size}.png"), bytes);

                // Also dump a nearest-neighbour upscale (no smoothing) of the small
                // sizes so individual source pixels can be inspected directly with an
                // ordinary image viewer, on both a light and a dark backing square.
                if (size <= 32)
                {
                    int factor = 20;
                    foreach (var (label, back) in new[] { ("light", Color.FromArgb(0xF3, 0xF3, 0xF3)), ("dark", Color.FromArgb(0x20, 0x20, 0x20)) })
                    {
                        using var big = new Bitmap(size * factor, size * factor, PixelFormat.Format32bppArgb);
                        using (var gg = Graphics.FromImage(big))
                        {
                            gg.InterpolationMode = InterpolationMode.NearestNeighbor;
                            gg.PixelOffsetMode = PixelOffsetMode.Half;
                            gg.Clear(back);
                            gg.DrawImage(bmp, 0, 0, big.Width, big.Height);
                        }
                        big.Save(Path.Combine(pngDumpDir, $"scanbridge_{size}_x{factor}_{label}.png"), ImageFormat.Png);
                    }
                }
            }

            Console.WriteLine($"rendered {size}x{size}: {bytes.Length} bytes PNG, 32bpp alpha");
        }

        WriteIco(outPath, frames);
        Console.WriteLine();
        Console.WriteLine($"wrote {outPath}");
        Console.WriteLine();

        return VerifyIco(outPath, frames.Count) ? 0 : 1;
    }

    private static Bitmap RenderMark(int size)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.CompositingQuality = CompositingQuality.HighQuality;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.Clear(Color.Transparent);

        // Everything below is expressed in the 512-unit space of assets/scanbridge.svg and
        // scaled here, so the drawing and the SVG cannot drift apart.
        float u = size / 512f;
        g.ScaleTransform(u, u);

        using var tile = new SolidBrush(Navy);
        using var ink = new SolidBrush(Color.White);

        // The tile is drawn at every size, and carries its own background on purpose. A tray
        // icon has no idea what colour it will be drawn against — light taskbar, dark taskbar,
        // an accent colour — and a bare glyph has to survive all of them. A filled tile only
        // has to survive being next to them.
        using (var p = RoundedRect(0, 0, 512, 512, 92))
            g.FillPath(tile, p);

        // Three levels of detail, because an .ico may carry different artwork per size and the
        // alternative is worse.
        //
        // The full mark is roughly 0.9 mm of ink at 16 pixels: the hangers are 7/512 of the
        // width, which is a fifth of one pixel, and the document lines are not much better.
        // Drawn faithfully at 16 the whole thing collapses into a blue square with grey haze
        // in it. So detail is dropped as the frame shrinks, and what remains is thickened
        // until it is at least two pixels — the smallest mark that survives antialiasing.
        // Where the tiers change was decided by rendering each size and looking at it, not by
        // picking round numbers. At 32 the suspension cables, the towers and the deck all fall
        // within a pixel or two of each other and merge into a grey band, and two document
        // lines fuse into a block — so 32 keeps the frame and the arch and drops the rest. 48
        // has just enough room for the real bridge.
        Detail detail = size >= 64 ? Detail.Full : size >= 48 ? Detail.Medium : Detail.Small;

        // At 32 the capture frame still resolves — its arms are far apart and each is a clean
        // two-pixel line — so it is kept, and only the bridge is simplified. Below that the
        // brackets would be four dashes in the corners, and the arch has to carry the mark
        // alone.
        if (size == 32)
        {
            using (var pen = NewPen(30))
            {
                DrawBracket(g, pen, x: 104, armStartY: 176, cornerY: 104, armEndX: 176, radius: 20);
                DrawBracket(g, pen, x: 408, armStartY: 176, cornerY: 104, armEndX: 336, radius: 20);
                DrawBracket(g, pen, x: 104, armStartY: 336, cornerY: 408, armEndX: 176, radius: 20);
                DrawBracket(g, pen, x: 408, armStartY: 336, cornerY: 408, armEndX: 336, radius: 20);
            }

            using var bridgePen = NewPen(40);
            g.DrawLine(bridgePen, 136, 320, 376, 320);
            using var arch32 = new GraphicsPath();
            arch32.AddBezier(160, 320, 160, 190, 352, 190, 352, 320);
            g.DrawPath(bridgePen, arch32);
            return bmp;
        }

        if (detail == Detail.Small)
        {
            // One arch over one deck, and nothing else.
            //
            // The first attempt at this tier kept the towers and the dipping cables. Rendered
            // at 16 and enlarged to look at the actual pixels, it was a white blob: the cable
            // sagged to within 54 units of the deck, which is 1.7 pixels, so the ink either
            // side of that gap merged. Whatever remains at this size has to be separated by at
            // least two pixels of background, which in this 512-unit space means about 64.
            const float heavy = 46f;   // ~1.4 px at 16, ~2.2 px at 24
            using var pen = NewPen(heavy);

            g.DrawLine(pen, 76, 352, 436, 352);                       // deck

            // Legs wide and apex high, so the opening under the arch is about seven pixels
            // across and five tall at 16. Drawn thicker and tighter, the arch was legible but
            // read as a solid cap with a nick in it rather than something spanning a gap.
            using var arch = new GraphicsPath();
            arch.AddBezier(112, 352, 112, 140, 400, 140, 400, 352);
            g.DrawPath(pen, arch);

            return bmp;
        }

        bool full = detail == Detail.Full;

        // Capture brackets. The corners say "scan" without drawing a scanner, which at this
        // size would be a grey box that could equally be a printer.
        using (var pen = NewPen(full ? 16 : 26))
        {
            // Arms run from the given y back to the corner, then along to armEndX. These
            // coordinates are the ones in the SVG; getting the pairing wrong the first time
            // produced four little hooks floating near the corners instead of a frame.
            DrawBracket(g, pen, x: 112, armStartY: 168, cornerY: 112, armEndX: 176);
            DrawBracket(g, pen, x: 400, armStartY: 168, cornerY: 112, armEndX: 344);
            DrawBracket(g, pen, x: 112, armStartY: 344, cornerY: 400, armEndX: 176);
            DrawBracket(g, pen, x: 400, armStartY: 344, cornerY: 400, armEndX: 344);
        }

        using (var pen = NewPen(full ? 16 : 22))
        {
            g.DrawLine(pen, 112, 273, 400, 273);                       // deck

            // Suspension cables, dipping between the towers and rising to the abutments.
            using var cables = new GraphicsPath();
            cables.AddBezier(118, 253, 153, 245, 170, 225, 178, 202);
            cables.AddBezier(178, 202, 195, 224, 220, 239, 256, 239);
            cables.AddBezier(256, 239, 292, 239, 317, 224, 334, 202);
            cables.AddBezier(334, 202, 342, 225, 359, 245, 394, 253);
            g.DrawPath(pen, cables);

            // Towers, one weight from top to deck. The source drawing had them at 16 above the
            // cable junction and 7 below it — the same tower in two thicknesses, which reads as
            // a mistake at any size large enough to notice.
            g.DrawLine(pen, 178, 177, 178, 273);
            g.DrawLine(pen, 334, 177, 334, 273);
        }

        // Hangers: the finest detail in the mark, and the first thing to go. Below 64 pixels
        // they are thinner than a pixel and only muddy the space under the cables.
        if (full)
        {
            using var pen = NewPen(7);
            g.DrawLine(pen, 210, 225, 210, 273);
            g.DrawLine(pen, 302, 225, 302, 273);
            g.DrawLine(pen, 256, 239, 256, 273);
        }

        // The page under the bridge: what is actually being carried across.
        if (full)
        {
            g.FillPath(ink, RoundedRect(207, 297, 98, 10, 5));
            g.FillPath(ink, RoundedRect(194, 323, 124, 10, 5));
            g.FillPath(ink, RoundedRect(181, 349, 150, 10, 5));
        }
        else
        {
            // Two heavier lines rather than three thin ones: three would merge into a block.
            g.FillPath(ink, RoundedRect(200, 305, 112, 18, 9));
            g.FillPath(ink, RoundedRect(182, 341, 148, 18, 9));
        }

        return bmp;
    }

    private enum Detail { Small, Medium, Full }

    private static Pen NewPen(float width) => new(Color.White, width)
    {
        StartCap = LineCap.Round,
        EndCap = LineCap.Round,
        LineJoin = LineJoin.Round,
    };

    /// <summary>
    /// One capture bracket: a vertical arm at <paramref name="x"/> running to
    /// <paramref name="cornerY"/>, a rounded corner, then a horizontal arm to
    /// <paramref name="armEndX"/>. Which corner it is falls out of the coordinates.
    /// </summary>
    private static void DrawBracket(Graphics g, Pen pen, float x, float armStartY,
                                    float cornerY, float armEndX, float radius = 16f)
    {
        float towardCorner = Math.Sign(cornerY - armStartY) * radius;
        float alongArm = Math.Sign(armEndX - x) * radius;

        using var path = new GraphicsPath();
        path.AddLine(x, armStartY, x, cornerY - towardCorner);
        path.AddBezier(x, cornerY - towardCorner, x, cornerY, x, cornerY, x + alongArm, cornerY);
        path.AddLine(x + alongArm, cornerY, armEndX, cornerY);
        g.DrawPath(pen, path);
    }

    private static GraphicsPath RoundedRect(float x, float y, float w, float h, float radius)
    {
        radius = Math.Max(0.01f, Math.Min(radius, Math.Min(w, h) / 2f));
        float d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(x, y, d, d, 180, 90);
        path.AddArc(x + w - d, y, d, d, 270, 90);
        path.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        path.AddArc(x, y + h - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static void WriteIco(string path, List<(int size, byte[] png)> frames)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var bw = new BinaryWriter(fs);

        // ICONDIR (6 bytes)
        bw.Write((short)0); // reserved, must be 0
        bw.Write((short)1); // type: 1 = icon
        bw.Write((short)frames.Count);

        int headerSize = 6 + 16 * frames.Count;
        int offset = headerSize;

        // ICONDIRENTRY[] (16 bytes each)
        foreach (var (size, png) in frames)
        {
            byte edge = size >= 256 ? (byte)0 : (byte)size; // 0 means 256 per spec
            bw.Write(edge);            // width
            bw.Write(edge);            // height
            bw.Write((byte)0);         // color count (0 = no palette, >=8bpp)
            bw.Write((byte)0);         // reserved
            bw.Write((short)1);        // color planes
            bw.Write((short)32);       // bits per pixel
            bw.Write(png.Length);      // size of image data
            bw.Write(offset);          // offset of image data from start of file
            offset += png.Length;
        }

        // Image data, PNG-encoded, in the same order as the directory entries.
        foreach (var (_, png) in frames)
        {
            bw.Write(png);
        }
    }

    private static bool VerifyIco(string path, int expectedCount)
    {
        var data = File.ReadAllBytes(path);
        using var ms = new MemoryStream(data);
        using var br = new BinaryReader(ms);

        short reserved = br.ReadInt16();
        short type = br.ReadInt16();
        short count = br.ReadInt16();

        Console.WriteLine("--- verification: read ICONDIR back from disk ---");
        Console.WriteLine($"ICONDIR  reserved={reserved} type={type} count={count}");

        bool ok = reserved == 0 && type == 1 && count == expectedCount;
        var seenSizes = new List<int>();

        for (int i = 0; i < count; i++)
        {
            byte width = br.ReadByte();
            byte height = br.ReadByte();
            byte colorCount = br.ReadByte();
            byte reservedEntry = br.ReadByte();
            short planes = br.ReadInt16();
            short bitCount = br.ReadInt16();
            int bytesInRes = br.ReadInt32();
            int imageOffset = br.ReadInt32();

            int w = width == 0 ? 256 : width;
            int h = height == 0 ? 256 : height;
            seenSizes.Add(w);

            bool boundsOk = imageOffset + bytesInRes <= data.Length;
            bool isPng = boundsOk && data.Length > imageOffset + 8
                && data[imageOffset] == 0x89 && data[imageOffset + 1] == 0x50
                && data[imageOffset + 2] == 0x4E && data[imageOffset + 3] == 0x47
                && data[imageOffset + 4] == 0x0D && data[imageOffset + 5] == 0x0A
                && data[imageOffset + 6] == 0x1A && data[imageOffset + 7] == 0x0A;

            // Read width/height back out of the embedded PNG's IHDR chunk (bytes 16..23)
            // as an independent check that the pixel data really is that size, not just
            // that the directory entry claims it is.
            int pngW = -1, pngH = -1;
            if (isPng && data.Length >= imageOffset + 24)
            {
                pngW = (data[imageOffset + 16] << 24) | (data[imageOffset + 17] << 16) | (data[imageOffset + 18] << 8) | data[imageOffset + 19];
                pngH = (data[imageOffset + 20] << 24) | (data[imageOffset + 21] << 16) | (data[imageOffset + 22] << 8) | data[imageOffset + 23];
            }

            bool sizeMatches = pngW == w && pngH == h;
            ok &= boundsOk && isPng && bitCount == 32 && sizeMatches;

            Console.WriteLine(
                $"  entry {i}: dir={w}x{h} bitCount={bitCount} planes={planes} colorCount={colorCount} "
                + $"bytes={bytesInRes} offset={imageOffset} pngSignature={isPng} pngIHDR={pngW}x{pngH} match={sizeMatches}");
        }

        Console.WriteLine();
        Console.WriteLine(ok
            ? $"OK: {count} entries, sizes {string.Join(",", seenSizes)}, all PNG/32bpp, directory size matches embedded IHDR size."
            : "FAILED: see entries above.");

        return ok;
    }
}
