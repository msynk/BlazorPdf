using System.Text.Json;
using System.Text.RegularExpressions;

namespace BlazorPdf;

/// <summary>
/// Canvas mode emits painted content as a display list (replayed onto a
/// per-page canvas by the viewer's JS) while the returned HTML carries only the
/// canvas placeholder, the selectable text layer and link overlays. The ops come
/// from the same paint funnels as the HTML backend, so state interpretation is
/// shared by construction.
/// </summary>
public class CanvasRendererTests
{
    private static BlazorPdfCanvasRenderResult Render(string content)
    {
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
                "/Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            TestPdf.Stream(content),
        };
        var doc = BlazorPdfDocument.Load(TestPdf.Build(bodies, rootObjNum: 1));
        return new BlazorPdfCanvasRenderer(doc.Pages[0], doc.XRef).Render();
    }

    private static List<JsonElement> Ops(BlazorPdfCanvasRenderResult result) =>
        JsonSerializer.Deserialize<List<JsonElement>>(result.OpsJson)!;

    private static string Code(JsonElement op) => op[0].GetString()!;

    [Fact]
    public void Emits_canvas_placeholder_and_no_painted_dom()
    {
        var result = Render("0.9 0.2 0.2 rg 40 700 100 50 re f BT /F1 12 Tf 40 600 Td (Hello) Tj ET");

        Assert.Contains("<canvas data-bp-canvas", result.TextLayerHtml);
        Assert.Equal(612, result.Width, precision: 0);
        Assert.Equal(792, result.Height, precision: 0);
        // No painted fill divs or painted text spans in the DOM...
        Assert.DoesNotContain("background:rgb", result.TextLayerHtml);
        Assert.DoesNotContain("user-select:none", result.TextLayerHtml);
        // ...but the selectable text layer is still there.
        Assert.Contains("data-bp-sel", result.TextLayerHtml);
        Assert.Contains(">Hello<", result.TextLayerHtml);
    }

    [Fact]
    public void Fill_and_text_ops_carry_geometry_and_style()
    {
        var result = Render("0.9 0.2 0.2 rg 40 700 100 50 re f BT /F1 12 Tf 40 600 Td (Hi) Tj ET");
        var ops = Ops(result);

        var fill = ops.Single(o => Code(o) == "f");
        Assert.Contains("M40 92", fill[1].GetString());   // 792-700=92: device space, y down
        Assert.Equal("rgb(230,51,51)", fill[3].GetString());

        var text = ops.Single(o => Code(o) == "t");
        Assert.Equal("Hi", text[1].GetString());
        Assert.Equal(12, text[2].GetDouble(), precision: 1);   // font size
        Assert.Equal("sans-serif", text[3].GetString());        // Core-14 substitute
        Assert.Equal(40, text[10].GetDouble(), precision: 1);   // baseline origin x
        Assert.Equal(192, text[11].GetDouble(), precision: 1);  // 792-600: baseline y
    }

    [Fact]
    public void Stroke_op_carries_dash_and_line_style()
    {
        var result = Render("0 0 1 RG 4 w 1 J [6 3] 0 d 40 100 m 200 100 l S");
        var ops = Ops(result);

        var stroke = ops.Single(o => Code(o) == "s");
        Assert.Equal("rgb(0,0,255)", stroke[2].GetString());
        Assert.Equal(4, stroke[3].GetDouble(), precision: 1);
        Assert.Equal(1, stroke[4].GetInt32());                        // round cap
        Assert.Equal(new[] { 6.0, 3.0 }, stroke[7].EnumerateArray().Select(e => e.GetDouble()));
    }

    [Fact]
    public void Clip_groups_emit_balanced_save_restore()
    {
        var result = Render("q 40 100 100 100 re W n 0 0 0 rg 0 0 612 792 re f Q 1 0 0 rg 10 10 5 5 re f");
        var ops = Ops(result);

        int saves = ops.Count(o => Code(o) == "g");
        int restores = ops.Count(o => Code(o) == "G");
        Assert.Equal(1, saves);
        Assert.Equal(saves, restores);
        // The clip precedes the clipped fill, and the restore precedes the second fill.
        var codes = ops.Select(Code).ToList();
        Assert.True(codes.IndexOf("g") < codes.IndexOf("f"));
        Assert.True(codes.IndexOf("G") < codes.LastIndexOf("f"));
    }

    [Fact]
    public void Axial_shading_emits_gradient_op_with_stops()
    {
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
                "/Resources << /Shading << /Sh0 4 0 R >> >> /Contents 5 0 R >>",
            "<< /ShadingType 2 /ColorSpace /DeviceRGB /Coords [0 0 612 0] " +
                "/Function << /FunctionType 2 /Domain [0 1] /C0 [1 0 0] /C1 [0 0 1] /N 1 >> " +
                "/Extend [true true] >>",
            TestPdf.Stream("q 100 100 200 100 re W n /Sh0 sh Q"),
        };
        var doc = BlazorPdfDocument.Load(TestPdf.Build(bodies, rootObjNum: 1));
        var result = new BlazorPdfCanvasRenderer(doc.Pages[0], doc.XRef).Render();
        var ops = Ops(result);

        var sh = ops.Single(o => Code(o) == "sh");
        Assert.Equal(2, sh[1].GetInt32());                       // axial -> linear gradient
        Assert.Equal(4, sh[2].GetArrayLength());                 // x0,y0,x1,y1
        Assert.True(sh[3].GetArrayLength() >= 2);                // sampled color stops
        Assert.Equal("rgb(255,0,0)", sh[3][0][1].GetString());   // C0 red at the start
        // The gradient paints inside the clip established before it.
        var codes = ops.Select(Code).ToList();
        Assert.True(codes.IndexOf("g") < codes.IndexOf("sh"));
    }

    [Fact]
    public void Html_mode_is_unaffected()
    {
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
                "/Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            TestPdf.Stream("0.9 0.2 0.2 rg 40 700 100 50 re f"),
        };
        var doc = BlazorPdfDocument.Load(TestPdf.Build(bodies, rootObjNum: 1));
        var renderer = new BlazorPdfHtmlRenderer(doc.Pages[0], doc.XRef);
        string html = renderer.Render();

        Assert.Null(renderer.CanvasOpsJson);            // no display list by default
        Assert.DoesNotContain("data-bp-canvas", html);  // no canvas placeholder
        Assert.Contains("background:rgb(230,51,51)", html); // painted DOM intact
    }
}
