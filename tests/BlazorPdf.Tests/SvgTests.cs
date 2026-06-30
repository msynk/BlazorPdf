using BlazorPdf.Engine;
using BlazorPdf.Engine.Svg;

namespace BlazorPdf.Tests;

public class SvgTests
{
    [Fact]
    public void Emits_svg_root_with_viewbox()
    {
        var doc = PdfDocument.Load(PdfBuilder.BuildContent("", width: 200, height: 300));
        var svg = SvgRenderer.Render(doc.Pages[0]);

        Assert.StartsWith("<svg", svg);
        Assert.Contains("viewBox=\"0 0 200 300\"", svg);
        Assert.EndsWith("</svg>", svg);
    }

    [Fact]
    public void Emits_filled_path_for_vector_graphics()
    {
        var doc = PdfDocument.Load(PdfBuilder.BuildContent("0 0 1 rg 50 50 100 100 re f"));
        var svg = SvgRenderer.Render(doc.Pages[0]);

        Assert.Contains("<path", svg);
        Assert.Contains("fill=\"#0000ff\"", svg);
    }

    [Fact]
    public void Emits_selectable_text_for_simple_fonts()
    {
        var doc = PdfDocument.Load(PdfBuilder.Build(1, compress: false));
        var svg = SvgRenderer.Render(doc.Pages[0]);

        Assert.Contains("<text", svg);
        Assert.Contains("font-family=\"sans-serif\"", svg); // Helvetica -> sans-serif
        Assert.Contains(">B<", svg); // first glyph of "BlazorPdf engine test"
    }

    [Fact]
    public void Emits_clippath_for_clipping()
    {
        var doc = PdfDocument.Load(PdfBuilder.BuildContent("100 100 200 200 re W n 0 0 1 rg 0 0 612 792 re f"));
        var svg = SvgRenderer.Render(doc.Pages[0]);

        Assert.Contains("<clipPath", svg);
        Assert.Contains("clip-path=\"url(#", svg);
    }

    [Fact]
    public void Emits_linear_gradient_for_axial_shading()
    {
        var doc = PdfDocument.Load(PdfBuilder.BuildShadingDoc(100, 100));
        var svg = SvgRenderer.Render(doc.Pages[0]);

        Assert.Contains("<linearGradient", svg);
        Assert.Contains("<stop", svg);
        Assert.Contains("url(#g", svg);
    }

    [Fact]
    public void Embeds_image_as_png_data_uri()
    {
        byte[] rgb = [255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 255];
        var doc = PdfDocument.Load(PdfBuilder.BuildImageDoc(2, 2, rgb, 100, 100));
        var svg = SvgRenderer.Render(doc.Pages[0]);

        Assert.Contains("<image", svg);
        Assert.Contains("href=\"data:image/png;base64,", svg);
        Assert.DoesNotContain("xlink:href=\"data:", svg);
    }

    [Fact]
    public void Embeds_jpeg_directly_as_jpeg_data_uri()
    {
        // DCTDecode images are embedded raw so the browser decodes them (robust).
        var jpeg = TestJpeg.BuildSolid(40, 120, 200);
        var doc = PdfDocument.Load(PdfBuilder.BuildJpegDoc(jpeg, 8, 8, 64, 64));
        var svg = SvgRenderer.Render(doc.Pages[0]);

        Assert.Contains("<image", svg);
        Assert.Contains("data:image/jpeg;base64,", svg);
        // The embedded payload is exactly the original JPEG bytes.
        Assert.Contains(Convert.ToBase64String(jpeg), svg);
    }

    [Fact]
    public void Accounts_for_page_rotation_in_viewbox()
    {
        var objects = new List<(string, byte[]?)>
        {
            ("<< /Type /Catalog /Pages 2 0 R >>", null),
            ("<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            ("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] /Rotate 90 >>", null),
        };
        var doc = PdfDocument.Load(PdfBuilder.BuildObjects(objects));
        var svg = SvgRenderer.Render(doc.Pages[0]);

        // 200x100 rotated 90 -> 100 wide, 200 tall.
        Assert.Contains("viewBox=\"0 0 100 200\"", svg);
    }
}
