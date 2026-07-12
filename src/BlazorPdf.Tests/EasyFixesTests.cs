
namespace BlazorPdf;

/// <summary>
/// Regression tests for the small correctness fixes: 1.7 (Crypt filter / non-inline
/// <c>/F</c>), 1.20 (Type3 indirect widths), 1.25 (Separation /None), 1.26
/// (indirect numbers in functions/shadings), 2.9 (GetInt32 EOF), 4.9 (d0/d1).
/// </summary>
public class EasyFixesTests
{
    // A minimal xref that resolves references from a number->object map.
    private sealed class MapXRef : IBlazorPdfXRef
    {
        private readonly Dictionary<int, object?> _map;
        public MapXRef(Dictionary<int, object?> map) => _map = map;
        public object? Fetch(BlazorPdfRef reference, bool suppressEncryption = false)
            => _map.TryGetValue(reference.Num, out var v) ? v : null;
        public object? FetchIfRef(object? value, bool suppressEncryption = false)
            => value is BlazorPdfRef r ? Fetch(r) : value;
    }

    // 2.9 — GetInt32 must honour its "-1 at end" contract when fewer than four
    // bytes remain rather than composing garbage from the -1 sentinels.
    [Fact]
    public void GetInt32_returns_minus_one_at_eof()
    {
        var stream = new BlazorPdfStream(new byte[] { 0x01, 0x02, 0x03 }); // only 3 bytes
        Assert.Equal(-1, stream.GetInt32());
    }

    [Fact]
    public void GetInt32_reads_full_word()
    {
        var stream = new BlazorPdfStream(new byte[] { 0x00, 0x00, 0x01, 0x02 });
        Assert.Equal(0x0102, stream.GetInt32());
    }

    // 1.25 — a Separation whose colorant is /None produces no marks; on the default
    // white page that maps to white regardless of the requested tint.
    [Fact]
    public void Separation_none_paints_nothing()
    {
        var arr = new List<object?> { BlazorPdfName.Get("Separation"), BlazorPdfName.Get("None"), BlazorPdfName.Get("DeviceGray") };
        var cs = BlazorPdfColorSpace.Create(arr, new InlineXRef(), null);
        Assert.Equal(((byte)255, (byte)255, (byte)255), cs.GetRgb([1.0]));
    }

    // A named (non-None) Separation is still a real tint, not forced to white.
    [Fact]
    public void Separation_named_colorant_is_not_forced_white()
    {
        var arr = new List<object?> { BlazorPdfName.Get("Separation"), BlazorPdfName.Get("Spot"), BlazorPdfName.Get("DeviceGray") };
        var cs = BlazorPdfColorSpace.Create(arr, new InlineXRef(), null);
        // With full tint (1.0) and no transform, the gray fallback is black.
        Assert.Equal(((byte)0, (byte)0, (byte)0), cs.GetRgb([1.0]));
    }

    // 1.26 — /C0 and /C1 elements given as indirect references must be resolved,
    // not silently read as 0.
    [Fact]
    public void Exponential_function_resolves_indirect_endpoint_numbers()
    {
        var xref = new MapXRef(new Dictionary<int, object?> { [10] = 0.0, [11] = 1.0 });
        var dict = new BlazorPdfDict();
        dict.Set("FunctionType", 2.0);
        dict.Set("Domain", new List<object?> { 0.0, 1.0 });
        dict.Set("C0", new List<object?> { new BlazorPdfRef(10, 0) });
        dict.Set("C1", new List<object?> { new BlazorPdfRef(11, 0) });
        dict.Set("N", 1.0);

        var fn = BlazorPdfFunction.Create(dict, xref)!;
        // Midpoint interpolates C0..C1 = 0..1 -> 0.5. Unresolved refs would give 0.
        Assert.Equal(0.5, fn.Eval([0.5])[0], 3);
    }

    // 1.7 — for a regular (non-inline) stream, /F is a file specification, not a
    // /Filter abbreviation, so it must be ignored by the decoder.
    [Fact]
    public void NonInline_stream_ignores_F_as_filter()
    {
        byte[] raw = { 1, 2, 3, 4, 5 };
        var dict = new BlazorPdfDict();
        dict.Set("F", BlazorPdfName.Get("FlateDecode")); // would be nonsense to inflate raw bytes
        var stream = new BlazorPdfStream(raw, 0, raw.Length, dict);

        Assert.Equal(raw, BlazorPdfStreamDecoder.Decode(stream)); // inline: false (default)
    }

