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

        float scale = size / 256f;

        using var steelBrush = new SolidBrush(Steel);
        using var tealBrush = new SolidBrush(Teal);

        // Left pier (the PC with the physical scanner).
        using (var p = RoundedRect(48 * scale, 48 * scale, 48 * scale, 176 * scale, 16 * scale))
            g.FillPath(steelBrush, p);

        // Right pier (the remote session).
        using (var p = RoundedRect(160 * scale, 48 * scale, 48 * scale, 176 * scale, 16 * scale))
            g.FillPath(steelBrush, p);

        // Deck (ScanBridge itself), drawn last so it overlaps both piers cleanly.
        using (var p = RoundedRect(32 * scale, 32 * scale, 192 * scale, 32 * scale, 16 * scale))
            g.FillPath(tealBrush, p);

        return bmp;
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
