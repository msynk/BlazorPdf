// Clean-room C# renderer inspired by the pdf.js display layer
// (`src/display/canvas.js` for the operator walk and `src/core/evaluator.js`
// for content evaluation). Unlike the SVG backend, this emits plain HTML DOM:
// <div> with CSS `clip-path: path()` for vector fills and clips, filled <div>
// outlines for strokes, <img> for rasters, <span> for selectable text and CSS
// gradients for shadings. See NOTICE.

using System.Globalization;
using System.Text;
using BlazorPdf.Core.Content;
using BlazorPdf.Core.Fonts;
using BlazorPdf.Core.Geometry;

namespace BlazorPdf.Core.Render;

/// <summary>
/// Renders a <see cref="PdfPage"/> to an HTML fragment. The page is a single
/// positioned <c>&lt;div&gt;</c> sized in PDF points; all content is laid out
/// in device pixels and scaled to fit via the <c>--bp-scale</c> CSS variable so
/// text stays selectable and graphics stay resolution independent.
/// </summary>
public sealed class HtmlRenderer
{
    private const int MaxFormDepth = 12;
    private const int CurveSegments = 12;
    private const double AscentFactor = 0.8; // approximate baseline offset

    private readonly PdfPage _page;
    private readonly IXRef _xref;
    private readonly Dictionary<string, PdfFont> _fontCache = new();

    private GraphicsState _state = new();
    private readonly Stack<GraphicsState> _stack = new();
    private readonly Stack<int> _groupDepthStack = new();
    private Dict? _resources;
    private Matrix _baseMatrix = Matrix.Identity;
    private double _viewW;
    private double _viewH;

    // Current path under construction (device space). The SVG-syntax string is
    // reused for CSS clip-path; the flattened subpaths drive stroke outlining.
    private readonly StringBuilder _pathData = new();
    private readonly List<List<(double X, double Y)>> _subpaths = new();
    private List<(double X, double Y)>? _currentSub;
    private double _curX, _curY;
    private double _startX, _startY;
    private bool? _pendingClipEvenOdd;

    private Matrix _textMatrix = Matrix.Identity;
    private Matrix _textLineMatrix = Matrix.Identity;

    private readonly StringBuilder _fontFaces = new();
    private readonly HashSet<string> _emittedFamilies = new();
    private readonly Dictionary<string, string?> _patternCache = new();
    private StringBuilder _html = new();
    private int _openGroups;
    private int _formDepth;

    private readonly int _rotationOffset;

    public HtmlRenderer(PdfPage page, IXRef xref, int rotationOffset = 0)
    {
        _page = page;
        _xref = xref;
        _resources = page.Resources;
        _rotationOffset = ((rotationOffset % 360) + 360) % 360;
    }

    /// <summary>Renders the page and returns a single positioned <c>&lt;div&gt;</c>.</summary>
    public string Render()
    {
        double[] mb = _page.MediaBox;
        double x0 = Math.Min(mb[0], mb[2]);
        double y0 = Math.Min(mb[1], mb[3]);
        double x1 = Math.Max(mb[0], mb[2]);
        double y1 = Math.Max(mb[1], mb[3]);
        double w = x1 - x0;
        double h = y1 - y0;

        (Matrix baseMatrix, double viewW, double viewH) = BuildViewport(
            ((_page.Rotate + _rotationOffset) % 360 + 360) % 360, x0, y0, x1, y1, w, h);
        _state.Ctm = baseMatrix;
        _baseMatrix = baseMatrix;
        _viewW = viewW;
        _viewH = viewH;

        List<Operation> ops;
        try
        {
            ops = new ContentParser(_page.GetContentBytes()).Parse();
        }
        catch (Exception ex)
        {
            ops = new List<Operation>();
            _html.Append($"<!-- content parse error: {Escape(ex.Message)} -->");
        }

        RunOps(ops);

        while (_openGroups > 0)
        {
            _html.Append("</div>");
            _openGroups--;
        }

        RenderAnnotations();

        var sb = new StringBuilder();
        sb.Append(string.Create(CultureInfo.InvariantCulture,
            $"<div class=\"bp-html-page\" style=\"position:absolute;left:0;top:0;width:{viewW:0.##}px;height:{viewH:0.##}px;overflow:hidden;background:#fff;color:#000;transform:scale(var(--bp-scale,1));transform-origin:top left\">"));
        if (_fontFaces.Length > 0)
        {
            sb.Append("<style>").Append(_fontFaces).Append("</style>");
        }
        sb.Append(_html);
        sb.Append("</div>");
        return sb.ToString();
    }

    private void RunOps(List<Operation> ops)
    {
        foreach (var op in ops)
        {
            Execute(op);
        }
    }

    private static (Matrix, double, double) BuildViewport(
        int rotate, double x0, double y0, double x1, double y1, double w, double h)
    {
        return rotate switch
        {
            90 => (new Matrix(0, 1, 1, 0, -y0, -x0), h, w),
            180 => (new Matrix(-1, 0, 0, 1, x1, -y0), w, h),
            270 => (new Matrix(0, -1, -1, 0, y1, x1), h, w),
            _ => (new Matrix(1, 0, 0, -1, -x0, y1), w, h),
        };
    }

