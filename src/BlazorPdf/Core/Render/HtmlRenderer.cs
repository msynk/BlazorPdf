// HTML renderer. Unlike the SVG backend, this emits plain HTML DOM:
// <div> with CSS `clip-path: path()` for vector fills and clips, filled <div>
// outlines for strokes, <img> for rasters, <span> for selectable text and CSS
// gradients for shadings.

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
    private readonly Dictionary<object, PdfFont> _fontCache;

    /// <summary>
    /// Optional resolver mapping a GoTo/named destination to a 1-based page
    /// number, used to render internal (intra-document) link annotations. When
    /// null, only external URI links are emitted.
    /// </summary>
    public Func<object?, int?>? DestinationResolver { get; set; }

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

    private readonly StringBuilder _fontFaces;         // document-wide accumulator (shared)
    private readonly HashSet<string> _emittedFamilies; // dedup across pages (shared)
    private readonly StringBuilder _pageFaces = new();  // faces first seen on THIS page
    private readonly bool _ownFontFaces;                // emit faces inline (no shared store)
    private readonly Dictionary<object, string?> _patternCache = new();
    private StringBuilder _html = new();
    private int _openGroups;

    // Marked-content / optional-content state. While OC content is hidden, output
    // is diverted to a scratch buffer that is discarded at the matching EMC.
    private int _mcDepth;
    private int _ocHiddenAtDepth = -1;
    private StringBuilder? _realHtml;
    private HashSet<string>? _ocgOff;
    private int _formDepth;

    private readonly int _rotationOffset;

    public HtmlRenderer(PdfPage page, IXRef xref, int rotationOffset = 0)
        : this(page, xref, null, rotationOffset)
    {
    }

    /// <summary>
    /// Creates a renderer that shares an embedded-font store across pages, so each
    /// font's <c>@font-face</c> base64 is emitted once for the whole document
    /// (retrieve it via <see cref="PdfFontStore.FontFaceStyle"/>). When
    /// <paramref name="fontStore"/> is <c>null</c> the page is self-contained.
    /// </summary>
    public HtmlRenderer(PdfPage page, IXRef xref, PdfFontStore? fontStore, int rotationOffset = 0)
    {
        _page = page;
        _xref = xref;
        _resources = page.Resources;
        _rotationOffset = ((rotationOffset % 360) + 360) % 360;

        if (fontStore is not null)
        {
            _fontCache = fontStore.Fonts;
            _emittedFamilies = fontStore.EmittedFamilies;
            _fontFaces = fontStore.FontFaces;
            _ownFontFaces = false; // the viewer emits the shared @font-face style
        }
        else
        {
            _fontCache = new Dictionary<object, PdfFont>();
            _emittedFamilies = new HashSet<string>();
            _fontFaces = new StringBuilder();
            _ownFontFaces = true;
        }
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
        // A self-contained page inlines its own @font-face rules. With a shared
        // document store the viewer emits them in a persistent <style> instead, so
        // they survive page eviction (an evicted page's inline <style> would be
        // removed while the dedup set still thinks the font was emitted → tofu).
        if (_ownFontFaces && _pageFaces.Length > 0)
        {
            sb.Append("<style>").Append(_pageFaces).Append("</style>");
        }
        sb.Append(_html);
        sb.Append("</div>");
        return sb.ToString();
    }

    private void RunOps(List<Operation> ops)
    {
        foreach (var op in ops)
        {
            try
            {
                Execute(op);
            }
            catch
            {
                // A single malformed operator must not abort the whole page;
                // skip it and continue with the rest of the content stream.
            }
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
                // Require all six operands: a short/garbled cm would otherwise
                // build an all-zero singular matrix and blank everything after it.
                if (op.Operands.Count >= 6)
                {
                    _state.Ctm = Matrix.Concat(_state.Ctm,
                        new Matrix(op.Num(0), op.Num(1), op.Num(2), op.Num(3), op.Num(4), op.Num(5)));
                }
                break;
            case "w": _state.LineWidth = op.Num(0); break;
            case "d": SetDash(op); break;
            case "J": _state.LineCap = (int)op.Num(0); break;
            case "j": _state.LineJoin = (int)op.Num(0); break;
            case "M": _state.MiterLimit = op.Num(0); break;
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

            // Marked content / optional content groups.
            case "BDC": BeginMarkedContent(op); break;
            case "BMC": _mcDepth++; break;
            case "EMC": EndMarkedContent(); break;

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
            if (_state.FillPattern is not null && TryRenderTilingFill(data, evenOdd))
            {
                // Tiling pattern painted its cells into a clipped group.
            }
            else
            {
                string fillPaint = _state.FillPattern is not null
                    ? ResolveFillPaint(_state.FillPattern) ?? _state.FillColor
                    : _state.FillColor;
                EmitFill(data, evenOdd, fillPaint, _state.FillAlpha);
            }
        }

        if (stroke)
        {
            double deviceWidth = Math.Max(0.75, _state.LineWidth * _state.Ctm.ScaleFactor);
            EmitStroke(data, deviceWidth);
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
    /// Strokes the current path by emitting an inline SVG <c>&lt;path&gt;</c>.
    /// The path data preserves the original cubic Béziers (the <c>C</c> commands
    /// built by <see cref="CurveTo"/>), so the browser draws true smooth curves
    /// with correct caps and joins instead of a flattened polygonal outline.
    /// </summary>
    private void EmitStroke(string pathData, double deviceWidth)
    {
        _html.Append(string.Create(CultureInfo.InvariantCulture,
            $"<svg width=\"{_viewW:0.##}\" height=\"{_viewH:0.##}\" viewBox=\"0 0 {_viewW:0.##} {_viewH:0.##}\" style=\"position:absolute;left:0;top:0;overflow:visible;pointer-events:none"));
        if (_state.BlendMode.Length > 0)
        {
            _html.Append(";mix-blend-mode:").Append(_state.BlendMode);
        }
        _html.Append("\"><path d=\"").Append(pathData).Append("\" fill=\"none\" stroke=\"");
        _html.Append(_state.StrokeColor).Append('"');
        _html.Append(string.Create(CultureInfo.InvariantCulture,
            $" stroke-width=\"{deviceWidth:0.###}\""));
        _html.Append(" stroke-linecap=\"").Append(LineCapName(_state.LineCap)).Append('"');
        _html.Append(" stroke-linejoin=\"").Append(LineJoinName(_state.LineJoin)).Append('"');
        if (_state.LineJoin == 0)
        {
            // Emit the PDF miter limit explicitly; SVG's default (4) differs from
            // PDF's (10), so miter joins would otherwise bevel too eagerly.
            _html.Append(string.Create(CultureInfo.InvariantCulture,
                $" stroke-miterlimit=\"{_state.MiterLimit:0.###}\""));
        }
        if (_state.StrokeAlpha < 1)
        {
            _html.Append(string.Create(CultureInfo.InvariantCulture,
                $" stroke-opacity=\"{_state.StrokeAlpha:0.###}\""));
        }
        if (_state.DashArray is { Length: > 0 } dash)
        {
            _html.Append(" stroke-dasharray=\"");
            for (int i = 0; i < dash.Length; i++)
            {
                if (i > 0)
                {
                    _html.Append(',');
                }
                _html.Append(string.Create(CultureInfo.InvariantCulture, $"{Math.Max(0, dash[i]):0.###}"));
            }
            _html.Append('"');
            if (_state.DashPhase != 0)
            {
                _html.Append(string.Create(CultureInfo.InvariantCulture,
                    $" stroke-dashoffset=\"{_state.DashPhase:0.###}\""));
            }
        }
        _html.Append("/></svg>");
    }

    private static string LineCapName(int cap) => cap switch
    {
        1 => "round",
        2 => "square",
        _ => "butt",
    };

    private static string LineJoinName(int join) => join switch
    {
        1 => "round",
        2 => "bevel",
        _ => "miter",
    };

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
        // Fill the current clip region (or the whole page when unclipped),
        // honoring the current fill alpha and blend mode.
        _html.Append("<div style=\"position:absolute;inset:0;background:");
        _html.Append(background);
        if (_state.FillAlpha < 1)
        {
            _html.Append(string.Create(CultureInfo.InvariantCulture, $";opacity:{_state.FillAlpha:0.###}"));
        }
        if (_state.BlendMode.Length > 0)
        {
            _html.Append(";mix-blend-mode:").Append(_state.BlendMode);
        }
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

    // ----- Optional content (OCG) -----

    private void BeginMarkedContent(Operation op)
    {
        _mcDepth++;
        // A "/OC <tag>" marked-content section is hidden when its optional-content
        // group is switched off in the default configuration.
        if (_ocHiddenAtDepth < 0 && op.Operands.Count >= 2
            && op.Operands[0] is Name tag && tag.Value == "OC"
            && IsOptionalContentHidden(op.Operands[1]))
        {
            _ocHiddenAtDepth = _mcDepth;
            _realHtml = _html;
            _html = new StringBuilder(); // divert & discard until the matching EMC
        }
    }

    private void EndMarkedContent()
    {
        if (_ocHiddenAtDepth == _mcDepth && _realHtml is not null)
        {
            _html = _realHtml; // drop the hidden content
            _realHtml = null;
            _ocHiddenAtDepth = -1;
        }
        if (_mcDepth > 0)
        {
            _mcDepth--;
        }
    }

    private bool IsOptionalContentHidden(object? operand)
    {
        object? raw = operand;
        if (operand is Name propName && _resources?.Get("Properties") is Dict props)
        {
            raw = props.GetRaw(propName.Value);
        }
        if (_xref.FetchIfRef(raw) is not Dict ocg)
        {
            return false;
        }

        // An OCMD references one or more OCGs; hidden only when every member is off.
        if (Primitives.IsName(ocg.Get("Type"), "OCMD"))
        {
            object? ocgs = ocg.GetRaw("OCGs");
            if (ocgs is List<object?> list)
            {
                if (list.Count == 0)
                {
                    return false;
                }
                foreach (var g in list)
                {
                    if (!IsOcgOff(g))
                    {
                        return false;
                    }
                }
                return true;
            }
            return IsOcgOff(ocgs);
        }
        return IsOcgOff(raw);
    }

    private bool IsOcgOff(object? raw)
    {
        if (_ocgOff is null)
        {
            _ocgOff = new HashSet<string>();
            if ((_xref as XRef)?.Root?.Get("OCProperties") is Dict ocp && ocp.Get("D") is Dict cfg
                && cfg.Get("OFF") is List<object?> off)
            {
                foreach (var item in off)
                {
                    if (item is Ref r)
                    {
                        _ocgOff.Add(r.ToRefString());
                    }
                }
            }
        }
        return raw is Ref rf && _ocgOff.Contains(rf.ToRefString());
    }

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

        // Transparency group: composite the group's content as a unit, then apply
        // the group-level alpha/blend once (isolation:isolate approximates a
        // non-knockout group). Reset the inner alpha so member objects don't get
        // the group's alpha applied twice.
        bool groupWrap = false;
        if (stream.Dict.Get("Group") is Dict grp && Primitives.IsName(grp.Get("S"), "Transparency")
            && (_state.FillAlpha < 1 || _state.BlendMode.Length > 0))
        {
            _html.Append("<div style=\"position:absolute;inset:0;isolation:isolate");
            if (_state.FillAlpha < 1)
            {
                _html.Append(string.Create(CultureInfo.InvariantCulture, $";opacity:{_state.FillAlpha:0.###}"));
            }
            if (_state.BlendMode.Length > 0)
            {
                _html.Append(";mix-blend-mode:").Append(_state.BlendMode);
            }
            _html.Append("\">");
            _state.FillAlpha = 1;
            _state.StrokeAlpha = 1;
            _state.BlendMode = "";
            groupWrap = true;
        }

        // Clip the form to its /BBox transformed into device space (spec §8.10.1):
        // form content must not paint outside its bounding box.
        bool bboxClip = false;
        if (stream.Dict.Get("BBox") is List<object?> bbox && bbox.Count >= 4)
        {
            var (c0x, c0y) = _state.Ctm.Apply(Num(bbox[0]), Num(bbox[1]));
            var (c1x, c1y) = _state.Ctm.Apply(Num(bbox[2]), Num(bbox[1]));
            var (c2x, c2y) = _state.Ctm.Apply(Num(bbox[2]), Num(bbox[3]));
            var (c3x, c3y) = _state.Ctm.Apply(Num(bbox[0]), Num(bbox[3]));
            _html.Append(string.Create(CultureInfo.InvariantCulture,
                $"<div style=\"position:absolute;inset:0;clip-path:path('M{c0x:0.##} {c0y:0.##} L{c1x:0.##} {c1y:0.##} L{c2x:0.##} {c2y:0.##} L{c3x:0.##} {c3y:0.##} Z')\">"));
            bboxClip = true;
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

        if (bboxClip)
        {
            _html.Append("</div>");
        }
        if (groupWrap)
        {
            _html.Append("</div>");
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

        // Clear any dangling path / clip state left by an unterminated content
        // stream so it cannot leak into annotation appearance rendering.
        _pathData.Clear();
        _subpaths.Clear();
        _currentSub = null;
        _pendingClipEvenOdd = null;

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
        object? action = _xref.FetchIfRef(annot.Get("A"));
        if (action is Dict actionDict && Primitives.IsName(actionDict.Get("S"), "URI")
            && actionDict.Get("URI") is PdfString u)
        {
            uri = u.AsLatin1();
        }

        // Resolve an internal GoTo/named destination to a target page number.
        int? destPage = null;
        if (uri is null && DestinationResolver is not null)
        {
            object? dest = annot.Get("Dest");
            if (dest is null && action is Dict a && Primitives.IsName(a.Get("S"), "GoTo"))
            {
                dest = a.Get("D");
            }
            if (dest is not null)
            {
                destPage = DestinationResolver(dest);
            }
        }

        double[] r = ToRect(rectArr);
        Matrix transform = Matrix.Concat(_baseMatrix, new Matrix(1, 0, 0, 1, r[0], r[1]));
        string style = string.Create(CultureInfo.InvariantCulture,
            $"position:absolute;left:0;top:0;width:{r[2] - r[0]:0.##}px;height:{r[3] - r[1]:0.##}px;transform:{transform.ToSvg()};transform-origin:0 0");

        if (uri is not null && IsAllowedUri(uri))
        {
            _html.Append($"<a href=\"{Escape(uri)}\" target=\"_blank\" rel=\"noopener noreferrer\" style=\"{style}\"></a>");
        }
        else if (destPage is int page)
        {
            // Internal link: the viewer delegates clicks on [data-bp-page] to
            // page navigation. Emitted as a div (no href) so nothing navigates away.
            _html.Append(string.Create(CultureInfo.InvariantCulture,
                $"<div data-bp-page=\"{page}\" style=\"{style};cursor:pointer\"></div>"));
        }
        // Otherwise (unknown/unsafe scheme, unresolved dest): drop the hotspot.
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

    // Schemes considered safe to emit as clickable links, mirroring pdf.js
    // `createValidAbsoluteUrl`. Anything else (notably javascript: and data:)
    // is dropped so a crafted /URI cannot execute script in the host app.
    private static readonly string[] AllowedUriSchemes = { "http", "https", "mailto", "ftp", "tel" };

    private static bool IsAllowedUri(string uri)
    {
        int colon = uri.IndexOf(':');
        if (colon <= 0)
        {
            return false; // no scheme, or leading ':' — treat as unsafe
        }
        // A URI scheme is letters/digits/+/-/. and must precede any '/', '?' or '#'.
        for (int i = 0; i < colon; i++)
        {
            char ch = uri[i];
            if (!(char.IsAsciiLetterOrDigit(ch) || ch is '+' or '-' or '.'))
            {
                return false;
            }
        }
        string scheme = uri[..colon].ToLowerInvariant();
        return Array.IndexOf(AllowedUriSchemes, scheme) >= 0;
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
        if (_resources?.Get("Font") is not Dict fonts)
        {
            return null;
        }
        // Key the cache by the font's object identity — the indirect reference if
        // present (value-equal), otherwise the dictionary instance — rather than
        // "depth:name". Different resource dictionaries can reuse a resource name
        // for different fonts, so a name-based key returned the wrong font.
        object? raw = fonts.GetRaw(name);
        object cacheKey = raw is Ref r ? r : _xref.FetchIfRef(raw) ?? name;
        if (_fontCache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }
        if (_xref.FetchIfRef(raw) is Dict fontDict)
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

        // Type3 glyphs are content-stream procedures, drawn as graphics rather
        // than emitted as selectable text.
        if (_state.Font.IsType3)
        {
            ShowType3Text(glyphs);
            return;
        }

        // `render` is what the browser paints; `real` is the selectable/searchable
        // text. For a glyph-mapped embedded font they differ: render uses a
        // per-code Private-Use-Area codepoint so the exact glyph is painted (no
        // shaping, no Unicode collisions), while real keeps the true Unicode.
        var render = new StringBuilder();
        var real = new StringBuilder();
        bool glyphMapped = _state.Font.UsesGlyphMap;
        double displacement = 0;

        foreach (var g in glyphs)
        {
            real.Append(g.Unicode);
            int pua = glyphMapped ? _state.Font.GlyphPuaChar(g.Code) : -1;
            if (pua >= 0)
            {
                render.Append((char)pua);
            }
            else
            {
                render.Append(g.Unicode);
            }
            double w0 = g.Width1000 / 1000.0 * _state.FontSize;
            double spacing = _state.CharSpacing + (g.IsSpace ? _state.WordSpacing : 0);
            displacement += (w0 + spacing) * _state.HorizScale;
        }

        // Emit every non-empty run, including invisible modes 3/7 (OCR layers on
        // scanned PDFs). `displacement` is the PDF-computed advance, used for width
        // correction.
        if (render.Length > 0)
        {
            EmitText(render.ToString(), real.ToString(), displacement);
        }

        _textMatrix = Matrix.Concat(_textMatrix, new Matrix(1, 0, 0, 1, displacement, 0));
    }

    /// <summary>
    /// Renders a run of Type3 glyphs by executing each glyph's content-stream
    /// procedure at the current text position, advancing the text matrix between
    /// glyphs (so a subsequent glyph is drawn to the right of the previous one).
    /// </summary>
    private void ShowType3Text(List<Glyph> glyphs)
    {
        PdfFont font = _state.Font!;
        Matrix fontMatrix = font.Type3!.FontMatrix;
        bool visible = _state.RenderMode is not (3 or 7);

        foreach (var g in glyphs)
        {
            if (visible && font.Type3.GetGlyphProcedure(g.Code) is { } proc)
            {
                RenderType3Glyph(proc, fontMatrix, font.Type3.Resources);
            }

            double w0 = g.Width1000 / 1000.0 * _state.FontSize;
            double spacing = _state.CharSpacing + (g.IsSpace ? _state.WordSpacing : 0);
            double displacement = (w0 + spacing) * _state.HorizScale;
            _textMatrix = Matrix.Concat(_textMatrix, new Matrix(1, 0, 0, 1, displacement, 0));
        }
    }

    private void RenderType3Glyph(PdfStream proc, Matrix fontMatrix, Dict? glyphResources)
    {
        if (_formDepth >= MaxFormDepth)
        {
            return;
        }
        _formDepth++;

        // Compose the glyph-space -> device transform from the current text
        // rendering matrix before mutating the graphics state.
        Matrix trm = Matrix.Concat(Matrix.Concat(_state.Ctm, _textMatrix),
            new Matrix(_state.FontSize * _state.HorizScale, 0, 0, _state.FontSize, 0, _state.TextRise));
        Matrix glyphToDevice = Matrix.Concat(trm, fontMatrix);

        _stack.Push(_state.Clone());
        _groupDepthStack.Push(_openGroups);
        Dict? savedResources = _resources;

        _state.Ctm = glyphToDevice;
        // Glyph procedures may reference their own resources; fall back to the
        // page resources so shared fonts/images still resolve.
        _resources = glyphResources ?? _page.Resources;

        try
        {
            byte[] content = Filters.StreamDecoder.Decode(proc);
            RunOps(new ContentParser(content).Parse());
        }
        catch
        {
            // Ignore malformed glyph procedures.
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

    private void EmitText(string renderText, string realText, double runAdvance)
    {
        // Map em-space (origin at the baseline, y up) to device pixels.
        Matrix trm = Matrix.Concat(Matrix.Concat(_state.Ctm, _textMatrix),
            new Matrix(_state.FontSize * _state.HorizScale, 0, 0, _state.FontSize, 0, _state.TextRise));

        double fontHeight = Math.Sqrt(trm.C * trm.C + trm.D * trm.D);
        if (fontHeight < 1e-3)
        {
            return;
        }

        // Target width in the span's LOCAL space (1em = fontHeight CSS px); the
        // viewer scales each run to this via --bp-sx so a substitute font's metrics
        // don't shift neighbouring runs.
        double denom = _state.FontSize * _state.HorizScale;
        double targetWidth = denom != 0 ? Math.Abs(runAdvance) * fontHeight / denom : 0;

        double a = trm.A / fontHeight;
        double b = trm.B / fontHeight;
        double c = -trm.C / fontHeight;
        double d = -trm.D / fontHeight;
        double e = trm.C * AscentFactor + trm.E;
        double f = trm.D * AscentFactor + trm.F;
        string transform = string.Create(CultureInfo.InvariantCulture,
            $"matrix({a:0.####},{b:0.####},{c:0.####},{d:0.####},{e:0.##},{f:0.##}) scaleX(var(--bp-sx,1))");

        int mode = _state.RenderMode;
        bool invisible = mode is 3 or 7;
        bool doFill = mode is 0 or 2 or 4 or 6;
        bool doStroke = mode is 1 or 2 or 5 or 6;

        // A glyph-mapped run paints exact glyphs via PUA codepoints (glyph layer,
        // not selectable) and carries the real Unicode on a transparent selection
        // layer. Otherwise a single selectable span suffices.
        if (renderText == realText)
        {
            AppendTextSpan(realText, fontHeight, targetWidth, transform, invisible, doFill, doStroke,
                embedded: true, glyphLayer: false, selectable: true);
        }
        else
        {
            AppendTextSpan(renderText, fontHeight, targetWidth, transform, invisible, doFill, doStroke,
                embedded: true, glyphLayer: true, selectable: false);
            AppendTextSpan(realText, fontHeight, targetWidth, transform, invisible: true, doFill: false, doStroke: false,
                embedded: false, glyphLayer: false, selectable: true);
        }
    }

    private void AppendTextSpan(string text, double fontHeight, double targetWidth, string transform,
        bool invisible, bool doFill, bool doStroke, bool embedded, bool glyphLayer, bool selectable)
    {
        _html.Append("<span");
        if (glyphLayer)
        {
            _html.Append(" data-bp-glyph"); // painted glyphs; excluded from search
        }
        // The painted glyph layer uses the embedded font's real advance metrics, so
        // it must NOT be width-corrected (that would re-stretch correct glyphs). Only
        // the selection layer and substitute-font runs get scaleX correction.
        if (!glyphLayer && targetWidth > 0.01)
        {
            _html.Append(string.Create(CultureInfo.InvariantCulture, $" data-w=\"{targetWidth:0.###}\""));
        }
        _html.Append(" style=\"position:absolute;left:0;top:0;white-space:pre;line-height:1");
        _html.Append(string.Create(CultureInfo.InvariantCulture, $";font-size:{fontHeight:0.###}px"));
        // Character/word spacing (Tc/Tw) are real inter-glyph gaps, not a stretch:
        // emit them as letter-/word-spacing so the run's natural width matches its
        // PDF advance (keeping the width-correction scaleX at ~1, no glyph spread).
        if (_state.FontSize != 0)
        {
            if (_state.CharSpacing != 0)
            {
                double ls = _state.CharSpacing * fontHeight / _state.FontSize;
                _html.Append(string.Create(CultureInfo.InvariantCulture, $";letter-spacing:{ls:0.###}px"));
            }
            if (_state.WordSpacing != 0)
            {
                double ws = _state.WordSpacing * fontHeight / _state.FontSize;
                _html.Append(string.Create(CultureInfo.InvariantCulture, $";word-spacing:{ws:0.###}px"));
            }
        }
        _html.Append(";color:").Append(invisible || !doFill ? "transparent" : _state.FillColor);
        if (!invisible && doStroke)
        {
            double sw = Math.Max(_state.LineWidth * _state.Ctm.ScaleFactor, 0.1);
            _html.Append(string.Create(CultureInfo.InvariantCulture,
                $";-webkit-text-stroke:{sw:0.###}px ")).Append(_state.StrokeColor);
        }
        if (!selectable)
        {
            _html.Append(";user-select:none;-webkit-user-select:none;pointer-events:none");
        }
        if (embedded)
        {
            AppendFontStyle(_html, _state.Font!);
        }
        else
        {
            _html.Append(";font-family:").Append(_state.Font!.GenericFamily);
        }
        _html.Append(";transform:").Append(transform).Append(";transform-origin:0 0");
        if (!invisible && doFill && _state.FillAlpha < 1)
        {
            _html.Append(string.Create(CultureInfo.InvariantCulture, $";opacity:{_state.FillAlpha:0.###}"));
        }
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
                string face =
                    $"@font-face{{font-family:'{family}';src:url(data:font/{fmt};base64,{b64}) format('{fmt}');}}";
                _fontFaces.Append(face);
                _pageFaces.Append(face);
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
            if (gs.Get("LC") is double lc)
            {
                _state.LineCap = (int)lc;
            }
            if (gs.Get("LJ") is double lj)
            {
                _state.LineJoin = (int)lj;
            }
            if (gs.Get("ML") is double ml)
            {
                _state.MiterLimit = ml;
            }
            // /D is [dashArray phase]; convert to device space like the `d` operator.
            if (gs.Get("D") is List<object?> dashSpec && dashSpec.Count >= 1
                && dashSpec[0] is List<object?> dashArr)
            {
                var pattern = dashArr.Where(o => o is double).Cast<double>()
                    .Select(v => v * _state.Ctm.ScaleFactor).ToArray();
                _state.DashArray = pattern.Length > 0 && pattern.Any(v => v > 0) ? pattern : null;
                _state.DashPhase = dashSpec.Count >= 2 && dashSpec[1] is double ph ? ph * _state.Ctm.ScaleFactor : 0;
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
            // Uncolored (PaintType 2) patterns carry their paint color in the
            // leading numeric operands; keep it as the current fill color so the
            // cell content (which sets no color of its own) paints with it.
            string? color = ColorViaSpace(_state.FillColorSpace, op);
            if (color is not null)
            {
                _state.FillColor = color;
            }
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
        // Key by the pattern's object identity (indirect ref or dict/stream
        // instance), not the bare resource name, so patterns in different
        // resource dictionaries that share a name don't alias each other.
        object cacheKey = name;
        if (_resources?.Get("Pattern") is Dict patterns)
        {
            object? raw = patterns.GetRaw(name);
            cacheKey = raw is Ref r ? r : _xref.FetchIfRef(raw) ?? name;
        }
        if (_patternCache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }
        _patternCache[cacheKey] = null; // guard against recursion

        string? result = BuildPattern(name);
        _patternCache[cacheKey] = result;
        return result;
    }

    private string? BuildPattern(string name)    {
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

    // Maximum number of pattern cells to emit for a single tiling fill; beyond
    // this the fill degrades to a solid color to bound the generated DOM.
    private const int MaxTiles = 4000;

    /// <summary>
    /// Renders a tiling pattern (PatternType 1) fill: the pattern cell's content
    /// stream is replayed across an XStep/YStep grid that covers the fill path's
    /// bounding box, all clipped to the fill path. Returns <c>false</c> (so the
    /// caller can fall back to a solid fill) when the pattern is not tiling or
    /// would require too many cells.
    /// </summary>
    private bool TryRenderTilingFill(string clipData, bool evenOdd)
    {
        if (_resources?.Get("Pattern") is not Dict patterns
            || patterns.Get(_state.FillPattern!) is not PdfStream patStream
            || patStream.Dict is null)
        {
            return false;
        }

        Dict pd = patStream.Dict;
        if ((pd.Get("PatternType") is double pt ? (int)pt : 0) != 1)
        {
            return false;
        }

        double xStep = Num(pd.Get("XStep"));
        double yStep = Num(pd.Get("YStep"));
        if (Math.Abs(xStep) < 1e-6 || Math.Abs(yStep) < 1e-6)
        {
            return false;
        }

        Matrix patternMatrix = pd.Get("Matrix") is List<object?> m && m.Count >= 6
            ? new Matrix(Num(m[0]), Num(m[1]), Num(m[2]), Num(m[3]), Num(m[4]), Num(m[5]))
            : Matrix.Identity;
        Matrix patToDevice = Matrix.Concat(_baseMatrix, patternMatrix);
        if (patToDevice.Invert() is not Matrix inv)
        {
            return false;
        }

        // Device-space bounding box of the fill region.
        if (!TryPathBounds(out double dMinX, out double dMinY, out double dMaxX, out double dMaxY))
        {
            return false;
        }

        // Map the device bbox corners into pattern space to find the cell range.
        double pMinX = double.MaxValue, pMinY = double.MaxValue;
        double pMaxX = double.MinValue, pMaxY = double.MinValue;
        foreach (var (dx, dy) in new[] { (dMinX, dMinY), (dMaxX, dMinY), (dMaxX, dMaxY), (dMinX, dMaxY) })
        {
            var (px, py) = inv.Apply(dx, dy);
            pMinX = Math.Min(pMinX, px); pMaxX = Math.Max(pMaxX, px);
            pMinY = Math.Min(pMinY, py); pMaxY = Math.Max(pMaxY, py);
        }

        (int iStart, int iEnd) = CellRange(pMinX, pMaxX, xStep);
        (int jStart, int jEnd) = CellRange(pMinY, pMaxY, yStep);
        long tileCount = (long)(iEnd - iStart + 1) * (jEnd - jStart + 1);
        if (tileCount is <= 0 or > MaxTiles)
        {
            return false;
        }

        double[] bbox = pd.Get("BBox") is List<object?> bb && bb.Count >= 4
            ? [Num(bb[0]), Num(bb[1]), Num(bb[2]), Num(bb[3])]
            : [0, 0, xStep, yStep];
        Dict? patRes = pd.Get("Resources") as Dict;

        // Clip the whole tiled group to the fill path.
        _html.Append("<div style=\"position:absolute;inset:0;clip-path:path(");
        if (evenOdd)
        {
            _html.Append("evenodd,");
        }
        _html.Append('\'').Append(clipData).Append("')\">");

        for (int j = jStart; j <= jEnd; j++)
        {
            for (int i = iStart; i <= iEnd; i++)
            {
                Matrix cellCtm = Matrix.Concat(patToDevice, new Matrix(1, 0, 0, 1, i * xStep, j * yStep));
                RunPatternCell(patStream, cellCtm, patRes, bbox);
            }
        }

        _html.Append("</div>");
        return true;
    }

    private static (int, int) CellRange(double min, double max, double step)
    {
        double lo = Math.Min(min / step, max / step);
        double hi = Math.Max(min / step, max / step);
        return ((int)Math.Floor(lo) - 1, (int)Math.Ceiling(hi) + 1);
    }

    private bool TryPathBounds(out double minX, out double minY, out double maxX, out double maxY)
    {
        minX = minY = double.MaxValue;
        maxX = maxY = double.MinValue;
        bool any = false;
        foreach (var sub in _subpaths)
        {
            foreach (var (x, y) in sub)
            {
                minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
                any = true;
            }
        }
        return any;
    }

    /// <summary>Replays a single tiling-pattern cell at <paramref name="cellCtm"/>, clipped to its BBox.</summary>
    private void RunPatternCell(PdfStream patStream, Matrix cellCtm, Dict? patRes, double[] bbox)
    {
        if (_formDepth >= MaxFormDepth)
        {
            return;
        }
        _formDepth++;

        // Clip the cell to its BBox (transformed to device space) so content does
        // not bleed past the tile when XStep/YStep are smaller than the BBox.
        var (c0x, c0y) = cellCtm.Apply(bbox[0], bbox[1]);
        var (c1x, c1y) = cellCtm.Apply(bbox[2], bbox[1]);
        var (c2x, c2y) = cellCtm.Apply(bbox[2], bbox[3]);
        var (c3x, c3y) = cellCtm.Apply(bbox[0], bbox[3]);
        _html.Append(string.Create(CultureInfo.InvariantCulture,
            $"<div style=\"position:absolute;inset:0;clip-path:path('M{c0x:0.##} {c0y:0.##} L{c1x:0.##} {c1y:0.##} L{c2x:0.##} {c2y:0.##} L{c3x:0.##} {c3y:0.##} Z')\">"));

        _stack.Push(_state.Clone());
        _groupDepthStack.Push(_openGroups);
        Dict? savedResources = _resources;

        _state.Ctm = cellCtm;
        // Clear the pattern from the cell's own state: a cell that issues a fill
        // before setting a color would otherwise re-enter this same pattern and
        // blow up the DOM / CPU with unbounded recursion.
        _state.FillPattern = null;
        _resources = patRes ?? _resources;

        try
        {
            byte[] content = Filters.StreamDecoder.Decode(patStream);
            RunOps(new ContentParser(content).Parse());
        }
        catch
        {
            // Ignore malformed pattern content.
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

        _html.Append("</div>");
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
        // Single CMYK→RGB implementation lives in ColorSpace (pdf.js polynomial).
        var (r, g, b) = ColorSpace.CmykToRgb(c, m, y, k);
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
