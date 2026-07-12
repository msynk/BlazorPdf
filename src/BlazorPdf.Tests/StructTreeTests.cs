
namespace BlazorPdf;

/// <summary>Phase 6.6: tagged-PDF logical structure tree exposure.</summary>
public class StructTreeTests
{
    [Fact]
    public void Builds_structure_tree_with_types_and_alt()
    {
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 5 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            "<< /Type /StructTreeRoot /K [6 0 R] >>",
            "<< /Type /StructElem /S /Document /K [7 0 R 8 0 R] >>",
            "<< /Type /StructElem /S /H1 /K [0] >>",
            "<< /Type /StructElem /S /Figure /Alt (A chart) /K [1] >>",
        };
        var doc = BlazorPdfDocument.Load(TestPdf.Build(bodies, rootObjNum: 1));
        var tree = doc.StructureTree;

        Assert.Single(tree);
        Assert.Equal("Document", tree[0].Type);
        Assert.Equal(2, tree[0].Children.Count);
        Assert.Equal("H1", tree[0].Children[0].Type);
        Assert.Equal("Figure", tree[0].Children[1].Type);
        Assert.Equal("A chart", tree[0].Children[1].Alt);
    }

    [Fact]
    public void Untagged_document_has_empty_structure()
    {
        var doc = BlazorPdfDocument.Load(TestPdf.HelloWorld());
        Assert.Empty(doc.StructureTree);
    }
}
