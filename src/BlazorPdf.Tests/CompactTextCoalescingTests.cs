using System.Text.RegularExpressions;

namespace BlazorPdf;

/// <summary>
/// Compact text coalescing merges same-line, same-style substitute-font painted
/// runs into one span per visual line (width-corrected to the line's total PDF
/// advance), while breaking on style changes, large gaps, and new baselines so
/// glyphs never paint far from their true positions. Exact (the default) keeps
/// every run as its own positioned span.
/// </summary>
public class CompactTextCoalescingTests
{
    // One TJ array per line, every character its own element with kerning between
    // (the real-world layout that fragments into one span per character).
    private const string PerGlyphTwoLines =
        "BT /F1 12 Tf " +
        "1 0 0 1 40 700 Tm [(H) -10 (e) -10 (l) -10 (l) -10 (o)] TJ " +
        "1 0 0 1 40 680 Tm [(w) -10 (o) -10 (r) -10 (l) -10 (d)] TJ ET";

    private static string Render(string content, BlazorPdfTextCoalescing coalescing)
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
        return new BlazorPdfHtmlRenderer(doc.Pages[0], doc.XRef) { TextCoalescing = coalescing }.Render();
    }

    /// <summary>Painted spans are those without the selection marker.</summary>
    private static int PaintedSpans(string html) =>
        Regex.Matches(html, "<span").Count - Regex.Matches(html, "data-bp-sel").Count;

    [Fact]
    public void Exact_keeps_one_painted_span_per_run()
    {
        string html = Render(PerGlyphTwoLines, BlazorPdfTextCoalescing.Exact);

        Assert.Equal(10, PaintedSpans(html)); // 10 characters -> 10 painted spans
    }

    [Fact]
    public void Compact_merges_each_line_into_one_painted_span()
    {
        string html = Render(PerGlyphTwoLines, BlazorPdfTextCoalescing.Compact);

        Assert.Equal(2, PaintedSpans(html)); // one per visual line
        Assert.Contains(">Hello<", html);
        Assert.Contains(">world<", html);
        // The coalesced span carries the line's total advance for width correction.
        Assert.Matches(new Regex("user-select:none[^>]*\">Hello<"), html);
    }

    [Fact]
    public void Compact_breaks_the_span_on_a_style_change()
    {
        // Same baseline, colour changes mid-line: must not merge across it.
        string html = Render(
            "BT /F1 12 Tf 1 0 0 1 40 700 Tm (red) Tj " +
            "1 0 0 0.5 rg 1 0 0 1 70 700 Tm (blue) Tj ET",
            BlazorPdfTextCoalescing.Compact);

        Assert.Equal(2, PaintedSpans(html));
        Assert.Contains(">red<", html);
        Assert.Contains(">blue<", html);
    }

    [Fact]
    public void Compact_breaks_the_span_on_a_large_gap()
    {
        // Two runs on one baseline separated by ~15em (table cells): bridging with
        // a space would paint the second cell in the wrong place, so it must break.
        string html = Render(
            "BT /F1 12 Tf 1 0 0 1 40 700 Tm (cell1) Tj " +
            "1 0 0 1 300 700 Tm (cell2) Tj ET",
            BlazorPdfTextCoalescing.Compact);

        Assert.Equal(2, PaintedSpans(html));
        Assert.Contains(">cell1<", html);
        Assert.Contains("left:300px", html); // second cell keeps its true position
    }

    [Fact]
    public void Compact_bridges_word_gaps_with_a_space()
    {
        // Word-sized gap (~0.35em at 12pt): merge with a space, like a text line.
        string html = Render(
            "BT /F1 12 Tf 1 0 0 1 40 700 Tm (foo) Tj " +
            "1 0 0 1 62 700 Tm (bar) Tj ET",
            BlazorPdfTextCoalescing.Compact);

        Assert.Equal(1, PaintedSpans(html));
        Assert.Contains(">foo bar<", html);
    }

    [Fact]
    public void Compact_flushes_pending_text_before_later_graphics()
    {
        // Text, then a filled rect: the coalesced span must be emitted BEFORE the
        // rect in the HTML so paint order (z-order) is preserved.
        string html = Render(
            "BT /F1 12 Tf 1 0 0 1 40 700 Tm (under) Tj ET " +
            "0 0 0 rg 30 690 100 20 re f",
            BlazorPdfTextCoalescing.Compact);

        int textPos = html.IndexOf(">under<", StringComparison.Ordinal);
        int rectPos = html.IndexOf("clip-path", StringComparison.Ordinal);
        Assert.True(textPos >= 0 && rectPos >= 0 && textPos < rectPos,
            $"expected painted text (at {textPos}) before rect (at {rectPos})");
    }
}
