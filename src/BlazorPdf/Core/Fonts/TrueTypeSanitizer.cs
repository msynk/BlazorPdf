// A conservative TrueType/OpenType (sfnt) sanitizer. Embedded PDF subset fonts
// frequently ship with an unsorted table directory, wrong table checksums, a
// wrong head.checkSumAdjustment, or unpadded tables — all of which strict font
// parsers (including the browser's OTS) reject, so the @font-face silently fails
// to load. This rebuilds a structurally valid sfnt: recognized tables are kept
// verbatim, the directory is re-sorted, tables are 4-byte aligned, and all
// checksums are recomputed. It does not repair broken table *contents*; when the
// input cannot be parsed it returns null and the caller keeps the raw bytes.

namespace BlazorPdf.Core.Fonts;

internal static class TrueTypeSanitizer
{
    /// <summary>
    /// Returns a structurally-normalized copy of an sfnt font, or <c>null</c> when
    /// the input is not a parseable sfnt (caller should keep the original bytes).
    /// </summary>
    public static byte[]? Sanitize(byte[] input)
    {
        if (input.Length < 12)
        {
            return null;
        }

        uint version = ReadU32(input, 0);
        // Accept TrueType (0x00010000 / 'true' / 'ttcf' not handled), and OpenType
        // with CFF ('OTTO'). Reject anything else.
        bool isKnown = version is 0x00010000 or 0x74727565 /*true*/ or 0x4F54544F /*OTTO*/;
        if (!isKnown)
        {
            return null;
        }

        int numTables = ReadU16(input, 4);
        if (numTables == 0 || numTables > 4096)
        {
            return null;
        }

        int dirOffset = 12;
        if (dirOffset + numTables * 16 > input.Length)
        {
            return null;
        }

        var tables = new List<(uint Tag, byte[] Data)>(numTables);
        var seen = new HashSet<uint>();
        for (int i = 0; i < numTables; i++)
        {
            int rec = dirOffset + i * 16;
            uint tag = ReadU32(input, rec);
            int off = (int)ReadU32(input, rec + 8);
            int len = (int)ReadU32(input, rec + 12);
            if (off < 0 || len < 0 || (long)off + len > input.Length)
            {
                return null; // corrupt directory entry
            }
            if (!seen.Add(tag))
            {
                continue; // drop duplicate tables
            }
            var data = new byte[len];
            Array.Copy(input, off, data, 0, len);
            tables.Add((tag, data));
        }

        // Require the tables every consumer needs to be present.
        if (!seen.Contains(Tag("head")) || !seen.Contains(Tag("maxp")) || !seen.Contains(Tag("hhea")))
        {
            return null;
        }

        // The sfnt spec requires the table directory sorted ascending by tag.
        tables.Sort((a, b) => a.Tag.CompareTo(b.Tag));

        return Serialize(version, tables);
    }

    private static byte[] Serialize(uint version, List<(uint Tag, byte[] Data)> tables)
    {
        int n = tables.Count;
        int headerSize = 12 + n * 16;

        // Lay out table bodies 4-byte aligned after the directory.
        var offsets = new int[n];
        int pos = headerSize;
        for (int i = 0; i < n; i++)
        {
            offsets[i] = pos;
            pos += (tables[i].Data.Length + 3) & ~3;
        }
        int total = pos;

        var output = new byte[total];

        // Offset table.
        WriteU32(output, 0, version);
        WriteU16(output, 4, (ushort)n);
        int entrySelector = 0;
        int searchRange = 16;
        while (searchRange * 2 <= n * 16)
        {
            searchRange *= 2;
            entrySelector++;
        }
        WriteU16(output, 6, (ushort)searchRange);
        WriteU16(output, 8, (ushort)entrySelector);
        WriteU16(output, 10, (ushort)(n * 16 - searchRange));

        // Directory + bodies with recomputed checksums.
        int headDirRec = -1;
        for (int i = 0; i < n; i++)
        {
            var (tag, data) = tables[i];
            Array.Copy(data, 0, output, offsets[i], data.Length);
            uint checksum = TableChecksum(output, offsets[i], data.Length);

            int rec = 12 + i * 16;
            WriteU32(output, rec, tag);
            WriteU32(output, rec + 4, checksum);
            WriteU32(output, rec + 8, (uint)offsets[i]);
            WriteU32(output, rec + 12, (uint)data.Length);

            if (tag == Tag("head"))
            {
                headDirRec = i;
            }
        }

        // head.checkSumAdjustment = 0xB1B0AFBA - checksum(whole file with the
        // adjustment field zeroed).
        if (headDirRec >= 0)
        {
            int headOffset = offsets[headDirRec];
            if (headOffset + 12 <= output.Length)
            {
                WriteU32(output, headOffset + 8, 0); // zero the adjustment field
                uint fileChecksum = TableChecksum(output, 0, output.Length);
                WriteU32(output, headOffset + 8, unchecked(0xB1B0AFBA - fileChecksum));
                // The head table's own directory checksum must reflect the zeroed
                // adjustment field per spec; recompute it with the field at zero.
                uint saved = ReadU32(output, headOffset + 8);
                WriteU32(output, headOffset + 8, 0);
                uint headChecksum = TableChecksum(output, headOffset, tables[headDirRec].Data.Length);
                WriteU32(output, 12 + headDirRec * 16 + 4, headChecksum);
                WriteU32(output, headOffset + 8, saved);
            }
        }

        return output;
    }

    private static uint TableChecksum(byte[] data, int offset, int length)
    {
        uint sum = 0;
        int i = offset;
        int end = offset + length;
        while (i + 4 <= end)
        {
            sum = unchecked(sum + ReadU32(data, i));
            i += 4;
        }
        // Trailing bytes are treated as a big-endian word padded with zeros.
        if (i < end)
        {
            uint last = 0;
            for (int b = 0; b < 4; b++)
            {
                last = (last << 8) | (uint)(i < end ? data[i] : 0);
                i++;
            }
            sum = unchecked(sum + last);
        }
        return sum;
    }

    private static uint Tag(string s) => ((uint)s[0] << 24) | ((uint)s[1] << 16) | ((uint)s[2] << 8) | s[3];

    private static uint ReadU32(byte[] d, int o) => ((uint)d[o] << 24) | ((uint)d[o + 1] << 16) | ((uint)d[o + 2] << 8) | d[o + 3];
    private static int ReadU16(byte[] d, int o) => (d[o] << 8) | d[o + 1];

    private static void WriteU32(byte[] d, int o, uint v)
    {
        d[o] = (byte)(v >> 24);
        d[o + 1] = (byte)(v >> 16);
        d[o + 2] = (byte)(v >> 8);
        d[o + 3] = (byte)v;
    }

    private static void WriteU16(byte[] d, int o, ushort v)
    {
        d[o] = (byte)(v >> 8);
        d[o + 1] = (byte)v;
    }
}
