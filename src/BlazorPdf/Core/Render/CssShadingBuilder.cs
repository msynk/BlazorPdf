// Axial/radial shading handling, emitted as CSS gradients for the HTML renderer.

using System.Globalization;
using System.Text;
using BlazorPdf.Core.Functions;
using BlazorPdf.Core.Geometry;

namespace BlazorPdf.Core.Render;

/// <summary>
/// Translates PDF axial (type 2) and radial (type 3) shadings into CSS
/// <c>linear-gradient</c>/<c>radial-gradient</c> background values, sampling the
/// shading's color function into gradient stops. Coordinates are expressed in
/// device pixels relative to the page box (origin top-left).
/// </summary>
internal static class CssShadingBuilder
{
    private const int StopCount = 24;

    /// <summary>
    /// Builds a CSS background value for <paramref name="shading"/>, or
    /// <c>null</c> if the shading type is unsupported.
    /// </summary>
    public static string? Build(Dict shading, IXRef xref, Dict? resources, Matrix ctm,
        double viewW, double viewH)
    {
        int type = shading.Get("ShadingType") is double d ? (int)d : 0;
        var cs = ColorSpace.Create(shading.Get("ColorSpace"), xref, resources);
        var fn = PdfFunction.Create(shading.Get("Function"), xref);

        double[] domain = ReadNumbers(shading.Get("Domain"));
        if (domain.Length < 2)
        {
            domain = [0, 1];
        }
        double[] coords = ReadNumbers(shading.Get("Coords"));

        return type switch
        {
            2 when coords.Length >= 4 => BuildAxial(coords, domain, cs, fn, ctm, viewW, viewH),
            3 when coords.Length >= 6 => BuildRadial(coords, domain, cs, fn, ctm),
            _ => null,
        };
    }

    private static string BuildAxial(double[] c, double[] domain, ColorSpace cs, PdfFunction? fn,
        Matrix ctm, double viewW, double viewH)
    {
        var (x0, y0) = ctm.Apply(c[0], c[1]);
        var (x1, y1) = ctm.Apply(c[2], c[3]);
        double dx = x1 - x0, dy = y1 - y0;
        double len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1e-6)
        {
            // Degenerate axis: fall back to the mid color.
            var (r, g, b) = SampleRgb(domain, 0.5, cs, fn);
            return $"rgb({r},{g},{b})";
        }

        double ux = dx / len, uy = dy / len;
        // CSS gradient angle: 0deg points up (-y), increasing clockwise toward +x.
        double angleDeg = Math.Atan2(dx, -dy) * 180.0 / Math.PI;
        double a = angleDeg * Math.PI / 180.0;
        double gradientLen = Math.Abs(viewW * Math.Sin(a)) + Math.Abs(viewH * Math.Cos(a));
        if (gradientLen < 1e-6)
        {
            gradientLen = len;
        }
        double cx = viewW / 2.0, cy = viewH / 2.0;

        double Pct(double px, double py)
            => ((px - cx) * ux + (py - cy) * uy + gradientLen / 2.0) / gradientLen * 100.0;

        double p0 = Pct(x0, y0);
        double p1 = Pct(x1, y1);

        var stops = new List<(double Pos, int R, int G, int B)>(StopCount);
        for (int i = 0; i < StopCount; i++)
        {
            double frac = (double)i / (StopCount - 1);
            double pos = p0 + frac * (p1 - p0);
            var (r, g, b) = SampleRgb(domain, frac, cs, fn);
            stops.Add((pos, r, g, b));
        }
        stops.Sort((l, r) => l.Pos.CompareTo(r.Pos));

        var sb = new StringBuilder();
        sb.Append(string.Create(CultureInfo.InvariantCulture, $"linear-gradient({angleDeg:0.##}deg"));
        foreach (var s in stops)
        {
            sb.Append(string.Create(CultureInfo.InvariantCulture, $",rgb({s.R},{s.G},{s.B}) {s.Pos:0.##}%"));
        }
        sb.Append(')');
        return sb.ToString();
    }

    private static string BuildRadial(double[] c, double[] domain, ColorSpace cs, PdfFunction? fn, Matrix ctm)
    {
        double scale = ctm.ScaleFactor;
        var (cx, cy) = ctm.Apply(c[3], c[4]);
        double r = Math.Max(c[5] * scale, 0.01);

        var sb = new StringBuilder();
        sb.Append(string.Create(CultureInfo.InvariantCulture,
            $"radial-gradient(circle {r:0.##}px at {cx:0.##}px {cy:0.##}px"));
        for (int i = 0; i < StopCount; i++)
        {
            double frac = (double)i / (StopCount - 1);
            var (rr, gg, bb) = SampleRgb(domain, frac, cs, fn);
            sb.Append(string.Create(CultureInfo.InvariantCulture, $",rgb({rr},{gg},{bb}) {frac * 100:0.##}%"));
        }
        sb.Append(')');
        return sb.ToString();
    }

    private static (int R, int G, int B) SampleRgb(double[] domain, double frac, ColorSpace cs, PdfFunction? fn)
    {
        double t = domain[0] + frac * (domain[1] - domain[0]);
        double[] comps = fn?.Eval([t]) ?? [t];
        return cs.GetRgb(comps);
    }

    private static double[] ReadNumbers(object? value)
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
}
