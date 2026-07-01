using BlazorPdf.Core;
using BlazorPdf.Core.Fonts;
using BlazorPdf.Core.Render;

namespace BlazorPdf.Tests;

public class Type3FontTests
{
    // A single-page document whose only font is a Type3 font. The glyph for
    // code 97 ('a') is a content-stream procedure that fills a rectangle.
    private static byte[] BuildType3Document()
    {
        var bodies = new List<string>
        {
            // 1: Catalog
            "<< /Type /Catalog /Pages 2 0 R >>",
            // 2: Pages
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            // 3: Page
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] " +
                "/Resources << /Font << /F1 4 0 R >> >> /Contents 8 0 R >>",
            // 4: Type3 font
            "<< /Type /Font /Subtype /Type3 /FontBBox [0 0 750 750] " +
                "/FontMatrix [0.001 0 0 0.001 0 0] /CharProcs 5 0 R /Encoding 6 0 R " +
                "/FirstChar 97 /LastChar 97 /Widths [750] >>",
            // 5: CharProcs
            "<< /a 7 0 R >>",
            // 6: Encoding
            "<< /Type /Encoding /Differences [97 /a] >>",
            // 7: Glyph procedure: declare width, then fill a rectangle.
            TestPdf.Stream("750 0 d0 0 0 750 750 re f"),
            // 8: Page content: show the 'a' glyph.
            TestPdf.Stream("BT /F1 100 Tf 20 20 Td (a) Tj ET"),
        };
        return TestPdf.Build(bodies, rootObjNum: 1);
    }

    [Fact]
    public void Type3_font_is_recognized()
    {
        var doc = PdfDocument.Load(BuildType3Document());
        var fontDict = (Dict)doc.Pages[0].Resources!.Get("Font")!;
        var font = PdfFont.Create((Dict)((Dict)fontDict).Get("F1")!, doc.XRef);

        Assert.True(font.IsType3);
        Assert.NotNull(font.Type3);
        Assert.NotNull(font.Type3!.GetGlyphProcedure(97)); // 'a'
        Assert.Null(font.Type3.GetGlyphProcedure(98));     // undefined code
    }

    [Fact]
    public void Type3_glyph_procedure_is_rendered_as_graphics()
    {
        var doc = PdfDocument.Load(BuildType3Document());
        string html = new HtmlRenderer(doc.Pages[0], doc.XRef).Render();

        // The glyph is drawn by executing its procedure (a rectangle fill),
        // which the HTML renderer emits as a clip-path filled <div>, not a <span>.
        Assert.Contains("clip-path:path(", html);
    }
}
