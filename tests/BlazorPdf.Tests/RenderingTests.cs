using BlazorPdf.Engine;
using BlazorPdf.Engine.Rendering;

namespace BlazorPdf.Tests;

public class RenderingTests
{
    [Fact]
    public void Matrix_applies_and_composes()
    {
        var t = PdfMatrix.Translate(10, 20);
        Assert.Equal((15, 25), t.Apply(5, 5));

        var scaleThenTranslate = PdfMatrix.Scale(2, 3).Multiply(PdfMatrix.Translate(1, 1));
        var (x, y) = scaleThenTranslate.Apply(4, 4);
        Assert.Equal(9, x, 6);   // 4*2 + 1
        Assert.Equal(13, y, 6);  // 4*3 + 1
    }

    [Fact]
    public void Fills_rectangle_with_solid_color()
    {
        var raster = new Raster(100, 100);
        raster.Clear(PdfColor.White);

        var rect = new (double, double)[] { (20, 20), (80, 20), (80, 80), (20, 80) };
        raster.FillPolygons([rect], new PdfColor(255, 0, 0), evenOdd: false);

        var center = raster.GetPixel(50, 50);
        Assert.Equal(255, center.R);
        Assert.Equal(0, center.G);
        Assert.Equal(0, center.B);

        var outside = raster.GetPixel(5, 5);
        Assert.Equal(255, outside.R);
        Assert.Equal(255, outside.G);
        Assert.Equal(255, outside.B);
    }

    [Fact]
    public void Even_odd_fill_leaves_inner_hole()
    {
        var raster = new Raster(100, 100);
        raster.Clear(PdfColor.White);

        var outer = new (double, double)[] { (10, 10), (90, 10), (90, 90), (10, 90) };
        var inner = new (double, double)[] { (35, 35), (65, 35), (65, 65), (35, 65) };
        raster.FillPolygons([outer, inner], PdfColor.Black, evenOdd: true);

        Assert.Equal(0, raster.GetPixel(15, 50).R);     // ring is filled
        Assert.Equal(255, raster.GetPixel(50, 50).R);   // hole stays white
    }

    [Fact]
    public void Renders_page_to_expected_dimensions()
    {
        var doc = PdfDocument.Load(PdfBuilder.BuildContent("", width: 200, height: 300));
        var image = PdfRenderer.Render(doc.Pages[0], scale: 2.0);

        Assert.Equal(400, image.Width);
        Assert.Equal(600, image.Height);
        Assert.Equal(image.Width * image.Height * 4, image.Pixels.Length);
    }

    [Fact]
    public void Renders_filled_rectangle_from_content_stream()
    {
        var doc = PdfDocument.Load(PdfBuilder.BuildContent("0 0 1 rg 0 0 612 792 re f"));
        var image = PdfRenderer.Render(doc.Pages[0], scale: 0.25);

        var raster = ToRaster(image);
        var p = raster.GetPixel(image.Width / 2, image.Height / 2);
        Assert.True(p.B > 200 && p.R < 60 && p.G < 60, $"expected blue, got ({p.R},{p.G},{p.B})");
    }

    [Fact]
    public void Renders_text_producing_dark_pixels()
    {
        var doc = PdfDocument.Load(PdfBuilder.Build(1, compress: false));
        var image = PdfRenderer.Render(doc.Pages[0], scale: 2.0);

        var dark = 0;
        for (var i = 0; i < image.Pixels.Length; i += 4)
        {
            if (image.Pixels[i] < 128 && image.Pixels[i + 1] < 128 && image.Pixels[i + 2] < 128) dark++;
        }
        Assert.True(dark > 50, $"expected rendered glyph pixels, found {dark}");
    }

