// Color-space handling: the device, ICC-by-component, indexed and separation cases.

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

    /// <summary>
    /// The initial color components for this space when it is selected with
    /// <c>cs</c>/<c>CS</c> (PDF 32000-1 §8.6.3): zero for device/CIE spaces,
    /// one for Separation/DeviceN tints.
    /// </summary>
    public virtual double[] DefaultComponents()
    {
        var comps = new double[Components];
        return comps;
    }

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
                    return ResolveDefault(resources, "DefaultGray", xref) ?? Gray;
                case "DeviceRGB":
                case "RGB":
                case "CalRGB":
                    return ResolveDefault(resources, "DefaultRGB", xref) ?? Rgb;
                case "DeviceCMYK":
                case "CMYK":
                    return ResolveDefault(resources, "DefaultCMYK", xref) ?? Cmyk;
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
                    return Rgb;
                case "Lab":
                    return LabColorSpace.Build(arr, xref);
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

    // Device names in a resource may be redirected to a calibrated default via
    // the resource's ColorSpace dict (/DefaultRGB etc.). Resolve that, but guard
    // against a default that just names the same device space (infinite loop).
    private static ColorSpace? ResolveDefault(Dict? resources, string key, IXRef xref)
    {
        if (resources?.Get("ColorSpace") is not Dict csDict || !csDict.Has(key))
        {
            return null;
        }
        if (xref.FetchIfRef(csDict.Get(key)) is Name dn &&
            dn.Value is "DeviceRGB" or "DeviceGray" or "DeviceCMYK" or "RGB" or "G" or "CMYK")
        {
            return null;
        }
        return Create(csDict.Get(key), xref, resources);
    }

    /// <summary>
    /// The per-component value ranges for this space (used, e.g., to scale an
    /// Indexed palette's lookup bytes into the base space). Defaults to [0,1] per
    /// component; CIE spaces with wider ranges (Lab) override this.
    /// </summary>
    public virtual double[] ComponentRanges()
    {
        var r = new double[Components * 2];
        for (int i = 0; i < Components; i++)
        {
            r[i * 2] = 0;
            r[i * 2 + 1] = 1;
        }
        return r;
    }

    /// <summary>
    /// Converts a CMYK quadruple (each 0..1) to sRGB using the polynomial fit
    /// from pdf.js (<c>DeviceCmykCS</c>), which is far closer to a real CMYK
    /// profile than the naive <c>(1-c)(1-k)</c> multiply. Single source of truth
    /// shared by the image, fill and stroke paths.
    /// </summary>
    public static (byte R, byte G, byte B) CmykToRgb(double c, double m, double y, double k)
    {
        c = Math.Clamp(c, 0, 1);
        m = Math.Clamp(m, 0, 1);
        y = Math.Clamp(y, 0, 1);
        k = Math.Clamp(k, 0, 1);

        double r = 255 +
            c * (-4.387332384609988 * c + 54.48615194189176 * m + 18.82290502165302 * y + 212.25662451639585 * k - 285.2331026137004) +
            m * (1.7149763477362134 * m - 5.6096736904047315 * y - 17.873870861415444 * k - 5.497006427196366) +
            y * (-2.5217340131683033 * y - 21.248923337353073 * k + 17.5119270841813) +
            k * (-21.86122147463605 * k - 189.48180835922747);
        double g = 255 +
            c * (8.841041422036149 * c + 60.118027045597366 * m + 6.871425592049007 * y + 31.159100130055922 * k - 79.2970844816548) +
            m * (-15.310361306967817 * m + 17.575251261109482 * y + 131.35250912493976 * k - 190.9453302588951) +
            y * (4.444339102852739 * y + 9.8632861493405 * k - 24.86741582555878) +
            k * (-20.737325471181034 * k - 187.80453709719578);
        double b = 255 +
            c * (0.8842522430003296 * c + 8.078677503112928 * m + 30.89978309703729 * y - 0.23883238689178934 * k - 14.183576799673286) +
            m * (10.49593273432072 * m + 63.02378494754052 * y + 50.606957656360734 * k - 112.23884253719248) +
            y * (0.03296041114873217 * y + 115.60384449646641 * k - 193.58209356861505) +
            k * (-22.33816807309886 * k - 180.12613974708367);

        return (Clamp255(r), Clamp255(g), Clamp255(b));
    }

    private static byte Clamp255(double v) => (byte)Math.Clamp((int)Math.Round(v), 0, 255);

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
            return CmykToRgb(cy, m, y, k);
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
        double[] ranges = _base.ComponentRanges();
        var baseComps = new double[n];
        for (int i = 0; i < n; i++)
        {
            int pos = index * n + i;
            double raw = pos < _lookup.Length ? _lookup[pos] / 255.0 : 0;
            // Scale the 0..255 lookup byte into the base space's component range,
            // so an Indexed-over-Lab (or any non-0..1 base) resolves correctly.
            double lo = ranges[i * 2];
            double hi = ranges[i * 2 + 1];
            baseComps[i] = lo + raw * (hi - lo);
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

    public override double[] DefaultComponents()
    {
        // Separation/DeviceN initial color is full tint (1.0) on each component.
        var comps = new double[_components];
        Array.Fill(comps, 1.0);
        return comps;
    }

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

/// <summary>
/// A CIE 1976 L*a*b* color space. Converts L*a*b* (with the document's white
/// point) to sRGB via the XYZ intermediate (PDF 32000-1 §8.6.5.4).
/// </summary>
internal sealed class LabColorSpace : ColorSpace
{
    private readonly double _xw, _yw, _zw;
    private readonly double _amin, _amax, _bmin, _bmax;

    private LabColorSpace(double xw, double yw, double zw, double[] range)
    {
        _xw = xw; _yw = yw; _zw = zw;
        _amin = range[0]; _amax = range[1]; _bmin = range[2]; _bmax = range[3];
    }

    public override int Components => 3;

    // L* spans [0,100]; a*/b* span the space's Range. Used to scale Indexed
    // palette bytes into Lab component space.
    public override double[] ComponentRanges() => [0, 100, _amin, _amax, _bmin, _bmax];

    public static LabColorSpace Build(List<object?> arr, IXRef xref)
    {
        double xw = 1, yw = 1, zw = 1;
        double[] range = [-100, 100, -100, 100];
        if (xref.FetchIfRef(arr.Count > 1 ? arr[1] : null) is Dict dict)
        {
            if (dict.Get("WhitePoint") is List<object?> wp && wp.Count >= 3)
            {
                xw = Num(wp[0]); yw = Num(wp[1]); zw = Num(wp[2]);
            }
            if (dict.Get("Range") is List<object?> r && r.Count >= 4)
            {
                range = [Num(r[0]), Num(r[1]), Num(r[2]), Num(r[3])];
            }
        }
        return new LabColorSpace(xw, yw, zw, range);
    }

    public override (byte, byte, byte) GetRgb(double[] c)
    {
        double ls = Math.Clamp(c.Length > 0 ? c[0] : 0, 0, 100);
        double as_ = Math.Clamp(c.Length > 1 ? c[1] : 0, _amin, _amax);
        double bs = Math.Clamp(c.Length > 2 ? c[2] : 0, _bmin, _bmax);

        double m = (ls + 16) / 116;
        double l = m + as_ / 500;
        double n = m - bs / 200;

        double x = _xw * Decode(l);
        double y = _yw * Decode(m);
        double z = _zw * Decode(n);

        // XYZ (D50-ish, per the white point) to linear sRGB.
        double r = 3.1339 * x - 1.6169 * y - 0.4906 * z;
        double g = -0.9785 * x + 1.9160 * y + 0.0333 * z;
        double b = 0.0720 * x - 0.2290 * y + 1.4057 * z;

        return (ToByte(Gamma(r)), ToByte(Gamma(g)), ToByte(Gamma(b)));
    }

    private static double Decode(double t)
        => t >= 6.0 / 29.0 ? t * t * t : 3 * (6.0 / 29.0) * (6.0 / 29.0) * (t - 4.0 / 29.0);

    private static double Gamma(double v)
    {
        v = Math.Clamp(v, 0, 1);
        return v <= 0.0031308 ? 12.92 * v : 1.055 * Math.Pow(v, 1 / 2.4) - 0.055;
    }

    private static double Num(object? o) => o is double d ? d : 0;
}