    private void Execute(Operation op)
    {
        switch (op.Operator)
        {
            // Graphics state.
            case "q":
                _stack.Push(_state.Clone());
                _groupDepthStack.Push(_openGroups);
                break;
            case "Q":
                if (_stack.Count > 0)
                {
                    _state = _stack.Pop();
                    int target = _groupDepthStack.Count > 0 ? _groupDepthStack.Pop() : 0;
                    while (_openGroups > target)
                    {
                        _html.Append("</div>");
                        _openGroups--;
                    }
                }
                break;
            case "cm":
                _state.Ctm = Matrix.Concat(_state.Ctm,
                    new Matrix(op.Num(0), op.Num(1), op.Num(2), op.Num(3), op.Num(4), op.Num(5)));
                break;
            case "w": _state.LineWidth = op.Num(0); break;
            case "d": SetDash(op); break;
            case "J": _state.LineCap = (int)op.Num(0); break;
            case "j": _state.LineJoin = (int)op.Num(0); break;
            case "M": break; // miter limit: not modeled by the fill-based stroker
            case "ri": case "i": break; // rendering intent / flatness: no-op
            case "gs": ApplyExtGState(op); break;

            // Colors.
            case "cs": _state.FillColorSpace = ResolveColorSpace(op); SetDefaultColor(false); break;
            case "CS": _state.StrokeColorSpace = ResolveColorSpace(op); SetDefaultColor(true); break;
            case "g": _state.FillColorSpace = ColorSpace.Gray; _state.FillColor = Gray(op.Num(0)); _state.FillPattern = null; break;
            case "G": _state.StrokeColorSpace = ColorSpace.Gray; _state.StrokeColor = Gray(op.Num(0)); break;
            case "rg": _state.FillColorSpace = ColorSpace.Rgb; _state.FillColor = Rgb(op.Num(0), op.Num(1), op.Num(2)); _state.FillPattern = null; break;
            case "RG": _state.StrokeColorSpace = ColorSpace.Rgb; _state.StrokeColor = Rgb(op.Num(0), op.Num(1), op.Num(2)); break;
            case "k": _state.FillColorSpace = ColorSpace.Cmyk; _state.FillColor = Cmyk(op.Num(0), op.Num(1), op.Num(2), op.Num(3)); _state.FillPattern = null; break;
            case "K": _state.StrokeColorSpace = ColorSpace.Cmyk; _state.StrokeColor = Cmyk(op.Num(0), op.Num(1), op.Num(2), op.Num(3)); break;
            case "sc":
            case "scn": SetFillColorN(op); break;
            case "SC":
            case "SCN": SetStrokeColorN(op); break;

            // Path construction.
            case "m": MoveTo(op.Num(0), op.Num(1)); break;
            case "l": LineTo(op.Num(0), op.Num(1)); break;
            case "c": CurveTo(op.Num(0), op.Num(1), op.Num(2), op.Num(3), op.Num(4), op.Num(5)); break;
            case "v": CurveTo(_curX, _curY, op.Num(0), op.Num(1), op.Num(2), op.Num(3)); break;
            case "y": CurveTo(op.Num(0), op.Num(1), op.Num(2), op.Num(3), op.Num(2), op.Num(3)); break;
            case "re": Rectangle(op.Num(0), op.Num(1), op.Num(2), op.Num(3)); break;
            case "h": ClosePath(); break;

            // Path painting.
            case "S": PaintPath(true, false, false); break;
            case "s": ClosePath(); PaintPath(true, false, false); break;
            case "f":
            case "F": PaintPath(false, true, false); break;
            case "f*": PaintPath(false, true, true); break;
            case "B": PaintPath(true, true, false); break;
            case "B*": PaintPath(true, true, true); break;
            case "b": ClosePath(); PaintPath(true, true, false); break;
            case "b*": ClosePath(); PaintPath(true, true, true); break;
            case "n": EndPathNoPaint(); break;
            case "W": _pendingClipEvenOdd = false; break;
            case "W*": _pendingClipEvenOdd = true; break;

            // Shadings and XObjects.
            case "sh": PaintShading(op); break;
            case "Do": DoXObject(op); break;
            case "INLINE_IMAGE": DrawInlineImage(op); break;

            // Text objects.
            case "BT":
                _textMatrix = Matrix.Identity;
                _textLineMatrix = Matrix.Identity;
                break;
            case "ET": break;
            case "Tc": _state.CharSpacing = op.Num(0); break;
            case "Tw": _state.WordSpacing = op.Num(0); break;
            case "Tz": _state.HorizScale = op.Num(0) / 100.0; break;
            case "TL": _state.Leading = op.Num(0); break;
            case "Ts": _state.TextRise = op.Num(0); break;
            case "Tr": _state.RenderMode = (int)op.Num(0); break;
            case "Tf": SetFont(op); break;
            case "Td": TextMove(op.Num(0), op.Num(1)); break;
            case "TD": _state.Leading = -op.Num(1); TextMove(op.Num(0), op.Num(1)); break;
            case "Tm":
                _textLineMatrix = new Matrix(op.Num(0), op.Num(1), op.Num(2), op.Num(3), op.Num(4), op.Num(5));
                _textMatrix = _textLineMatrix;
                break;
            case "T*": TextMove(0, -_state.Leading); break;
            case "Tj": ShowText(op.Operands.Count > 0 ? op.Operands[0] : null); break;
            case "TJ": ShowTextArray(op.Operands.Count > 0 ? op.Operands[0] as List<object?> : null); break;
            case "'":
                TextMove(0, -_state.Leading);
                ShowText(op.Operands.Count > 0 ? op.Operands[0] : null);
                break;
            case "\"":
                _state.WordSpacing = op.Num(0);
                _state.CharSpacing = op.Num(1);
                TextMove(0, -_state.Leading);
                ShowText(op.Operands.Count > 2 ? op.Operands[2] : null);
                break;
        }
    }

    // ----- Path building (coordinates transformed to device space) -----

