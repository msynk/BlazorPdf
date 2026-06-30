// Clean-room C# port of axial/radial shading handling from pdf.js
// `src/core/pattern.js` (RadialAxialShading), emitted as SVG gradients.
// See NOTICE.

using System.Globalization;
using System.Text;
using BlazorPdf.Core.Functions;
using BlazorPdf.Core.Geometry;

namespace BlazorPdf.Core.Render;

/// <summary>
/// Translates PDF axial (type 2) and radial (type 3) shadings into SVG gradient
/// definitions, sampling the shading's color function into gradient stops.
/// </summary>
internal static class ShadingBuilder
{
    private const int StopCount = 24;

    /// <summary>
    /// Builds a gradient definition for <paramref name="shading"/> and returns
    /// its <c>url(#id)</c> fill reference, or <c>null</c> if unsupported.
    /// </summary>
    public static string? Build(Dict shading, IXRef xref, Dict? resources, Matrix ctm, string id, StringBuilder defs)
    {
        int type = shading.Get("ShadingType") is double d ? (int)d : 0;
        var cs = ColorSpace.Create(shading.Get("ColorSpace"), xref, resources);
        var fn = PdfFunction.Create(shading.Get("Function"), xref);

        double[] domain = ReadNumbers(shading.Get("Domain"));
        if (domain.Length < 2)
        {
            domain = [0, 1];
        }
        bool[] extend = ReadExtend(shading.Get("Extend"));
        double[] coords = ReadNumbers(shading.Get("Coords"));

        return type switch
        {
            2 when coords.Length >= 4 => BuildAxial(coords, domain, extend, cs, fn, ctm, id, defs),
            3 when coords.Length >= 6 => BuildRadial(coords, domain, extend, cs, fn, ctm, id, defs),
            _ => null,
        };
    }

    private static string BuildAxial(double[] c, double[] domain, bool[] extend,
        ColorSpace cs, PdfFunction? fn, Matrix ctm, string id, StringBuilder defs)
    {
        var (x0, y0) = ctm.Apply(c[0], c[1]);
        var (x1, y1) = ctm.Apply(c[2], c[3]);

        defs.Append(string.Create(CultureInfo.InvariantCulture,
            $"<linearGradient id=\"{id}\" gradientUnits=\"userSpaceOnUse\" x1=\"{x0:0.##}\" y1=\"{y0:0.##}\" x2=\"{x1:0.##}\" y2=\"{y1:0.##}\" spreadMethod=\"pad\">"));
        AppendStops(defs, domain, cs, fn);
        defs.Append("</linearGradient>");
        return $"url(#{id})";
    }

    private static string BuildRadial(double[] c, double[] domain, bool[] extend,
        ColorSpace cs, PdfFunction? fn, Matrix ctm, string id, StringBuilder defs)
    {
        // Use the outer circle for cx/cy/r and the inner circle as the focus.
        double scale = ctm.ScaleFactor;
        var (fx, fy) = ctm.Apply(c[0], c[1]);
        var (cx, cy) = ctm.Apply(c[3], c[4]);
        double fr = c[2] * scale;
        double r = c[5] * scale;

        defs.Append(string.Create(CultureInfo.InvariantCulture,
            $"<radialGradient id=\"{id}\" gradientUnits=\"userSpaceOnUse\" cx=\"{cx:0.##}\" cy=\"{cy:0.##}\" r=\"{Math.Max(r, 0.01):0.##}\" fx=\"{fx:0.##}\" fy=\"{fy:0.##}\" fr=\"{Math.Max(fr, 0):0.##}\" spreadMethod=\"pad\">"));
        AppendStops(defs, domain, cs, fn);
        defs.Append("</radialGradient>");
        return $"url(#{id})";
    }

    private static void AppendStops(StringBuilder defs, double[] domain, ColorSpace cs, PdfFunction? fn)
    {
        for (int i = 0; i < StopCount; i++)
        {
            double frac = (double)i / (StopCount - 1);
            double t = domain[0] + frac * (domain[1] - domain[0]);
            double[] comps = fn?.Eval([t]) ?? [t];
            var (r, g, b) = cs.GetRgb(comps);
            defs.Append(string.Create(CultureInfo.InvariantCulture,
                $"<stop offset=\"{frac * 100:0.#}%\" stop-color=\"rgb({r},{g},{b})\"/>"));
        }
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

    private static bool[] ReadExtend(object? value)
    {
        if (value is List<object?> arr && arr.Count >= 2)
        {
            return [arr[0] is bool a && a, arr[1] is bool b && b];
        }
        return [false, false];
    }
}
