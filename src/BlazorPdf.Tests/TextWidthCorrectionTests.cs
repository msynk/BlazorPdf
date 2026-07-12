using System.Text.RegularExpressions;

namespace BlazorPdf;

/// <summary>
/// The text layer emits each run's PDF-computed advance (data-w) plus a
/// scaleX(--bp-sx) hook so the viewer can correct substitute-font spacing so runs
/// occupy exactly their PDF advance (the pdf.js text-layer technique).
/// </summary>
public class TextWidthCorrectionTests
{
    private static string Render(string content)
    {
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 400] " +
                "/Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            TestPdf.Stream(content),
        };
        var doc = BlazorPdfDocument.Load(TestPdf.Build(bodies, rootObjNum: 1));
        return new BlazorPdfHtmlRenderer(doc.Pages[0], doc.XRef).Render();
    }

    [Fact]
    public void Emits_target_advance_and_scale_hook()
    {
        string html = Render("BT /F1 24 Tf 50 200 Td (Hello) Tj ET");

        Assert.Contains("data-w=\"", html);
        Assert.Contains("scaleX(var(--bp-sx,1))", html);
    }

    [Fact]
    public void Target_advance_scales_linearly_with_font_size()
    {
        // Same text and CTM, font size doubled: the target advance must double.
        string html = Render(
            "BT /F1 24 Tf 50 200 Td (Hello) Tj ET " +
            "BT /F1 48 Tf 50 100 Td (Hello) Tj ET");

        // Both the painted spans and the coalesced selection spans carry data-w; the
        // two distinct target advances (24pt and 48pt "Hello") should differ by 2x.
        var widths = Regex.Matches(html, "data-w=\"([0-9.]+)\"")
            .Select(m => double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture))
            .OrderBy(w => w)
            .ToList();

        Assert.NotEmpty(widths);
        double w24 = widths.First();
        double w48 = widths.Last();
        Assert.True(w24 > 0 && w48 > 0);
        Assert.Equal(2.0, w48 / w24, precision: 2); // 48pt run is twice as wide as 24pt
    }
}