    [Fact]
    public void Renders_multiple_fill_colors_from_content_stream()
    {
        const string content =
            "0 0 1 rg 0 700 612 92 re f\n" +   // blue band (top)
            "1 0 0 rg 0 350 612 92 re f\n" +   // red band (middle)
            "0 1 0 rg 0 0 612 92 re f";        // green band (bottom)

        var doc = PdfDocument.Load(PdfBuilder.BuildContent(content));
        var image = PdfRenderer.Render(doc.Pages[0], scale: 0.5);
        var raster = ToRaster(image);

        var cx = image.Width / 2;
        var top = raster.GetPixel(cx, (int)(image.Height * 0.05));
        var mid = raster.GetPixel(cx, (int)(image.Height * 0.50));
        var bot = raster.GetPixel(cx, (int)(image.Height * 0.95));

        Assert.True(top.B > 200 && top.R < 60, $"top should be blue, got ({top.R},{top.G},{top.B})");
        Assert.True(mid.R > 200 && mid.B < 60, $"mid should be red, got ({mid.R},{mid.G},{mid.B})");
        Assert.True(bot.G > 200 && bot.R < 60, $"bottom should be green, got ({bot.R},{bot.G},{bot.B})");
    }

    [Fact]
    public void Renders_embedded_truetype_glyph_as_filled_outline()
    {
        var ttf = TrueTypeBuilder.Build();
        var doc = PdfDocument.Load(PdfBuilder.BuildTrueTypeDoc(ttf));
        var image = PdfRenderer.Render(doc.Pages[0], scale: 1.0);
        var raster = ToRaster(image);

        // The glyph is a filled rectangle; a scan line through it should be a long
        // contiguous run of dark pixels (a stroked fallback would only mark edges).
        const int y = 480;
        var run = 0;
        var maxRun = 0;
        for (var x = 0; x < image.Width; x++)
        {
            var p = raster.GetPixel(x, y);
            if (p.R < 80 && p.G < 80 && p.B < 80) { run++; maxRun = Math.Max(maxRun, run); }
            else run = 0;
        }

        Assert.True(maxRun > 300, $"expected a filled glyph span, longest dark run was {maxRun}");
    }

    [Fact]
    public void Renders_real_system_truetype_font_when_available()
    {
        // Validates format-4 cmap + real glyf outlines end-to-end. Skips on machines
        // without the font (e.g., non-Windows CI) so it never produces a false failure.
        var candidates = new[]
        {
            @"C:\Windows\Fonts\arial.ttf",
            @"C:\Windows\Fonts\segoeui.ttf",
            "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
        };
        var path = candidates.FirstOrDefault(File.Exists);
        if (path is null) return; // not available; nothing to assert

        var ttf = File.ReadAllBytes(path);
        var doc = PdfDocument.Load(PdfBuilder.BuildTrueTypeDoc(ttf, "Hi", fontSize: 300));
        var image = PdfRenderer.Render(doc.Pages[0], scale: 1.0);

        var dark = 0;
        for (var i = 0; i < image.Pixels.Length; i += 4)
        {
            if (image.Pixels[i] < 100 && image.Pixels[i + 1] < 100 && image.Pixels[i + 2] < 100) dark++;
        }
        Assert.True(dark > 200, $"expected real glyph pixels, found {dark}");
    }

    [Fact]
    public void Renders_rgb_image_xobject_with_correct_orientation()
    {
        // 2x2 image, top row red/green, bottom row blue/white (top-down).
        byte[] rgb =
        [
            255, 0, 0,  0, 255, 0,
            0, 0, 255,  255, 255, 255,
        ];

        var doc = PdfDocument.Load(PdfBuilder.BuildImageDoc(2, 2, rgb, 100, 100));
        var image = PdfRenderer.Render(doc.Pages[0], scale: 1.0);
        var raster = ToRaster(image);

        var topLeft = raster.GetPixel(10, 10);
        var topRight = raster.GetPixel(90, 10);
        var bottomLeft = raster.GetPixel(10, 90);
        var bottomRight = raster.GetPixel(90, 90);

        Assert.True(topLeft.R > 200 && topLeft.G < 60, $"top-left red, got ({topLeft.R},{topLeft.G},{topLeft.B})");
        Assert.True(topRight.G > 200 && topRight.R < 60, $"top-right green, got ({topRight.R},{topRight.G},{topRight.B})");
        Assert.True(bottomLeft.B > 200 && bottomLeft.R < 60, $"bottom-left blue, got ({bottomLeft.R},{bottomLeft.G},{bottomLeft.B})");
        Assert.True(bottomRight.R > 200 && bottomRight.G > 200 && bottomRight.B > 200, "bottom-right white");
    }