    // 1.7 — the Crypt filter is not a data transform in the decode pipeline; it
    // passes bytes through unchanged (decryption already happened in the xref layer).
    [Fact]
    public void Crypt_filter_is_a_passthrough()
    {
        byte[] raw = { 9, 8, 7, 6 };
        var dict = new BlazorPdfDict();
        dict.Set("Filter", BlazorPdfName.Get("Crypt"));
        var stream = new BlazorPdfStream(raw, 0, raw.Length, dict);

        Assert.Equal(raw, BlazorPdfStreamDecoder.Decode(stream));
    }

    // 4.9 — inside a Type3 `d1` glyph, colour operators are suppressed (the glyph
    // takes its colour from the text-showing context). With `d0` they apply.
    [Fact]
    public void Type3_d1_glyph_ignores_local_colour_but_d0_applies()
    {
        // d1 variant: outer fill green; glyph tries red; d1 must keep it green.
        string d1Html = RenderType3WithGlyph("750 0 0 0 750 750 d1 1 0 0 rg 0 0 750 750 re f");
        Assert.Contains("rgb(0,255,0)", d1Html);      // inherited green wins
        Assert.DoesNotContain("rgb(255,0,0)", d1Html); // local red suppressed

        // d0 variant: the same red operator now takes effect.
        string d0Html = RenderType3WithGlyph("750 0 d0 1 0 0 rg 0 0 750 750 re f");
        Assert.Contains("rgb(255,0,0)", d0Html);
    }

    // 4.8 — a link with /QuadPoints emits one clickable hotspot per quad (e.g. a
    // URL that wraps across two lines) rather than a single Rect-sized hotspot.
    [Fact]
    public void Link_with_quadpoints_emits_one_hotspot_per_quad()
    {
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Annots [4 0 R] >>",
            // Two quads (8 numbers each) over a link spanning two lines.
            "<< /Type /Annot /Subtype /Link /Rect [10 10 190 60] " +
                "/QuadPoints [10 40 100 40 10 60 100 60  10 10 190 30 10 10 190 30] " +
                "/A << /S /URI /URI (https://example.com/) >> >>",
        };
        var doc = BlazorPdfDocument.Load(TestPdf.Build(bodies, rootObjNum: 1));
        string html = new BlazorPdfHtmlRenderer(doc.Pages[0], doc.XRef).Render();

