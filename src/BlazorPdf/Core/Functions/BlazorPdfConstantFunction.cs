// PDF function evaluation (Types 0, 2, 3, 4).


namespace BlazorPdf;

/// <summary>A function that always returns a fixed output (fallback).</summary>
internal sealed class BlazorPdfConstantFunction : BlazorPdfFunction
{
    private readonly double[] _value;
    public BlazorPdfConstantFunction(double[] value) => _value = value;
    public override double[] Eval(double[] input) => _value;
}
