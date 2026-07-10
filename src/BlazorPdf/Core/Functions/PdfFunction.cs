// PDF function evaluation (Types 0, 2, 3, 4).

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
            0 when obj is PdfStream stream => SampledFunction.Build(stream, xref),
            2 => ExponentialFunction.Build(dict, xref),
            3 => StitchingFunction.Build(dict, xref),
            4 when obj is PdfStream calc => PostScriptFunction.Build(calc),
            _ => null,
        };
    }

    protected static double[] ReadNumbers(object? value, IXRef? xref = null)
    {
        if (value is not List<object?> arr)
        {
            return [];
        }
        var result = new double[arr.Count];
        for (int i = 0; i < arr.Count; i++)
        {
            // Elements (/Domain, /C0, /C1, /Range, /Encode, …) may be indirect refs (1.26).
            result[i] = Primitives.ResolveNumber(xref, arr[i]);
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

    public static ExponentialFunction Build(Dict dict, IXRef? xref = null)
    {
        double[] c0 = ReadNumbers(dict.Get("C0"), xref);
        double[] c1 = ReadNumbers(dict.Get("C1"), xref);
        if (c0.Length == 0) c0 = [0];
        if (c1.Length == 0) c1 = [1];
        double n = dict.Get("N") is double d ? d : 1;
        double[] domain = ReadNumbers(dict.Get("Domain"), xref);
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

        double[] bounds = ReadNumbers(dict.Get("Bounds"), xref);
        double[] encode = ReadNumbers(dict.Get("Encode"), xref);
        double[] domain = ReadNumbers(dict.Get("Domain"), xref);
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

/// <summary>Type 0: sampled function with multilinear interpolation (m inputs, n outputs).</summary>
internal sealed class SampledFunction : PdfFunction
{
    private readonly byte[] _samples;
    private readonly int _bps;
    private readonly int[] _size;
    private readonly int _inCount;
    private readonly int _outCount;
    private readonly double[] _domain;
    private readonly double[] _encode;
    private readonly double[] _decode;
    private readonly double[] _range;

    private SampledFunction(byte[] samples, int bps, int[] size, int inCount, int outCount,
        double[] domain, double[] encode, double[] decode, double[] range)
    {
        _samples = samples;
        _bps = bps;
        _size = size;
        _inCount = inCount;
        _outCount = outCount;
        _domain = domain;
        _encode = encode;
        _decode = decode;
        _range = range;
    }

    public static SampledFunction? Build(PdfStream stream, IXRef? xref = null)
    {
        Dict dict = stream.Dict!;
        double[] domain = ReadNumbers(dict.Get("Domain"), xref);
        double[] range = ReadNumbers(dict.Get("Range"), xref);
        double[] sizeArr = ReadNumbers(dict.Get("Size"), xref);
        int bps = ToInt(dict.Get("BitsPerSample"), 8);
        if (domain.Length < 2 || range.Length < 2 || sizeArr.Length < 1)
        {
            return null;
        }

        int inCount = domain.Length / 2;
        int outCount = range.Length / 2;
        var size = new int[inCount];
        for (int i = 0; i < inCount; i++)
        {
            size[i] = i < sizeArr.Length ? Math.Max(1, (int)sizeArr[i]) : 1;
        }

        // Encode defaults to [0 size0-1 0 size1-1 ...]; missing entries in a
        // partial Encode array fall back to those per-axis defaults rather than
        // discarding the values that were supplied.
        double[] providedEncode = ReadNumbers(dict.Get("Encode"), xref);
        var encode = new double[inCount * 2];
        for (int i = 0; i < inCount; i++)
        {
            encode[i * 2] = i * 2 < providedEncode.Length ? providedEncode[i * 2] : 0;
            encode[i * 2 + 1] = i * 2 + 1 < providedEncode.Length ? providedEncode[i * 2 + 1] : size[i] - 1;
        }

        // Decode defaults to Range; a partial Decode falls back to Range per entry.
        double[] providedDecode = ReadNumbers(dict.Get("Decode"), xref);
        var decode = new double[range.Length];
        for (int i = 0; i < range.Length; i++)
        {
            decode[i] = i < providedDecode.Length ? providedDecode[i] : range[i];
        }

        byte[] samples = StreamDecoder.Decode(stream);
        return new SampledFunction(samples, bps, size, inCount, outCount, domain, encode, decode, range);
    }

    public override double[] Eval(double[] input)
    {
        // Encode each input to a (fractional) sample coordinate within its axis.
        var e = new double[_inCount];
        var i0 = new int[_inCount];
        var frac = new double[_inCount];
        for (int k = 0; k < _inCount; k++)
        {
            double x = Clamp(k < input.Length ? input[k] : 0, _domain[k * 2], _domain[k * 2 + 1]);
            double enc = Interp(x, _domain[k * 2], _domain[k * 2 + 1], _encode[k * 2], _encode[k * 2 + 1]);
            enc = Clamp(enc, 0, _size[k] - 1);
            i0[k] = (int)Math.Floor(enc);
            if (i0[k] >= _size[k] - 1)
            {
                i0[k] = Math.Max(0, _size[k] - 1);
                frac[k] = 0;
            }
            else
            {
                frac[k] = enc - i0[k];
            }
            e[k] = enc;
        }

        double maxVal = Math.Pow(2, _bps) - 1;
        var output = new double[_outCount];

        // Multilinear interpolation over the 2^m surrounding sample corners.
        int corners = 1 << _inCount;
        for (int corner = 0; corner < corners; corner++)
        {
            double weight = 1;
            long flatIndex = 0;
            long stride = 1;
            for (int k = 0; k < _inCount; k++)
            {
                bool upper = (corner & (1 << k)) != 0;
                int idx = i0[k] + (upper ? 1 : 0);
                if (idx > _size[k] - 1)
                {
                    idx = _size[k] - 1;
                }
                weight *= upper ? frac[k] : 1 - frac[k];
                flatIndex += idx * stride;
                stride *= _size[k];
            }
            if (weight == 0)
            {
                continue;
            }
            for (int c = 0; c < _outCount; c++)
            {
                output[c] += weight * (ReadSample(flatIndex * _outCount + c) / maxVal);
            }
        }

        // Apply the Decode array to map [0,1] sample space onto the output range,
        // then clamp to /Range (PDF 32000-1 §7.10.2).
        for (int c = 0; c < _outCount; c++)
        {
            double d0 = _decode.Length > c * 2 ? _decode[c * 2] : 0;
            double d1 = _decode.Length > c * 2 + 1 ? _decode[c * 2 + 1] : 1;
            output[c] = d0 + output[c] * (d1 - d0);
            if (_range.Length > c * 2 + 1)
            {
                output[c] = Clamp(output[c], _range[c * 2], _range[c * 2 + 1]);
            }
        }
        return output;
    }

    private double ReadSample(long index)
    {
        long bitPos = index * _bps;
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
