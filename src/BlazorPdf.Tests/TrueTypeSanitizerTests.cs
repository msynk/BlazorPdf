using BlazorPdf.Core.Fonts;

namespace BlazorPdf.Tests;

/// <summary>
/// Phase 3.1: the sfnt sanitizer must turn a structurally-sloppy embedded font
/// (unsorted table directory, wrong checksums) into a well-formed one that a
/// strict parser accepts, and reject non-fonts.
/// </summary>
public class TrueTypeSanitizerTests
{
    [Fact]
    public void Rejects_non_sfnt_data()
    {
        Assert.Null(TrueTypeSanitizer.Sanitize(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 }));
        Assert.Null(TrueTypeSanitizer.Sanitize(Array.Empty<byte>()));
    }

    [Fact]
    public void Sorts_directory_and_fixes_checksums()
    {
        // Build a minimal sfnt with the required tables listed OUT of tag order
        // ("maxp","head","hhea") and deliberately wrong checksums.
        byte[] font = BuildFont(new[] { "maxp", "head", "hhea" });
        byte[]? clean = TrueTypeSanitizer.Sanitize(font);

        Assert.NotNull(clean);
        // Valid sfnt header.
        Assert.Equal(0x00010000u, ReadU32(clean!, 0));
        int n = (clean![4] << 8) | clean[5];
        Assert.Equal(3, n);

        // Directory tags must now be ascending: head < hhea < maxp.
        uint t0 = ReadU32(clean, 12);
        uint t1 = ReadU32(clean, 12 + 16);
        uint t2 = ReadU32(clean, 12 + 32);
        Assert.True(t0 < t1 && t1 < t2, "directory not sorted ascending");

        // The whole-file checksum must satisfy the head.checkSumAdjustment rule:
        // 0xB1B0AFBA - fileChecksum == checkSumAdjustment.
        Assert.True(HeadChecksumValid(clean), "head.checkSumAdjustment is not consistent");
    }

    [Fact]
    public void Returns_null_when_required_table_missing()
    {
        // Missing "head" -> not sanitizable.
        byte[] font = BuildFont(new[] { "maxp", "hhea", "cmap" });
        Assert.Null(TrueTypeSanitizer.Sanitize(font));
    }

    // ----- helpers: assemble a tiny sfnt -----

    private static byte[] BuildFont(string[] tags)
    {
        int n = tags.Length;
        int header = 12 + n * 16;
        // Each table is 8 bytes of arbitrary content.
        var tableData = new byte[8];
        for (int i = 0; i < 8; i++) tableData[i] = (byte)(i + 1);

        int total = header + n * 8;
        var buf = new byte[total];
        WriteU32(buf, 0, 0x00010000);
        buf[4] = 0; buf[5] = (byte)n;
        int off = header;
        for (int i = 0; i < n; i++)
        {
            int rec = 12 + i * 16;
            WriteU32(buf, rec, Tag(tags[i]));
            WriteU32(buf, rec + 4, 0xDEADBEEF); // wrong checksum on purpose
            WriteU32(buf, rec + 8, (uint)off);
            WriteU32(buf, rec + 12, 8);
            Array.Copy(tableData, 0, buf, off, 8);
            off += 8;
        }
        return buf;
    }

    private static bool HeadChecksumValid(byte[] font)
    {
        int n = (font[4] << 8) | font[5];
        int headOffset = -1;
        for (int i = 0; i < n; i++)
        {
            if (ReadU32(font, 12 + i * 16) == Tag("head"))
            {
                headOffset = (int)ReadU32(font, 12 + i * 16 + 8);
                break;
            }
        }
        if (headOffset < 0) return false;
        uint adjustment = ReadU32(font, headOffset + 8);

        // Recompute the whole-file checksum with the adjustment field zeroed.
        var copy = (byte[])font.Clone();
        WriteU32(copy, headOffset + 8, 0);
        uint sum = 0;
        for (int i = 0; i + 4 <= copy.Length; i += 4) sum = unchecked(sum + ReadU32(copy, i));
        return unchecked(0xB1B0AFBA - sum) == adjustment;
    }

    private static uint Tag(string s) => ((uint)s[0] << 24) | ((uint)s[1] << 16) | ((uint)s[2] << 8) | s[3];
    private static uint ReadU32(byte[] d, int o) => ((uint)d[o] << 24) | ((uint)d[o + 1] << 16) | ((uint)d[o + 2] << 8) | d[o + 3];
    private static void WriteU32(byte[] d, int o, uint v)
    {
        d[o] = (byte)(v >> 24); d[o + 1] = (byte)(v >> 16); d[o + 2] = (byte)(v >> 8); d[o + 3] = (byte)v;
    }
}
