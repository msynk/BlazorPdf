// Clean-room C# port of the affine-transform helpers from pdf.js
// `src/shared/util.js` (Util.transform / applyTransform). See NOTICE.

using System.Globalization;

namespace BlazorPdf.Core.Geometry;

/// <summary>
/// A 2-D affine transform stored as the six PDF coefficients [a b c d e f],
/// mapping (x, y) to (a·x + c·y + e, b·x + d·y + f).
/// </summary>
public readonly struct Matrix
{
    public double A { get; }
    public double B { get; }
    public double C { get; }
    public double D { get; }
    public double E { get; }
    public double F { get; }

    public Matrix(double a, double b, double c, double d, double e, double f)
    {
        A = a; B = b; C = c; D = d; E = e; F = f;
    }

    /// <summary>The identity transform.</summary>
    public static readonly Matrix Identity = new(1, 0, 0, 1, 0, 0);

    /// <summary>
    /// Concatenates two transforms so that the result applies <paramref name="inner"/>
    /// first and <paramref name="outer"/> second. Equivalent to pdf.js
    /// <c>Util.transform(outer, inner)</c>.
    /// </summary>
    public static Matrix Concat(Matrix outer, Matrix inner) => new(
        outer.A * inner.A + outer.C * inner.B,
        outer.B * inner.A + outer.D * inner.B,
        outer.A * inner.C + outer.C * inner.D,
        outer.B * inner.C + outer.D * inner.D,
        outer.A * inner.E + outer.C * inner.F + outer.E,
        outer.B * inner.E + outer.D * inner.F + outer.F);

    /// <summary>Transforms the point (x, y).</summary>
    public (double X, double Y) Apply(double x, double y)
        => (A * x + C * y + E, B * x + D * y + F);

    /// <summary>Transforms the direction (dx, dy), ignoring translation.</summary>
    public (double X, double Y) ApplyDirection(double dx, double dy)
        => (A * dx + C * dy, B * dx + D * dy);

    /// <summary>An approximate uniform scale factor (used for line widths and font sizes).</summary>
    public double ScaleFactor
    {
        get
        {
            double sx = Math.Sqrt(A * A + B * B);
            double sy = Math.Sqrt(C * C + D * D);
            return (sx + sy) / 2.0;
        }
    }

    /// <summary>Renders the transform as an SVG <c>matrix(...)</c> string.</summary>
    public string ToSvg()
    {
        return string.Create(CultureInfo.InvariantCulture,
            $"matrix({A:0.####},{B:0.####},{C:0.####},{D:0.####},{E:0.####},{F:0.####})");
    }

    public override string ToString() => ToSvg();
}
