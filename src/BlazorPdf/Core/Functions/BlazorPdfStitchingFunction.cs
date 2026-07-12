// PDF function evaluation (Types 0, 2, 3, 4).


namespace BlazorPdf;

/// <summary>Type 3: stitching of sub-functions over sub-domains.</summary>
internal sealed class BlazorPdfStitchingFunction : BlazorPdfFunction
{
    private readonly BlazorPdfFunction[] _functions;
    private readonly double[] _bounds;
    private readonly double[] _encode;
    private readonly double[] _domain;

    private BlazorPdfStitchingFunction(BlazorPdfFunction[] functions, double[] bounds, double[] encode, double[] domain)
    {
        _functions = functions;
        _bounds = bounds;
        _encode = encode;
        _domain = domain;
    }

    public static BlazorPdfStitchingFunction? Build(BlazorPdfDict dict, IBlazorPdfXRef xref)
    {
        if (dict.Get("Functions") is not List<object?> fnArr)
        {
            return null;
        }
        var functions = new List<BlazorPdfFunction>();
        foreach (var item in fnArr)
        {
            var fn = Create(item, xref);
            functions.Add(fn ?? new BlazorPdfConstantFunction([0]));
        }

        double[] bounds = ReadNumbers(dict.Get("Bounds"), xref);
        double[] encode = ReadNumbers(dict.Get("Encode"), xref);
        double[] domain = ReadNumbers(dict.Get("Domain"), xref);
        if (domain.Length < 2) domain = [0, 1];
        return new BlazorPdfStitchingFunction(functions.ToArray(), bounds, encode, domain);
    }

    public override double[] Eval(double[] input)
    {
        double x = Clamp(input.Length > 0 ? input[0] : 0, _domain[0], _domain[1]);

        int k = 0;
        while (k < _bounds.Length && x >= _bounds[k])
        {
            k++;
        }
        if (k >= _functions.Length)
        {
            k = _functions.Length - 1;
        }

        double lo = k == 0 ? _domain[0] : _bounds[k - 1];
        double hi = k < _bounds.Length ? _bounds[k] : _domain[1];
        double e0 = k * 2 < _encode.Length ? _encode[k * 2] : 0;
        double e1 = k * 2 + 1 < _encode.Length ? _encode[k * 2 + 1] : 1;

        double t = hi > lo ? (x - lo) / (hi - lo) : 0;
        double encoded = e0 + t * (e1 - e0);
        return _functions[k].Eval([encoded]);
    }
}
