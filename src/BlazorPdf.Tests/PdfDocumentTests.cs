using BlazorPdf.Core;
using BlazorPdf.Core.Render;

namespace BlazorPdf.Tests;

public class PdfDocumentTests
{
    [Fact]
    public void Loads_single_page_document()
    {
        var doc = PdfDocument.Load(TestPdf.HelloWorld());
        Assert.Equal(1, doc.PageCount);
        Assert.Equal("1.7", doc.Version);
    }

    [Fact]
    public void Page_geometry_matches_mediabox()
    {
        var doc = PdfDocument.Load(TestPdf.HelloWorld());
        var page = doc.Pages[0];
        Assert.Equal(200, page.Width);
        Assert.Equal(200, page.Height);
        Assert.Equal(0, page.Rotate);
    }

    [Fact]
    public void Renders_text_content()
    {
        var doc = PdfDocument.Load(TestPdf.HelloWorld());
        string html = new HtmlRenderer(doc.Pages[0], doc.XRef).Render();
        Assert.Contains("Hello", html);
        Assert.Contains("bp-html-page", html);
    }

    [Fact]
    public void Parses_outline_with_resolved_destination()
    {
        var doc = PdfDocument.Load(TestPdf.HelloWorld());
        var outline = doc.Outline;

        Assert.Single(outline);
        Assert.Equal("Chapter 1", outline[0].Title);
        Assert.Equal(1, outline[0].PageNumber);
    }

    [Fact]
    public void Document_without_outline_is_empty()
    {
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] >>",
        };
        var doc = PdfDocument.Load(TestPdf.Build(bodies, rootObjNum: 1));

        Assert.Equal(1, doc.PageCount);
        Assert.Empty(doc.Outline);
    }
}
