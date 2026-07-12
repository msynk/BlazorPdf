// Color-space handling: the device, ICC-by-component, indexed and separation cases.


namespace BlazorPdf;

/// <summary>A Separation / DeviceN color space using a tint-transform function.</summary>
internal sealed class BlazorPdfSeparationColorSpace : BlazorPdfColorSpace
{
    private readonly BlazorPdfColorSpace _alternate;
    private readonly BlazorPdfFunction? _tint;
    private readonly int _components;
    private readonly bool _isNone;

    private BlazorPdfSeparationColorSpace(BlazorPdfColorSpace alternate, BlazorPdfFunction? tint, int components, bool isNone)
    {
        _alternate = alternate;
        _tint = tint;
        _components = components;
        _isNone = isNone;
    }

    public override int Components => _components;

    public override double[] DefaultComponents()
    {
        // Separation/DeviceN initial color is full tint (1.0) on each component.
        var comps = new double[_components];
        Array.Fill(comps, 1.0);
        return comps;
    }

    public static BlazorPdfSeparationColorSpace Build(List<object?> arr, IBlazorPdfXRef xref, BlazorPdfDict? resources)
    {
        // [/Separation name alt tint]  or  [/DeviceN [names] alt tint]
        bool isDeviceN = (xref.FetchIfRef(arr[0]) as BlazorPdfName)?.Value == "DeviceN";
        int components = 1;
        // A Separation whose single colorant is /None produces no visible marks
        // (PDF 32000-1 §8.6.6.4); DeviceN counts as "None" only if every colorant is.
        bool isNone;
        if (isDeviceN && xref.FetchIfRef(arr.Count > 1 ? arr[1] : null) is List<object?> names)
        {
            components = names.Count;
            isNone = names.Count > 0 && names.All(n => (xref.FetchIfRef(n) as BlazorPdfName)?.Value == "None");
        }
        else
        {
            isNone = (xref.FetchIfRef(arr.Count > 1 ? arr[1] : null) as BlazorPdfName)?.Value == "None";
        }
        var alternate = Create(arr.Count > 2 ? arr[2] : null, xref, resources);
        var tint = BlazorPdfFunction.Create(arr.Count > 3 ? arr[3] : null, xref);
        return new BlazorPdfSeparationColorSpace(alternate, tint, components, isNone);
    }

    public override (byte, byte, byte) GetRgb(double[] comps)
    {
        // The /None colorant is a no-op: it never paints ink, so on the default
        // page it maps to white (the closest this HTML renderer can get to "no marks").
        if (_isNone)
        {
            return (255, 255, 255);
        }
        if (_tint is null)
        {
            byte v = ToByte(1 - (comps.Length > 0 ? comps[0] : 0));
            return (v, v, v);
        }
        double[] alt = _tint.Eval(comps);
        return _alternate.GetRgb(alt);
    }
}
