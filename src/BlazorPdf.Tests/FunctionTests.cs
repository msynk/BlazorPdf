using BlazorPdf.Core;
using BlazorPdf.Core.Functions;

namespace BlazorPdf.Tests;

public class FunctionTests
{
    private static readonly InlineXRef XRef = new();

    private static PdfFunction BuildSampled2D()
    {
        // A 2-input, 1-output sampled function on a 2x2 grid. Samples are stored
        // with the first input dimension varying fastest: index = i0 + size0*i1.
        //   (0,0)=0  (1,0)=255  (0,1)=255  (1,1)=255
        var dict = new Dict();
        dict.Set("FunctionType", 0.0);
        dict.Set("Domain", new List<object?> { 0.0, 1.0, 0.0, 1.0 });
        dict.Set("Range", new List<object?> { 0.0, 1.0 });
        dict.Set("Size", new List<object?> { 2.0, 2.0 });
        dict.Set("BitsPerSample", 8.0);
        var stream = new PdfStream([0, 255, 255, 255], dict: dict);
        return PdfFunction.Create(stream, XRef)!;
    }

    [Fact]
    public void Sampled_function_supports_multiple_inputs()
    {
        var fn = BuildSampled2D();
        Assert.NotNull(fn);

        Assert.Equal(0.0, fn.Eval([0, 0])[0], 3);
        Assert.Equal(1.0, fn.Eval([1, 1])[0], 3);
        // Bilinear interpolation along each axis from the (0,0)=0 corner.
        Assert.Equal(0.5, fn.Eval([0.5, 0])[0], 3);
        Assert.Equal(0.5, fn.Eval([0, 0.5])[0], 3);
    }

    [Fact]
    public void Exponential_function_interpolates()
    {
        var dict = new Dict();
        dict.Set("FunctionType", 2.0);
        dict.Set("Domain", new List<object?> { 0.0, 1.0 });
        dict.Set("C0", new List<object?> { 0.0, 0.0, 0.0 });
        dict.Set("C1", new List<object?> { 1.0, 0.5, 0.0 });
        dict.Set("N", 1.0);
        var fn = PdfFunction.Create(dict, XRef)!;

        double[] mid = fn.Eval([0.5]);
        Assert.Equal(0.5, mid[0], 3);
        Assert.Equal(0.25, mid[1], 3);
        Assert.Equal(0.0, mid[2], 3);
    }
}
