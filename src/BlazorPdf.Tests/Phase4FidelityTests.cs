
namespace BlazorPdf;

/// <summary>Phase 4A rendering-fidelity regressions.</summary>
public class Phase4FidelityTests
{
    // 4.7 — content inside a BDC /OC section whose OCG is OFF must not render.
    [Fact]
    public void Optional_content_group_off_is_hidden()
    {
        string content =
            "BT /F1 24 Tf 50 100 Td (VisibleText) Tj ET " +
            "/OC /P0 BDC BT /F1 24 Tf 50 50 Td (HiddenText) Tj ET EMC";
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /OCProperties << /D << /OFF [5 0 R] >> /OCGs [5 0 R] >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] " +
                "/Resources << /Font << /F1 4 0 R >> /Properties << /P0 5 0 R >> >> /Contents 6 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            "<< /Type /OCG /Name (Layer1) >>",
            TestPdf.Stream(content),
        };
        var doc = BlazorPdfDocument.Load(TestPdf.Build(bodies, rootObjNum: 1));
        string html = new BlazorPdfHtmlRenderer(doc.Pages[0], doc.XRef).Render();

        Assert.Contains("VisibleText", html);
        Assert.DoesNotContain("HiddenText", html);
    }

    // Sanity: when the OCG is ON (not in /OFF), the content renders.
    [Fact]
    public void Optional_content_group_on_is_visible()
    {
        string content =
            "/OC /P0 BDC BT /F1 24 Tf 50 50 Td (ShownText) Tj ET EMC";
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /OCProperties << /D << /OFF [] >> /OCGs [5 0 R] >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] " +
                "/Resources << /Font << /F1 4 0 R >> /Properties << /P0 5 0 R >> >> /Contents 6 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            "<< /Type /OCG /Name (Layer1) >>",
            TestPdf.Stream(content),
        };
        var doc = BlazorPdfDocument.Load(TestPdf.Build(bodies, rootObjNum: 1));
        string html = new BlazorPdfHtmlRenderer(doc.Pages[0], doc.XRef).Render();

        Assert.Contains("ShownText", html);
    }

    // 4.8 — a Link annotation with an internal GoTo destination emits a
    // data-bp-page hotspot the viewer can navigate.
    [Fact]
    public void Internal_link_emits_page_hotspot()
    {
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Annots [5 0 R] >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>",
            "<< /Type /Annot /Subtype /Link /Rect [10 10 100 30] /Dest [4 0 R /Fit] >>",
        };
        var doc = BlazorPdfDocument.Load(TestPdf.Build(bodies, rootObjNum: 1));
        var renderer = new BlazorPdfHtmlRenderer(doc.Pages[0], doc.XRef)
        {
            DestinationResolver = d => doc.ResolveDestinationPage(d),
        };
        string html = renderer.Render();

        Assert.Contains("data-bp-page=\"2\"", html);
    }
}
