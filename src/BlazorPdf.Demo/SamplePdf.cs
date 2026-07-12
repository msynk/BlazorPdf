using System.Text;

namespace BlazorPdf.Demo;

/// <summary>
/// Builds a small, valid multi-page PDF entirely in memory (with a correct xref table)
/// so the demo has something to show without shipping a binary file.
/// </summary>
public static class SamplePdf
{
    public static byte[] Build(int pages = 3, string title = "BlazorPdf")
    {
        if (pages < 1) pages = 1;

        // Object layout: 1=Catalog, 2=Pages, 3=Font, 4=Image, then per page: pageObj, contentObj.
        var objects = new List<string>();

        var kids = new StringBuilder();
        for (var i = 0; i < pages; i++)
        {
            var pageObj = 6 + (i * 2);
            kids.Append($"{pageObj} 0 R ");
        }

        objects.Add("<< /Type /Catalog /Pages 2 0 R >>");                                  // 1
        objects.Add($"<< /Type /Pages /Kids [{kids.ToString().Trim()}] /Count {pages} >>"); // 2
        objects.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");             // 3
        objects.Add(BuildGradientImage(96, 64));                                            // 4
        objects.Add(                                                                        // 5
            "<< /ShadingType 2 /ColorSpace /DeviceRGB /Coords [36 0 576 0] " +
            "/Function << /FunctionType 2 /Domain [0 1] /C0 [0.20 0.55 0.95] /C1 [0.95 0.45 0.20] /N 1 >> " +
            "/Extend [true true] >>");

        for (var i = 0; i < pages; i++)
        {
            var contentObj = 7 + (i * 2);
            objects.Add(
                $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
                $"/Contents {contentObj} 0 R /Resources << /Font << /F1 3 0 R >> " +
                $"/XObject << /Im0 4 0 R >> /Shading << /Sh0 5 0 R >> >> >>");

            var line1 = Escape($"{title} - sample document");
            var line2 = Escape($"Page {i + 1} of {pages}");
            var content =
                // Blue header bar.
                "0.10 0.45 0.82 rg\n0 740 612 52 re f\n" +
                // White title text on the bar.
                $"1 1 1 rg\nBT /F1 24 Tf 36 754 Td ({line1}) Tj ET\n" +
                // Dark body text.
                $"0.12 0.12 0.12 rg\nBT /F1 16 Tf 36 700 Td ({line2}) Tj ET\n" +
                "0.12 0.12 0.12 rg\nBT /F1 11 Tf 36 676 Td (Rendered entirely in C-sharp - no browser PDF engine) Tj ET\n" +
                // Filled shapes.
                "0.90 0.22 0.22 rg\n60 560 150 80 re f\n" +
                "0.20 0.70 0.35 rg\n240 560 150 80 re f\n" +
                "0.95 0.75 0.15 rg\n420 560 130 80 re f\n" +
                // Stroked border around the shapes.
                "0 0 0 RG\n2 w\n44 544 524 112 re S\n" +
                // Diagonal stroked line.
                "0.25 0.30 0.75 RG\n2 w\n60 510 m 552 470 l S\n" +
                // A Bezier curve.
                "0.85 0.40 0.10 RG\n4 w\n60 320 m 200 480 400 160 552 320 c S\n" +
                // An even-odd filled ring (outer square with inner hole).
                "0.55 0.30 0.75 rg\n80 120 160 160 re 120 160 80 80 re f*\n" +
                // An axial gradient bar painted via the sh operator (clipped).
                "q 320 120 250 60 re W n /Sh0 sh Q\n" +
                // A decoded RGB image XObject (gradient).
                "q 200 134 0 0 330 110 cm /Im0 Do Q\n" +
                // A kerned, per-glyph paragraph (every character its own TJ
                // element) - the layout many PDF generators emit. Lets the demo's
                // "Compact text spans" option show its effect: Exact renders one
                // DOM span per character here, Compact one span per line.
                "0.25 0.25 0.30 rg\nBT /F1 10 Tf " +
                PerGlyphLine(36, 96, "This paragraph is written one character per show-text run,") +
                PerGlyphLine(36, 82, "the way many PDF generators kern text. Toggle the Compact") +
                PerGlyphLine(36, 68, "text spans option to merge each line into a single element.") +
                "ET";

            objects.Add($"<< /Length {Encoding.ASCII.GetByteCount(content)} >>\nstream\n{content}\nendstream");
        }

        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");

        var offsets = new int[objects.Count];
        for (var i = 0; i < objects.Count; i++)
        {
            offsets[i] = sb.Length; // ASCII content: char count == byte count.
            sb.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        var xrefPos = sb.Length;
        sb.Append("xref\n");
        sb.Append($"0 {objects.Count + 1}\n");
        sb.Append("0000000000 65535 f \n");
        foreach (var off in offsets)
        {
            sb.Append($"{off:0000000000} 00000 n \n");
        }

        sb.Append("trailer\n");
        sb.Append($"<< /Size {objects.Count + 1} /Root 1 0 R >>\n");
        sb.Append("startxref\n");
        sb.Append($"{xrefPos}\n");
        sb.Append("%%EOF");

        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static string Escape(string text) =>
        text.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");

    /// <summary>
    /// Emits one text line as a TJ array with every character its own element and
    /// a small kerning adjustment between them (one show-text run per character).
    /// </summary>
    private static string PerGlyphLine(int x, int y, string text)
    {
        var sb = new StringBuilder();
        sb.Append($"1 0 0 1 {x} {y} Tm [");
        foreach (var ch in text)
        {
            sb.Append('(').Append(Escape(ch.ToString())).Append(") -4 ");
        }
        sb.Append("] TJ ");
        return sb.ToString();
    }

    /// <summary>
    /// Builds an RGB image XObject containing a gradient, encoded with ASCIIHexDecode
    /// so the binary samples fit the text-based PDF builder.
    /// </summary>
    private static string BuildGradientImage(int w, int h)
    {
        var hex = new StringBuilder(w * h * 6);
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var r = (byte)(x * 255 / (w - 1));
                var g = (byte)(y * 255 / (h - 1));
                var b = (byte)(255 - x * 255 / (w - 1));
                hex.Append(r.ToString("X2")).Append(g.ToString("X2")).Append(b.ToString("X2"));
            }
        }
        hex.Append('>');

        return $"<< /Type /XObject /Subtype /Image /Width {w} /Height {h} /BitsPerComponent 8 " +
               $"/ColorSpace /DeviceRGB /Filter /ASCIIHexDecode /Length {hex.Length} >>\n" +
               $"stream\n{hex}\nendstream";
    }
}
