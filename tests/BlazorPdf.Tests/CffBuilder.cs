namespace BlazorPdf.Tests;

/// <summary>
/// Builds a minimal bare CFF font with one rectangle glyph mapped from 'A' via a
/// custom Encoding, for testing the engine's CFF parser and Type2 interpreter without
/// shipping a sample font. Offsets in the Top DICT are encoded as fixed 5-byte
/// integers so the layout is stable.
/// </summary>
public static class CffBuilder
{
    public static byte[] Build()
    {
        // gid0 = .notdef (endchar), gid1 = rectangle (100,0)-(900,700).
        var notdef = new byte[] { 14 };
        var rect = new byte[]
        {
            239, 139, 21,                       // 100 0 rmoveto
            249, 180, 139, 139, 249, 80, 253, 180, 139, 5, // 800 0  0 700  -800 0 rlineto
            14,                                  // endchar
        };

        var nameIndex = Index([System.Text.Encoding.ASCII.GetBytes("FONT")]);
        var charStrings = Index([notdef, rect]);

        // Compute absolute offsets (Top DICT INDEX has a fixed size).
        const int header = 4;
        var topIndexLen = 2 + 1 + 2 + 29; // count + offSize + 2 offsets + 29-byte dict
        var topStart = header + nameIndex.Length;
        var strStart = topStart + topIndexLen;
        var gsubStart = strStart + 2;
        var csStart = gsubStart + 2;
        var charsetStart = csStart + charStrings.Length;
        var encStart = charsetStart + 3;
        var privStart = encStart + 3;

        var topDict = TopDict(charsetStart, encStart, csStart, 0, privStart);
        if (topDict.Length != 29) throw new InvalidOperationException($"Top DICT length {topDict.Length} != 29");
        var topIndex = Index([topDict]);

        var charset = new byte[] { 0, 0, 1 };          // format 0, SID 1 for gid 1
        var encoding = new byte[] { 0, 1, 0x41 };       // format 0, 1 code: 'A' -> gid 1

        var output = new List<byte> { 1, 0, 4, 1 };     // header: v1.0, hdrSize 4, offSize 1
        output.AddRange(nameIndex);
        output.AddRange(topIndex);
        output.AddRange(Index([]));   // String INDEX (empty)
        output.AddRange(Index([]));   // Global Subr INDEX (empty)
        output.AddRange(charStrings);
        output.AddRange(charset);
        output.AddRange(encoding);

        return [.. output];
    }

    private static byte[] Index(List<byte[]> items)
    {
        var ms = new List<byte> { (byte)(items.Count >> 8), (byte)(items.Count & 0xFF) };
        if (items.Count == 0) return [.. ms];

        ms.Add(1); // offSize = 1
        var off = 1;
        ms.Add((byte)off);
        foreach (var it in items) { off += it.Length; ms.Add((byte)off); }
        foreach (var it in items) ms.AddRange(it);
        return [.. ms];
    }

    private static byte[] TopDict(int charset, int enc, int cs, int privSize, int privOff)
    {
        var d = new List<byte>();
        Int32(d, charset); d.Add(15);   // charset
        Int32(d, enc); d.Add(16);       // Encoding
        Int32(d, cs); d.Add(17);        // CharStrings
        Int32(d, privSize); Int32(d, privOff); d.Add(18); // Private [size offset]
        return [.. d];
    }

    private static void Int32(List<byte> d, int v)
    {
        d.Add(29);
        d.Add((byte)(v >> 24));
        d.Add((byte)(v >> 16));
        d.Add((byte)(v >> 8));
        d.Add((byte)v);
    }
}
