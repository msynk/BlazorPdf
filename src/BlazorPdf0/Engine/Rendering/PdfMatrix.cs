namespace BlazorPdf.Engine.Rendering;

/// <summary>
/// A 2D affine transform using the PDF/PostScript row-vector convention
/// <c>[x' y' 1] = [x y 1] · [[a b 0][c d 0][e f 1]]</c>.
/// </summary>
public readonly struct PdfMatrix(double a, double b, double c, double d, double e, double f)
{
    /// <summary>Matrix coefficient a.</summary>
    public double A { get; } = a;
    /// <summary>Matrix coefficient b.</summary>
    public double B { get; } = b;
    /// <summary>Matrix coefficient c.</summary>
    public double C { get; } = c;
    /// <summary>Matrix coefficient d.</summary>
    public double D { get; } = d;
    /// <summary>Matrix translation e (x).</summary>
    public double E { get; } = e;
    /// <summary>Matrix translation f (y).</summary>
    public double F { get; } = f;

    /// <summary>The identity transform.</summary>
    public static readonly PdfMatrix Identity = new(1, 0, 0, 1, 0, 0);

    /// <summary>Maps a point through the transform.</summary>
    public (double X, double Y) Apply(double x, double y) =>
        (A * x + C * y + E, B * x + D * y + F);

    /// <summary>Returns <c>this · other</c> (apply <c>this</c> first, then <c>other</c>).</summary>
    public PdfMatrix Multiply(PdfMatrix m) => new(
        A * m.A + B * m.C,
        A * m.B + B * m.D,
        C * m.A + D * m.C,
        C * m.B + D * m.D,
        E * m.A + F * m.C + m.E,
        E * m.B + F * m.D + m.F);

    /// <summary>A translation transform.</summary>
    public static PdfMatrix Translate(double tx, double ty) => new(1, 0, 0, 1, tx, ty);

    /// <summary>A scaling transform.</summary>
    public static PdfMatrix Scale(double sx, double sy) => new(sx, 0, 0, sy, 0, 0);

    /// <summary>The uniform scale magnitude (used to size device-space line widths).</summary>
    public double ScaleFactor => Math.Sqrt(Math.Abs(A * D - B * C));

    /// <summary>Returns the inverse transform, or identity when this matrix is singular.</summary>
    public PdfMatrix Invert()
    {
        var det = A * D - B * C;
        if (Math.Abs(det) < 1e-12) return Identity;

        var ia = D / det;
        var ib = -B / det;
        var ic = -C / det;
        var id = A / det;
        var ie = -(E * ia + F * ic);
        var iff = -(E * ib + F * id);
        return new PdfMatrix(ia, ib, ic, id, ie, iff);
    }
}
