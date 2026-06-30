using System.IO.Compression;

namespace BlazorPdf.Engine.Rendering;

/// <summary>
/// Encodes a <see cref="RenderedImage"/> to a PNG (8-bit RGBA) using only the .NET BCL
/// (zlib via <see cref="ZLibStream"/> + a CRC32). Useful for exporting rendered pages
/// or thumbnails server-side.
/// </summary>
public static class PngEncoder
{
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];
    private static readonly uint[] CrcTable = BuildCrcTable();

    /// <summary>Encodes the image as a PNG byte array.</summary>
    public static byte[] Encode(RenderedImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        using var output = new MemoryStream();
        output.Write(Signature);

        // IHDR
        var ihdr = new byte[13];
        WriteBe(ihdr, 0, (uint)image.Width);
        WriteBe(ihdr, 4, (uint)image.Height);
        ihdr[8] = 8;   // bit depth
        ihdr[9] = 6;   // color type RGBA
        ihdr[10] = 0;  // compression
        ihdr[11] = 0;  // filter
        ihdr[12] = 0;  // interlace
        WriteChunk(output, "IHDR", ihdr);

        // IDAT: zlib-compressed scanlines, each prefixed with filter byte 0 (None).
        WriteChunk(output, "IDAT", CompressScanlines(image));

        WriteChunk(output, "IEND", []);
        return output.ToArray();
    }

    private static byte[] CompressScanlines(RenderedImage image)
    {
        var stride = image.Width * 4;
        var raw = new byte[(stride + 1) * image.Height];
        for (var y = 0; y < image.Height; y++)
        {
            raw[y * (stride + 1)] = 0; // filter: None
            Array.Copy(image.Pixels, y * stride, raw, y * (stride + 1) + 1, stride);
        }

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(raw);
        }
        return compressed.ToArray();
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        Span<byte> len = stackalloc byte[4];
        WriteBe(len, 0, (uint)data.Length);
        stream.Write(len);

        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        stream.Write(typeBytes);
        stream.Write(data);

        var crc = Crc32(typeBytes, data);
        Span<byte> crcBytes = stackalloc byte[4];
        WriteBe(crcBytes, 0, crc);
        stream.Write(crcBytes);
    }

    private static void WriteBe(Span<byte> buffer, int offset, uint value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }

    private static uint Crc32(byte[] a, byte[] b)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var x in a) crc = CrcTable[(crc ^ x) & 0xFF] ^ (crc >> 8);
        foreach (var x in b) crc = CrcTable[(crc ^ x) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (var n = 0u; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }
            table[n] = c;
        }
        return table;
    }
}
