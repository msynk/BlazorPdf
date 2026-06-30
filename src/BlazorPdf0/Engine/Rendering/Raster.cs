namespace BlazorPdf.Engine.Rendering;

/// <summary>Separable PDF blend modes.</summary>
public enum BlendMode
{
    /// <summary>Source replaces backdrop.</summary>
    Normal,
    /// <summary>Multiplies backdrop and source.</summary>
    Multiply,
    /// <summary>Inverse-multiplies (lightens).</summary>
    Screen,
    /// <summary>Multiply or screen depending on backdrop.</summary>
    Overlay,
    /// <summary>Keeps the darker of the two.</summary>
    Darken,
    /// <summary>Keeps the lighter of the two.</summary>
    Lighten,
    /// <summary>Multiply or screen depending on source.</summary>
    HardLight,
    /// <summary>Softer overlay.</summary>
    SoftLight,
    /// <summary>Absolute difference.</summary>
    Difference,
    /// <summary>Lower-contrast difference.</summary>
    Exclusion,
}

/// <summary>
/// A software RGBA raster surface. Fills polygons with an anti-aliased scanline
/// algorithm (nonzero or even-odd winding) and strokes polylines by converting each
/// segment to a filled quad. Pure CPU; no GPU or browser involvement.
/// </summary>
public sealed class Raster
{
    private const int SubSamples = 4;

    private readonly byte[] _pixels;

    /// <summary>Surface width in pixels.</summary>
    public int Width { get; }
    /// <summary>Surface height in pixels.</summary>
    public int Height { get; }
    /// <summary>Raw RGBA pixel buffer.</summary>
    public byte[] Pixels => _pixels;

    /// <summary>Creates a transparent raster of the given size (minimum 1×1).</summary>
    public Raster(int width, int height)
    {
        Width = Math.Max(1, width);
        Height = Math.Max(1, height);
        _pixels = new byte[Width * Height * 4];
    }

    /// <summary>Fills the entire surface with a solid color.</summary>
    public void Clear(PdfColor color)
    {
        for (var i = 0; i < _pixels.Length; i += 4)
        {
            _pixels[i] = color.R;
            _pixels[i + 1] = color.G;
            _pixels[i + 2] = color.B;
            _pixels[i + 3] = color.A;
        }
    }

    /// <summary>Wraps the current pixels in a <see cref="RenderedImage"/>.</summary>
    public RenderedImage ToImage() => new(Width, Height, _pixels);

    /// <summary>Reads a pixel (for tests/inspection).</summary>
    public PdfColor GetPixel(int x, int y)
    {
        var i = (y * Width + x) * 4;
        return new PdfColor(_pixels[i], _pixels[i + 1], _pixels[i + 2], _pixels[i + 3]);
    }

    private readonly record struct Edge(double X0, double Y0, double X1, double Y1, int Winding);

    /// <summary>The active clip mask (coverage 0..255, length Width*Height), or null for none.</summary>
    public byte[]? Clip { get; set; }

    /// <summary>Constant alpha applied to all painting (fill/stroke alpha from ExtGState).</summary>
    public double GlobalAlpha { get; set; } = 1.0;

    /// <summary>Active separable blend mode.</summary>
    public BlendMode Mode { get; set; } = BlendMode.Normal;

    /// <summary>Fills a set of sub-paths (each a closed polygon in device space).</summary>
    public void FillPolygons(IReadOnlyList<IReadOnlyList<(double X, double Y)>> subpaths, PdfColor color, bool evenOdd)
    {
        Scan(subpaths, evenOdd, (y, coverage) => BlendRow(y, coverage, color));
    }

    /// <summary>Rasterizes sub-paths into a coverage mask (0..255), ignoring the active clip.</summary>
    public byte[] ComputeMask(IReadOnlyList<IReadOnlyList<(double X, double Y)>> subpaths, bool evenOdd)
    {
        var mask = new byte[Width * Height];
        Scan(subpaths, evenOdd, (y, coverage) =>
        {
            var row = y * Width;
            for (var x = 0; x < Width; x++)
            {
                var c = coverage[x];
                if (c <= 0) continue;
                mask[row + x] = (byte)Math.Min(255, (int)Math.Round((c > 1f ? 1f : c) * 255f));
            }
        });
        return mask;
    }

