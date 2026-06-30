using System.Globalization;
using System.Text;
using BlazorPdf.Engine.Fonts;
using BlazorPdf.Engine.Rendering;

namespace BlazorPdf.Engine.Svg;

/// <summary>
/// Renders a <see cref="PdfPage"/> to an SVG document (standard DOM elements) instead
/// of a raster. Vector graphics become <c>&lt;path&gt;</c>, clips become
/// <c>&lt;clipPath&gt;</c>, images embed as PNG data URIs, axial/radial shadings become
/// gradients, and text becomes selectable <c>&lt;text&gt;</c> (CID fonts fall back to
/// glyph outlines). Coordinates are emitted in PDF points with a y-flip; the host sets
/// width/height for crisp scaling at any zoom.
/// </summary>
public sealed class SvgRenderer
{
    private readonly StringBuilder _body = new();
    private readonly StringBuilder _defs = new();
    private SvgGraphicsState _state = new();
    private readonly Stack<SvgGraphicsState> _stack = new();
    private PdfDocument _doc = null!;
    private readonly Stack<PdfDictionary?> _resources = new();

    private PdfMatrix _baseMatrix = PdfMatrix.Identity;
    private readonly StringBuilder _d = new();
    private (double X, double Y) _cursorUser;
    private (double X, double Y) _startUser;
    private bool _pendingClip;
    private bool _pendingClipEvenOdd;
    private int _idCounter;
    private int _formDepth;

    private PdfFont? _currentFont;
    private bool _fillPatternMode;
    private PdfMatrix _tm = PdfMatrix.Identity;
    private PdfMatrix _tlm = PdfMatrix.Identity;
    private double _penX;

    private double _widthPts, _heightPts;
    private int _rotation;

    /// <summary>Renders a page to a standalone SVG document string.</summary>
    public static string Render(PdfPage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        return new SvgRenderer().RenderInternal(page);
    }

    private string RenderInternal(PdfPage page)
    {
        _doc = page.Document;
        var box = page.MediaBox;
        double x0 = box[0], y0 = box[1];
        _widthPts = Math.Abs(box[2] - box[0]);
        _heightPts = Math.Abs(box[3] - box[1]);
        _rotation = page.Rotation;

        // Map user space (y up) to SVG space (y down), in points.
        _baseMatrix = new PdfMatrix(1, 0, 0, -1, -x0, _heightPts + y0);
        _state = new SvgGraphicsState { Ctm = _baseMatrix };
        _resources.Push(page.Resources);

        try
        {
            Interpret(page.GetContentBytes());
        }
        catch
        {
            // Emit whatever succeeded.
        }

        return Compose();
    }

    private string Compose()
    {
        // Outer dimensions account for page rotation.
        var vw = _rotation is 90 or 270 ? _heightPts : _widthPts;
        var vh = _rotation is 90 or 270 ? _widthPts : _heightPts;

        var sb = new StringBuilder();
        sb.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" xmlns:xlink=\"http://www.w3.org/1999/xlink\" ")
          .Append($"viewBox=\"0 0 {F(vw)} {F(vh)}\" width=\"100%\" height=\"100%\" preserveAspectRatio=\"xMidYMid meet\">");

        if (_defs.Length > 0)
        {
            sb.Append("<defs>").Append(_defs).Append("</defs>");
        }

        // Rotation wrapper (clockwise about the page) plus white page background.
        var rot = _rotation switch
        {
            90 => $"translate({F(_heightPts)} 0) rotate(90)",
            180 => $"translate({F(_widthPts)} {F(_heightPts)}) rotate(180)",
            270 => $"translate(0 {F(_widthPts)}) rotate(270)",
            _ => null,
        };

        if (rot is not null) sb.Append($"<g transform=\"{rot}\">");
        sb.Append($"<rect x=\"0\" y=\"0\" width=\"{F(_widthPts)}\" height=\"{F(_heightPts)}\" fill=\"#ffffff\"/>");
        sb.Append(_body);
        if (rot is not null) sb.Append("</g>");

