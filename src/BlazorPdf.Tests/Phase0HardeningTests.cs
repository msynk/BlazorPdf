using System.Text;

namespace BlazorPdf;

/// <summary>
/// Regression tests for the Phase 0 security &amp; process-safety hotfixes:
/// hostile input must not crash the process, inject script, or silently produce
/// wrong output.
/// </summary>
public class Phase0HardeningTests
{
    // 0.1 — javascript:/data: URIs in link annotations must never become clickable.
    [Fact]
    public void Link_annotation_with_javascript_uri_is_dropped()
    {
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Annots [4 0 R] >>",
            "<< /Type /Annot /Subtype /Link /Rect [10 10 100 30] " +
                "/A << /S /URI /URI (javascript:alert\\(1\\)) >> >>",
        };
        var doc = BlazorPdfDocument.Load(TestPdf.Build(bodies, rootObjNum: 1));
        string html = new BlazorPdfHtmlRenderer(doc.Pages[0], doc.XRef).Render();

        Assert.DoesNotContain("javascript:", html);
        Assert.DoesNotContain("<a ", html);
    }

    // 0.1 — a normal http(s) link is still emitted as an anchor.
    [Fact]
    public void Link_annotation_with_http_uri_is_kept()
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

        Assert.Contains("<a href=\"https://example.com/\"", html);
        Assert.Contains("rel=\"noopener noreferrer\"", html);
    }

    // 0.2 — deeply nested arrays raise a catchable exception, not a StackOverflow.
    [Fact]
    public void Deeply_nested_arrays_throw_instead_of_crashing()
    {
        byte[] bytes = Encoding.Latin1.GetBytes(new string('[', 4096));
        var parser = new BlazorPdfParser(new BlazorPdfLexer(new BlazorPdfStream(bytes)));

        Assert.Throws<BlazorPdfFormatException>(() => parser.GetObj());
    }

    // 0.3 — a page tree with duplicate /Kids refs must not duplicate pages.
    [Fact]
    public void Duplicate_kids_do_not_duplicate_pages()
    {
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] >>",
        };
        var doc = BlazorPdfDocument.Load(TestPdf.Build(bodies, rootObjNum: 1));

        Assert.Equal(1, doc.PageCount);
    }

    // 0.8 — invisible (Tr 3) text is emitted transparently so it stays selectable.
    [Fact]
    public void Invisible_text_is_emitted_as_transparent()
    {
        string content = "BT /F1 24 Tf 3 Tr 50 100 Td (SecretLayer) Tj ET";
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
        string html = new BlazorPdfHtmlRenderer(doc.Pages[0], doc.XRef).Render();

        Assert.Contains("SecretLayer", html);
        Assert.Contains("color:transparent", html);
    }

    // 0.11 — a declared /Encrypt with no usable handler fails loudly and typed.
    [Fact]
    public void Unsupported_encryption_throws_typed_exception()
    {
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] >>",
            "<< /Filter /CustomHandler /V 1 /R 2 >>",
        };
        byte[] pdf = TestPdf.Build(bodies, rootObjNum: 1,
            trailerExtra: " /Encrypt 4 0 R /ID [<01020304> <01020304>]");

        Assert.Throws<BlazorPdfUnsupportedEncryptionException>(() => BlazorPdfDocument.Load(pdf));
    }
}