        int anchors = html.Split("<a href=\"https://example.com/\"").Length - 1;
        Assert.Equal(2, anchors);
    }

    // 4.8 — with no /QuadPoints the whole /Rect remains a single hotspot.
    [Fact]
    public void Link_without_quadpoints_emits_single_hotspot()
    {
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Annots [4 0 R] >>",
            "<< /Type /Annot /Subtype /Link /Rect [10 10 100 30] " +
                "/A << /S /URI /URI (https://example.com/) >> >>",
        };
        var doc = BlazorPdfDocument.Load(TestPdf.Build(bodies, rootObjNum: 1));
        string html = new BlazorPdfHtmlRenderer(doc.Pages[0], doc.XRef).Render();

        int anchors = html.Split("<a href=\"https://example.com/\"").Length - 1;
        Assert.Equal(1, anchors);
    }

    // 2.8 — a catalog /Version later than the header takes precedence.
    [Fact]
    public void Catalog_version_overrides_header_version()
    {
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /Version /2.0 >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>",
        };
        // TestPdf writes a "%PDF-1.7" header; the catalog /Version raises it to 2.0.
        var doc = BlazorPdfDocument.Load(TestPdf.Build(bodies, rootObjNum: 1));
        Assert.Equal("2.0", doc.Version);
    }

    // 4.13 — permission bits are surfaced; an unencrypted document grants all.
    [Fact]
    public void Permissions_unencrypted_grants_everything()
    {
        var perms = new BlazorPdfPermissions(0, encrypted: false);
        Assert.True(perms.CanPrint);
        Assert.True(perms.CanCopy);
        Assert.True(perms.CanModify);
        Assert.True(perms.CanAnnotate);
    }

    // 4.13 — for an encrypted document the individual /P bits are honoured.
    [Fact]
    public void Permissions_reflects_p_bits()
    {
        // Bit 3 (print) set, bit 5 (copy) clear.
        int p = 1 << (3 - 1);
        var perms = new BlazorPdfPermissions(p, encrypted: true);
        Assert.True(perms.CanPrint);
        Assert.False(perms.CanCopy);
        Assert.False(perms.CanModify);
    }

    // 2.5 — junk prepended before the %PDF header shifts every stored offset; the
    // reader must add the header offset so the normal xref path still works (no
    // fallback to full-scan recovery).
    [Fact]
    public void Prepended_junk_before_header_still_parses_via_xref()
    {
        byte[] pdf = TestPdf.HelloWorld();
        byte[] junk = System.Text.Encoding.Latin1.GetBytes("some prepended junk\n");
        byte[] combined = new byte[junk.Length + pdf.Length];
        Array.Copy(junk, 0, combined, 0, junk.Length);
        Array.Copy(pdf, 0, combined, junk.Length, pdf.Length);

        var doc = BlazorPdfDocument.Load(combined);

        Assert.Equal(1, doc.PageCount);
        Assert.NotNull(doc.Pages[0].Resources);
        // The classic xref path handled it; recovery-by-scanning never ran.
        Assert.DoesNotContain(doc.Warnings, w => w.Contains("Rebuilding"));
    }

    // 1.15 — an image with no /Interpolate (the default) drawn scaled up renders
    // with crisp (pixelated) sampling; /Interpolate true keeps smooth sampling.
    [Fact]
    public void Image_interpolation_controls_pixelated_rendering()
    {
        Assert.Contains("image-rendering:pixelated", RenderUpscaledImage(interpolate: false));
        Assert.DoesNotContain("image-rendering:pixelated", RenderUpscaledImage(interpolate: true));
    }

    private static string RenderUpscaledImage(bool interpolate)
    {
        // A 2x2 RGB image (red, green, blue, white) as ASCII hex, drawn 100x100 so
        // each 1px source sample covers 50 device px (clearly upscaled).
        string interpEntry = interpolate ? " /Interpolate true" : "";
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] " +
                "/Resources << /XObject << /Im0 4 0 R >> >> /Contents 5 0 R >>",
            TestPdf.Stream("FF000000FF000000FFFFFFFF>",
                " /Type /XObject /Subtype /Image /Width 2 /Height 2 " +
                "/ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /ASCIIHexDecode" + interpEntry),
            TestPdf.Stream("q 100 0 0 100 20 20 cm /Im0 Do Q"),
        };
        var doc = BlazorPdfDocument.Load(TestPdf.Build(bodies, rootObjNum: 1));
        return new BlazorPdfHtmlRenderer(doc.Pages[0], doc.XRef).Render();
    }

    // 4.6 — the sh operator honours a shading /BBox by clipping the painted
    // region; without a /BBox the fill covers the clip region unclipped.
    [Fact]
    public void Shading_bbox_clips_the_sh_fill()
    {
        Assert.Contains("clip-path:path(", RenderAxialShading(withBBox: true));
        Assert.DoesNotContain("clip-path:path(", RenderAxialShading(withBBox: false));
    }

    private static string RenderAxialShading(bool withBBox)
    {
        string bbox = withBBox ? " /BBox [10 10 100 100]" : "";
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] " +
                "/Resources << /Shading << /Sh0 4 0 R >> >> /Contents 6 0 R >>",
            "<< /ShadingType 2 /ColorSpace /DeviceRGB /Coords [0 0 200 0] " +
                "/Function 5 0 R /Extend [true true]" + bbox + " >>",
            "<< /FunctionType 2 /Domain [0 1] /C0 [1 0 0] /C1 [0 0 1] /N 1 >>",
            TestPdf.Stream("/Sh0 sh"),
        };
        var doc = BlazorPdfDocument.Load(TestPdf.Build(bodies, rootObjNum: 1));
        return new BlazorPdfHtmlRenderer(doc.Pages[0], doc.XRef).Render();
    }

    private static string RenderType3WithGlyph(string glyphProc)
    {
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] " +
                "/Resources << /Font << /F1 4 0 R >> >> /Contents 8 0 R >>",
            "<< /Type /Font /Subtype /Type3 /FontBBox [0 0 750 750] " +
                "/FontMatrix [0.001 0 0 0.001 0 0] /CharProcs 5 0 R /Encoding 6 0 R " +
                "/FirstChar 97 /LastChar 97 /Widths [750] >>",
            "<< /a 7 0 R >>",
            "<< /Type /Encoding /Differences [97 /a] >>",
            TestPdf.Stream(glyphProc),
            // Outer content sets the fill colour green before showing the glyph.
            TestPdf.Stream("0 1 0 rg BT /F1 100 Tf 20 20 Td (a) Tj ET"),
        };
        var doc = BlazorPdfDocument.Load(TestPdf.Build(bodies, rootObjNum: 1));
        return new BlazorPdfHtmlRenderer(doc.Pages[0], doc.XRef).Render();
    }
}