    private void StartSub(double dx, double dy)
    {
        _currentSub = new List<(double X, double Y)> { (dx, dy) };
        _subpaths.Add(_currentSub);
    }

    private void MoveTo(double x, double y)
    {
        _curX = _startX = x;
        _curY = _startY = y;
        var (dx, dy) = _state.Ctm.Apply(x, y);
        StartSub(dx, dy);
        _pathData.Append(string.Create(CultureInfo.InvariantCulture, $"M{dx:0.##} {dy:0.##} "));
    }

    private void LineTo(double x, double y)
    {
        _curX = x;
        _curY = y;
        var (dx, dy) = _state.Ctm.Apply(x, y);
        if (_currentSub is null)
        {
            StartSub(dx, dy);
        }
        else
        {
            _currentSub.Add((dx, dy));
        }
        _pathData.Append(string.Create(CultureInfo.InvariantCulture, $"L{dx:0.##} {dy:0.##} "));
    }

    private void CurveTo(double x1, double y1, double x2, double y2, double x3, double y3)
    {
        var (a, b) = _state.Ctm.Apply(x1, y1);
        var (c, d) = _state.Ctm.Apply(x2, y2);
        var (e, f) = _state.Ctm.Apply(x3, y3);

        (double X, double Y) p0 = _currentSub is { Count: > 0 }
            ? _currentSub[^1]
            : _state.Ctm.Apply(_curX, _curY);
        if (_currentSub is null)
        {
            StartSub(p0.X, p0.Y);
        }

        // Flatten the cubic Bezier so strokes can be outlined.
        for (int i = 1; i <= CurveSegments; i++)
        {
            double t = (double)i / CurveSegments;
            double mt = 1 - t;
            double bx = mt * mt * mt * p0.X + 3 * mt * mt * t * a + 3 * mt * t * t * c + t * t * t * e;
            double by = mt * mt * mt * p0.Y + 3 * mt * mt * t * b + 3 * mt * t * t * d + t * t * t * f;
            _currentSub!.Add((bx, by));
        }

        _curX = x3;
        _curY = y3;
        _pathData.Append(string.Create(CultureInfo.InvariantCulture,
            $"C{a:0.##} {b:0.##} {c:0.##} {d:0.##} {e:0.##} {f:0.##} "));
    }

    private void Rectangle(double x, double y, double w, double h)
    {
        MoveTo(x, y);
        LineTo(x + w, y);
        LineTo(x + w, y + h);
        LineTo(x, y + h);
        ClosePath();
    }

    private void ClosePath()
    {
        _curX = _startX;
        _curY = _startY;
        if (_currentSub is { Count: > 0 })
        {
            _currentSub.Add(_currentSub[0]); // close the polyline for stroking
        }
        _pathData.Append("Z ");
    }

    private void ResetPath()
    {
        _pathData.Clear();
        _subpaths.Clear();
        _currentSub = null;
    }

    private void PaintPath(bool stroke, bool fill, bool evenOdd)
    {
        if (_pathData.Length == 0)
        {
            ApplyPendingClip();
            return;
        }

        string data = _pathData.ToString().Trim();

        if (fill)
        {
            string fillPaint = _state.FillPattern is not null
                ? ResolveFillPaint(_state.FillPattern) ?? _state.FillColor
                : _state.FillColor;
            EmitFill(data, evenOdd, fillPaint, _state.FillAlpha);
        }

        if (stroke)
        {
            double deviceWidth = Math.Max(0.5, _state.LineWidth * _state.Ctm.ScaleFactor);
            string? outline = BuildStrokeOutline(deviceWidth);
            if (outline is not null)
            {
                EmitFill(outline, false, _state.StrokeColor, _state.StrokeAlpha);
            }
        }

        ApplyPendingClip(data);
        ResetPath();
    }

    private void EmitFill(string pathData, bool evenOdd, string paint, double alpha)
    {
        _html.Append("<div style=\"position:absolute;inset:0;background:");
        _html.Append(paint);
        _html.Append(";clip-path:path(");
        if (evenOdd)
        {
            _html.Append("evenodd,");
        }
        _html.Append('\'').Append(pathData).Append("')");
        if (alpha < 1)
        {
            _html.Append(string.Create(CultureInfo.InvariantCulture, $";opacity:{alpha:0.###}"));
        }
        if (_state.BlendMode.Length > 0)
        {
            _html.Append(";mix-blend-mode:").Append(_state.BlendMode);
        }
        _html.Append("\"></div>");
    }

    /// <summary>
    /// Builds a fill path that approximates stroking <see cref="_subpaths"/> at
    /// the given device width: a quad per segment plus a small square at each
    /// vertex to cover joins and caps. Filled with nonzero winding.
    /// </summary>
    private string? BuildStrokeOutline(double width)
    {
        double hw = Math.Max(width / 2.0, 0.35);
        var sb = new StringBuilder();
        bool any = false;
        bool dashed = _state.DashArray is { Length: > 0 };

        foreach (var sub in _subpaths)
        {
            if (sub.Count == 1)
            {
                // Lone moveto with a dot cap (rare).
                AppendSquare(sb, sub[0].X, sub[0].Y, hw);
                any = true;
                continue;
            }

            if (dashed)
            {
                any |= AppendDashedSubpath(sb, sub, hw);
                continue;
            }

            for (int i = 0; i + 1 < sub.Count; i++)
            {
                var (x0, y0) = sub[i];
                var (x1, y1) = sub[i + 1];
                double dx = x1 - x0, dy = y1 - y0;
                double len = Math.Sqrt(dx * dx + dy * dy);
                if (len < 1e-6)
                {
                    continue;
                }
                double nx = -dy / len * hw, ny = dx / len * hw;
                sb.Append(string.Create(CultureInfo.InvariantCulture,
                    $"M{x0 + nx:0.##} {y0 + ny:0.##} L{x1 + nx:0.##} {y1 + ny:0.##} L{x1 - nx:0.##} {y1 - ny:0.##} L{x0 - nx:0.##} {y0 - ny:0.##} Z "));
                any = true;
            }
            // Cover joins/caps with squares at each vertex.
            foreach (var (vx, vy) in sub)
            {
                AppendSquare(sb, vx, vy, hw);
            }
        }

        return any ? sb.ToString().Trim() : null;
    }

