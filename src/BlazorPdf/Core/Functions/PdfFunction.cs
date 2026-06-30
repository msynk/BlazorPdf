// Clean-room C# port of PDF function evaluation from pdf.js
// `src/core/function.js` (Types 0, 2, 3, 4). See NOTICE.

using BlazorPdf.Core.Filters;

namespace BlazorPdf.Core.Functions;

/// <summary>
/// Evaluates a PDF function object (used by shadings, transfer functions, etc.).
/// Supports sampled (Type 0), exponential interpolation (Type 2), stitching
/// (Type 3) and a subset of PostScript calculator (Type 4) functions.
/// </summary>
public abstract class PdfFunction
{
    /// <summary>Evaluates the function for the given inputs.</summary>
    public abstract double[] Eval(double[] input);

    /// <summary>Builds a function from a dictionary/stream object, or <c>null</c> if unsupported.</summary>
    public static PdfFunction? Create(object? obj, IXRef xref)
    {
        obj = xref.FetchIfRef(obj);

        // An array of functions evaluates each and concatenates the outputs.
        if (obj is List<object?> array)
        {
            var parts = new List<PdfFunction>();
            foreach (var item in array)
            {
                var fn = Create(item, xref);
                if (fn is not null)
                {
                    parts.Add(fn);
                }
            }
            return parts.Count > 0 ? new ArrayFunction(parts) : null;
        }

        Dict? dict = obj as Dict ?? (obj as PdfStream)?.Dict;
        if (dict is null)
        {
            return null;
        }

        int type = ToInt(dict.Get("FunctionType"), -1);
        return type switch
        {
            0 when obj is PdfStream stream => SampledFunction.Build(stream),
            2 => ExponentialFunction.Build(dict),
            3 => StitchingFunction.Build(dict, xref),
            4 when obj is PdfStream calc => PostScriptFunction.Build(calc),
            _ => null,
        };
    }

    protected static double[] ReadNumbers(object? value)
    {
        if (value is not List<object?> arr)
        {
            return [];
        }
        var result = new double[arr.Count];
        for (int i = 0; i < arr.Count; i++)
        {
            result[i] = arr[i] is double d ? d : 0;
        }
        return result;
    }

    protected static double Clamp(double v, double lo, double hi) => Math.Max(lo, Math.Min(hi, v));

    protected static int ToInt(object? value, int fallback) => value is double d ? (int)d : fallback;

    private sealed class ArrayFunction : PdfFunction
    {
        private readonly List<PdfFunction> _functions;
        public ArrayFunction(List<PdfFunction> functions) => _functions = functions;

        public override double[] Eval(double[] input)
        {
            var output = new List<double>();
            foreach (var fn in _functions)
            {
                output.AddRange(fn.Eval(input));
            }
            return output.ToArray();
        }
    }
}

/// <summary>Type 2: exponential interpolation between C0 and C1.</summary>
internal sealed class ExponentialFunction : PdfFunction
{
    private readonly double[] _c0;
    private readonly double[] _c1;
    private readonly double _n;
    private readonly double[] _domain;

    private ExponentialFunction(double[] c0, double[] c1, double n, double[] domain)
    {
        _c0 = c0;
        _c1 = c1;
        _n = n;
        _domain = domain;
    }

    public static ExponentialFunction Build(Dict dict)
    {
        double[] c0 = ReadNumbers(dict.Get("C0"));
        double[] c1 = ReadNumbers(dict.Get("C1"));
        if (c0.Length == 0) c0 = [0];
        if (c1.Length == 0) c1 = [1];
        double n = dict.Get("N") is double d ? d : 1;
        double[] domain = ReadNumbers(dict.Get("Domain"));
        if (domain.Length < 2) domain = [0, 1];
        return new ExponentialFunction(c0, c1, n, domain);
    }

    public override double[] Eval(double[] input)
    {
        double x = Clamp(input.Length > 0 ? input[0] : 0, _domain[0], _domain[1]);
        double xn = _n == 1 ? x : Math.Pow(x, _n);
        int len = Math.Max(_c0.Length, _c1.Length);
        var output = new double[len];
        for (int i = 0; i < len; i++)
        {
            double a = i < _c0.Length ? _c0[i] : 0;
            double b = i < _c1.Length ? _c1[i] : 0;
            output[i] = a + xn * (b - a);
        }
        return output;
    }
}

/// <summary>Type 3: stitching of sub-functions over sub-domains.</summary>
internal sealed class StitchingFunction : PdfFunction
{
    private readonly PdfFunction[] _functions;
    private readonly double[] _bounds;
    private readonly double[] _encode;
    private readonly double[] _domain;

    private StitchingFunction(PdfFunction[] functions, double[] bounds, double[] encode, double[] domain)
    {
        _functions = functions;
        _bounds = bounds;
        _encode = encode;
        _domain = domain;
    }

