using BlazorPdf.Engine;
using BlazorPdf.Engine.Rendering;

namespace BlazorPdf.Tests;

/// <summary>
/// Robustness tests: malformed, random and truncated input must fail gracefully
/// (PdfParseException or an empty document) and never throw unexpected exceptions,
/// and rendering must tolerate partial/odd documents.
/// </summary>
public class HardeningTests
{
    [Fact]
    public void Random_bytes_never_throw_unexpectedly()
    {
        var rng = new Random(1234);
        for (var iter = 0; iter < 300; iter++)
        {
            var len = rng.Next(0, 2048);
            var bytes = new byte[len];
            rng.NextBytes(bytes);

            try
            {
                var doc = PdfDocument.Load(bytes);
                RenderAll(doc);
            }
            catch (PdfParseException)
            {
                // Acceptable: not a PDF.
            }
        }
    }

    [Fact]
    public void Random_bytes_with_pdf_header_never_throw_unexpectedly()
    {
        var rng = new Random(99);
        var header = "%PDF-1.5\n"u8.ToArray();
        for (var iter = 0; iter < 200; iter++)
        {
            var body = new byte[rng.Next(0, 1500)];
            rng.NextBytes(body);
            var bytes = header.Concat(body).ToArray();

            try
            {
                var doc = PdfDocument.Load(bytes);
                RenderAll(doc);
            }
            catch (PdfParseException)
            {
            }
        }
    }

    [Fact]
    public void Truncated_valid_pdf_never_throws()
    {
        var full = PdfBuilder.Build(3, compress: true);
        for (var cut = full.Length; cut > 0; cut -= 17)
        {
            var slice = new byte[cut];
            Array.Copy(full, slice, cut);

            try
            {
                var doc = PdfDocument.Load(slice);
                RenderAll(doc);
                _ = doc.ExtractText();
            }
            catch (PdfParseException)
            {
            }
        }
    }

    [Fact]
    public void Renders_a_feature_combined_torture_document()
    {
        // 2x2 RGB image (red, green / blue, white).
        byte[] rgb = [255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 255];

        var content =
            "0.1 0.1 0.1 0.7 k 0 0 200 100 re f " +              // CMYK background
            "q 20 20 160 60 re W n /Sh0 sh Q " +                 // clipped axial shading
            "q 40 0 0 30 80 50 cm /Im0 Do Q " +                 // image XObject
            "q 1 0 0 1 10 10 cm /Fm0 Do Q";                      // form XObject
        var contentBytes = System.Text.Encoding.ASCII.GetBytes(content);

        var formContent = System.Text.Encoding.ASCII.GetBytes("1 0 0 rg 0 0 40 40 re f");

        var objects = new List<(string, byte[]?)>
        {
            ("<< /Type /Catalog /Pages 2 0 R >>", null),
            ("<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            ("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] /Rotate 90 /Contents 4 0 R " +
             "/Resources << /XObject << /Fm0 5 0 R /Im0 6 0 R >> /Shading << /Sh0 7 0 R >> >> >>", null),
            ($"<< /Length {contentBytes.Length} >>", contentBytes),
            ($"<< /Type /XObject /Subtype /Form /BBox [0 0 50 50] /Matrix [1 0 0 1 0 0] /Length {formContent.Length} >>", formContent),
            ("<< /Type /XObject /Subtype /Image /Width 2 /Height 2 /BitsPerComponent 8 /ColorSpace /DeviceRGB /Length 12 >>", rgb),
            ("<< /ShadingType 2 /ColorSpace /DeviceRGB /Coords [20 0 180 0] " +
             "/Function << /FunctionType 2 /Domain [0 1] /C0 [1 1 0] /C1 [0 1 1] /N 1 >> /Extend [true true] >>", null),
        };

        var doc = PdfDocument.Load(PdfBuilder.BuildObjects(objects));
        Assert.Equal(1, doc.PageCount);

        var image = PdfRenderer.Render(doc.Pages[0], scale: 2.0);

        // Page is 200x100 rotated 90 degrees -> output is 200 tall, 400 wide.
        Assert.Equal(200, image.Width);
        Assert.Equal(400, image.Height);

        // Some content was painted (not entirely one flat color).
        var distinct = new HashSet<int>();
        for (var i = 0; i < image.Pixels.Length; i += 4)
        {
            distinct.Add((image.Pixels[i] << 16) | (image.Pixels[i + 1] << 8) | image.Pixels[i + 2]);
            if (distinct.Count > 5) break;
        }
        Assert.True(distinct.Count > 3, "expected varied content from the combined features");
    }

    private static void RenderAll(PdfDocument doc)
    {
        foreach (var page in doc.Pages)
        {
            _ = PdfRenderer.Render(page, scale: 0.5);
        }
    }
}
