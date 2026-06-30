using BlazorPdf.Core;
using BlazorPdf.Core.Render;

namespace BlazorPdf.Tests;

/// <summary>
/// Renders single pages whose content streams exercise specific operators, and
/// asserts on the emitted HTML/CSS.
/// </summary>
public class RenderOperatorTests
{
    private static string Render(string content)
    {
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 300] " +
                "/Resources << >> /Contents 4 0 R >>",
            TestPdf.Stream(content),
        };
        var doc = PdfDocument.Load(TestPdf.Build(bodies, rootObjNum: 1));
        return new HtmlRenderer(doc.Pages[0], doc.XRef).Render();
    }

    [Fact]
    public void Fills_rectangle_with_rgb_color()
    {
        string html = Render("1 0 0 rg 10 10 100 100 re f");
        Assert.Contains("rgb(255,0,0)", html);
        Assert.Contains("clip-path:path", html);
    }

    [Fact]
    public void Cmyk_fill_converts_to_rgb()
    {
        string html = Render("0 1 1 0 k 10 10 50 50 re f");
        Assert.Contains("rgb(255,0,0)", html);
    }

    [Fact]
    public void Stroked_path_emits_outline()
    {
        string html = Render("0 0 1 RG 5 w 10 10 m 200 200 l S");
        Assert.Contains("rgb(0,0,255)", html);
        Assert.Contains("clip-path:path", html);
    }

    [Fact]
    public void Dashed_stroke_is_broken_into_segments()
    {
        // A long horizontal dashed line should yield multiple separate quads.
        string solid = Render("2 w 0 100 m 300 100 l S");
        string dashed = Render("2 w [10 10] 0 d 0 100 m 300 100 l S");

        int solidQuads = CountMoves(solid);
        int dashedQuads = CountMoves(dashed);
        Assert.True(dashedQuads > solidQuads,
            $"dashed ({dashedQuads}) should have more sub-paths than solid ({solidQuads})");
    }

    [Fact]
    public void Separation_colorspace_paints_via_tint_transform()
    {
        // /Sep is a Separation space whose tint transform is an exponential
        // function producing CMYK; full tint (1.0) -> solid color.
        string content =
            "/CS0 cs 1 scn 10 10 50 50 re f";
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 300] " +
                "/Resources << /ColorSpace << /CS0 5 0 R >> >> /Contents 4 0 R >>",
            TestPdf.Stream(content),
            // [/Separation /Spot /DeviceCMYK <fn>] with a type-2 tint transform
            // mapping t -> (0, t, t, 0) i.e. red at full tint.
            "[ /Separation /Spot /DeviceCMYK 6 0 R ]",
            "<< /FunctionType 2 /Domain [0 1] /C0 [0 0 0 0] /C1 [0 1 1 0] /N 1 >>",
        };
        var doc = PdfDocument.Load(TestPdf.Build(bodies, rootObjNum: 1));
        string html = new HtmlRenderer(doc.Pages[0], doc.XRef).Render();
        Assert.Contains("rgb(255,0,0)", html);
    }

    private static int CountMoves(string html)
    {
        int count = 0;
        int idx = 0;
        while ((idx = html.IndexOf('M', idx)) != -1)
        {
            count++;
            idx++;
        }
        return count;
    }
}