    private void Scan(IReadOnlyList<IReadOnlyList<(double X, double Y)>> subpaths, bool evenOdd, Action<int, float[]> rowSink)
    {
        var edges = new List<Edge>();
        var minY = double.MaxValue;
        var maxY = double.MinValue;

        foreach (var path in subpaths)
        {
            if (path.Count < 2) continue;
            for (var i = 0; i < path.Count; i++)
            {
                var p0 = path[i];
                var p1 = path[(i + 1) % path.Count];
                if (p0.Y == p1.Y) continue;

                edges.Add(p0.Y < p1.Y
                    ? new Edge(p0.X, p0.Y, p1.X, p1.Y, 1)
                    : new Edge(p1.X, p1.Y, p0.X, p0.Y, -1));

                minY = Math.Min(minY, Math.Min(p0.Y, p1.Y));
                maxY = Math.Max(maxY, Math.Max(p0.Y, p1.Y));
            }
        }

        if (edges.Count == 0) return;

        var yStart = Math.Max(0, (int)Math.Floor(minY));
        var yEnd = Math.Min(Height - 1, (int)Math.Ceiling(maxY));
        var coverage = new float[Width];
        var crossings = new List<(double X, int W)>();

        for (var y = yStart; y <= yEnd; y++)
        {
            Array.Clear(coverage, 0, Width);

            for (var s = 0; s < SubSamples; s++)
            {
                var sampleY = y + (s + 0.5) / SubSamples;
                crossings.Clear();

                foreach (var e in edges)
                {
                    if (sampleY < e.Y0 || sampleY >= e.Y1) continue;
                    var t = (sampleY - e.Y0) / (e.Y1 - e.Y0);
                    crossings.Add((e.X0 + t * (e.X1 - e.X0), e.Winding));
                }

                if (crossings.Count < 2) continue;
                crossings.Sort((a, b) => a.X.CompareTo(b.X));

                var winding = 0;
                for (var i = 0; i < crossings.Count - 1; i++)
                {
                    winding += crossings[i].W;
                    var inside = evenOdd ? (winding % 2 != 0) : (winding != 0);
                    if (inside)
                    {
                        AddSpan(coverage, crossings[i].X, crossings[i + 1].X, 1f / SubSamples);
                    }
                }
            }

            rowSink(y, coverage);
        }
    }

    /// <summary>Strokes a polyline by filling a quad per segment, with square joins at vertices.</summary>
    public void StrokePolyline(IReadOnlyList<(double X, double Y)> points, double width, PdfColor color)
    {
        if (points.Count < 2) return;
        var half = Math.Max(0.35, width / 2.0);

        for (var i = 0; i < points.Count - 1; i++)
        {
            var (x0, y0) = points[i];
            var (x1, y1) = points[i + 1];
            var dx = x1 - x0;
            var dy = y1 - y0;
            var len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 1e-6) continue;

            var nx = -dy / len * half;
            var ny = dx / len * half;

            var quad = new (double, double)[]
            {
                (x0 + nx, y0 + ny),
                (x1 + nx, y1 + ny),
                (x1 - nx, y1 - ny),
                (x0 - nx, y0 - ny),
            };
            FillPolygons([quad], color, evenOdd: false);
        }

