// Clean-room C# port of color-space handling from pdf.js `src/core/colorspace.js`
// (the device, ICC-by-component, indexed and separation cases). See NOTICE.

using BlazorPdf.Core.Filters;
using BlazorPdf.Core.Functions;

namespace BlazorPdf.Core.Render;

/// <summary>
/// Converts color component values into RGB. Supports the device spaces,
/// ICCBased/Cal spaces (approximated by component count), Indexed palettes and
/// Separation/DeviceN tint transforms.
/// </summary>
public abstract class ColorSpace
{
    /// <summary>Number of color components consumed per sample.</summary>
    public abstract int Components { get; }

    /// <summary>Converts normalized (0..1) components to an 8-bit RGB triple.</summary>
    public abstract (byte R, byte G, byte B) GetRgb(double[] comps);

    /// <summary>Builds a color space from a PDF color-space object.</summary>
    public static ColorSpace Create(object? obj, IXRef xref, Dict? resources)
    {
        obj = xref.FetchIfRef(obj);

        if (obj is Name name)
        {
            switch (name.Value)
            {
                case "DeviceGray":
                case "G":
                case "CalGray":
                    return Gray;
                case "DeviceRGB":
                case "RGB":
                case "CalRGB":
                    return Rgb;
                case "DeviceCMYK":
                case "CMYK":
                    return Cmyk;
                case "Pattern":
                    return Rgb;
            }
            // A named space defined in the resource dictionary.
            if (resources?.Get("ColorSpace") is Dict csDict && csDict.Has(name.Value))
            {
                return Create(csDict.Get(name.Value), xref, resources);
            }
            return Gray;
        }

        if (obj is List<object?> arr && arr.Count > 0)
        {
            string kind = (xref.FetchIfRef(arr[0]) as Name)?.Value ?? "";
            switch (kind)
            {
                case "ICCBased":
                    if (xref.FetchIfRef(arr[1]) is PdfStream icc && icc.Dict is not null)
                    {
                        int n = icc.Dict.Get("N") is double dn ? (int)dn : 3;
                        return n switch { 1 => Gray, 4 => Cmyk, _ => Rgb };
                    }
                    return Rgb;
                case "CalRGB":
                case "Lab":
                    return Rgb;
                case "CalGray":
                    return Gray;
                case "Indexed":
                case "I":
                    return IndexedColorSpace.Build(arr, xref, resources);
                case "Separation":
                case "DeviceN":
                    return SeparationColorSpace.Build(arr, xref, resources);
                case "Pattern":
                    return arr.Count > 1 ? Create(arr[1], xref, resources) : Rgb;
            }
        }

        return Gray;
    }

    public static readonly ColorSpace Gray = new DeviceGray();
    public static readonly ColorSpace Rgb = new DeviceRgb();
    public static readonly ColorSpace Cmyk = new DeviceCmyk();

    protected static byte ToByte(double v) => (byte)Math.Clamp((int)Math.Round(v * 255), 0, 255);

    private sealed class DeviceGray : ColorSpace
    {
        public override int Components => 1;
        public override (byte, byte, byte) GetRgb(double[] c)
        {
            byte g = ToByte(c.Length > 0 ? c[0] : 0);
            return (g, g, g);
        }
    }

    private sealed class DeviceRgb : ColorSpace
    {
        public override int Components => 3;
        public override (byte, byte, byte) GetRgb(double[] c)
            => (ToByte(c.Length > 0 ? c[0] : 0), ToByte(c.Length > 1 ? c[1] : 0), ToByte(c.Length > 2 ? c[2] : 0));
    }

    private sealed class DeviceCmyk : ColorSpace
    {
        public override int Components => 4;
        public override (byte, byte, byte) GetRgb(double[] c)
        {
            double cy = c.Length > 0 ? c[0] : 0;
            double m = c.Length > 1 ? c[1] : 0;
            double y = c.Length > 2 ? c[2] : 0;
            double k = c.Length > 3 ? c[3] : 0;
            return (ToByte((1 - cy) * (1 - k)), ToByte((1 - m) * (1 - k)), ToByte((1 - y) * (1 - k)));
        }
    }
}

/// <summary>An Indexed (palette) color space.</summary>
internal sealed class IndexedColorSpace : ColorSpace
{
    private readonly ColorSpace _base;
    private readonly byte[] _lookup;
    private readonly int _hival;

    private IndexedColorSpace(ColorSpace baseCs, byte[] lookup, int hival)
    {
        _base = baseCs;
        _lookup = lookup;
        _hival = hival;
    }

    public override int Components => 1;

    public static IndexedColorSpace Build(List<object?> arr, IXRef xref, Dict? resources)
    {
        var baseCs = Create(arr.Count > 1 ? arr[1] : null, xref, resources);
        int hival = xref.FetchIfRef(arr.Count > 2 ? arr[2] : null) is double d ? (int)d : 0;
        byte[] lookup;
        object? lookupObj = xref.FetchIfRef(arr.Count > 3 ? arr[3] : null);
        if (lookupObj is PdfString s)
        {
            lookup = s.Bytes;
        }
        else if (lookupObj is PdfStream stream)
        {
            lookup = StreamDecoder.Decode(stream);
        }
        else
        {
            lookup = [];
        }
        return new IndexedColorSpace(baseCs, lookup, hival);
    }

    public override (byte, byte, byte) GetRgb(double[] comps)
    {
        int index = (int)Math.Round(comps.Length > 0 ? comps[0] : 0);
        index = Math.Clamp(index, 0, _hival);
        int n = _base.Components;
        var baseComps = new double[n];
        for (int i = 0; i < n; i++)
        {
            int pos = index * n + i;
            baseComps[i] = pos < _lookup.Length ? _lookup[pos] / 255.0 : 0;
        }
        return _base.GetRgb(baseComps);
    }
}

/// <summary>A Separation / DeviceN color space using a tint-transform function.</summary>
internal sealed class SeparationColorSpace : ColorSpace
{
    private readonly ColorSpace _alternate;
    private readonly PdfFunction? _tint;
    private readonly int _components;

    private SeparationColorSpace(ColorSpace alternate, PdfFunction? tint, int components)
    {
        _alternate = alternate;
        _tint = tint;
        _components = components;
    }

    public override int Components => _components;

    public static SeparationColorSpace Build(List<object?> arr, IXRef xref, Dict? resources)
    {
        // [/Separation name alt tint]  or  [/DeviceN [names] alt tint]
        bool isDeviceN = (xref.FetchIfRef(arr[0]) as Name)?.Value == "DeviceN";
        int components = 1;
        if (isDeviceN && xref.FetchIfRef(arr.Count > 1 ? arr[1] : null) is List<object?> names)
        {
            components = names.Count;
        }
        var alternate = Create(arr.Count > 2 ? arr[2] : null, xref, resources);
        var tint = PdfFunction.Create(arr.Count > 3 ? arr[3] : null, xref);
        return new SeparationColorSpace(alternate, tint, components);
    }

    public override (byte, byte, byte) GetRgb(double[] comps)
    {
        if (_tint is null)
        {
            byte v = ToByte(1 - (comps.Length > 0 ? comps[0] : 0));
            return (v, v, v);
        }
        double[] alt = _tint.Eval(comps);
        return _alternate.GetRgb(alt);
    }
}