    public static StitchingFunction? Build(Dict dict, IXRef xref)
    {
        if (dict.Get("Functions") is not List<object?> fnArr)
        {
            return null;
        }
        var functions = new List<PdfFunction>();
        foreach (var item in fnArr)
        {
            var fn = Create(item, xref);
            functions.Add(fn ?? new ConstantFunction([0]));
        }

        double[] bounds = ReadNumbers(dict.Get("Bounds"));
        double[] encode = ReadNumbers(dict.Get("Encode"));
        double[] domain = ReadNumbers(dict.Get("Domain"));
        if (domain.Length < 2) domain = [0, 1];
        return new StitchingFunction(functions.ToArray(), bounds, encode, domain);
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

/// <summary>Type 0: sampled function with multilinear interpolation (1-D input).</summary>
internal sealed class SampledFunction : PdfFunction
{
    private readonly byte[] _samples;
    private readonly int _bps;
    private readonly int _size;
    private readonly int _outCount;
    private readonly double[] _domain;
    private readonly double[] _encode;
    private readonly double[] _decode;
    private readonly double[] _range;

    private SampledFunction(byte[] samples, int bps, int size, int outCount,
        double[] domain, double[] encode, double[] decode, double[] range)
    {
        _samples = samples;
        _bps = bps;
        _size = size;
        _outCount = outCount;
        _domain = domain;
        _encode = encode;
        _decode = decode;
        _range = range;
    }

    public static SampledFunction? Build(PdfStream stream)
    {
        Dict dict = stream.Dict!;
        double[] domain = ReadNumbers(dict.Get("Domain"));
        double[] range = ReadNumbers(dict.Get("Range"));
        double[] sizeArr = ReadNumbers(dict.Get("Size"));
        int bps = ToInt(dict.Get("BitsPerSample"), 8);
        if (domain.Length < 2 || range.Length < 2 || sizeArr.Length < 1)
        {
            return null;
        }

        // Only single-input sampled functions are supported here.
        int size = (int)sizeArr[0];
        int outCount = range.Length / 2;
        double[] encode = ReadNumbers(dict.Get("Encode"));
        if (encode.Length < 2) encode = [0, size - 1];
        double[] decode = ReadNumbers(dict.Get("Decode"));
        if (decode.Length < range.Length) decode = range;

        byte[] samples = StreamDecoder.Decode(stream);
        return new SampledFunction(samples, bps, size, outCount, domain, encode, decode, range);
    }

    public override double[] Eval(double[] input)
    {
        double x = Clamp(input.Length > 0 ? input[0] : 0, _domain[0], _domain[1]);
        double e = Interp(x, _domain[0], _domain[1], _encode[0], _encode[1]);
        e = Clamp(e, 0, _size - 1);

        int i0 = (int)Math.Floor(e);
        int i1 = Math.Min(i0 + 1, _size - 1);
        double frac = e - i0;

        var output = new double[_outCount];
        double maxVal = Math.Pow(2, _bps) - 1;
        for (int c = 0; c < _outCount; c++)
        {
            double s0 = ReadSample(i0 * _outCount + c) / maxVal;
            double s1 = ReadSample(i1 * _outCount + c) / maxVal;
            double s = s0 + frac * (s1 - s0);
            double d0 = _decode.Length > c * 2 ? _decode[c * 2] : 0;
            double d1 = _decode.Length > c * 2 + 1 ? _decode[c * 2 + 1] : 1;
            output[c] = d0 + s * (d1 - d0);
        }
        return output;
    }

    private double ReadSample(int index)
    {
        long bitPos = (long)index * _bps;
        long bytePos = bitPos / 8;
        int bitOffset = (int)(bitPos % 8);
        int value = 0;
        int bitsRead = 0;
        while (bitsRead < _bps && bytePos < _samples.Length)
        {
            int available = 8 - bitOffset;
            int take = Math.Min(available, _bps - bitsRead);
            int b = _samples[bytePos];
            int shifted = (b >> (available - take)) & ((1 << take) - 1);
            value = (value << take) | shifted;
            bitsRead += take;
            bitOffset += take;
            if (bitOffset >= 8)
            {
                bitOffset = 0;
                bytePos++;
            }
        }
        return value;
    }

    private static double Interp(double x, double xmin, double xmax, double ymin, double ymax)
        => xmax > xmin ? ymin + (x - xmin) * (ymax - ymin) / (xmax - xmin) : ymin;
}

/// <summary>A function that always returns a fixed output (fallback).</summary>
internal sealed class ConstantFunction : PdfFunction
{
    private readonly double[] _value;
    public ConstantFunction(double[] value) => _value = value;
    public override double[] Eval(double[] input) => _value;
}