    /// <summary>
    /// Emits stroke quads for a single subpath broken up by the current dash
    /// pattern (<see cref="GraphicsState.DashArray"/>), advancing a cycle cursor
    /// across all segments so dashes are continuous across vertices.
    /// </summary>
    private bool AppendDashedSubpath(StringBuilder sb, List<(double X, double Y)> sub, double hw)
    {
        double[] dash = _state.DashArray!;
        double cycle = dash.Sum();
        if (cycle <= 0)
        {
            return false;
        }

        // Position within the dash cycle, honoring the initial phase.
        double remaining = dash[0];
        int dashIndex = 0;
        bool on = true;
        double phase = _state.DashPhase % cycle;
        while (phase > 0)
        {
            if (phase >= remaining)
            {
                phase -= remaining;
                dashIndex = (dashIndex + 1) % dash.Length;
                remaining = dash[dashIndex];
                on = !on;
            }
            else
            {
                remaining -= phase;
                phase = 0;
            }
        }

        bool any = false;
        for (int i = 0; i + 1 < sub.Count; i++)
        {
            var (x0, y0) = sub[i];
            var (x1, y1) = sub[i + 1];
            double dx = x1 - x0, dy = y1 - y0;
            double segLen = Math.Sqrt(dx * dx + dy * dy);
            if (segLen < 1e-6)
            {
                continue;
            }
            double ux = dx / segLen, uy = dy / segLen;
            double nx = -uy * hw, ny = ux * hw;
            double pos = 0;
            while (pos < segLen)
            {
                double step = Math.Min(remaining, segLen - pos);
                if (on && step > 1e-6)
                {
                    double ax = x0 + ux * pos, ay = y0 + uy * pos;
                    double bx = x0 + ux * (pos + step), by = y0 + uy * (pos + step);
                    sb.Append(string.Create(CultureInfo.InvariantCulture,
                        $"M{ax + nx:0.##} {ay + ny:0.##} L{bx + nx:0.##} {by + ny:0.##} L{bx - nx:0.##} {by - ny:0.##} L{ax - nx:0.##} {ay - ny:0.##} Z "));
                    any = true;
                }
                pos += step;
                remaining -= step;
                if (remaining <= 1e-9)
                {
                    dashIndex = (dashIndex + 1) % dash.Length;
                    remaining = dash[dashIndex];
                    on = !on;
                }
            }
        }
        return any;
    }

    private static void AppendSquare(StringBuilder sb, double cx, double cy, double hw)
    {
        sb.Append(string.Create(CultureInfo.InvariantCulture,
            $"M{cx - hw:0.##} {cy - hw:0.##} L{cx + hw:0.##} {cy - hw:0.##} L{cx + hw:0.##} {cy + hw:0.##} L{cx - hw:0.##} {cy + hw:0.##} Z "));
    }

    private void EndPathNoPaint()
    {
        ApplyPendingClip(_pathData.Length > 0 ? _pathData.ToString().Trim() : null);
        ResetPath();
    }

    private void ApplyPendingClip(string? data = null)
    {
        if (_pendingClipEvenOdd is null)
        {
            return;
        }
        data ??= _pathData.Length > 0 ? _pathData.ToString().Trim() : null;
        bool evenOdd = _pendingClipEvenOdd.Value;
        _pendingClipEvenOdd = null;

        if (string.IsNullOrEmpty(data))
        {
            return;
        }

        _html.Append("<div style=\"position:absolute;inset:0;clip-path:path(");
        if (evenOdd)
        {
            _html.Append("evenodd,");
        }
        _html.Append('\'').Append(data).Append("')\">");
        _openGroups++;
    }

    // ----- Shadings -----

    private void PaintShading(Operation op)
    {
        if (op.Operands.Count == 0 || op.Operands[0] is not Name name)
        {
            return;
        }
        if (_resources?.Get("Shading") is not Dict shadings)
        {
            return;
        }
        object? shadingObj = shadings.Get(name.Value);
        Dict? shading = shadingObj as Dict ?? (shadingObj as PdfStream)?.Dict;
        if (shading is null)
        {
            return;
        }

        string? background = CssShadingBuilder.Build(shading, _xref, _resources, _state.Ctm, _viewW, _viewH);
        if (background is null)
        {
            return;
        }
        // Fill the current clip region (or the whole page when unclipped).
        _html.Append("<div style=\"position:absolute;inset:0;background:");
        _html.Append(background);
        _html.Append("\"></div>");
    }

    // ----- XObjects -----

    private void DoXObject(Operation op)
    {
        if (op.Operands.Count == 0 || op.Operands[0] is not Name name)
        {
            return;
        }
        if (_resources?.Get("XObject") is not Dict xobjects || xobjects.Get(name.Value) is not PdfStream stream
            || stream.Dict is null)
        {
            return;
        }

        string subtype = (stream.Dict.Get("Subtype") as Name)?.Value ?? "";
        if (subtype == "Image")
        {
            DrawImage(stream);
        }
        else if (subtype == "Form")
        {
            DrawForm(stream);
        }
    }