    [Fact]
    public void Renders_indexed_image_xobject()
    {
        // Palette: 0 = red, 1 = green. 8 bpc indices, 2x1 image (red, green).
        var palette = new byte[] { 255, 0, 0, 0, 255, 0 };
        var paletteHex = string.Concat(palette.Select(b => b.ToString("X2")));
        var content = "q 100 0 0 100 0 0 cm /Im0 Do Q";
        var contentBytes = System.Text.Encoding.ASCII.GetBytes(content);

        var objects = new List<(string, byte[]?)>
        {
            ("<< /Type /Catalog /Pages 2 0 R >>", null),
            ("<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            ("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R " +
             "/Resources << /XObject << /Im0 5 0 R >> >> >>", null),
            ($"<< /Length {contentBytes.Length} >>", contentBytes),
            ($"<< /Type /XObject /Subtype /Image /Width 2 /Height 1 /BitsPerComponent 8 " +
             $"/ColorSpace [/Indexed /DeviceRGB 1 <{paletteHex}>] /Length 2 >>", new byte[] { 0, 1 }),
        };

        var doc = PdfDocument.Load(PdfBuilder.BuildObjects(objects));
        var image = PdfRenderer.Render(doc.Pages[0], scale: 1.0);
        var raster = ToRaster(image);

        var left = raster.GetPixel(25, 50);
        var right = raster.GetPixel(75, 50);
        Assert.True(left.R > 200 && left.G < 60, $"left red, got ({left.R},{left.G},{left.B})");
        Assert.True(right.G > 200 && right.R < 60, $"right green, got ({right.R},{right.G},{right.B})");
    }

    [Theory]
    [InlineData(220, 30, 30)]
    [InlineData(40, 200, 60)]
    [InlineData(50, 70, 210)]
    [InlineData(180, 180, 180)]
    public void Decodes_baseline_jpeg_dctdecode_image(byte r, byte g, byte b)
    {
        var jpeg = TestJpeg.BuildSolid(r, g, b);
        var doc = PdfDocument.Load(PdfBuilder.BuildJpegDoc(jpeg, 8, 8, 64, 64));
        var image = PdfRenderer.Render(doc.Pages[0], scale: 1.0);
        var raster = ToRaster(image);

        var p = raster.GetPixel(32, 32);
        Assert.True(Math.Abs(p.R - r) <= 14, $"R {p.R} vs {r}");
        Assert.True(Math.Abs(p.G - g) <= 14, $"G {p.G} vs {g}");
        Assert.True(Math.Abs(p.B - b) <= 14, $"B {p.B} vs {b}");
    }

    [Fact]
    public void Decodes_multi_mcu_jpeg()
    {
        // 16x16 = 4 MCUs, exercising the MCU loop and DC predictor (diff 0 after the first).
        var jpeg = TestJpeg.BuildSolid(16, 16, 30, 160, 220);
        var doc = PdfDocument.Load(PdfBuilder.BuildJpegDoc(jpeg, 16, 16, 64, 64));
        var image = PdfRenderer.Render(doc.Pages[0], scale: 1.0);
        var raster = ToRaster(image);

        // Check all four quadrants decoded to the same color.
        foreach (var (x, y) in new[] { (16, 16), (48, 16), (16, 48), (48, 48) })
        {
            var p = raster.GetPixel(x, y);
            Assert.True(Math.Abs(p.R - 30) <= 16 && Math.Abs(p.G - 160) <= 16 && Math.Abs(p.B - 220) <= 16,
                $"quadrant ({x},{y}) = ({p.R},{p.G},{p.B})");
        }
    }

