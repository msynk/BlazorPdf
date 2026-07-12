
namespace BlazorPdf;

/// <summary>Phase 6.5: AcroForm field extraction.</summary>
public class AcroFormTests
{
    [Fact]
    public void Extracts_form_fields_with_type_and_value()
    {
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [5 0 R 6 0 R] >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            "<< /FT /Tx /T (name) /V (Alice) >>",
            "<< /FT /Btn /T (agree) /V /Yes >>",
        };
        var doc = BlazorPdfDocument.Load(TestPdf.Build(bodies, rootObjNum: 1));
        var fields = doc.FormFields;

        Assert.Equal(2, fields.Count);
        Assert.Equal("name", fields[0].Name);
        Assert.Equal("Tx", fields[0].Type);
        Assert.Equal("Alice", fields[0].Value);
        Assert.Equal("agree", fields[1].Name);
        Assert.Equal("Btn", fields[1].Type);
        Assert.Equal("Yes", fields[1].Value);
    }

    [Fact]
    public void Qualified_name_joins_parent_and_child()
    {
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [5 0 R] >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            "<< /T (address) /Kids [6 0 R] >>",
            "<< /FT /Tx /T (city) /V (Oslo) >>",
        };
        var doc = BlazorPdfDocument.Load(TestPdf.Build(bodies, rootObjNum: 1));
        var fields = doc.FormFields;

        Assert.Single(fields);
        Assert.Equal("address.city", fields[0].Name);
        Assert.Equal("Oslo", fields[0].Value);
    }

    [Fact]
    public void Document_without_form_has_no_fields()
    {
        var doc = BlazorPdfDocument.Load(TestPdf.HelloWorld());
        Assert.Empty(doc.FormFields);
    }
}