        // Fill a small square at each corner to close the gaps left between butt-capped
        // segments (e.g. rectangle borders). Skipped for thin strokes where it is moot.
        if (half <= 0.6) return;
        var jdx = points[0].X - points[^1].X;
        var jdy = points[0].Y - points[^1].Y;
        var closed = jdx * jdx + jdy * jdy < 1e-6;
        for (var i = 0; i < points.Count; i++)
        {
            var join = (i != 0 && i != points.Count - 1) || (closed && i == 0);
            if (!join) continue;
            var (jx, jy) = points[i];
            var sq = new (double, double)[]
            {
                (jx - half, jy - half), (jx + half, jy - half),
                (jx + half, jy + half), (jx - half, jy + half),
            };
            FillPolygons([sq], color, evenOdd: false);
        }
    }

    private static void AddSpan(float[] coverage, double xa, double xb, float amount)
    {
        if (xb < xa) (xa, xb) = (xb, xa);
        xa = Math.Max(0, xa);
        xb = Math.Min(coverage.Length, xb);
        if (xb <= xa) return;

        var iL = (int)Math.Floor(xa);
        var iR = (int)Math.Floor(xb - 1e-9);

        if (iL == iR)
        {
            coverage[iL] += amount * (float)(xb - xa);
            return;
        }

        coverage[iL] += amount * (float)(iL + 1 - xa);
        for (var i = iL + 1; i < iR; i++) coverage[i] += amount;
        if (iR < coverage.Length) coverage[iR] += amount * (float)(xb - iR);
    }

    private void BlendRow(int y, float[] coverage, PdfColor color)
    {
        var rowOffset = y * Width * 4;
        var clipRow = y * Width;
        var sa = color.A / 255.0 * GlobalAlpha;

        for (var x = 0; x < Width; x++)
        {
            var cov = coverage[x];
            if (cov <= 0) continue;
            if (cov > 1f) cov = 1f;

            var alpha = cov * sa;
            if (Clip is { } clip) alpha *= clip[clipRow + x] / 255.0;
            if (alpha <= 0) continue;

            CompositePixel(rowOffset + x * 4, color, alpha);
        }
    }

    private void CompositePixel(int i, PdfColor color, double alpha)
    {
        double dr = _pixels[i] / 255.0, dg = _pixels[i + 1] / 255.0, db = _pixels[i + 2] / 255.0;
        double sr = color.R / 255.0, sg = color.G / 255.0, sb = color.B / 255.0;

        if (Mode != BlendMode.Normal)
        {
            sr = Blend(dr, sr);
            sg = Blend(dg, sg);
            sb = Blend(db, sb);
        }

        _pixels[i] = (byte)Math.Round((sr * alpha + dr * (1 - alpha)) * 255);
        _pixels[i + 1] = (byte)Math.Round((sg * alpha + dg * (1 - alpha)) * 255);
        _pixels[i + 2] = (byte)Math.Round((sb * alpha + db * (1 - alpha)) * 255);
        _pixels[i + 3] = (byte)Math.Min(255, _pixels[i + 3] + alpha * 255);
    }

    private double Blend(double cb, double cs) => Mode switch
    {
        BlendMode.Multiply => cb * cs,
        BlendMode.Screen => cb + cs - cb * cs,
        BlendMode.Darken => Math.Min(cb, cs),
        BlendMode.Lighten => Math.Max(cb, cs),
        BlendMode.Overlay => HardLight(cs, cb),
        BlendMode.HardLight => HardLight(cb, cs),
        BlendMode.Difference => Math.Abs(cb - cs),
        BlendMode.Exclusion => cb + cs - 2 * cb * cs,
        BlendMode.SoftLight => SoftLight(cb, cs),
        _ => cs,
    };

    private static double HardLight(double cb, double cs) =>
        cs <= 0.5 ? cb * (2 * cs) : cb + (2 * cs - 1) - cb * (2 * cs - 1);

    private static double SoftLight(double cb, double cs)
    {
        if (cs <= 0.5) return cb - (1 - 2 * cs) * cb * (1 - cb);
        var d = cb <= 0.25 ? ((16 * cb - 12) * cb + 4) * cb : Math.Sqrt(cb);
        return cb + (2 * cs - 1) * (d - cb);
    }

    /// <summary>Alpha-blends a single color over a pixel (source-over), honoring the clip.</summary>
    public void BlendPixel(int x, int y, PdfColor color, double alpha)
    {
        if (x < 0 || y < 0 || x >= Width || y >= Height || alpha <= 0) return;
        alpha *= GlobalAlpha;
        if (alpha > 1) alpha = 1;
        if (Clip is { } clip) alpha *= clip[y * Width + x] / 255.0;
        if (alpha <= 0) return;

        CompositePixel((y * Width + x) * 4, color, alpha);
    }

    /// <summary>Returns a copy of this raster rotated clockwise by 0/90/180/270 degrees.</summary>
    public Raster Rotated(int degrees)
    {
        degrees = ((degrees % 360) + 360) % 360;
        if (degrees == 0) return this;

        var (nw, nh) = degrees is 90 or 270 ? (Height, Width) : (Width, Height);
        var dst = new Raster(nw, nh);

        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                var src = (y * Width + x) * 4;
                var (dx, dy) = degrees switch
                {
                    90 => (Height - 1 - y, x),
                    180 => (Width - 1 - x, Height - 1 - y),
                    _ => (y, Width - 1 - x), // 270
                };
                var di = (dy * nw + dx) * 4;
                dst._pixels[di] = _pixels[src];
                dst._pixels[di + 1] = _pixels[src + 1];
                dst._pixels[di + 2] = _pixels[src + 2];
                dst._pixels[di + 3] = _pixels[src + 3];
            }
        }

        return dst;
    }
}
