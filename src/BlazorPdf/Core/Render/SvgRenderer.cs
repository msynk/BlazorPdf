// Clean-room C# port inspired by the pdf.js SVG backend (`src/display/svg.js`)
// and content evaluator (`src/core/evaluator.js`). It walks a page's operator
// list and emits plain SVG DOM elements: <path> for vector graphics, <text>
// for selectable text, <image> for rasters, gradients for shadings, and
// <clipPath> groups for clipping. See NOTICE.

using System.Globalization;
using System.Text;
using BlazorPdf.Core.Content;
using BlazorPdf.Core.Fonts;
using BlazorPdf.Core.Geometry;

namespace BlazorPdf.Core.Render;

/// <summary>
/// Renders a <see cref="PdfPage"/> to an SVG document string. All output is
/// native SVG DOM so text remains selectable and graphics stay resolution
/// independent.
/// </summary>
public sealed class SvgRenderer
{
    private const int MaxFormDepth = 12;

    private readonly PdfPage _page;
    private readonly IXRef _xref;
    private readonly Dictionary<string, PdfFont> _fontCache = new();

    private GraphicsState _state = new();
    private readonly Stack<GraphicsState> _stack = new();
    private readonly Stack<int> _groupDepthStack = new();
    private Dict? _resources;
    private Matrix _baseMatrix = Matrix.Identity;

    // Current path under construction, in device coordinates.
    private readonly StringBuilder _pathData = new();
    private double _curX, _curY;
    private double _startX, _startY;
    private bool? _pendingClipEvenOdd;

    // Text object matrices.
    private Matrix _textMatrix = Matrix.Identity;
    private Matrix _textLineMatrix = Matrix.Identity;

    private readonly StringBuilder _defs = new();
    private readonly StringBuilder _fontFaces = new();
    private readonly HashSet<string> _emittedFamilies = new();
    private readonly Dictionary<string, string?> _patternCache = new();
    private StringBuilder _svgBody = new();
    private int _openGroups;
    private int _idCounter;
    private int _formDepth;

    private readonly int _rotationOffset;

    public SvgRenderer(PdfPage page, IXRef xref, int rotationOffset = 0)
    {
        _page = page;
        _xref = xref;
        _resources = page.Resources;
        _rotationOffset = ((rotationOffset % 360) + 360) % 360;
    }

    /// <summary>Renders the page and returns a complete <c>&lt;svg&gt;</c> element.</summary>
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

        List<Operation> ops;
        try
        {
            ops = new ContentParser(_page.GetContentBytes()).Parse();
        }
        catch (Exception ex)
        {
            ops = new List<Operation>();
            _svgBody.Append($"<!-- content parse error: {Escape(ex.Message)} -->");
        }

        RunOps(ops);

        // Close any clip groups left open by unbalanced q/Q.
        while (_openGroups > 0)
        {
            _svgBody.Append("</g>");
            _openGroups--;
        }

        RenderAnnotations();

