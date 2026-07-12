
namespace BlazorPdf;

public class DocumentInfoTests
{
    [Fact]
    public void Reads_info_dictionary_metadata()
    {
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] >>",
            "<< /Title (Quarterly Report) /Author (Ada) /Producer (BlazorPdf) " +
                "/Creator (Unit Test) /CreationDate (D:20240115093000+02'00') /Company (Acme) >>",
        };
        var doc = BlazorPdfDocument.Load(TestPdf.Build(bodies, rootObjNum: 1, trailerExtra: " /Info 4 0 R"));

        var meta = doc.Metadata;
        Assert.Equal("Quarterly Report", meta.Title);
        Assert.Equal("Ada", meta.Author);
        Assert.Equal("BlazorPdf", meta.Producer);
        Assert.Equal("Unit Test", meta.Creator);
        Assert.NotNull(meta.CreationDate);
        Assert.Equal(2024, meta.CreationDate!.Value.Year);
        Assert.Equal(1, meta.CreationDate.Value.Month);
        Assert.Equal(15, meta.CreationDate.Value.Day);
        Assert.Equal(TimeSpan.FromHours(2), meta.CreationDate.Value.Offset);
        Assert.Equal("Acme", meta.Custom["Company"]);
    }

    [Fact]
    public void Metadata_is_empty_when_no_info()
    {
        var doc = BlazorPdfDocument.Load(TestPdf.HelloWorld());
        Assert.Null(doc.Metadata.Title);
        Assert.Null(doc.Metadata.CreationDate);
    }

    [Theory]
    [InlineData("D:2024", 2024, 1, 1)]
    [InlineData("D:20240229120000Z", 2024, 2, 29)]
    [InlineData("20231231", 2023, 12, 31)]
    public void Parses_pdf_dates(string raw, int year, int month, int day)
    {
        var date = BlazorPdfMetadata.ParseDate(raw);
        Assert.NotNull(date);
        Assert.Equal(year, date!.Value.Year);
        Assert.Equal(month, date.Value.Month);
        Assert.Equal(day, date.Value.Day);
    }

    [Fact]
    public void Reads_page_labels_number_tree()
    {
        // Pages 0-1 use lowercase roman (i, ii); pages 2-3 use decimal from 1.
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /PageLabels " +
                "<< /Nums [0 << /S /r >> 2 << /S /D /St 1 >>] >> >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R 5 0 R 6 0 R] /Count 4 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] >>",
        };
        var doc = BlazorPdfDocument.Load(TestPdf.Build(bodies, rootObjNum: 1));

        Assert.Equal(4, doc.PageCount);
        Assert.Equal(["i", "ii", "1", "2"], doc.PageLabels);
    }

    [Fact]
    public void Page_labels_default_to_page_number()
    {
        var doc = BlazorPdfDocument.Load(TestPdf.HelloWorld());
        Assert.Equal(["1"], doc.PageLabels);
    }

    [Fact]
    public void Page_exposes_crop_and_other_boxes()
    {
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] " +
                "/CropBox [10 10 190 190] /TrimBox [20 20 180 180] >>",
        };
        var doc = BlazorPdfDocument.Load(TestPdf.Build(bodies, rootObjNum: 1));
        var page = doc.Pages[0];

        Assert.Equal([0, 0, 200, 200], page.MediaBox);
        Assert.Equal([10, 10, 190, 190], page.CropBox);
        Assert.Equal([20, 20, 180, 180], page.TrimBox);
        // BleedBox/ArtBox are absent, so they default to the CropBox.
        Assert.Equal(page.CropBox, page.BleedBox);
        Assert.Equal(page.CropBox, page.ArtBox);
    }

    [Fact]
    public void Crop_box_is_clipped_to_media_box()
    {
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /CropBox [-10 -10 150 150] >>",
        };
        var doc = BlazorPdfDocument.Load(TestPdf.Build(bodies, rootObjNum: 1));
        Assert.Equal([0, 0, 100, 100], doc.Pages[0].CropBox);
    }
}