    private void DrawImage(PdfStream stream)
    {
        var fill = ParseRgb(_state.FillColor);
        string? uri = PdfImage.BuildDataUri(stream, _xref, _resources, fill);
        if (uri is null)
        {
            return;
        }
        EmitImage(uri, PixelSize(stream.Dict!, "Width", "W"), PixelSize(stream.Dict!, "Height", "H"));
    }

    private void DrawInlineImage(Operation op)
    {
        if (op.Operands.Count < 2 || op.Operands[0] is not Dict dict || op.Operands[1] is not byte[] data)
        {
            return;
        }
        var stream = new PdfStream(data, 0, data.Length, dict);
        var fill = ParseRgb(_state.FillColor);
        string? uri = PdfImage.BuildDataUri(stream, _xref, _resources, fill);
        if (uri is not null)
        {
            EmitImage(uri, PixelSize(dict, "Width", "W"), PixelSize(dict, "Height", "H"));
        }
    }

    private void EmitImage(string uri, int pixelW, int pixelH)
    {
        if (pixelW <= 0)
        {
            pixelW = 1;
        }
        if (pixelH <= 0)
        {
            pixelH = 1;
        }

        // The image occupies the unit square; flip Y so row 0 is at the top. The
        // <img> element is sized to its native pixels so the browser samples at
        // full resolution before the matrix scales it into place.
        Matrix unit = Matrix.Concat(_state.Ctm, new Matrix(1, 0, 0, -1, 0, 1));
        Matrix m = Matrix.Concat(unit, new Matrix(1.0 / pixelW, 0, 0, 1.0 / pixelH, 0, 0));

        _html.Append("<img src=\"");
        _html.Append(uri);
        _html.Append(string.Create(CultureInfo.InvariantCulture,
            $"\" style=\"position:absolute;left:0;top:0;width:{pixelW}px;height:{pixelH}px;transform:{m.ToSvg()};transform-origin:0 0"));
        if (_state.FillAlpha < 1)
        {
            _html.Append(string.Create(CultureInfo.InvariantCulture, $";opacity:{_state.FillAlpha:0.###}"));
        }
        if (_state.BlendMode.Length > 0)
        {
            _html.Append(";mix-blend-mode:").Append(_state.BlendMode);
        }
        _html.Append("\"/>");
    }

    private static int PixelSize(Dict dict, string key1, string key2)
        => dict.Get(key1, key2) is double d ? (int)d : 0;

    private void DrawForm(PdfStream stream)
    {
        if (_formDepth >= MaxFormDepth)
        {
            return;
        }
        _formDepth++;

        // Emulate q ... Q around the form.
        _stack.Push(_state.Clone());
        _groupDepthStack.Push(_openGroups);
        Dict? savedResources = _resources;

        if (stream.Dict!.Get("Matrix") is List<object?> mtx && mtx.Count >= 6)
        {
            _state.Ctm = Matrix.Concat(_state.Ctm, new Matrix(
                Num(mtx[0]), Num(mtx[1]), Num(mtx[2]), Num(mtx[3]), Num(mtx[4]), Num(mtx[5])));
        }

        if (stream.Dict.Get("Resources") is Dict formResources)
        {
            _resources = formResources;
        }

        try
        {
            byte[] content = Filters.StreamDecoder.Decode(stream);
            RunOps(new ContentParser(content).Parse());
        }
        catch
        {
            // Ignore malformed form content.
        }

        _resources = savedResources;
        if (_stack.Count > 0)
        {
            _state = _stack.Pop();
            int target = _groupDepthStack.Count > 0 ? _groupDepthStack.Pop() : 0;
            while (_openGroups > target)
            {
                _html.Append("</div>");
                _openGroups--;
            }
        }
        _formDepth--;
    }

    // ----- Annotations -----

    private void RenderAnnotations()
    {
        if (_page.Dict.Get("Annots") is not List<object?> annots)
        {
            return;
        }

        foreach (var item in annots)
        {
            if (_xref.FetchIfRef(item) is not Dict annot)
            {
                continue;
            }

            int flags = annot.Get("F") is double f ? (int)f : 0;
            if ((flags & 0x2) != 0 || (flags & 0x20) != 0) // Hidden or NoView
            {
                continue;
            }

            DrawAnnotationAppearance(annot);
            DrawLinkOverlay(annot);
        }
    }

    private void DrawAnnotationAppearance(Dict annot)
    {
        if (annot.Get("Rect") is not List<object?> rectArr || rectArr.Count < 4)
        {
            return;
        }
        PdfStream? appearance = ResolveAppearance(annot);
        if (appearance?.Dict is null)
        {
            return;
        }

        double[] rect = ToRect(rectArr);
        double[] bbox = appearance.Dict.Get("BBox") is List<object?> bb && bb.Count >= 4
            ? ToRect(bb)
            : [0, 0, rect[2] - rect[0], rect[3] - rect[1]];
        Matrix formMatrix = appearance.Dict.Get("Matrix") is List<object?> m && m.Count >= 6
            ? new Matrix(Num(m[0]), Num(m[1]), Num(m[2]), Num(m[3]), Num(m[4]), Num(m[5]))
            : Matrix.Identity;

        Matrix a = ComputeAppearanceMatrix(bbox, formMatrix, rect);

        // Reset to a clean state anchored at the page base transform.
        _stack.Clear();
        _groupDepthStack.Clear();
        _state = new GraphicsState { Ctm = Matrix.Concat(_baseMatrix, a) };
        _resources = _page.Resources;

        DrawForm(appearance);

        while (_openGroups > 0)
        {
            _html.Append("</div>");
            _openGroups--;
        }
    }

