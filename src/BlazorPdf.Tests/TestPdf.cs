using System.Text;
using BlazorPdf.Core;

namespace BlazorPdf.Tests;

/// <summary>
/// Assembles minimal but spec-valid PDF byte streams (header, indirect objects,
/// a classic cross-reference table and trailer) for use as test fixtures.
/// </summary>
internal static class TestPdf
{
    /// <summary>
    /// Builds a PDF from the given object bodies. Body <c>i</c> becomes object
    /// number <c>i + 1</c>; the trailer's <c>/Root</c> points to
    /// <paramref name="rootObjNum"/>.
    /// </summary>
    public static byte[] Build(IReadOnlyList<string> bodies, int rootObjNum, string trailerExtra = "")
    {
        using var ms = new MemoryStream();

        void Write(string s)
        {
            byte[] b = Encoding.Latin1.GetBytes(s);
            ms.Write(b, 0, b.Length);
        }

        Write("%PDF-1.7\n");

        var offsets = new long[bodies.Count + 1];
        for (int i = 0; i < bodies.Count; i++)
        {
            offsets[i + 1] = ms.Position;
            Write($"{i + 1} 0 obj\n{bodies[i]}\nendobj\n");
        }

        long xrefPos = ms.Position;
        Write($"xref\n0 {bodies.Count + 1}\n");
        Write("0000000000 65535 f \n");                // free head, exactly 20 bytes
        for (int i = 1; i <= bodies.Count; i++)
        {
            Write($"{offsets[i]:D10} 00000 n \n");     // in-use entry, exactly 20 bytes
        }

        Write($"trailer\n<< /Size {bodies.Count + 1} /Root {rootObjNum} 0 R{trailerExtra} >>\n");
        Write($"startxref\n{xrefPos}\n%%EOF");

        return ms.ToArray();
    }

    /// <summary>Wraps raw content into a stream object body with a correct /Length.</summary>
    public static string Stream(string content, string extraDictEntries = "")
    {
        int length = Encoding.Latin1.GetByteCount(content);
        return $"<< /Length {length}{extraDictEntries} >>\nstream\n{content}\nendstream";
    }

    /// <summary>
    /// A complete single-page document: 200x200 page, one Helvetica font, a
    /// content stream that shows "Hello", and a one-entry outline pointing at the
    /// page. Exercises parsing, page-tree traversal, rendering and bookmarks.
    /// </summary>
    public static byte[] HelloWorld()
    {
        string content = "BT /F1 24 Tf 50 100 Td (Hello) Tj ET";
        var bodies = new List<string>
        {
            // 1: Catalog
            "<< /Type /Catalog /Pages 2 0 R /Outlines 5 0 R >>",
            // 2: Pages
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            // 3: Page
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] " +
                "/Resources << /Font << /F1 4 0 R >> >> /Contents 6 0 R >>",
            // 4: Font
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            // 5: Outlines root
            "<< /Type /Outlines /First 7 0 R /Last 7 0 R /Count 1 >>",
            // 6: Contents
            Stream(content),
            // 7: Outline item -> page 3
            "<< /Title (Chapter 1) /Parent 5 0 R /Dest [3 0 R /XYZ 0 200 0] >>",
        };
        return Build(bodies, rootObjNum: 1);
    }
}

/// <summary>An <see cref="IXRef"/> for object graphs built in-memory (no indirect refs).</summary>
internal sealed class InlineXRef : IXRef
{
    public object? Fetch(Ref reference, bool suppressEncryption = false) => null;
    public object? FetchIfRef(object? value, bool suppressEncryption = false)
        => value is Ref ? null : value;
}