    [Fact]
    public void Clips_fill_to_clip_path()
    {
        // Clip to a rectangle, then fill the whole page blue.
        var content = "0 0 1 rg 100 100 200 200 re W n 0 0 612 792 re f";
        var doc = PdfDocument.Load(PdfBuilder.BuildContent(content));
        var image = PdfRenderer.Render(doc.Pages[0], scale: 0.25);
        var raster = ToRaster(image);

        // Clip region in device space: x in [25,75], y in [123,173].
        var inside = raster.GetPixel(50, 148);
        var outside = raster.GetPixel(120, 40);

        Assert.True(inside.B > 200 && inside.R < 60, $"inside clip should be blue, got ({inside.R},{inside.G},{inside.B})");
        Assert.True(outside.R > 240 && outside.G > 240 && outside.B > 240, $"outside clip should be unpainted, got ({outside.R},{outside.G},{outside.B})");
    }

    [Fact]
    public void Clip_is_released_after_Q()
    {
        // Clip + fill inside q/Q, then paint a green rectangle outside the old clip.
        var content =
            "q 100 100 200 200 re W n 0 0 1 rg 0 0 612 792 re f Q " +
            "0 1 0 rg 400 600 100 100 re f";
        var doc = PdfDocument.Load(PdfBuilder.BuildContent(content));
        var image = PdfRenderer.Render(doc.Pages[0], scale: 0.25);
        var raster = ToRaster(image);

        // Green rect device space: x in [100,125], y in [23,48].
        var green = raster.GetPixel(110, 35);
        Assert.True(green.G > 200 && green.R < 60, $"post-Q fill should be green (clip released), got ({green.R},{green.G},{green.B})");
    }

    [Fact]
    public void Renders_embedded_cff_glyph_as_filled_outline()
    {
        var cff = CffBuilder.Build();
        var doc = PdfDocument.Load(PdfBuilder.BuildCffDoc(cff));
        var image = PdfRenderer.Render(doc.Pages[0], scale: 1.0);
        var raster = ToRaster(image);

        const int y = 480;
        var run = 0;
        var maxRun = 0;
        for (var x = 0; x < image.Width; x++)
        {
            var p = raster.GetPixel(x, y);
            if (p.R < 80 && p.G < 80 && p.B < 80) { run++; maxRun = Math.Max(maxRun, run); }
            else run = 0;
        }

        Assert.True(maxRun > 300, $"expected a filled CFF glyph span, longest dark run was {maxRun}");
    }

    [Fact]
    public void Paints_axial_shading_with_sh_operator()
    {
        var doc = PdfDocument.Load(PdfBuilder.BuildShadingDoc(100, 100));
        var image = PdfRenderer.Render(doc.Pages[0], scale: 1.0);
        var raster = ToRaster(image);

        var left = raster.GetPixel(5, 50);
        var right = raster.GetPixel(95, 50);

        Assert.True(left.R > 180 && left.B < 80, $"left should be red, got ({left.R},{left.G},{left.B})");
        Assert.True(right.B > 180 && right.R < 80, $"right should be blue, got ({right.R},{right.G},{right.B})");
    }

    [Fact]
    public void Fills_with_shading_pattern_clipped_to_path()
    {
        var doc = PdfDocument.Load(PdfBuilder.BuildShadingPatternDoc(100, 100));
        var image = PdfRenderer.Render(doc.Pages[0], scale: 1.0);
        var raster = ToRaster(image);

        var insideLeft = raster.GetPixel(30, 50);   // near red end
        var insideRight = raster.GetPixel(70, 50);  // near green end
        var outside = raster.GetPixel(90, 50);      // outside the filled rect

        Assert.True(insideLeft.R > 150 && insideLeft.G < 120, $"left of pattern should be reddish, got ({insideLeft.R},{insideLeft.G},{insideLeft.B})");
        Assert.True(insideRight.G > 150 && insideRight.R < 120, $"right of pattern should be greenish, got ({insideRight.R},{insideRight.G},{insideRight.B})");
        Assert.True(outside.R > 240 && outside.G > 240 && outside.B > 240, $"outside pattern should be white, got ({outside.R},{outside.G},{outside.B})");
    }

