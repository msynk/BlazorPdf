using BlazorPdf.Engine.Fonts;

namespace BlazorPdf.Tests;

public class Standard14MetricsTests
{
    [Theory]
    [InlineData("Helvetica", 'i', 222)]
    [InlineData("Helvetica", 'W', 944)]
    [InlineData("Helvetica", ' ', 278)]
    [InlineData("Helvetica-Bold", 'W', 944)]
    [InlineData("Times-Roman", 'A', 722)]
    [InlineData("Times-Roman", 'i', 278)]
    [InlineData("Times-Bold", 'W', 1000)]
    [InlineData("Courier", 'i', 600)]
    [InlineData("Courier-Bold", 'W', 600)]
    public void Returns_standard_afm_widths(string baseFont, char ch, int expected)
    {
        Assert.Equal(expected, Standard14Metrics.GetWidth(baseFont, ch), 0.001);
    }

    [Fact]
    public void Strips_subset_prefix()
    {
        Assert.Equal(944, Standard14Metrics.GetWidth("ABCDEF+Helvetica", 'W'), 0.001);
    }

    [Fact]
    public void Simple_font_without_widths_uses_standard14_metrics()
    {
        var doc = BlazorPdf.Engine.PdfDocument.Load(PdfBuilder.Build(1, compress: false));
        var dict = new BlazorPdf.Engine.PdfDictionary();
        dict.Items["Subtype"] = new BlazorPdf.Engine.PdfName("Type1");
        dict.Items["BaseFont"] = new BlazorPdf.Engine.PdfName("Helvetica");

        var font = PdfFont.Load(doc, dict);

        Assert.Equal(944, font.GetWidth('W'), 0.001);
        Assert.Equal(222, font.GetWidth('i'), 0.001);
    }
}
