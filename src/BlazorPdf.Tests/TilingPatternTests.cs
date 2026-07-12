using System.Text.RegularExpressions;

namespace BlazorPdf;

public class TilingPatternTests
{
    // A page filled with a colored tiling pattern (PatternType 1): each 10x10
    // cell paints a small black square, tiled across a 100x100 fill.
    private static byte[] BuildTilingDocument()
    {
        string patternDict =
            " /Type /Pattern /PatternType 1 /PaintType 1 /TilingType 1 " +
            "/BBox [0 0 10 10] /XStep 10 /YStep 10 /Resources << >> /Matrix [1 0 0 1 0 0]";

        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] " +
                "/Resources << /Pattern << /P1 4 0 R >> >> /Contents 5 0 R >>",
            // 4: tiling pattern cell (fills a 5x5 square)
            TestPdf.Stream("0 0 5 5 re f", patternDict),
            // 5: page content: fill a 100x100 rect with the pattern
            TestPdf.Stream("/Pattern cs /P1 scn 0 0 100 100 re f"),
        };
        return TestPdf.Build(bodies, rootObjNum: 1);
    }

    [Fact]
    public void Tiling_pattern_fill_emits_multiple_cells()
    {
        var doc = BlazorPdfDocument.Load(BuildTilingDocument());
        string html = new BlazorPdfHtmlRenderer(doc.Pages[0], doc.XRef).Render();

        // Many clipped cells are emitted rather than a single solid fill.
        int clips = Regex.Matches(html, "clip-path:path\\(").Count;
        Assert.True(clips > 10, $"expected many tiled cells, got {clips}");

        // The cells paint the current (black) fill color.
        Assert.Contains("background:rgb(0,0,0)", html);
    }

    [Fact]
    public void Non_pattern_fill_is_unaffected()
    {
        // A plain colored fill should still produce exactly one filled div.
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R >>",
            TestPdf.Stream("1 0 0 rg 0 0 100 100 re f"),
        };
        var doc = BlazorPdfDocument.Load(TestPdf.Build(bodies, rootObjNum: 1));
        string html = new BlazorPdfHtmlRenderer(doc.Pages[0], doc.XRef).Render();

        Assert.Contains("background:rgb(255,0,0)", html);
    }
}
