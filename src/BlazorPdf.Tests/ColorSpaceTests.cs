using BlazorPdf.Core;
using BlazorPdf.Core.Render;

namespace BlazorPdf.Tests;

public class ColorSpaceTests
{
    private static readonly IXRef Xref = new InlineXRef();

    [Fact]
    public void DeviceGray_midpoint()
        => Assert.Equal((128, 128, 128), ColorSpace.Gray.GetRgb([0.5]));

    [Fact]
    public void DeviceRgb_primary()
        => Assert.Equal(((byte)255, (byte)0, (byte)0), ColorSpace.Rgb.GetRgb([1, 0, 0]));

    [Fact]
    public void DeviceCmyk_to_red()
        => Assert.Equal(((byte)255, (byte)0, (byte)0), ColorSpace.Cmyk.GetRgb([0, 1, 1, 0]));

    [Fact]
    public void Device_default_components_are_zero()
    {
        Assert.Equal(new double[] { 0, 0, 0 }, ColorSpace.Rgb.DefaultComponents());
        Assert.Single(ColorSpace.Gray.DefaultComponents());
    }

    [Fact]
    public void Lab_black_is_black()
    {
        var lab = ColorSpace.Create(MakeLabArray(), Xref, null);
        Assert.Equal(((byte)0, (byte)0, (byte)0), lab.GetRgb([0, 0, 0]));
    }

    [Fact]
    public void Lab_white_is_bright()
    {
        var lab = ColorSpace.Create(MakeLabArray(), Xref, null);
        var (r, g, b) = lab.GetRgb([100, 0, 0]);
        Assert.True(r > 200 && g > 200 && b > 200, $"expected bright, got ({r},{g},{b})");
    }

    [Fact]
    public void Lab_positive_a_is_reddish()
    {
        var lab = ColorSpace.Create(MakeLabArray(), Xref, null);
        var (r, _, _) = lab.GetRgb([60, 80, 0]);
        var (rGray, _, _) = lab.GetRgb([60, 0, 0]);
        Assert.True(r >= rGray, "increasing a* should not reduce the red channel");
    }

    [Fact]
    public void Indexed_palette_lookup()
    {
        // [/Indexed /DeviceRGB 1 <palette>] with two entries: red, green.
        var palette = new PdfString([255, 0, 0, 0, 255, 0]);
        var arr = new List<object?> { Name.Get("Indexed"), Name.Get("DeviceRGB"), 1.0, palette };
        var cs = ColorSpace.Create(arr, Xref, null);

        Assert.Equal(1, cs.Components);
        Assert.Equal(((byte)255, (byte)0, (byte)0), cs.GetRgb([0]));
        Assert.Equal(((byte)0, (byte)255, (byte)0), cs.GetRgb([1]));
    }

    private static List<object?> MakeLabArray()
    {
        var dict = new Dict();
        dict.Set("WhitePoint", new List<object?> { 1.0, 1.0, 1.0 });
        dict.Set("Range", new List<object?> { -100.0, 100.0, -100.0, 100.0 });
        return new List<object?> { Name.Get("Lab"), dict };
    }
}
