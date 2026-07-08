using System.Text;
using BlazorPdf.Core;

namespace BlazorPdf.Tests;

/// <summary>
/// Regression tests for Phase 2 damaged-file recovery: a document with a broken
/// cross-reference table must still open by scanning for objects (like pdf.js
/// XRef.indexObjects), reporting the problem via <see cref="PdfDocument.Warnings"/>.
/// </summary>
public class Phase2RecoveryTests
{
    // 2.1/2.5 — startxref points nowhere: recovery rebuilds from a full scan.
    [Fact]
    public void Recovers_from_bad_startxref_offset()
    {
        byte[] pdf = TestPdf.HelloWorld();
        string s = Encoding.Latin1.GetString(pdf);
        // Point startxref at an offset past the end of the file.
        int idx = s.LastIndexOf("startxref", StringComparison.Ordinal);
        string corrupted = s[..idx] + "startxref\n999999\n%%EOF";
        byte[] bytes = Encoding.Latin1.GetBytes(corrupted);

        var doc = PdfDocument.Load(bytes);

        Assert.Equal(1, doc.PageCount);
        Assert.NotEmpty(doc.Warnings);
    }

    // 2.1 — the entire xref/trailer tail is missing (truncated file): the catalog
    // is recovered by scanning for the /Type /Catalog object.
    [Fact]
    public void Recovers_from_truncated_tail()
    {
        byte[] pdf = TestPdf.HelloWorld();
        string s = Encoding.Latin1.GetString(pdf);
        int cut = s.LastIndexOf("\nxref", StringComparison.Ordinal);
        byte[] truncated = pdf[..cut];

        var doc = PdfDocument.Load(truncated);

        Assert.Equal(1, doc.PageCount);
        Assert.Contains("Hello", new BlazorPdf.Core.Render.HtmlRenderer(doc.Pages[0], doc.XRef).Render());
        Assert.NotEmpty(doc.Warnings);
    }

    // A clean file must NOT report warnings (recovery didn't run).
    [Fact]
    public void Clean_file_has_no_warnings()
    {
        var doc = PdfDocument.Load(TestPdf.HelloWorld());
        Assert.Empty(doc.Warnings);
    }
}
