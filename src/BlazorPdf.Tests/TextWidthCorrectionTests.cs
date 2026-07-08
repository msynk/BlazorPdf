using System.Text.RegularExpressions;
using BlazorPdf.Core;
using BlazorPdf.Core.Render;

namespace BlazorPdf.Tests;

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
        var doc = PdfDocument.Load(TestPdf.Build(bodies, rootObjNum: 1));
        return new HtmlRenderer(doc.Pages[0], doc.XRef).Render();
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

        var matches = Regex.Matches(html, "data-w=\"([0-9.]+)\"");
        Assert.Equal(2, matches.Count);
        double w24 = double.Parse(matches[0].Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        double w48 = double.Parse(matches[1].Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);

        Assert.True(w24 > 0 && w48 > 0);
        Assert.Equal(2.0, w48 / w24, precision: 2); // 48pt run is twice as wide as 24pt
    }
}
