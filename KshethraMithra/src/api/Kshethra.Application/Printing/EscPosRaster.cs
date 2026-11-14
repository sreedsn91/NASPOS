using System.Buffers.Binary;
using System.IO.Compression;

namespace Kshethra.Application.Printing;

/// <summary>
/// Turns rendered receipt pages (PNG) into one ESC/POS job for a thermal receipt printer: each page is sent as a raster
/// image (GS v 0) followed by a feed-and-cut (GS V 66), so every page comes out as its own cut slip whatever the Windows
/// driver's cut setting is. Printing pictures keeps Malayalam and the exact PDF layout.
/// </summary>
public static class EscPosRaster
{
    private const byte Esc = 0x1B, Gs = 0x1D;
    private const int BandRows = 256;   // rows per GS v 0 command; small bands suit printers with little buffer

    public static byte[] Job(IEnumerable<byte[]> pngPages)
    {
        using var o = new MemoryStream();
        o.Write([Esc, 0x40]);                                   // ESC @ : reset
        foreach (var png in pngPages)
        {
            var (w, h, dark) = Decode(png);
            var last = LastInkRow(w, h, dark);
            int widthBytes = (w + 7) / 8;
            for (int y0 = 0; y0 <= last; y0 += BandRows)
            {
                int rows = Math.Min(BandRows, last + 1 - y0);
                o.Write([Gs, 0x76, 0x30, 0x00, (byte)widthBytes, (byte)(widthBytes >> 8), (byte)rows, (byte)(rows >> 8)]);
                var band = new byte[widthBytes * rows];
                for (int y = 0; y < rows; y++)
                    for (int x = 0; x < w; x++)
                        if (dark[(y0 + y) * w + x]) band[y * widthBytes + x / 8] |= (byte)(0x80 >> (x % 8));
                o.Write(band);
            }
            o.Write([Gs, 0x56, 0x42, 0x50]);                    // GS V 66 80 : feed 10 mm, then full cut
        }
        return o.ToArray();
    }

    private static int LastInkRow(int w, int h, bool[] dark)
    {
        for (int y = h - 1; y >= 0; y--)
            for (int x = 0; x < w; x++)
                if (dark[y * w + x]) return y;
        return 0;
    }

    /// <summary>Minimal PNG reader (8-bit grey / RGB / grey+alpha / RGBA, not interlaced) returning a dark-pixel mask.</summary>
    public static (int Width, int Height, bool[] Dark) Decode(byte[] png)
    {
        if (png.Length < 8 || png[1] != 'P' || png[2] != 'N' || png[3] != 'G') throw new InvalidDataException("Not a PNG image.");
        int pos = 8, w = 0, h = 0, colorType = 0;
        using var idat = new MemoryStream();
        while (pos + 8 <= png.Length)
        {
            int len = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(pos));
            var type = System.Text.Encoding.ASCII.GetString(png, pos + 4, 4);
            var data = png.AsSpan(pos + 8, len);
            if (type == "IHDR")
            {
                w = BinaryPrimitives.ReadInt32BigEndian(data);
                h = BinaryPrimitives.ReadInt32BigEndian(data[4..]);
                if (data[8] != 8 || data[12] != 0) throw new InvalidDataException("Only 8-bit, non-interlaced PNG is supported.");
                colorType = data[9];
            }
            else if (type == "IDAT") idat.Write(data);
            else if (type == "IEND") break;
            pos += 12 + len;
        }
        int channels = colorType switch { 0 => 1, 2 => 3, 4 => 2, 6 => 4, _ => throw new InvalidDataException($"PNG colour type {colorType} is not supported.") };
        int stride = w * channels;
        var raw = new byte[(stride + 1) * h];
        idat.Position = 0;
        using (var z = new ZLibStream(idat, CompressionMode.Decompress)) z.ReadExactly(raw);

        var dark = new bool[w * h];
        var prev = new byte[stride];
        var cur = new byte[stride];
        for (int y = 0; y < h; y++)
        {
            int filter = raw[y * (stride + 1)];
            Array.Copy(raw, y * (stride + 1) + 1, cur, 0, stride);
            for (int i = 0; i < stride; i++)
            {
                int a = i >= channels ? cur[i - channels] : 0, b = prev[i], c = i >= channels ? prev[i - channels] : 0;
                cur[i] = (byte)(cur[i] + filter switch
                {
                    1 => a,
                    2 => b,
                    3 => (a + b) / 2,
                    4 => Paeth(a, b, c),
                    _ => 0,
                });
            }
            for (int x = 0; x < w; x++)
            {
                int p = x * channels;
                int lum = channels >= 3 ? (cur[p] * 299 + cur[p + 1] * 587 + cur[p + 2] * 114) / 1000 : cur[p];
                int alpha = channels == 4 ? cur[p + 3] : channels == 2 ? cur[p + 1] : 255;
                lum = 255 - (255 - lum) * alpha / 255;           // transparent = white paper
                dark[y * w + x] = lum < 160;
            }
            (prev, cur) = (cur, prev);
        }
        return (w, h, dark);
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }
}
