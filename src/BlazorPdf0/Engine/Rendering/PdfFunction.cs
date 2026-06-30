namespace BlazorPdf.Engine.Rendering;

/// <summary>
/// Evaluates a PDF function (used by shadings). Supports Type 2 (exponential), Type 3
/// (stitching), Type 0 (sampled, 1-input), and an array of single-output functions.
/// Type 4 (PostScript calculator) is approximated by its range midpoint.
/// </summary>
internal sealed class PdfFunction
{
    private int _type;
    private double _domainLo, _domainHi = 1;

    // Type 2
    private double[] _c0 = [0];
    private double[] _c1 = [1];
    private double _n = 1;

    // Type 3
    private PdfFunction[] _functions = [];
    private double[] _bounds = [];
    private double[] _encode = [];

    // Type 0
    private byte[] _samples = [];
    private int _bitsPerSample = 8;
    private int _size;
    private int _nOut = 1;
    private double[] _range = [];
    private double[] _sampleEncode = [];

    // Array-of-functions
    private PdfFunction[] _array = [];

    public static PdfFunction? Parse(PdfDocument doc, PdfObject? obj)
    {
        obj = doc.Resolve(obj);

        if (obj is PdfArray arr)
        {
            var fns = new List<PdfFunction>();
            foreach (var item in arr.Items)
            {
                if (Parse(doc, item) is { } f) fns.Add(f);
            }
            return fns.Count == 0 ? null : new PdfFunction { _type = -1, _array = [.. fns] };
        }

        var dict = obj as PdfDictionary ?? (obj as PdfStream)?.Dictionary;
        if (dict is null) return null;

        var fn = new PdfFunction
        {
            _type = (doc.Resolve(dict.Get("FunctionType")) as PdfNumber)?.AsInt ?? -1,
        };

        if (doc.Resolve(dict.Get("Domain")) is PdfArray { Count: >= 2 } dom)
        {
            fn._domainLo = AsD(doc, dom[0]);
            fn._domainHi = AsD(doc, dom[1]);
        }

        switch (fn._type)
        {
            case 2:
                fn._c0 = ReadArray(doc, dict.Get("C0")) ?? [0];
                fn._c1 = ReadArray(doc, dict.Get("C1")) ?? [1];
                fn._n = (doc.Resolve(dict.Get("N")) as PdfNumber)?.Value ?? 1;
                break;
            case 3:
                {
                    var subs = new List<PdfFunction>();
                    if (doc.Resolve(dict.Get("Functions")) is PdfArray fa)
                    {
                        foreach (var item in fa.Items)
                        {
                            if (Parse(doc, item) is { } f) subs.Add(f);
                        }
                    }
                    fn._functions = [.. subs];
                    fn._bounds = ReadArray(doc, dict.Get("Bounds")) ?? [];
                    fn._encode = ReadArray(doc, dict.Get("Encode")) ?? [];
                    break;
                }
            case 0 when obj is PdfStream stream:
                fn._samples = PdfFilters.Decode(stream, doc);
                fn._bitsPerSample = (doc.Resolve(dict.Get("BitsPerSample")) as PdfNumber)?.AsInt ?? 8;
                fn._size = (doc.Resolve(dict.Get("Size")) as PdfArray)?.Items is { Count: > 0 } sz
                    ? (doc.Resolve(sz[0]) as PdfNumber)?.AsInt ?? 2 : 2;
                fn._range = ReadArray(doc, dict.Get("Range")) ?? [0, 1];
                fn._nOut = fn._range.Length / 2;
                fn._sampleEncode = ReadArray(doc, dict.Get("Encode")) ?? [0, fn._size - 1];
                break;
            case 4:
                fn._range = ReadArray(doc, dict.Get("Range")) ?? [0, 1];
                break;
        }

        return fn;
    }

    /// <summary>Number of output components.</summary>
    public int OutputCount => _type switch
    {
        -1 => _array.Length,
        2 => _c1.Length,
        3 => _functions.Length > 0 ? _functions[0].OutputCount : 1,
        0 => _nOut,
        4 => _range.Length / 2,
        _ => 1,
    };

