using System.IO.Compression;
using System.Text;

namespace BlazorPdf.Tests;

/// <summary>
/// Builds small, valid PDFs in memory for testing the engine, with optional
/// FlateDecode compression of content streams and a correct xref table.
/// </summary>
public static class PdfBuilder
{
    public static byte[] Build(int pages, bool compress)
    {
        var objects = new List<(string head, byte[]? stream)>();

        var kids = new StringBuilder();
        for (var i = 0; i < pages; i++)
        {
            kids.Append($"{4 + i * 2} 0 R ");
        }

        objects.Add(("<< /Type /Catalog /Pages 2 0 R >>", null));
        objects.Add(($"<< /Type /Pages /Kids [{kids.ToString().Trim()}] /Count {pages} >>", null));
        objects.Add(("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>", null));

        for (var i = 0; i < pages; i++)
        {
            var contentObj = 5 + i * 2;
            objects.Add((
                $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
                $"/Contents {contentObj} 0 R /Resources << /Font << /F1 3 0 R >> >> >>", null));

            var text =
                $"BT /F1 24 Tf 72 720 Td (BlazorPdf engine test) Tj ET\n" +
                $"BT /F1 14 Tf 72 690 Td (Page {i + 1} of {pages}) Tj ET";
            var contentBytes = Encoding.ASCII.GetBytes(text);

            if (compress)
            {
                var compressed = Deflate(contentBytes);
                var head = $"<< /Length {compressed.Length} /Filter /FlateDecode >>";
                objects.Add((head, compressed));
            }
            else
            {
                var head = $"<< /Length {contentBytes.Length} >>";
                objects.Add((head, contentBytes));
            }
        }

        return Assemble(objects);
    }

    private static byte[] Assemble(List<(string head, byte[]? stream)> objects)
    {
        using var ms = new MemoryStream();
        void WriteAscii(string s) => ms.Write(Encoding.ASCII.GetBytes(s));

        WriteAscii("%PDF-1.5\n");

        var offsets = new int[objects.Count];
        for (var i = 0; i < objects.Count; i++)
        {
            offsets[i] = (int)ms.Position;
            WriteAscii($"{i + 1} 0 obj\n{objects[i].head}\n");
            if (objects[i].stream is { } stream)
            {
                WriteAscii("stream\n");
                ms.Write(stream);
                WriteAscii("\nendstream\n");
            }
            WriteAscii("endobj\n");
        }

        var xref = (int)ms.Position;
        WriteAscii("xref\n");
        WriteAscii($"0 {objects.Count + 1}\n");
        WriteAscii("0000000000 65535 f \n");
        foreach (var off in offsets)
        {
            WriteAscii($"{off:0000000000} 00000 n \n");
        }
        WriteAscii("trailer\n");
        WriteAscii($"<< /Size {objects.Count + 1} /Root 1 0 R >>\n");
        WriteAscii("startxref\n");
        WriteAscii($"{xref}\n");
        WriteAscii("%%EOF");

        return ms.ToArray();
    }

    /// <summary>Builds a single-page PDF (no fonts) with the given raw content stream.</summary>
    public static byte[] BuildContent(string content, int width = 612, int height = 792)
    {
        var bytes = Encoding.ASCII.GetBytes(content);
        var objects = new List<(string head, byte[]? stream)>
        {
            ("<< /Type /Catalog /Pages 2 0 R >>", null),
            ("<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            ($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {width} {height}] /Contents 4 0 R >>", null),
            ($"<< /Length {bytes.Length} >>", bytes),
        };
        return Assemble(objects);
    }

    /// <summary>Builds a single-page PDF embedding the given TrueType font as FontFile2.</summary>
    public static byte[] BuildTrueTypeDoc(byte[] ttf, string text = "A", int fontSize = 600)
    {
        var content = $"BT /F1 {fontSize} Tf 50 100 Td ({text}) Tj ET";
        var contentBytes = Encoding.ASCII.GetBytes(content);

        var objects = new List<(string head, byte[]? stream)>
        {
            ("<< /Type /Catalog /Pages 2 0 R >>", null),
            ("<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            ("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
             "/Resources << /Font << /F1 5 0 R >> >> >>", null),
            ($"<< /Length {contentBytes.Length} >>", contentBytes),
            ("<< /Type /Font /Subtype /TrueType /BaseFont /TestFont /FirstChar 65 /LastChar 65 " +
             "/Widths [1000] /FontDescriptor 6 0 R >>", null),
            ("<< /Type /FontDescriptor /FontName /TestFont /Flags 32 /FontFile2 7 0 R >>", null),
            ($"<< /Length {ttf.Length} /Length1 {ttf.Length} >>", ttf),
        };

        return Assemble(objects);
    }

    /// <summary>Builds a single-page PDF that draws a raw RGB image XObject over the whole page.</summary>
    public static byte[] BuildImageDoc(int imgW, int imgH, byte[] rgb, int pageW = 100, int pageH = 100)
    {
        var content = $"q {pageW} 0 0 {pageH} 0 0 cm /Im0 Do Q";
        var contentBytes = Encoding.ASCII.GetBytes(content);

        var objects = new List<(string head, byte[]? stream)>
        {
            ("<< /Type /Catalog /Pages 2 0 R >>", null),
            ("<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            ($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {pageW} {pageH}] /Contents 4 0 R " +
             "/Resources << /XObject << /Im0 5 0 R >> >> >>", null),
            ($"<< /Length {contentBytes.Length} >>", contentBytes),
            ($"<< /Type /XObject /Subtype /Image /Width {imgW} /Height {imgH} " +
             $"/BitsPerComponent 8 /ColorSpace /DeviceRGB /Length {rgb.Length} >>", rgb),
        };

        return Assemble(objects);
    }