    [Fact]
    public void Applies_constant_fill_alpha_from_extgstate()
    {
        var content = "/GS0 gs 1 0 0 rg 0 0 100 100 re f";
        var cb = System.Text.Encoding.ASCII.GetBytes(content);
        var objects = new List<(string, byte[]?)>
        {
            ("<< /Type /Catalog /Pages 2 0 R >>", null),
            ("<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            ("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R " +
             "/Resources << /ExtGState << /GS0 5 0 R >> >> >>", null),
            ($"<< /Length {cb.Length} >>", cb),
            ("<< /ca 0.5 >>", null),
        };

        var doc = PdfDocument.Load(PdfBuilder.BuildObjects(objects));
        var raster = ToRaster(PdfRenderer.Render(doc.Pages[0], scale: 1.0));

        // Red at 50% over white -> (255, ~128, ~128).
        var p = raster.GetPixel(50, 50);
        Assert.True(p.R > 245, $"R {p.R}");
        Assert.True(Math.Abs(p.G - 128) <= 12 && Math.Abs(p.B - 128) <= 12, $"({p.R},{p.G},{p.B})");
    }

    [Fact]
    public void Applies_multiply_blend_mode()
    {
        var content = "0.5 0.5 0.5 rg 0 0 100 100 re f /GSm gs 0 1 0 rg 0 0 100 100 re f";
        var cb = System.Text.Encoding.ASCII.GetBytes(content);
        var objects = new List<(string, byte[]?)>
        {
            ("<< /Type /Catalog /Pages 2 0 R >>", null),
            ("<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            ("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R " +
             "/Resources << /ExtGState << /GSm 5 0 R >> >> >>", null),
            ($"<< /Length {cb.Length} >>", cb),
            ("<< /BM /Multiply >>", null),
        };

        var doc = PdfDocument.Load(PdfBuilder.BuildObjects(objects));
        var raster = ToRaster(PdfRenderer.Render(doc.Pages[0], scale: 1.0));

        // gray(128) Multiply green(0,255,0) -> (0,128,0).
        var p = raster.GetPixel(50, 50);
        Assert.True(p.R < 12 && p.B < 12 && Math.Abs(p.G - 128) <= 12, $"({p.R},{p.G},{p.B})");
    }

    [Fact]
    public void Encodes_valid_png_with_correct_dimensions()
    {
        var raster = new Raster(20, 12);
        raster.Clear(new PdfColor(10, 20, 30));
        var png = raster.ToImage().ToPng();

        // PNG signature.
        byte[] sig = [137, 80, 78, 71, 13, 10, 26, 10];
        Assert.True(png.Take(8).SequenceEqual(sig), "PNG signature");

        // First chunk is IHDR with width/height.
        Assert.Equal('I', (char)png[12]);
        Assert.Equal('H', (char)png[13]);
        var width = (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19];
        var height = (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23];
        Assert.Equal(20, width);
        Assert.Equal(12, height);
        Assert.Equal(8, png[24]);  // bit depth
        Assert.Equal(6, png[25]);  // RGBA

        // IEND present at the end.
        var tail = System.Text.Encoding.ASCII.GetString(png, png.Length - 8, 4);
        Assert.Equal("IEND", tail);
    }

    private static Raster ToRaster(RenderedImage image)
    {
        var raster = new Raster(image.Width, image.Height);
        Array.Copy(image.Pixels, raster.Pixels, image.Pixels.Length);
        return raster;
    }
}
