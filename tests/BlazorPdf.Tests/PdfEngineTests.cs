using BlazorPdf.Engine;

namespace BlazorPdf.Tests;

public class PdfEngineTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(7)]
    public void Load_counts_pages(int pages)
    {
        var doc = PdfDocument.Load(PdfBuilder.Build(pages, compress: false));
        Assert.Equal(pages, doc.PageCount);
    }

    [Fact]
    public void Load_reads_page_dimensions()
    {
        var doc = PdfDocument.Load(PdfBuilder.Build(1, compress: false));
        var page = doc.Pages[0];

        Assert.Equal(612, page.Width, 3);
        Assert.Equal(792, page.Height, 3);
        Assert.Equal(0, page.Rotation);
    }

    [Fact]
    public void Extracts_text_from_uncompressed_stream()
    {
        var doc = PdfDocument.Load(PdfBuilder.Build(2, compress: false));
        var text = doc.Pages[0].ExtractText();

        Assert.Contains("BlazorPdf engine test", text);
        Assert.Contains("Page 1 of 2", text);
    }

    [Fact]
    public void Extracts_text_through_flate_filter()
    {
        var doc = PdfDocument.Load(PdfBuilder.Build(3, compress: true));

        Assert.Contains("BlazorPdf engine test", doc.Pages[0].ExtractText());
        Assert.Contains("Page 2 of 3", doc.Pages[1].ExtractText());
        Assert.Contains("Page 3 of 3", doc.Pages[2].ExtractText());
    }

    [Fact]
    public void Search_finds_matches_per_page()
    {
        var doc = PdfDocument.Load(PdfBuilder.Build(3, compress: true));

        var hits = doc.Search("BlazorPdf");
        Assert.Equal(3, hits.Count);
        Assert.All(hits, h => Assert.True(h.Occurrences >= 1));

        var page2 = doc.Search("Page 2 of 3");
        Assert.Single(page2);
        Assert.Equal(2, page2[0].PageNumber);
    }

    [Fact]
    public void Search_is_case_insensitive_by_default()
    {
        var doc = PdfDocument.Load(PdfBuilder.Build(1, compress: false));

        Assert.NotEmpty(doc.Search("blazorpdf"));
        Assert.Empty(doc.Search("blazorpdf", caseSensitive: true));
    }

    [Fact]
    public void Rejects_non_pdf_data()
    {
        Assert.Throws<PdfParseException>(() => PdfDocument.Load([1, 2, 3, 4, 5, 6]));
    }

    [Fact]
    public void Extract_all_text_separates_pages_with_form_feed()
    {
        var doc = PdfDocument.Load(PdfBuilder.Build(2, compress: false));
        var all = doc.ExtractText();

        Assert.Contains('\f', all);
        Assert.Equal(2, all.Split('\f').Length);
    }
}
