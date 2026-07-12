using System.Text;

namespace BlazorPdf;

/// <summary>
/// Phase 3.3: parse an embedded Type1 program and wrap it as OpenType/CFF. The
/// test synthesizes a real (eexec + charstring encrypted) minimal Type1 font,
/// runs the whole pipeline, and asserts a structurally-valid OTF comes out.
/// </summary>
public class Type1FontTests
{
    [Fact]
    public void Parses_interprets_and_wraps_a_type1_font()
    {
        byte[] t1 = BuildMinimalType1();

        var font = BlazorPdfType1Font.Parse(t1);
        Assert.NotNull(font);
        Assert.True(font!.CharStrings.ContainsKey("A"));

        var outline = font.BuildOutline("A");
        Assert.NotNull(outline);
        Assert.Equal(700, outline!.AdvanceWidth);
        Assert.True(outline.Segments.Count >= 3); // move + 2 lines

        byte[]? otf = BlazorPdfCffFontWriter.FromType1(font, "TestFont");
        Assert.NotNull(otf);
        Assert.Equal(0x4F54544Fu, ReadU32(otf!, 0)); // 'OTTO'
        Assert.True(HasTable(otf!, "CFF "), "missing CFF table");
        Assert.True(HasTable(otf!, "cmap"), "missing cmap table");
        Assert.True(HasTable(otf!, "head"), "missing head table");
        Assert.True(HasTable(otf!, "hmtx"), "missing hmtx table");
    }

    [Fact]
    public void Rejects_non_type1_data()
    {
        Assert.Null(BlazorPdfType1Font.Parse(new byte[] { 1, 2, 3, 4 }));
        Assert.Null(BlazorPdfType1Font.Parse(Encoding.ASCII.GetBytes("just some text no eexec")));
    }

    // The CFF the writer produces must round-trip through the CFF parser (glyph
    // names recovered from the charset) and re-wrap into a valid OpenType font.
    [Fact]
    public void Cff_roundtrips_through_parser_and_wrapper()
    {
        var font = BlazorPdfType1Font.Parse(BuildMinimalType1())!;
        byte[] otf = BlazorPdfCffFontWriter.FromType1(font, "TestFont")!;
        byte[] cff = ExtractTable(otf, "CFF ");
        Assert.NotEmpty(cff);

        var parser = BlazorPdfCffFontParser.Parse(cff);
        Assert.NotNull(parser);
        Assert.True(parser!.NumGlyphs >= 2);
        Assert.True(parser.NameToGid.ContainsKey("A"), "CFF charset name 'A' not recovered");

        var cmapMap = new Dictionary<int, int> { { 0x41, parser.NameToGid["A"] } };
        byte[] cmap = BlazorPdf.BlazorPdfCmapBuilder.BuildUnicodeCmap(cmapMap);
        byte[]? wrapped = BlazorPdfCffFontWriter.WrapBareCff(cff, parser.NumGlyphs, parser.FontMatrix, cmap, "X");

        Assert.NotNull(wrapped);
        Assert.Equal(0x4F54544Fu, ReadU32(wrapped!, 0)); // 'OTTO'
        Assert.True(HasTable(wrapped!, "CFF "));
        Assert.True(HasTable(wrapped!, "cmap"));
    }

    private static byte[] ExtractTable(byte[] otf, string tag)
    {
        int n = (otf[4] << 8) | otf[5];
        for (int i = 0; i < n; i++)
        {
            int rec = 12 + i * 16;
            if ((char)otf[rec] == tag[0] && (char)otf[rec + 1] == tag[1]
                && (char)otf[rec + 2] == tag[2] && (char)otf[rec + 3] == tag[3])
            {
                int off = (int)ReadU32(otf, rec + 8);
                int len = (int)ReadU32(otf, rec + 12);
                return otf[off..(off + len)];
            }
        }
        return Array.Empty<byte>();
    }

    // ----- build a minimal but real Type1 font -----

    private static byte[] BuildMinimalType1()
    {
        // Charstring for "A": 0 700 hsbw  100 0 rmoveto  200 0 rlineto  0 300 rlineto  closepath endchar
        byte[] cs =
        {
            139,            // 0 (sbx)
            249, 80,        // 700 (wx)
            13,             // hsbw
            239, 139,       // 100 0
            21,             // rmoveto
            247, 92, 139,   // 200 0
            5,              // rlineto
            139, 247, 192,  // 0 300
            5,              // rlineto
            9,              // closepath
            14,             // endchar
        };
        byte[] encCs = Encrypt(cs, 4330, skip: 4); // charstring encryption, lenIV=4

        var priv = new List<byte>();
        void Text(string s) => priv.AddRange(Encoding.Latin1.GetBytes(s));
        Text("/lenIV 4 def\n/CharStrings 1 dict dup begin\n");
        Text($"/A {encCs.Length} RD ");
        priv.AddRange(encCs);
        Text(" ND\nend\n");

        byte[] encPriv = Encrypt(priv.ToArray(), 55665, skip: 4); // eexec encryption

        var clear = new List<byte>();
        void Clear(string s) => clear.AddRange(Encoding.Latin1.GetBytes(s));
        Clear("%!FontType1-1.0\n");
        Clear("/FontMatrix [0.001 0 0 0.001 0 0] def\n");
        Clear("/Encoding StandardEncoding def\n");
        Clear("currentfile eexec ");
        clear.AddRange(encPriv);
        return clear.ToArray();
    }

    // Type1 eexec/charstring encryption (the inverse of the decrypt in the engine).
    private static byte[] Encrypt(byte[] plain, ushort r0, int skip)
    {
        const ushort c1 = 52845, c2 = 22719;
        var full = new byte[skip + plain.Length];
        Array.Copy(plain, 0, full, skip, plain.Length);
        ushort r = r0;
        var outb = new byte[full.Length];
        for (int i = 0; i < full.Length; i++)
        {
            byte cipher = (byte)(full[i] ^ (r >> 8));
            outb[i] = cipher;
            r = (ushort)((cipher + r) * c1 + c2);
        }
        return outb;
    }

    private static uint ReadU32(byte[] d, int o)
        => ((uint)d[o] << 24) | ((uint)d[o + 1] << 16) | ((uint)d[o + 2] << 8) | d[o + 3];

    private static bool HasTable(byte[] otf, string tag)
    {
        int n = (otf[4] << 8) | otf[5];
        for (int i = 0; i < n; i++)
        {
            int rec = 12 + i * 16;
            if ((char)otf[rec] == tag[0] && (char)otf[rec + 1] == tag[1]
                && (char)otf[rec + 2] == tag[2] && (char)otf[rec + 3] == tag[3])
            {
                return true;
            }
        }
        return false;
    }
}
