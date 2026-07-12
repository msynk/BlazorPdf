using System.Text.RegularExpressions;
using BlazorPdf.Core;
using BlazorPdf.Core.Render;

namespace BlazorPdf.Tests;

/// <summary>
/// Selection is handled by a separate, coalesced text layer (the pdf.js model):
/// the painted glyph spans stay per-run for fidelity, but a transparent selection
/// layer on top merges adjacent runs into one span per visual line so double-click
/// words, triple-click lines, click-drag and copy behave like normal text instead
/// of fragmenting per glyph. Only that layer (data-bp-sel) is selectable/searchable.
/// </summary>
public class TextLayerSelectionTests
{
    private static string Render(string content, string fontObj =
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>")
    {
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
                "/Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
            fontObj,
            TestPdf.Stream(content),
        };
        var doc = PdfDocument.Load(TestPdf.Build(bodies, rootObjNum: 1));
        return new HtmlRenderer(doc.Pages[0], doc.XRef).Render();
    }

    [Fact]
    public void Emits_a_coalesced_selection_layer_on_top()
    {
        string html = Render("BT /F1 12 Tf 1 0 0 1 40 700 Tm (Hello) Tj ET");

        Assert.Contains("class=\"bp-text-layer\"", html);
        // The selection layer is the only selectable/searchable text.
        Assert.Contains("data-bp-sel", html);
        // Its spans are transparent and positioned with real geometry + scaleX.
        Assert.Matches(new Regex("data-bp-sel[^>]*color:transparent"), html);
        // The container collapses its flow-level <br> separators (font-size:0) so
        // multi-line selection doesn't paint stray highlights at the page's edge.
        Assert.Matches(new Regex("bp-text-layer[^>]*font-size:0"), html);
    }

    [Fact]
    public void Painted_spans_are_not_selectable_or_searchable()
    {
        string html = Render("BT /F1 12 Tf 1 0 0 1 40 700 Tm (Hello) Tj ET");

        // The painted layer opts out of selection and hit-testing.
        Assert.Contains("user-select:none", html);
        // Only the selection layer carries the searchable marker; the painted span
        // (with the visible fill colour) is not marked selectable.
        Assert.Matches(new Regex("color:rgb\\(0,0,0\\)[^>]*user-select:none"), html);
    }

    [Fact]
    public void Coalesces_runs_on_a_line_and_breaks_between_lines()
    {
        // Two runs share a baseline; a third drops one line below. Per-glyph PDFs
        // emit runs exactly like this (one show-text op each).
        string html = Render(
            "BT /F1 12 Tf " +
            "1 0 0 1 40 700 Tm (alpha) Tj " +
            "1 0 0 1 120 700 Tm (beta) Tj " +
            "1 0 0 1 40 680 Tm (gamma) Tj ET");

        // The two same-line runs merge into one selection span (gap bridged by a
        // space); the new line becomes its own span, separated by a <br> for copy.
        Assert.Contains(">alpha beta<", html);
        Assert.Contains(">gamma<", html);
        Assert.Equal(2, Regex.Matches(html, "data-bp-sel").Count);
        Assert.Single(Regex.Matches(html, "<br>"));
    }

    [Fact]
    public void Invisible_text_has_no_painted_layer_only_a_selection_span()
    {
        // Render mode 3 = invisible (OCR layer). No glyph layer, no embedded font.
        string html = Render("BT /F1 12 Tf 3 Tr 40 700 Td (scanned text) Tj ET");

        Assert.Single(Regex.Matches(html, "<span"));   // selection span only
        Assert.DoesNotContain("data-bp-glyph", html);  // nothing painted
        Assert.Contains("data-bp-sel", html);
    }
}