    /// <summary>Assembles a custom list of object bodies (and optional streams) into a PDF.</summary>
    public static byte[] BuildObjects(List<(string head, byte[]? stream)> objects) => Assemble(objects);

    /// <summary>Builds a single-page PDF drawing a JPEG (DCTDecode) image over the whole page.</summary>
    public static byte[] BuildJpegDoc(byte[] jpeg, int imgW, int imgH, int pageW = 64, int pageH = 64)
    {
        var content = $"q {pageW} 0 0 {pageH} 0 0 cm /Im0 Do Q";
        var contentBytes = Encoding.ASCII.GetBytes(content);

        var objects = new List<(string head, byte[]? stream)>
        {
            ("<< /Type /Catalog /Pages 2 0 R >>", null),
            ("<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            ($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {pageW} {pageH}] /Contents 4 0 R " +
             "/Resources << /XObject << /Im0 5 0 R >> >> >>", null),
            ($"<< /Length {contentBytes.Length} >>", contentBytes),
            ($"<< /Type /XObject /Subtype /Image /Width {imgW} /Height {imgH} " +
             $"/BitsPerComponent 8 /ColorSpace /DeviceRGB /Filter /DCTDecode /Length {jpeg.Length} >>", jpeg),
        };

        return Assemble(objects);
    }

    /// <summary>Builds a single-page PDF embedding the given CFF font as FontFile3 (Type1C).</summary>
    public static byte[] BuildCffDoc(byte[] cff, string text = "A", int fontSize = 600)
    {
        var content = $"BT /F1 {fontSize} Tf 50 100 Td ({text}) Tj ET";
        var contentBytes = Encoding.ASCII.GetBytes(content);

        var objects = new List<(string head, byte[]? stream)>
        {
            ("<< /Type /Catalog /Pages 2 0 R >>", null),
            ("<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            ("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
             "/Resources << /Font << /F1 5 0 R >> >> >>", null),
            ($"<< /Length {contentBytes.Length} >>", contentBytes),
            ("<< /Type /Font /Subtype /Type1 /BaseFont /TestCff /FirstChar 65 /LastChar 65 " +
             "/Widths [1000] /FontDescriptor 6 0 R >>", null),
            ("<< /Type /FontDescriptor /FontName /TestCff /Flags 4 /FontFile3 7 0 R >>", null),
            ($"<< /Length {cff.Length} /Subtype /Type1C >>", cff),
        };

        return Assemble(objects);
    }

    /// <summary>Builds a page that paints an axial (red→blue) shading via the sh operator.</summary>
    public static byte[] BuildShadingDoc(int w = 100, int h = 100)
    {
        var content = $"q 0 0 {w} {h} re W n /Sh0 sh Q";
        var contentBytes = Encoding.ASCII.GetBytes(content);

        var objects = new List<(string head, byte[]? stream)>
        {
            ("<< /Type /Catalog /Pages 2 0 R >>", null),
            ("<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            ($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {w} {h}] /Contents 4 0 R " +
             "/Resources << /Shading << /Sh0 5 0 R >> >> >>", null),
            ($"<< /Length {contentBytes.Length} >>", contentBytes),
            ($"<< /ShadingType 2 /ColorSpace /DeviceRGB /Coords [0 0 {w} 0] " +
             "/Function << /FunctionType 2 /Domain [0 1] /C0 [1 0 0] /C1 [0 0 1] /N 1 >> " +
             "/Extend [true true] >>", null),
        };

        return Assemble(objects);
    }

    /// <summary>Builds a page that fills a rectangle with a red→green axial shading pattern.</summary>
    public static byte[] BuildShadingPatternDoc(int w = 100, int h = 100)
    {
        var content = "/Pattern cs /P0 scn 20 20 60 60 re f";
        var contentBytes = Encoding.ASCII.GetBytes(content);

        var objects = new List<(string head, byte[]? stream)>
        {
            ("<< /Type /Catalog /Pages 2 0 R >>", null),
            ("<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            ($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {w} {h}] /Contents 4 0 R " +
             "/Resources << /Pattern << /P0 5 0 R >> >> >>", null),
            ($"<< /Length {contentBytes.Length} >>", contentBytes),
            ("<< /Type /Pattern /PatternType 2 /Matrix [1 0 0 1 0 0] " +
             "/Shading << /ShadingType 2 /ColorSpace /DeviceRGB /Coords [20 0 80 0] " +
             "/Function << /FunctionType 2 /Domain [0 1] /C0 [1 0 0] /C1 [0 1 0] /N 1 >> " +
             "/Extend [true true] >> >>", null),
        };

        return Assemble(objects);
    }

    private static byte[] Deflate(byte[] data)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(data);
        }
        return output.ToArray();
    }
}
