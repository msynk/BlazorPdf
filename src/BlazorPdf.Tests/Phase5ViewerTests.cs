
namespace BlazorPdf;

/// <summary>Phase 5: viewer/engine public API additions.</summary>
public class Phase5ViewerTests
{
    // 5.2/5.8 — text extraction without rendering HTML, for search/copy.
    [Fact]
    public void Extracts_page_text_without_rendering()
    {
        var doc = BlazorPdfDocument.Load(TestPdf.HelloWorld());
        string text = doc.Pages[0].ExtractText();
        Assert.Contains("Hello", text);
    }

    [Fact]
    public void Extracts_text_from_multiple_show_operators()
    {
        string content = "BT /F1 12 Tf 20 100 Td (Foo) Tj 0 -20 Td (Bar) Tj ET";
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] " +
                "/Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            TestPdf.Stream(content),
        };
        var doc = BlazorPdfDocument.Load(TestPdf.Build(bodies, rootObjNum: 1));
        string text = doc.Pages[0].ExtractText();

        Assert.Contains("Foo", text);
        Assert.Contains("Bar", text);
    }
}
