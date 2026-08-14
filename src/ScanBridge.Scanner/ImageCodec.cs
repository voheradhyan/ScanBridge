using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ScanBridge.Protocol;

namespace ScanBridge.Scanner;

/// <summary>Geometry of a decoded DIB, read from its BITMAPINFOHEADER.</summary>
public readonly record struct DibInfo(int Width, int Height, int BitsPerPixel, int DpiX, int DpiY, int TotalBytes);

/// <summary>
/// Turns the device-independent bitmap a TWAIN native transfer hands back into the encoded
/// form that goes on the wire.
///
/// Compression happens here, at the source, and not on the transport: a 600 dpi A4 colour
/// page is roughly 100 MB raw and 3-5 MB as JPEG, and there is no reason to push the raw
/// form through a pipe and an RDP channel first.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ImageCodec
{
    private const int BitmapFileHeaderSize = 14;
    private const int BitmapInfoHeaderSize = 40;

    /// <summary>Reads the header of a DIB held in unmanaged memory.</summary>
    public static DibInfo ReadDibInfo(IntPtr dib)
    {
        if (dib == IntPtr.Zero) throw new ScanException(ScanErrorCode.DriverFault, "The scanner returned no image.");

        int headerSize = Marshal.ReadInt32(dib, 0);
        if (headerSize < BitmapInfoHeaderSize)
            throw new ScanException(ScanErrorCode.DriverFault, "The scanner returned a malformed bitmap header.");

        int width = Marshal.ReadInt32(dib, 4);
        int height = Marshal.ReadInt32(dib, 8);
        short bitCount = Marshal.ReadInt16(dib, 14);
        int compression = Marshal.ReadInt32(dib, 16);
        int sizeImage = Marshal.ReadInt32(dib, 20);
        int xPelsPerMeter = Marshal.ReadInt32(dib, 24);
        int yPelsPerMeter = Marshal.ReadInt32(dib, 28);
        int clrUsed = Marshal.ReadInt32(dib, 32);

        if (compression != 0)
            throw new ScanException(ScanErrorCode.DriverFault,
                "The scanner returned a compressed bitmap, which is not supported.");

        // A negative height means the rows are stored top-down.
        int absoluteHeight = Math.Abs(height);
        int stride = (width * bitCount + 31) / 32 * 4;
        if (sizeImage <= 0) sizeImage = stride * absoluteHeight;

        int paletteEntries = clrUsed > 0 ? clrUsed : bitCount <= 8 ? 1 << bitCount : 0;

        // 39.37 pixels per metre is one inch; drivers that omit resolution get 300 dpi,
        // which is only used for reporting since the caller knows what it asked for.
        int dpiX = xPelsPerMeter > 0 ? (int)Math.Round(xPelsPerMeter / 39.3700787) : 300;
        int dpiY = yPelsPerMeter > 0 ? (int)Math.Round(yPelsPerMeter / 39.3700787) : 300;

        int total = headerSize + paletteEntries * 4 + sizeImage;
        return new DibInfo(width, absoluteHeight, bitCount, dpiX, dpiY, total);
    }

    /// <summary>
    /// Encodes a DIB as JPEG or PNG.
    ///
    /// The DIB is wrapped in a BITMAPFILEHEADER and handed to GDI+ rather than being walked
    /// by hand: GDI+ already understands every palette layout, bit depth and row order a
    /// scanner driver might produce, and getting those wrong silently yields inverted or
    /// sheared pages.
    /// </summary>
    public static byte[] EncodeDib(IntPtr dib, DibInfo info, PageEncoding encoding, int jpegQuality)
    {
        int paletteBytes = info.BitsPerPixel <= 8
            ? (1 << info.BitsPerPixel) * 4
            : 0;

        int offBits = BitmapFileHeaderSize + BitmapInfoHeaderSize + paletteBytes;
        byte[] file = new byte[BitmapFileHeaderSize + info.TotalBytes];

        // BITMAPFILEHEADER: 'BM', file size, two reserved words, offset to the pixel data.
        file[0] = (byte)'B';
        file[1] = (byte)'M';
        BitConverter.TryWriteBytes(file.AsSpan(2), file.Length);
        BitConverter.TryWriteBytes(file.AsSpan(10), offBits);

        Marshal.Copy(dib, file, BitmapFileHeaderSize, info.TotalBytes);

        using var source = new MemoryStream(file, writable: false);
        using var bitmap = new Bitmap(source);
        using var output = new MemoryStream();

        if (encoding == PageEncoding.Jpeg && info.BitsPerPixel > 1)
        {
            ImageCodecInfo? jpeg = GetEncoder(ImageFormat.Jpeg);
            if (jpeg is not null)
            {
                using var parameters = new EncoderParameters(1);
                using var quality = new EncoderParameter(Encoder.Quality, (long)jpegQuality);
                parameters.Param[0] = quality;
                bitmap.Save(output, jpeg, parameters);
                return output.ToArray();
            }
        }

        // PNG for bitonal and greyscale, and as the fallback whenever JPEG is unavailable:
        // JPEG artefacts on 1-bit text are unacceptable and lossless costs little there.
        bitmap.Save(output, ImageFormat.Png);
        return output.ToArray();
    }

    /// <summary>The encoding actually used for a given depth, so the page header stays truthful.</summary>
    public static PageEncoding EffectiveEncoding(PageEncoding requested, int bitsPerPixel)
        => requested == PageEncoding.Jpeg && bitsPerPixel > 1 ? PageEncoding.Jpeg : PageEncoding.Png;

    private static ImageCodecInfo? GetEncoder(ImageFormat format)
        => ImageCodecInfo.GetImageEncoders().FirstOrDefault(codec => codec.FormatID == format.Guid);
}