        var sb = new StringBuilder();
        sb.Append(string.Create(CultureInfo.InvariantCulture,
            $"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {viewW:0.##} {viewH:0.##}\" width=\"{viewW:0.##}\" height=\"{viewH:0.##}\">"));
        sb.Append("<rect x=\"0\" y=\"0\" width=\"100%\" height=\"100%\" fill=\"white\"/>");
        if (_defs.Length > 0 || _fontFaces.Length > 0)
        {
            sb.Append("<defs>");
            if (_fontFaces.Length > 0)
            {
                sb.Append("<style type=\"text/css\">").Append(_fontFaces).Append("</style>");
            }
            sb.Append(_defs);
            sb.Append("</defs>");
        }
        sb.Append(_svgBody);
        sb.Append("</svg>");
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
                        _svgBody.Append("</g>");
                        _openGroups--;
                    }
                }
                break;
            case "cm":
                _state.Ctm = Matrix.Concat(_state.Ctm,
                    new Matrix(op.Num(0), op.Num(1), op.Num(2), op.Num(3), op.Num(4), op.Num(5)));
                break;
            case "w": _state.LineWidth = op.Num(0); break;
            case "gs": ApplyExtGState(op); break;

            // Colors.
            case "g": _state.FillColor = Gray(op.Num(0)); _state.FillPattern = null; break;
            case "G": _state.StrokeColor = Gray(op.Num(0)); break;
            case "rg": _state.FillColor = Rgb(op.Num(0), op.Num(1), op.Num(2)); _state.FillPattern = null; break;
            case "RG": _state.StrokeColor = Rgb(op.Num(0), op.Num(1), op.Num(2)); break;
            case "k": _state.FillColor = Cmyk(op.Num(0), op.Num(1), op.Num(2), op.Num(3)); _state.FillPattern = null; break;
            case "K": _state.StrokeColor = Cmyk(op.Num(0), op.Num(1), op.Num(2), op.Num(3)); break;
            case "sc":
            case "scn": SetFillColorN(op); break;
            case "SC":
            case "SCN": _state.StrokeColor = ColorFromComponents(op) ?? _state.StrokeColor; break;

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

    private void MoveTo(double x, double y)
    {
        _curX = _startX = x;
        _curY = _startY = y;
        var (dx, dy) = _state.Ctm.Apply(x, y);
        _pathData.Append(string.Create(CultureInfo.InvariantCulture, $"M{dx:0.##} {dy:0.##} "));
    }

    private void LineTo(double x, double y)
    {
        _curX = x;
        _curY = y;
        var (dx, dy) = _state.Ctm.Apply(x, y);
        _pathData.Append(string.Create(CultureInfo.InvariantCulture, $"L{dx:0.##} {dy:0.##} "));
    }

    private void CurveTo(double x1, double y1, double x2, double y2, double x3, double y3)
    {
        var (a, b) = _state.Ctm.Apply(x1, y1);
        var (c, d) = _state.Ctm.Apply(x2, y2);
        var (e, f) = _state.Ctm.Apply(x3, y3);
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
        _pathData.Append("Z ");
    }

    private void PaintPath(bool stroke, bool fill, bool evenOdd)
    {
        if (_pathData.Length == 0)
        {
            ApplyPendingClip();
            return;
        }

        string data = _pathData.ToString().Trim();

        var attrs = new StringBuilder();
        if (fill)
        {
            string fillPaint = _state.FillPattern is not null
                ? ResolveFillPaint(_state.FillPattern) ?? _state.FillColor
                : _state.FillColor;
            attrs.Append($" fill=\"{fillPaint}\"");
        }
        else
        {
            attrs.Append(" fill=\"none\"");
        }
        if (fill && evenOdd)
        {
            attrs.Append(" fill-rule=\"evenodd\"");
        }
        if (fill && _state.FillAlpha < 1)
        {
            attrs.Append(string.Create(CultureInfo.InvariantCulture, $" fill-opacity=\"{_state.FillAlpha:0.###}\""));
        }
        if (stroke)
        {
            double deviceWidth = Math.Max(0.1, _state.LineWidth * _state.Ctm.ScaleFactor);
            attrs.Append($" stroke=\"{_state.StrokeColor}\"");
            attrs.Append(string.Create(CultureInfo.InvariantCulture, $" stroke-width=\"{deviceWidth:0.###}\""));
            if (_state.StrokeAlpha < 1)
            {
                attrs.Append(string.Create(CultureInfo.InvariantCulture, $" stroke-opacity=\"{_state.StrokeAlpha:0.###}\""));
            }
        }

        _svgBody.Append($"<path d=\"{data}\"{attrs}{StyleAttr()}/>");

        ApplyPendingClip(data);
        _pathData.Clear();
    }

    private void EndPathNoPaint()
    {
        ApplyPendingClip(_pathData.Length > 0 ? _pathData.ToString().Trim() : null);
        _pathData.Clear();
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

        string id = $"clip{_idCounter++}";
        _defs.Append($"<clipPath id=\"{id}\" clipPathUnits=\"userSpaceOnUse\"><path d=\"{data}\"");
        if (evenOdd)
        {
            _defs.Append(" clip-rule=\"evenodd\"");
        }
        _defs.Append("/></clipPath>");

        _svgBody.Append($"<g clip-path=\"url(#{id})\">");
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

        string id = $"grad{_idCounter++}";
        string? fill = ShadingBuilder.Build(shading, _xref, _resources, _state.Ctm, id, _defs);
        if (fill is null)
        {
            return;
        }
        // Fill the current clip region (or the whole page when unclipped).
        _svgBody.Append($"<rect x=\"-100000\" y=\"-100000\" width=\"200000\" height=\"200000\" fill=\"{fill}\"/>");
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
        EmitImage(uri);
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
            EmitImage(uri);
        }
    }

    private void EmitImage(string uri)
    {
        // The image occupies the unit square; flip Y so row 0 is at the top.
        Matrix m = Matrix.Concat(_state.Ctm, new Matrix(1, 0, 0, -1, 0, 1));
        _svgBody.Append("<image x=\"0\" y=\"0\" width=\"1\" height=\"1\" preserveAspectRatio=\"none\" transform=\"");
        _svgBody.Append(m.ToSvg());
        if (_state.FillAlpha < 1)
        {
            _svgBody.Append(string.Create(CultureInfo.InvariantCulture, $"\" opacity=\"{_state.FillAlpha:0.###}"));
        }
        _svgBody.Append("\" href=\"");
        _svgBody.Append(uri);
        _svgBody.Append('"');
        _svgBody.Append(StyleAttr());
        _svgBody.Append("/>");
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
                _svgBody.Append("</g>");
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
            _svgBody.Append("</g>");
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
        _svgBody.Append($"<a href=\"{Escape(uri)}\" target=\"_blank\"><rect transform=\"");
        _svgBody.Append(_baseMatrix.ToSvg());
        _svgBody.Append(string.Create(CultureInfo.InvariantCulture,
            $"\" x=\"{r[0]:0.##}\" y=\"{r[1]:0.##}\" width=\"{r[2] - r[0]:0.##}\" height=\"{r[3] - r[1]:0.##}\" fill=\"transparent\"/></a>"));
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
        double runWidth = 0;
        double displacement = 0;

        foreach (var g in glyphs)
        {
            text.Append(g.Unicode);
            double w0 = g.Width1000 / 1000.0 * _state.FontSize;
            double spacing = _state.CharSpacing + (g.IsSpace ? _state.WordSpacing : 0);
            runWidth += w0;
            displacement += (w0 + spacing) * _state.HorizScale;
        }

        if (text.Length > 0 && _state.RenderMode != 7)
        {
            EmitText(text.ToString(), runWidth);
        }

        _textMatrix = Matrix.Concat(_textMatrix, new Matrix(1, 0, 0, 1, displacement, 0));
    }

    private void EmitText(string text, double runWidth)
    {
        Matrix m = Matrix.Concat(_state.Ctm, _textMatrix);
        Matrix local = new(_state.HorizScale, 0, 0, -1, 0, _state.TextRise);
        Matrix final = Matrix.Concat(m, local);

        string fill = _state.RenderMode is 3 or 7 ? "transparent" : _state.FillColor;

        var sb = _svgBody;
        sb.Append("<text transform=\"");
        sb.Append(final.ToSvg());
        sb.Append(string.Create(CultureInfo.InvariantCulture, $"\" font-size=\"{Math.Abs(_state.FontSize):0.###}\""));
        sb.Append($" fill=\"{fill}\"");
        AppendFontAttributes(sb, _state.Font!);
        sb.Append(" xml:space=\"preserve\"");
        if (runWidth > 0)
        {
            sb.Append(string.Create(CultureInfo.InvariantCulture,
                $" textLength=\"{runWidth:0.###}\" lengthAdjust=\"spacingAndGlyphs\""));
        }
        sb.Append(StyleAttr());
        sb.Append('>');
        sb.Append(Escape(text));
        sb.Append("</text>");
    }

    private void AppendFontAttributes(StringBuilder sb, PdfFont font)
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
            sb.Append($" font-family=\"{family},{font.GenericFamily}\"");
        }
        else
        {
            sb.Append($" font-family=\"{font.GenericFamily}\"");
        }
        if (font.Bold)
        {
            sb.Append(" font-weight=\"bold\"");
        }
        if (font.Italic)
        {
            sb.Append(" font-style=\"italic\"");
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
            _state.FillColor = ColorFromComponents(op) ?? _state.FillColor;
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

        if (patternType == 2)
        {
            object? shadingObj = patDict.Get("Shading");
            Dict? shading = shadingObj as Dict ?? (shadingObj as PdfStream)?.Dict;
            if (shading is null)
            {
                return null;
            }
            string id = $"grad{_idCounter++}";
            return ShadingBuilder.Build(shading, _xref, _resources, Matrix.Concat(_baseMatrix, matrix), id, _defs);
        }
        if (patternType == 1 && obj is PdfStream tileStream)
        {
            return RenderTilingPattern(tileStream, patDict, matrix);
        }
        return null;
    }

    private string? RenderTilingPattern(PdfStream stream, Dict patDict, Matrix matrix)
    {
        if (_formDepth >= MaxFormDepth)
        {
            return null;
        }

        double[] bbox = patDict.Get("BBox") is List<object?> bb && bb.Count >= 4
            ? [Num(bb[0]), Num(bb[1]), Num(bb[2]), Num(bb[3])]
            : [0, 0, 1, 1];
        double xStep = patDict.Get("XStep") is double xs && xs != 0 ? xs : bbox[2] - bbox[0];
        double yStep = patDict.Get("YStep") is double ys && ys != 0 ? ys : bbox[3] - bbox[1];
        if (xStep == 0 || yStep == 0)
        {
            return null;
        }

        // Snapshot renderer state, render the tile cell in pattern space, restore.
        var savedBody = _svgBody;
        var savedState = _state;
        var savedResources = _resources;
        int savedGroups = _openGroups;
        string savedPath = _pathData.ToString();
        Matrix savedTm = _textMatrix, savedTlm = _textLineMatrix;

        _svgBody = new StringBuilder();
        _openGroups = 0;
        _pathData.Clear();
        _state = new GraphicsState { Ctm = Matrix.Identity }; // tile drawn in pattern space
        _resources = patDict.Get("Resources") as Dict ?? _resources;
        _formDepth++;

        try
        {
            RunOps(new ContentParser(Filters.StreamDecoder.Decode(stream)).Parse());
        }
        catch
        {
            // ignore malformed tile content
        }
        while (_openGroups > 0)
        {
            _svgBody.Append("</g>");
            _openGroups--;
        }
        string tileBody = _svgBody.ToString();

        _formDepth--;
        _svgBody = savedBody;
        _state = savedState;
        _resources = savedResources;
        _openGroups = savedGroups;
        _pathData.Clear();
        _pathData.Append(savedPath);
        _textMatrix = savedTm;
        _textLineMatrix = savedTlm;

        string id = $"pat{_idCounter++}";
        Matrix transform = Matrix.Concat(_baseMatrix, matrix);
        _defs.Append(string.Create(CultureInfo.InvariantCulture,
            $"<pattern id=\"{id}\" patternUnits=\"userSpaceOnUse\" patternTransform=\"{transform.ToSvg()}\" x=\"{bbox[0]:0.##}\" y=\"{bbox[1]:0.##}\" width=\"{xStep:0.##}\" height=\"{yStep:0.##}\" overflow=\"visible\">"));
        _defs.Append(tileBody);
        _defs.Append("</pattern>");
        return $"url(#{id})";
    }

    private string StyleAttr()
        => _state.BlendMode.Length == 0 ? "" : $" style=\"mix-blend-mode:{_state.BlendMode}\"";

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
