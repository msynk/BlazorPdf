namespace BlazorPdf.Tests;

/// <summary>
/// Builds a minimal but valid TrueType font in memory with one custom glyph
/// (a filled rectangle) mapped from 'A', for testing the engine's font parser and
/// glyph rasterization without shipping any external font file.
/// </summary>
public static class TrueTypeBuilder
{
    public static byte[] Build()
    {
        var glyf = BuildGlyf();          // glyph 1 outline (34 bytes), glyph 0 empty
        var loca = BuildLoca(glyf.Length);
        var head = BuildHead();
        var maxp = BuildMaxp();
        var hhea = BuildHhea();
        var hmtx = BuildHmtx();
        var cmap = BuildCmap();

        var tables = new (string Tag, byte[] Data)[]
        {
            ("cmap", cmap), ("glyf", glyf), ("head", head), ("hhea", hhea),
            ("hmtx", hmtx), ("loca", loca), ("maxp", maxp),
        };

        return Assemble(tables);
    }

    private static byte[] Assemble((string Tag, byte[] Data)[] tables)
    {
        var numTables = tables.Length;
        var dataStart = 12 + numTables * 16;

        // Lay out table data with 4-byte alignment.
        var offsets = new int[numTables];
        var lengths = new int[numTables];
        var cursor = dataStart;
        for (var i = 0; i < numTables; i++)
        {
            offsets[i] = cursor;
            lengths[i] = tables[i].Data.Length;
            cursor += (lengths[i] + 3) & ~3;
        }

        var output = new List<byte>();
        WU32(output, 0x00010000); // sfnt version
        WU16(output, numTables);
        WU16(output, 0); WU16(output, 0); WU16(output, 0); // search range fields

        for (var i = 0; i < numTables; i++)
        {
            foreach (var ch in tables[i].Tag) output.Add((byte)ch);
            WU32(output, 0); // checksum (ignored by the parser)
            WU32(output, offsets[i]);
            WU32(output, lengths[i]);
        }

        for (var i = 0; i < numTables; i++)
        {
            output.AddRange(tables[i].Data);
            var pad = ((lengths[i] + 3) & ~3) - lengths[i];
            for (var p = 0; p < pad; p++) output.Add(0);
        }

        return [.. output];
    }

    private static byte[] BuildGlyf()
    {
        var b = new List<byte>();
        WI16(b, 1);                 // numberOfContours
        WI16(b, 100); WI16(b, 0); WI16(b, 900); WI16(b, 700); // bbox
        WU16(b, 3);                 // endPtsOfContours[0]
        WU16(b, 0);                 // instructionLength
        b.Add(0x01); b.Add(0x01); b.Add(0x01); b.Add(0x01);   // 4 on-curve flags
        WI16(b, 100); WI16(b, 800); WI16(b, 0); WI16(b, -800); // x deltas
        WI16(b, 0); WI16(b, 0); WI16(b, 700); WI16(b, 0);      // y deltas
        return [.. b]; // 34 bytes
    }

    private static byte[] BuildLoca(int glyfLen)
    {
        var b = new List<byte>();
        WU16(b, 0);              // glyph 0 offset / 2
        WU16(b, 0);              // glyph 1 offset / 2 (glyph 0 is empty)
        WU16(b, glyfLen / 2);    // end offset / 2
        return [.. b];
    }

    private static byte[] BuildHead()
    {
        var b = new byte[54];
        b[0] = 0x00; b[1] = 0x01; // version 1.0
        b[18] = 0x03; b[19] = 0xE8; // unitsPerEm = 1000
        // indexToLocFormat (offset 50) = 0 (short) by default
        return b;
    }

    private static byte[] BuildMaxp()
    {
        var b = new List<byte>();
        WU32(b, 0x00010000); // version
        WU16(b, 2);          // numGlyphs
        return [.. b];
    }

    private static byte[] BuildHhea()
    {
        var b = new byte[36];
        b[34] = 0x00; b[35] = 0x02; // numberOfHMetrics = 2
        return b;
    }

    private static byte[] BuildHmtx()
    {
        var b = new List<byte>();
        WU16(b, 0); WI16(b, 0);       // glyph 0
        WU16(b, 1000); WI16(b, 100);  // glyph 1
        return [.. b];
    }

    private static byte[] BuildCmap()
    {
        var b = new List<byte>();
        WU16(b, 0);  // version
        WU16(b, 1);  // numTables
        WU16(b, 1);  // platformID = Macintosh
        WU16(b, 0);  // encodingID = Roman
        WU32(b, 12); // offset to subtable

        // Format 0 byte-encoding table.
        WU16(b, 0);    // format
        WU16(b, 262);  // length
        WU16(b, 0);    // language
        var glyphIds = new byte[256];
        glyphIds['A'] = 1;
        b.AddRange(glyphIds);
        return [.. b];
    }

    private static void WU16(List<byte> b, int v)
    {
        b.Add((byte)((v >> 8) & 0xFF));
        b.Add((byte)(v & 0xFF));
    }

    private static void WI16(List<byte> b, int v) => WU16(b, v & 0xFFFF);

    private static void WU32(List<byte> b, long v)
    {
        b.Add((byte)((v >> 24) & 0xFF));
        b.Add((byte)((v >> 16) & 0xFF));
        b.Add((byte)((v >> 8) & 0xFF));
        b.Add((byte)(v & 0xFF));
    }
}