        sb.Append("</svg>");
        return sb.ToString();
    }

    // --- Interpreter ---

    private void Interpret(byte[] content)
    {
        var lexer = new PdfLexer(content);
        var parser = new PdfParser(lexer);
        var operands = new List<PdfObject>();

        while (true)
        {
            var save = lexer.Position;
            var token = lexer.Next();
            if (token.Kind == PdfTokenKind.Eof) break;

            switch (token.Kind)
            {
                case PdfTokenKind.Number:
                    operands.Add(new PdfNumber(token.Number, token.Text == "int"));
                    break;
                case PdfTokenKind.String:
                    operands.Add(new PdfString(token.Bytes!));
                    break;
                case PdfTokenKind.Name:
                    operands.Add(new PdfName(token.Text!));
                    break;
                case PdfTokenKind.ArrayStart or PdfTokenKind.DictStart:
                    lexer.Position = save;
                    operands.Add(parser.ParseObject());
                    break;
                case PdfTokenKind.Keyword:
                    Execute(token.Text!, operands, lexer);
                    operands.Clear();
                    break;
                default:
                    operands.Clear();
                    break;
            }
        }
    }

    private static double Num(List<PdfObject> ops, int i) =>
        i >= 0 && i < ops.Count && ops[i] is PdfNumber n ? n.Value : 0;

    private double AsD(PdfObject? o) => _doc.Resolve(o) is PdfNumber n ? n.Value : 0;

    private (double X, double Y) Dev(double ux, double uy) => _state.Ctm.Apply(ux, uy);

    internal static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    private PdfDictionary? CurrentResources => _resources.Count > 0 ? _resources.Peek() : null;

    private PdfObject? LookupResource(string category, string name)
    {
        var res = _doc.Resolve(CurrentResources?.Get(category)) as PdfDictionary;
        return res is null ? null : _doc.Resolve(res.Get(name));
    }

    private void Execute(string op, List<PdfObject> ops, PdfLexer lexer)
    {
        switch (op)
        {
            case "q": _stack.Push(_state.Clone()); break;
            case "Q": if (_stack.Count > 0) _state = _stack.Pop(); break;
            case "cm":
                _state.Ctm = new PdfMatrix(Num(ops, 0), Num(ops, 1), Num(ops, 2), Num(ops, 3), Num(ops, 4), Num(ops, 5)).Multiply(_state.Ctm);
                break;
            case "w": _state.LineWidth = Num(ops, 0); break;
            case "gs": if (ops.OfType<PdfName>().LastOrDefault() is { } gn) ApplyExtGState(gn.Value); break;

            case "m": MoveTo(Num(ops, 0), Num(ops, 1)); break;
            case "l": LineTo(Num(ops, 0), Num(ops, 1)); break;
            case "c": CurveTo(Num(ops, 0), Num(ops, 1), Num(ops, 2), Num(ops, 3), Num(ops, 4), Num(ops, 5)); break;
            case "v": CurveTo(_cursorUser.X, _cursorUser.Y, Num(ops, 0), Num(ops, 1), Num(ops, 2), Num(ops, 3)); break;
            case "y": CurveTo(Num(ops, 0), Num(ops, 1), Num(ops, 2), Num(ops, 3), Num(ops, 2), Num(ops, 3)); break;
            case "re": Rect(Num(ops, 0), Num(ops, 1), Num(ops, 2), Num(ops, 3)); break;
            case "h": Close(); break;

            case "f" or "F": FillPath(false); EndPaint(); break;
            case "f*": FillPath(true); EndPaint(); break;
            case "S": StrokePath(); EndPaint(); break;
            case "s": Close(); StrokePath(); EndPaint(); break;
            case "B" or "B*": FillPath(op == "B*"); StrokePath(); EndPaint(); break;
            case "b" or "b*": Close(); FillPath(op == "b*"); StrokePath(); EndPaint(); break;
            case "n": EndPaint(); break;
            case "W": _pendingClip = true; _pendingClipEvenOdd = false; break;
            case "W*": _pendingClip = true; _pendingClipEvenOdd = true; break;

            case "g": _state.FillColor = Gray(Num(ops, 0)); _state.FillComponents = 1; _state.FillShading = null; break;
            case "G": _state.StrokeColor = Gray(Num(ops, 0)); _state.StrokeComponents = 1; break;
            case "rg": _state.FillColor = Rgb(Num(ops, 0), Num(ops, 1), Num(ops, 2)); _state.FillComponents = 3; _state.FillShading = null; break;
            case "RG": _state.StrokeColor = Rgb(Num(ops, 0), Num(ops, 1), Num(ops, 2)); _state.StrokeComponents = 3; break;
            case "k": _state.FillColor = Cmyk(Num(ops, 0), Num(ops, 1), Num(ops, 2), Num(ops, 3)); _state.FillComponents = 4; _state.FillShading = null; break;
            case "K": _state.StrokeColor = Cmyk(Num(ops, 0), Num(ops, 1), Num(ops, 2), Num(ops, 3)); _state.StrokeComponents = 4; break;
            case "cs": _state.FillComponents = SpaceComps(ops); _fillPatternMode = ops.OfType<PdfName>().LastOrDefault()?.Value == "Pattern"; _state.FillShading = null; break;
            case "CS": _state.StrokeComponents = SpaceComps(ops); break;
            case "sc" or "scn": SetFillScn(ops); break;
            case "SC" or "SCN": _state.StrokeColor = ColorOf(ops, _state.StrokeComponents); break;

            case "BT": _tm = PdfMatrix.Identity; _tlm = PdfMatrix.Identity; _penX = 0; break;
            case "ET": break;
            case "Td": TextMove(Num(ops, 0), Num(ops, 1)); break;
            case "TD": _state.Leading = -Num(ops, 1); TextMove(Num(ops, 0), Num(ops, 1)); break;
            case "Tm": _tlm = new PdfMatrix(Num(ops, 0), Num(ops, 1), Num(ops, 2), Num(ops, 3), Num(ops, 4), Num(ops, 5)); _tm = _tlm; _penX = 0; break;
            case "T*": TextMove(0, -_state.Leading); break;
            case "Tc": _state.CharSpacing = Num(ops, 0); break;
            case "Tw": _state.WordSpacing = Num(ops, 0); break;
            case "Tz": _state.HorizontalScale = Num(ops, 0) / 100.0; break;
            case "TL": _state.Leading = Num(ops, 0); break;
            case "Ts": _state.TextRise = Num(ops, 0); break;
            case "Tf": _currentFont = ops.OfType<PdfName>().LastOrDefault() is { } fn ? ResolveFont(fn.Value) : null; _state.FontSize = Num(ops, ops.Count - 1); break;
            case "Tr": _state.TextRenderMode = (int)Num(ops, 0); break;
            case "Tj": if (ops.LastOrDefault() is PdfString s) ShowText(s.Bytes); break;
            case "TJ": if (ops.LastOrDefault() is PdfArray a) ShowArray(a); break;
            case "'": TextMove(0, -_state.Leading); if (ops.LastOrDefault() is PdfString s1) ShowText(s1.Bytes); break;
            case "\"":
                _state.WordSpacing = Num(ops, 0); _state.CharSpacing = Num(ops, 1);
                TextMove(0, -_state.Leading);
                if (ops.LastOrDefault() is PdfString s2) ShowText(s2.Bytes);
                break;

            case "Do": if (ops.OfType<PdfName>().LastOrDefault() is { } xn) DoXObject(xn.Value); break;
            case "sh":
                if (ops.OfType<PdfName>().LastOrDefault() is { } shn && LookupResource("Shading", shn.Value) is { } sho)
                {
                    var sd = sho as PdfDictionary ?? (sho as PdfStream)?.Dictionary;
                    if (sd is not null) PaintShadingArea(sd);
                }
                break;
            case "BI": SkipInlineImage(lexer); break;
        }
    }

    // --- Path construction (device-space SVG path data) ---

    private void MoveTo(double ux, double uy) { var (x, y) = Dev(ux, uy); _d.Append($"M{F(x)} {F(y)} "); _startUser = (ux, uy); _cursorUser = (ux, uy); }
    private void LineTo(double ux, double uy) { var (x, y) = Dev(ux, uy); _d.Append($"L{F(x)} {F(y)} "); _cursorUser = (ux, uy); }

    private void CurveTo(double x1, double y1, double x2, double y2, double x3, double y3)
    {
        var c1 = Dev(x1, y1); var c2 = Dev(x2, y2); var p3 = Dev(x3, y3);
        _d.Append($"C{F(c1.X)} {F(c1.Y)} {F(c2.X)} {F(c2.Y)} {F(p3.X)} {F(p3.Y)} ");
        _cursorUser = (x3, y3);
    }

    private void Rect(double x, double y, double w, double h)
    {
        MoveTo(x, y); LineTo(x + w, y); LineTo(x + w, y + h); LineTo(x, y + h); Close();
    }

    private void Close() { _d.Append("Z "); _cursorUser = _startUser; }

    private string ClipAttr() => _state.ClipId is null ? "" : $" clip-path=\"url(#{_state.ClipId})\"";

    private void FillPath(bool evenOdd)
    {
        if (_state.FillShading is { } shading) { FillWithShading(shading); return; }
        var fr = evenOdd ? " fill-rule=\"evenodd\"" : "";
        var op = _state.FillAlpha < 1 ? $" fill-opacity=\"{F(_state.FillAlpha)}\"" : "";
        _body.Append($"<path d=\"{_d}\" fill=\"{_state.FillColor}\"{fr}{op}{ClipAttr()}/>");
    }

    private void StrokePath()
    {
        var w = _state.LineWidth * _state.Ctm.ScaleFactor;
        if (w <= 0) w = 1;
        var op = _state.StrokeAlpha < 1 ? $" stroke-opacity=\"{F(_state.StrokeAlpha)}\"" : "";
        _body.Append($"<path d=\"{_d}\" fill=\"none\" stroke=\"{_state.StrokeColor}\" stroke-width=\"{F(w)}\"{op}{ClipAttr()}/>");
    }

    private void EndPaint()
    {
        if (_pendingClip && _d.Length > 0)
        {
            var id = $"c{_idCounter++}";
            var parent = _state.ClipId is null ? "" : $" clip-path=\"url(#{_state.ClipId})\"";
            var fr = _pendingClipEvenOdd ? " clip-rule=\"evenodd\"" : "";
            _defs.Append($"<clipPath id=\"{id}\"{parent}><path d=\"{_d}\"{fr}/></clipPath>");
            _state.ClipId = id;
        }
        _d.Clear();
        _pendingClip = false;
    }

    private readonly Dictionary<PdfDictionary, PdfFont> _fontCache = [];

    private void ApplyExtGState(string name)
    {
        if (LookupResource("ExtGState", name) is not PdfDictionary g) return;
        if (_doc.Resolve(g.Get("ca")) is PdfNumber ca) _state.FillAlpha = Math.Clamp(ca.Value, 0, 1);
        if (_doc.Resolve(g.Get("CA")) is PdfNumber bca) _state.StrokeAlpha = Math.Clamp(bca.Value, 0, 1);
        if (_doc.Resolve(g.Get("LW")) is PdfNumber lw) _state.LineWidth = lw.Value;
    }

    private static string Gray(double v) => Hex(v, v, v);
    private static string Rgb(double r, double g, double b) => Hex(r, g, b);
    private static string Cmyk(double c, double m, double y, double k) => Hex((1 - c) * (1 - k), (1 - m) * (1 - k), (1 - y) * (1 - k));
    private static string Hex(double r, double g, double b) => $"#{Cl(r):x2}{Cl(g):x2}{Cl(b):x2}";
    private static int Cl(double v) => Math.Clamp((int)Math.Round(v * 255), 0, 255);
    private static string HexOf(PdfColor c) => $"#{c.R:x2}{c.G:x2}{c.B:x2}";

    private static int SpaceComps(List<PdfObject> ops) =>
        ops.OfType<PdfName>().LastOrDefault()?.Value switch
        {
            "DeviceGray" or "CalGray" or "G" => 1,
            "DeviceCMYK" or "CMYK" => 4,
            _ => 3,
        };

    private static string ColorOf(List<PdfObject> ops, int components)
    {
        var nums = ops.OfType<PdfNumber>().Select(n => n.Value).ToArray();
        return nums.Length switch
        {
            >= 4 => Cmyk(nums[^4], nums[^3], nums[^2], nums[^1]),
            3 => Rgb(nums[0], nums[1], nums[2]),
            1 => Gray(nums[0]),
            _ => "#000000",
        };
    }

    private void SetFillScn(List<PdfObject> ops)
    {
        if (_fillPatternMode && ops.OfType<PdfName>().LastOrDefault() is { } name)
        {
            ResolveFillPattern(name.Value);
        }
        else
        {
            _state.FillColor = ColorOf(ops, _state.FillComponents);
            _state.FillShading = null;
        }
    }

    private void ResolveFillPattern(string name)
    {
        _state.FillShading = null;
        var p = LookupResource("Pattern", name);
        var dict = p as PdfDictionary ?? (p as PdfStream)?.Dictionary;
        if (dict is null) return;

        var patternType = (_doc.Resolve(dict.Get("PatternType")) as PdfNumber)?.AsInt ?? 0;
        if (patternType == 2 && _doc.Resolve(dict.Get("Shading")) is { } sh)
        {
            var sd = sh as PdfDictionary ?? (sh as PdfStream)?.Dictionary;
            if (sd is not null) { _state.FillShading = sd; _state.FillShadingMatrix = ReadMatrix(dict.Get("Matrix")); }
        }
        else
        {
            _state.FillColor = "#808080"; // tiling pattern fallback
        }
    }

    private PdfFont? ResolveFont(string name)
    {
        if (LookupResource("Font", name) is not PdfDictionary fd) return null;
        if (_fontCache.TryGetValue(fd, out var cached)) return cached;
        try { var f = PdfFont.Load(_doc, fd); _fontCache[fd] = f; return f; }
        catch { return null; }
    }

    private void TextMove(double tx, double ty)
    {
        _tlm = PdfMatrix.Translate(tx, ty).Multiply(_tlm);
        _tm = _tlm;
        _penX = 0;
    }

    private void ShowArray(PdfArray array)
    {
        foreach (var item in array.Items)
        {
            if (item is PdfString s) ShowText(s.Bytes);
            else if (item is PdfNumber n) _penX -= n.Value / 1000.0 * _state.FontSize * _state.HorizontalScale;
        }
    }

    private void ShowText(byte[] bytes)
    {
        var fs = _state.FontSize;
        var th = _state.HorizontalScale;
        var font = _currentFont;

        if (font is null)
        {
            foreach (var _ in bytes) _penX += 0.5 * fs * th;
            return;
        }

        var a = _tm.Multiply(_state.Ctm);
        foreach (var code in font.Decode(bytes))
        {
            if (_state.TextRenderMode != 3 && !font.IsSpace(code)) DrawGlyph(font, code, fs, th, _state.TextRise, a);
            var w = font.GetWidth(code) / 1000.0 * fs + _state.CharSpacing;
            if (font.IsSpace(code)) w += _state.WordSpacing;
            _penX += w * th;
        }
    }

    private void DrawGlyph(PdfFont font, int code, double fs, double th, double rise, PdfMatrix a)
    {
        if (font.TwoByte)
        {
            var contours = font.GetContours(code);
            if (contours is not null) EmitOutline(contours, fs, th, rise, a);
            return;
        }

        var unicode = WinAnsiEncoding.ToUnicode(code);
        string ch;
        try { ch = char.ConvertFromUtf32(unicode); }
        catch { ch = ((char)code).ToString(); }
        if (string.IsNullOrEmpty(ch)) return;

        var ma = a.A * fs * th;
        var mb = a.B * fs * th;
        var mc = -a.C * fs;
        var md = -a.D * fs;
        var me = a.A * _penX + a.C * rise + a.E;
        var mf = a.B * _penX + a.D * rise + a.F;

        var (fam, wt, st) = FontCss(font.BaseFont);
        var op = _state.FillAlpha < 1 ? $" fill-opacity=\"{F(_state.FillAlpha)}\"" : "";
        _body.Append($"<text transform=\"matrix({F(ma)} {F(mb)} {F(mc)} {F(md)} {F(me)} {F(mf)})\" " +
                     $"font-size=\"1\" font-family=\"{fam}\"{wt}{st} fill=\"{_state.FillColor}\"{op}{ClipAttr()} " +
                     $"xml:space=\"preserve\">{Escape(ch)}</text>");
    }

    private void EmitOutline(List<List<(double X, double Y)>> contours, double fs, double th, double rise, PdfMatrix a)
    {
        var sb = new StringBuilder();
        foreach (var contour in contours)
        {
            for (var i = 0; i < contour.Count; i++)
            {
                var ex = contour[i].X / 1000.0;
                var ey = contour[i].Y / 1000.0;
                var dev = a.Apply(_penX + ex * fs * th, rise + ey * fs);
                sb.Append(i == 0 ? "M" : "L").Append($"{F(dev.X)} {F(dev.Y)} ");
            }
            sb.Append("Z ");
        }
        var op = _state.FillAlpha < 1 ? $" fill-opacity=\"{F(_state.FillAlpha)}\"" : "";
        _body.Append($"<path d=\"{sb}\" fill=\"{_state.FillColor}\"{op}{ClipAttr()}/>");
    }

    private static (string Family, string Weight, string Style) FontCss(string baseFont)
    {
        var n = baseFont.ToLowerInvariant();
        var plus = n.IndexOf('+');
        if (plus == 6) n = n[(plus + 1)..];

        var family = n.Contains("courier") || n.Contains("mono") ? "monospace"
            : n.Contains("times") || n.Contains("serif") || n.Contains("georgia") || n.Contains("roman") ? "serif"
            : "sans-serif";
        var weight = n.Contains("bold") ? " font-weight=\"bold\"" : "";
        var style = n.Contains("italic") || n.Contains("oblique") ? " font-style=\"italic\"" : "";
        return (family, weight, style);
    }

    private void DoXObject(string name)
    {
        if (LookupResource("XObject", name) is not PdfStream stream) return;
        var subtype = (_doc.Resolve(stream.Dictionary.Get("Subtype")) as PdfName)?.Value;
        if (subtype == "Form") RunForm(stream); else DrawImage(stream);
    }

    private void RunForm(PdfStream form)
    {
        if (_formDepth >= 12) return;
        _formDepth++;
        _stack.Push(_state.Clone());

        if (_doc.Resolve(form.Dictionary.Get("Matrix")) is PdfArray { Count: >= 6 } m)
        {
            _state.Ctm = new PdfMatrix(AsD(m[0]), AsD(m[1]), AsD(m[2]), AsD(m[3]), AsD(m[4]), AsD(m[5])).Multiply(_state.Ctm);
        }

        var formResources = _doc.Resolve(form.Dictionary.Get("Resources")) as PdfDictionary;
        _resources.Push(formResources ?? CurrentResources);
        try { Interpret(PdfFilters.Decode(form, _doc)); }
        finally { _resources.Pop(); if (_stack.Count > 0) _state = _stack.Pop(); _formDepth--; }
    }

    private void DrawImage(PdfStream stream)
    {
        var dict = stream.Dictionary;
        var filters = ImageFilters(dict);

        // DOM backend: embed browser-decodable codecs directly (robust, and the right
        // approach for SVG). JPEG/JPEG2000 are decoded by the browser's <image>.
        if (filters.Contains("DCTDecode") || filters.Contains("DCT"))
        {
            EmitEncodedImage(stream, "image/jpeg");
            return;
        }
        if (filters.Contains("JPXDecode"))
        {
            EmitEncodedImage(stream, "image/jp2");
            return;
        }

        // Everything else: decode to RGBA in C# and embed as PNG.
        var img = PdfImage.Decode(_doc, stream);
        if (img is null) return;

        RenderedImage ri;
        if (img.IsStencil && img.StencilPaint is { } paint)
        {
            var (r, g, b) = ParseHex(_state.FillColor);
            var rgba = new byte[img.Width * img.Height * 4];
            for (var i = 0; i < paint.Length; i++)
            {
                if (!paint[i]) continue;
                var o = i * 4;
                rgba[o] = r; rgba[o + 1] = g; rgba[o + 2] = b; rgba[o + 3] = 255;
            }
            ri = new RenderedImage(img.Width, img.Height, rgba);
        }
        else if (img.Rgba is { } rgbaData)
        {
            ri = new RenderedImage(img.Width, img.Height, rgbaData);
        }
        else
        {
            return;
        }

        EmitImageElement($"data:image/png;base64,{Convert.ToBase64String(ri.ToPng())}");
    }

    private void EmitEncodedImage(PdfStream stream, string mime)
    {
        // PdfFilters.Decode applies any pre-codec filters and passes the codec bytes
        // through unchanged, so this yields the raw JPEG/JP2 the browser can decode.
        var bytes = PdfFilters.Decode(stream, _doc);
        if (bytes.Length == 0) return;
        EmitImageElement($"data:{mime};base64,{Convert.ToBase64String(bytes)}");
    }

    private void EmitImageElement(string href)
    {
        var m = new PdfMatrix(1, 0, 0, -1, 0, 1).Multiply(_state.Ctm); // flip image y, then CTM
        var op = _state.FillAlpha < 1 ? $" opacity=\"{F(_state.FillAlpha)}\"" : "";
        // Use SVG2 `href` (xlink:href is dropped when SVG is injected as inline HTML).
        _body.Append($"<image x=\"0\" y=\"0\" width=\"1\" height=\"1\" preserveAspectRatio=\"none\" " +
                     $"transform=\"matrix({F(m.A)} {F(m.B)} {F(m.C)} {F(m.D)} {F(m.E)} {F(m.F)})\"{op}{ClipAttr()} " +
                     $"href=\"{href}\"/>");
    }

    private List<string> ImageFilters(PdfDictionary dict)
    {
        var names = new List<string>();
        var f = _doc.Resolve(dict.Get("Filter") ?? dict.Get("F"));
        if (f is PdfName n) names.Add(n.Value);
        else if (f is PdfArray arr)
        {
            foreach (var item in arr.Items)
            {
                if (_doc.Resolve(item) is PdfName fn) names.Add(fn.Value);
            }
        }
        return names;
    }

    private void PaintShadingArea(PdfDictionary sh)
    {
        var grad = BuildGradient(sh, _state.Ctm);
        if (grad is null) return;
        _body.Append($"<rect x=\"0\" y=\"0\" width=\"{F(_widthPts)}\" height=\"{F(_heightPts)}\" fill=\"url(#{grad})\"{ClipAttr()}/>");
    }

    private void FillWithShading(PdfDictionary sh)
    {
        var grad = BuildGradient(sh, _state.FillShadingMatrix.Multiply(_baseMatrix));
        var fill = grad is null ? "#808080" : $"url(#{grad})";
        _body.Append($"<path d=\"{_d}\" fill=\"{fill}\"{ClipAttr()}/>");
    }

    private string? BuildGradient(PdfDictionary sh, PdfMatrix toDevice)
    {
        var type = (_doc.Resolve(sh.Get("ShadingType")) as PdfNumber)?.AsInt ?? 0;
        if (type is not (2 or 3)) return null;

        var func = PdfFunction.Parse(_doc, sh.Get("Function"));
        if (func is null) return null;
        var coords = ReadDoubles(sh.Get("Coords"));
        if (coords is null || coords.Length < (type == 2 ? 4 : 6)) return null;

        var cs = ColorSpaceInfo.Parse(_doc, _doc.Resolve(sh.Get("ColorSpace")));
        double t0 = 0, t1 = 1;
        if (_doc.Resolve(sh.Get("Domain")) is PdfArray { Count: >= 2 } dom) { t0 = AsD(dom[0]); t1 = AsD(dom[1]); }

        const int n = 24;
        var stops = new StringBuilder();
        for (var i = 0; i <= n; i++)
        {
            var s = i / (double)n;
            var col = cs.ToColor(func.Eval(t0 + s * (t1 - t0)));
            stops.Append($"<stop offset=\"{F(s)}\" stop-color=\"{HexOf(col)}\"/>");
        }

        var id = $"g{_idCounter++}";
        if (type == 2)
        {
            var p0 = toDevice.Apply(coords[0], coords[1]);
            var p1 = toDevice.Apply(coords[2], coords[3]);
            _defs.Append($"<linearGradient id=\"{id}\" gradientUnits=\"userSpaceOnUse\" " +
                         $"x1=\"{F(p0.X)}\" y1=\"{F(p0.Y)}\" x2=\"{F(p1.X)}\" y2=\"{F(p1.Y)}\">{stops}</linearGradient>");
        }
        else
        {
            var c1 = toDevice.Apply(coords[3], coords[4]);
            var f0 = toDevice.Apply(coords[0], coords[1]);
            var r = coords[5] * toDevice.ScaleFactor;
            _defs.Append($"<radialGradient id=\"{id}\" gradientUnits=\"userSpaceOnUse\" " +
                         $"cx=\"{F(c1.X)}\" cy=\"{F(c1.Y)}\" r=\"{F(r)}\" fx=\"{F(f0.X)}\" fy=\"{F(f0.Y)}\">{stops}</radialGradient>");
        }
        return id;
    }

    private PdfMatrix ReadMatrix(PdfObject? obj) =>
        _doc.Resolve(obj) is PdfArray { Count: >= 6 } m
            ? new PdfMatrix(AsD(m[0]), AsD(m[1]), AsD(m[2]), AsD(m[3]), AsD(m[4]), AsD(m[5]))
            : PdfMatrix.Identity;

    private double[]? ReadDoubles(PdfObject? obj)
    {
        if (_doc.Resolve(obj) is not PdfArray arr) return null;
        var result = new double[arr.Count];
        for (var i = 0; i < arr.Count; i++) result[i] = AsD(arr[i]);
        return result;
    }

    private static (byte R, byte G, byte B) ParseHex(string hex)
    {
        if (hex.Length == 7 && hex[0] == '#'
            && int.TryParse(hex.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var r)
            && int.TryParse(hex.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var g)
            && int.TryParse(hex.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
        {
            return ((byte)r, (byte)g, (byte)b);
        }
        return (0, 0, 0);
    }

    private static void SkipInlineImage(PdfLexer lexer)
    {
        var data = lexer.Data;
        var idx = PdfParser.IndexOf(data, "EI", lexer.Position);
        lexer.Position = idx < 0 ? data.Length : idx + 2;
    }

    private static string Escape(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