    private PdfStream? ResolveAppearance(Dict annot)
    {
        if (annot.Get("AP") is not Dict ap)
        {
            return null;
        }
        object? normal = ap.Get("N");
        if (normal is PdfStream stream)
        {
            return stream;
        }
        // Appearance sub-dictionary keyed by the current appearance state (/AS).
        if (normal is Dict states)
        {
            string? state = (annot.Get("AS") as Name)?.Value;
            if (state is not null && states.Get(state) is PdfStream selected)
            {
                return selected;
            }
            foreach (var key in states.Keys)
            {
                if (states.Get(key) is PdfStream first)
                {
                    return first;
                }
            }
        }
        return null;
    }

    private void DrawLinkOverlay(Dict annot)
    {
        if (!Primitives.IsName(annot.Get("Subtype"), "Link"))
        {
            return;
        }
        if (annot.Get("Rect") is not List<object?> rectArr || rectArr.Count < 4)
        {
            return;
        }

        string? uri = null;
        if (annot.Get("A") is Dict action && Primitives.IsName(action.Get("S"), "URI")
            && action.Get("URI") is PdfString u)
        {
            uri = u.AsLatin1();
        }
        if (uri is null)
        {
            return;
        }

        double[] r = ToRect(rectArr);
        Matrix transform = Matrix.Concat(_baseMatrix, new Matrix(1, 0, 0, 1, r[0], r[1]));
        _html.Append($"<a href=\"{Escape(uri)}\" target=\"_blank\" rel=\"noopener noreferrer\" style=\"position:absolute;left:0;top:0;");
        _html.Append(string.Create(CultureInfo.InvariantCulture,
            $"width:{r[2] - r[0]:0.##}px;height:{r[3] - r[1]:0.##}px;transform:{transform.ToSvg()};transform-origin:0 0\"></a>"));
    }

    private static Matrix ComputeAppearanceMatrix(double[] bbox, Matrix formMatrix, double[] rect)
    {
        // Transform the BBox corners by the form matrix, then map the resulting
        // bounding box onto the annotation Rect (PDF spec §12.5.5).
        Span<(double X, double Y)> corners =
        [
            formMatrix.Apply(bbox[0], bbox[1]),
            formMatrix.Apply(bbox[2], bbox[1]),
            formMatrix.Apply(bbox[2], bbox[3]),
            formMatrix.Apply(bbox[0], bbox[3]),
        ];

        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var (cx, cy) in corners)
        {
            minX = Math.Min(minX, cx);
            minY = Math.Min(minY, cy);
            maxX = Math.Max(maxX, cx);
            maxY = Math.Max(maxY, cy);
        }

        double bw = maxX - minX;
        double bh = maxY - minY;
        double rw = rect[2] - rect[0];
        double rh = rect[3] - rect[1];
        double sx = bw != 0 ? rw / bw : 1;
        double sy = bh != 0 ? rh / bh : 1;

