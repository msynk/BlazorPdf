
namespace BlazorPdf;

/// <summary>
/// Phase 3.2: recover glyph mappings from a subset TrueType font and emit a clean
/// (3,1) Unicode cmap the browser (OTS) accepts.
/// </summary>
public class SyntheticCmapTests
{
    [Fact]
    public void CmapBuilder_emits_valid_windows_unicode_subtable()
    {
        var map = new Dictionary<int, int> { { 0x41, 3 }, { 0x42, 4 }, { 0x61, 7 } };
        byte[] cmap = BlazorPdfCmapBuilder.BuildUnicodeCmap(map);

        Assert.Equal(0, U16(cmap, 0)); // version
        Assert.Equal(1, U16(cmap, 2)); // numTables
        Assert.Equal(3, U16(cmap, 4)); // platform Windows
        Assert.Equal(1, U16(cmap, 6)); // encoding Unicode BMP
        int subOff = (int)U32(cmap, 8);
        Assert.Equal(12, subOff);
        Assert.Equal(4, U16(cmap, subOff)); // format 4
    }

    [Fact]
    public void Builds_synthetic_cmap_from_post_names()
    {
        // A minimal sfnt carrying only a post (v2) table mapping glyph "A" -> gid 3.
        byte[] font = FontWithPost();

        var encoding = new string[256];
        encoding[0x41] = "A";
        byte[]? cmap = BlazorPdfSfntGlyphMapper.BuildSyntheticCmap(
            font, encoding, symbolic: false, code => code == 0x41 ? "A" : "", out var mappedCodes);

        Assert.NotNull(cmap);
        Assert.Equal(4, U16(cmap!, (int)U32(cmap, 8))); // a format-4 subtable
        Assert.Contains(0x41, mappedCodes);            // code 'A' resolved to a glyph
    }

    private static byte[] FontWithPost()
    {
        // post v2: 32-byte header, numGlyphs(2), index[numGlyphs], names.
        // 4 glyphs; gid 3 -> index 258 -> extra name "A".
        var post = new List<byte>();
        void U32b(uint v) { post.Add((byte)(v >> 24)); post.Add((byte)(v >> 16)); post.Add((byte)(v >> 8)); post.Add((byte)v); }
        void U16b(int v) { post.Add((byte)(v >> 8)); post.Add((byte)v); }
        U32b(0x00020000); // version 2
        for (int i = 0; i < 7; i++) U32b(0); // italicAngle..maxMemType1 (28 bytes)
        U16b(4);          // numberOfGlyphs
        U16b(0); U16b(1); U16b(2); U16b(258); // indices (gid3 -> extra name 0)
        post.Add(1); post.Add((byte)'A'); // one Pascal string "A"

        byte[] postData = post.ToArray();

        // sfnt: header + 1 table directory entry for "post".
        int headerSize = 12 + 16;
        var buf = new byte[headerSize + ((postData.Length + 3) & ~3)];
        buf[0] = 0x00; buf[1] = 0x01; buf[2] = 0x00; buf[3] = 0x00; // version 1.0
        buf[4] = 0x00; buf[5] = 0x01; // numTables = 1
        int rec = 12;
        buf[rec] = (byte)'p'; buf[rec + 1] = (byte)'o'; buf[rec + 2] = (byte)'s'; buf[rec + 3] = (byte)'t';
        // checksum (ignored by mapper) 4 bytes zero
        int off = headerSize;
        buf[rec + 8] = (byte)(off >> 24); buf[rec + 9] = (byte)(off >> 16); buf[rec + 10] = (byte)(off >> 8); buf[rec + 11] = (byte)off;
        int len = postData.Length;
        buf[rec + 12] = (byte)(len >> 24); buf[rec + 13] = (byte)(len >> 16); buf[rec + 14] = (byte)(len >> 8); buf[rec + 15] = (byte)len;
        Array.Copy(postData, 0, buf, off, postData.Length);
        return buf;
    }

    private static int U16(byte[] d, int o) => (d[o] << 8) | d[o + 1];
    private static uint U32(byte[] d, int o) => ((uint)d[o] << 24) | ((uint)d[o + 1] << 16) | ((uint)d[o + 2] << 8) | d[o + 3];
}