    public double[] Eval(double t)
    {
        t = Math.Clamp(t, Math.Min(_domainLo, _domainHi), Math.Max(_domainLo, _domainHi));

        switch (_type)
        {
            case -1:
                var outs = new double[_array.Length];
                for (var i = 0; i < _array.Length; i++)
                {
                    var r = _array[i].Eval(t);
                    outs[i] = r.Length > 0 ? r[0] : 0;
                }
                return outs;

            case 2:
                var p = Math.Pow(Normalize(t), _n);
                var res = new double[_c0.Length];
                for (var i = 0; i < res.Length; i++) res[i] = _c0[i] + p * (_c1[i] - _c0[i]);
                return res;

            case 3:
                return EvalStitching(t);

            case 0:
                return EvalSampled(t);

            case 4:
                var mid = new double[_range.Length / 2];
                for (var i = 0; i < mid.Length; i++) mid[i] = (_range[i * 2] + _range[i * 2 + 1]) / 2;
                return mid;

            default:
                return [t];
        }
    }

    private double Normalize(double t)
    {
        var span = _domainHi - _domainLo;
        return Math.Abs(span) < 1e-9 ? 0 : (t - _domainLo) / span;
    }

    private double[] EvalStitching(double t)
    {
        var k = 0;
        while (k < _bounds.Length && t >= _bounds[k]) k++;
        if (k >= _functions.Length) k = Math.Max(0, _functions.Length - 1);

        var lo = k == 0 ? _domainLo : _bounds[k - 1];
        var hi = k < _bounds.Length ? _bounds[k] : _domainHi;
        var encLo = k * 2 < _encode.Length ? _encode[k * 2] : 0;
        var encHi = k * 2 + 1 < _encode.Length ? _encode[k * 2 + 1] : 1;

        var e = Math.Abs(hi - lo) < 1e-9 ? encLo : encLo + (t - lo) * (encHi - encLo) / (hi - lo);
        return _functions.Length > 0 ? _functions[k].Eval(e) : [e];
    }

    private double[] EvalSampled(double t)
    {
        var e = _sampleEncode.Length >= 2
            ? _sampleEncode[0] + Normalize(t) * (_sampleEncode[1] - _sampleEncode[0])
            : Normalize(t) * (_size - 1);
        e = Math.Clamp(e, 0, _size - 1);

        var i0 = (int)Math.Floor(e);
        var i1 = Math.Min(i0 + 1, _size - 1);
        var frac = e - i0;
        var maxVal = (1 << _bitsPerSample) - 1;

        var result = new double[_nOut];
        for (var c = 0; c < _nOut; c++)
        {
            var s0 = ReadSample(i0 * _nOut + c) / (double)maxVal;
            var s1 = ReadSample(i1 * _nOut + c) / (double)maxVal;
            var v = s0 + frac * (s1 - s0);
            var rLo = c * 2 < _range.Length ? _range[c * 2] : 0;
            var rHi = c * 2 + 1 < _range.Length ? _range[c * 2 + 1] : 1;
            result[c] = rLo + v * (rHi - rLo);
        }
        return result;
    }

    private int ReadSample(int index)
    {
        if (_bitsPerSample == 8)
        {
            return index < _samples.Length ? _samples[index] : 0;
        }
        if (_bitsPerSample == 16)
        {
            var o = index * 2;
            return o + 1 < _samples.Length ? (_samples[o] << 8) | _samples[o + 1] : 0;
        }

        // Generic bit reader for 1/2/4 bps.
        var bitPos = index * _bitsPerSample;
        var v = 0;
        for (var b = 0; b < _bitsPerSample; b++)
        {
            var bp = bitPos + b;
            var bit = bp / 8 < _samples.Length ? (_samples[bp / 8] >> (7 - (bp % 8))) & 1 : 0;
            v = (v << 1) | bit;
        }
        return v;
    }

    private static double[]? ReadArray(PdfDocument doc, PdfObject? obj)
    {
        if (doc.Resolve(obj) is not PdfArray arr) return null;
        var result = new double[arr.Count];
        for (var i = 0; i < arr.Count; i++) result[i] = AsD(doc, arr[i]);
        return result;
    }

    private static double AsD(PdfDocument doc, PdfObject? o) => doc.Resolve(o) is PdfNumber n ? n.Value : 0;
}
