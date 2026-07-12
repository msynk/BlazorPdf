
namespace BlazorPdf;

public class PdfDocumentTests
{
    [Fact]
    public void Loads_single_page_document()
    {
        var doc = BlazorPdfDocument.Load(TestPdf.HelloWorld());
        Assert.Equal(1, doc.PageCount);
        Assert.Equal("1.7", doc.Version);
    }

    [Fact]
    public void Page_geometry_matches_mediabox()
    {
        var doc = BlazorPdfDocument.Load(TestPdf.HelloWorld());
        var page = doc.Pages[0];
        Assert.Equal(200, page.Width);
        Assert.Equal(200, page.Height);
        Assert.Equal(0, page.Rotate);
    }

    [Fact]
    public void Renders_text_content()
    {
        var doc = BlazorPdfDocument.Load(TestPdf.HelloWorld());
        string html = new BlazorPdfHtmlRenderer(doc.Pages[0], doc.XRef).Render();
        Assert.Contains("Hello", html);
        Assert.Contains("bp-html-page", html);
    }

    [Fact]
    public void Parses_outline_with_resolved_destination()
    {
        var doc = BlazorPdfDocument.Load(TestPdf.HelloWorld());
        var outline = doc.Outline;

        Assert.Single(outline);
        Assert.Equal("Chapter 1", outline[0].Title);
        Assert.Equal(1, outline[0].PageNumber);
    }

    [Fact]
    public void Outline_destination_carries_view_parameters()
    {
        var doc = BlazorPdfDocument.Load(TestPdf.HelloWorld());
        var dest = doc.Outline[0].Destination;

        Assert.NotNull(dest);
        Assert.Equal(1, dest!.PageNumber);
        Assert.Equal(BlazorPdfDestinationFit.XYZ, dest.Fit);   // [3 0 R /XYZ 0 200 0]
        Assert.Equal(0, dest.Left);
        Assert.Equal(200, dest.Top);
    }

    [Fact]
    public void Fit_destinations_are_parsed()
    {
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /Outlines 4 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>",
            "<< /Type /Outlines /First 5 0 R /Last 5 0 R /Count 1 >>",
            "<< /Title (Fit rect) /Parent 4 0 R /Dest [3 0 R /FitR 10 20 190 180] >>",
        };
        var doc = BlazorPdfDocument.Load(TestPdf.Build(bodies, rootObjNum: 1));
        var dest = doc.Outline[0].Destination;

        Assert.NotNull(dest);
        Assert.Equal(BlazorPdfDestinationFit.FitR, dest!.Fit);
        Assert.Equal(10, dest.Left);
        Assert.Equal(20, dest.Bottom);
        Assert.Equal(190, dest.Right);
        Assert.Equal(180, dest.Top);
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
        var doc = BlazorPdfDocument.Load(TestPdf.Build(bodies, rootObjNum: 1));

        Assert.Equal(1, doc.PageCount);
        Assert.Empty(doc.Outline);
    }
}