        return new Matrix(sx, 0, 0, sy, rect[0] - minX * sx, rect[1] - minY * sy);
    }

    private static double[] ToRect(List<object?> arr)
    {
        double x0 = Num(arr[0]), y0 = Num(arr[1]), x1 = Num(arr[2]), y1 = Num(arr[3]);
        return [Math.Min(x0, x1), Math.Min(y0, y1), Math.Max(x0, x1), Math.Max(y0, y1)];
    }

    // ----- Text -----

    private void SetFont(Operation op)
    {
        if (op.Operands.Count < 2 || op.Operands[0] is not Name fontName)
        {
            return;
        }
        _state.FontSize = op.Num(1);
        _state.FontResourceName = fontName.Value;
        _state.Font = ResolveFont(fontName.Value);
    }

    private PdfFont? ResolveFont(string name)
    {
        string cacheKey = $"{_formDepth}:{name}";
        if (_fontCache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }
        if (_resources?.Get("Font") is Dict fonts && fonts.Get(name) is Dict fontDict)
        {
            var font = PdfFont.Create(fontDict, _xref);
            _fontCache[cacheKey] = font;
            return font;
        }
        return null;
    }

    private void TextMove(double tx, double ty)
    {
        _textLineMatrix = Matrix.Concat(_textLineMatrix, new Matrix(1, 0, 0, 1, tx, ty));
        _textMatrix = _textLineMatrix;
    }

    private void ShowTextArray(List<object?>? array)
    {
        if (array is null)
        {
            return;
        }
        foreach (var item in array)
        {
            if (item is PdfString)
            {
                ShowText(item);
            }
            else if (item is double adjust)
            {
                double tx = -adjust / 1000.0 * _state.FontSize * _state.HorizScale;
                _textMatrix = Matrix.Concat(_textMatrix, new Matrix(1, 0, 0, 1, tx, 0));
            }
        }
    }

    private void ShowText(object? operand)
    {
        if (operand is not PdfString s || _state.Font is null || _state.FontSize == 0)
        {
            return;
        }

        var glyphs = _state.Font.Decode(s.Bytes).ToList();
        var text = new StringBuilder();
        double displacement = 0;

        foreach (var g in glyphs)
        {
            text.Append(g.Unicode);
            double w0 = g.Width1000 / 1000.0 * _state.FontSize;
            double spacing = _state.CharSpacing + (g.IsSpace ? _state.WordSpacing : 0);
            displacement += (w0 + spacing) * _state.HorizScale;
        }

        if (text.Length > 0 && _state.RenderMode != 7 && _state.RenderMode != 3)
        {
            EmitText(text.ToString());
        }

        _textMatrix = Matrix.Concat(_textMatrix, new Matrix(1, 0, 0, 1, displacement, 0));
    }

    private void EmitText(string text)
    {
        // Map em-space (origin at the baseline, y up) to device pixels.
        Matrix trm = Matrix.Concat(Matrix.Concat(_state.Ctm, _textMatrix),
            new Matrix(_state.FontSize * _state.HorizScale, 0, 0, _state.FontSize, 0, _state.TextRise));

        double fontHeight = Math.Sqrt(trm.C * trm.C + trm.D * trm.D);
        if (fontHeight < 1e-3)
        {
            return;
        }

        // Compose with a CSS-local -> em-space mapping so the <span> top-left and
        // alphabetic baseline land correctly (CSS y is down, baseline ~ascent).
        double a = trm.A / fontHeight;
        double b = trm.B / fontHeight;
        double c = -trm.C / fontHeight;
        double d = -trm.D / fontHeight;
        double e = trm.C * AscentFactor + trm.E;
        double f = trm.D * AscentFactor + trm.F;

        _html.Append("<span style=\"position:absolute;left:0;top:0;white-space:pre;line-height:1");
        _html.Append(string.Create(CultureInfo.InvariantCulture, $";font-size:{fontHeight:0.###}px"));
        _html.Append(";color:").Append(_state.FillColor);
        AppendFontStyle(_html, _state.Font!);
        _html.Append(string.Create(CultureInfo.InvariantCulture,
            $";transform:matrix({a:0.####},{b:0.####},{c:0.####},{d:0.####},{e:0.##},{f:0.##});transform-origin:0 0"));
        if (_state.BlendMode.Length > 0)
        {
            _html.Append(";mix-blend-mode:").Append(_state.BlendMode);
        }
        _html.Append("\">");
        _html.Append(Escape(text));
        _html.Append("</span>");
    }

    private void AppendFontStyle(StringBuilder sb, PdfFont font)
    {
        if (font.HasEmbedded)
        {
            string family = font.FontFaceFamily;
            if (_emittedFamilies.Add(family))
            {
                string b64 = Convert.ToBase64String(font.EmbeddedProgram!);
                string fmt = font.EmbeddedFormat!;
                _fontFaces.Append(
                    $"@font-face{{font-family:'{family}';src:url(data:font/{fmt};base64,{b64}) format('{fmt}');}}");
            }
            sb.Append(";font-family:").Append(family).Append(',').Append(font.GenericFamily);
        }
        else
        {
            sb.Append(";font-family:").Append(font.GenericFamily);
        }
        if (font.Bold)
        {
            sb.Append(";font-weight:bold");
        }
        if (font.Italic)
        {
            sb.Append(";font-style:italic");
        }
    }

    private void ApplyExtGState(Operation op)
    {
        if (op.Operands.Count == 0 || op.Operands[0] is not Name name)
        {
            return;
        }
        if (_resources?.Get("ExtGState") is Dict ext && ext.Get(name.Value) is Dict gs)
        {
            if (gs.Get("ca") is double ca)
            {
                _state.FillAlpha = ca;
            }
            if (gs.Get("CA") is double strokeAlpha)
            {
                _state.StrokeAlpha = strokeAlpha;
            }
            if (gs.Get("LW") is double lw)
            {
                _state.LineWidth = lw;
            }
            object? bm = gs.Get("BM");
            string? bmName = bm switch
            {
                Name n => n.Value,
                List<object?> arr when arr.Count > 0 && arr[0] is Name first => first.Value,
                _ => null,
            };
            if (bmName is not null)
            {
                _state.BlendMode = BlendCss(bmName);
            }
        }
    }

    // ----- Patterns & blend modes -----

    private void SetFillColorN(Operation op)
    {
        // scn/sc with a trailing name selects a pattern; otherwise it's a color.
        if (op.Operands.Count > 0 && op.Operands[^1] is Name pattern)
        {
            _state.FillPattern = pattern.Value;
        }
        else
        {
            _state.FillPattern = null;
            _state.FillColor = ColorViaSpace(_state.FillColorSpace, op) ?? _state.FillColor;
        }
    }

    private void SetStrokeColorN(Operation op)
    {
        if (op.Operands.Count > 0 && op.Operands[^1] is Name)
        {
            return; // stroke patterns are not modeled; keep the prior color
        }
        _state.StrokeColor = ColorViaSpace(_state.StrokeColorSpace, op) ?? _state.StrokeColor;
    }

    /// <summary>Resolves the operand of a <c>cs</c>/<c>CS</c> operator to a color space.</summary>
    private ColorSpace? ResolveColorSpace(Operation op)
    {
        if (op.Operands.Count == 0 || op.Operands[0] is not Name name)
        {
            return null;
        }
        return name.Value switch
        {
            "DeviceGray" or "G" => ColorSpace.Gray,
            "DeviceRGB" or "RGB" => ColorSpace.Rgb,
            "DeviceCMYK" or "CMYK" => ColorSpace.Cmyk,
            "Pattern" => null,
            _ => ColorSpace.Create(name, _xref, _resources),
        };
    }

    /// <summary>Sets the current color to the initial value of the selected space.</summary>
    private void SetDefaultColor(bool stroke)
    {
        ColorSpace? cs = stroke ? _state.StrokeColorSpace : _state.FillColorSpace;
        string color = cs is null ? "rgb(0,0,0)" : RgbString(cs.GetRgb(cs.DefaultComponents()));
        if (stroke)
        {
            _state.StrokeColor = color;
        }
        else
        {
            _state.FillColor = color;
            _state.FillPattern = null;
        }
    }

    /// <summary>
    /// Converts the numeric operands of <c>sc</c>/<c>scn</c> through the current
    /// color space. Falls back to inferring the space from the operand count.
    /// </summary>
    private static string? ColorViaSpace(ColorSpace? cs, Operation op)
    {
        var nums = op.Operands.Where(o => o is double).Cast<double>().ToArray();
        if (nums.Length == 0)
        {
            return null;
        }
        if (cs is not null)
        {
            return RgbString(cs.GetRgb(nums));
        }
        return ColorFromComponents(op);
    }

    private static string RgbString((byte R, byte G, byte B) c) => $"rgb({c.R},{c.G},{c.B})";

    private void SetDash(Operation op)
    {
        _state.DashArray = null;
        _state.DashPhase = 0;
        if (op.Operands.Count >= 1 && op.Operands[0] is List<object?> arr && arr.Count > 0)
        {
            var pattern = arr.Where(o => o is double).Cast<double>()
                .Select(v => v * _state.Ctm.ScaleFactor).ToArray();
            if (pattern.Length > 0 && pattern.Any(v => v > 0))
            {
                _state.DashArray = pattern;
                if (op.Operands.Count >= 2 && op.Operands[1] is double phase)
                {
                    _state.DashPhase = phase * _state.Ctm.ScaleFactor;
                }
            }
        }
    }

    private string? ResolveFillPaint(string name)
    {
        if (_patternCache.TryGetValue(name, out var cached))
        {
            return cached;
        }
        _patternCache[name] = null; // guard against recursion

        string? result = BuildPattern(name);
        _patternCache[name] = result;
        return result;
    }

    private string? BuildPattern(string name)
    {
        if (_resources?.Get("Pattern") is not Dict patterns)
        {
            return null;
        }
        object? obj = patterns.Get(name);
        Dict? patDict = obj as Dict ?? (obj as PdfStream)?.Dict;
        if (patDict is null)
        {
            return null;
        }

        Matrix matrix = patDict.Get("Matrix") is List<object?> m && m.Count >= 6
            ? new Matrix(Num(m[0]), Num(m[1]), Num(m[2]), Num(m[3]), Num(m[4]), Num(m[5]))
            : Matrix.Identity;
        int patternType = patDict.Get("PatternType") is double pt ? (int)pt : 0;

        // Shading patterns (type 2) map to CSS gradients. Tiling patterns
        // (type 1) have no simple HTML equivalent and fall back to a solid color.
        if (patternType == 2)
        {
            object? shadingObj = patDict.Get("Shading");
            Dict? shading = shadingObj as Dict ?? (shadingObj as PdfStream)?.Dict;
            if (shading is null)
            {
                return null;
            }
            return CssShadingBuilder.Build(shading, _xref, _resources,
                Matrix.Concat(_baseMatrix, matrix), _viewW, _viewH);
        }
        return null;
    }

    private static string BlendCss(string pdfMode) => pdfMode switch
    {
        "Multiply" => "multiply",
        "Screen" => "screen",
        "Overlay" => "overlay",
        "Darken" => "darken",
        "Lighten" => "lighten",
        "ColorDodge" => "color-dodge",
        "ColorBurn" => "color-burn",
        "HardLight" => "hard-light",
        "SoftLight" => "soft-light",
        "Difference" => "difference",
        "Exclusion" => "exclusion",
        "Hue" => "hue",
        "Saturation" => "saturation",
        "Color" => "color",
        "Luminosity" => "luminosity",
        _ => "", // Normal / Compatible
    };

    // ----- Colors -----

    private static string Gray(double v)
    {
        int c = Clamp255(v);
        return $"rgb({c},{c},{c})";
    }

    private static string Rgb(double r, double g, double b)
        => $"rgb({Clamp255(r)},{Clamp255(g)},{Clamp255(b)})";

    private static string Cmyk(double c, double m, double y, double k)
    {
        int r = Clamp255((1 - c) * (1 - k));
        int g = Clamp255((1 - m) * (1 - k));
        int b = Clamp255((1 - y) * (1 - k));
        return $"rgb({r},{g},{b})";
    }

    private static string? ColorFromComponents(Operation op)
    {
        var nums = op.Operands.Where(o => o is double).Cast<double>().ToList();
        return nums.Count switch
        {
            1 => Gray(nums[0]),
            3 => Rgb(nums[0], nums[1], nums[2]),
            4 => Cmyk(nums[0], nums[1], nums[2], nums[3]),
            _ => null,
        };
    }

    private static (byte R, byte G, byte B) ParseRgb(string rgb)
    {
        // Parses "rgb(r,g,b)" produced by the color helpers above.
        int start = rgb.IndexOf('(');
        int end = rgb.IndexOf(')');
        if (start < 0 || end <= start)
        {
            return (0, 0, 0);
        }
        var parts = rgb[(start + 1)..end].Split(',');
        byte P(int i) => i < parts.Length && int.TryParse(parts[i].Trim(), out int v) ? (byte)Math.Clamp(v, 0, 255) : (byte)0;
        return (P(0), P(1), P(2));
    }

    private static int Clamp255(double v) => (int)Math.Round(Math.Clamp(v, 0, 1) * 255);

    private static double Num(object? value) => value is double d ? d : 0;

    private static string Escape(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (char ch in text)
        {
            switch (ch)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '"': sb.Append("&quot;"); break;
                default:
                    if (ch < 0x20 && ch is not ('\t' or '\n' or '\r'))
                    {
                        break;
                    }
                    sb.Append(ch);
                    break;
            }
        }
        return sb.ToString();
    }
}
