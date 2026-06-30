using BlazorPdf.Engine.Fonts;

namespace BlazorPdf.Engine.Rendering;

/// <summary>
/// Rasterizes a <see cref="PdfPage"/> to an RGBA bitmap by interpreting its content
/// stream. Implements graphics state, path construction and painting, color spaces
/// (Gray/RGB/CMYK), Bézier flattening, and text drawn with the built-in
/// <see cref="StrokeFont"/>. Entirely CPU-based and dependency-free.
/// </summary>
public sealed class PdfRenderer
{
    private const int BezierSegments = 16;

    private Raster _raster = null!;
    private GraphicsState _state = new();
    private readonly Stack<GraphicsState> _stack = new();

    // Current path: a list of subpaths, each a list of device-space points.
    private readonly List<List<(double X, double Y)>> _path = [];
    private List<(double X, double Y)>? _current;
    private (double X, double Y) _start;
    private (double X, double Y) _cursorUser; // last point in user space (for curves)

    // Text matrices.
    private PdfMatrix _tm = PdfMatrix.Identity;
    private PdfMatrix _tlm = PdfMatrix.Identity;
    private double _penX;

    // Resource scope (page or nested form) and font cache.
    private PdfDocument _doc = null!;
    private readonly Stack<PdfDictionary?> _resources = new();
    private readonly Dictionary<PdfDictionary, PdfFont> _fontCache = [];
    private PdfFont? _currentFont;
    private int _formDepth;

    private bool _pendingClip;
    private bool _pendingClipEvenOdd;
    private PdfMatrix _baseMatrix = PdfMatrix.Identity;
    private bool _fillPatternMode;

    /// <summary>Renders a page at the given scale (1.0 = 72 DPI). Honors page rotation.</summary>
    public static RenderedImage Render(PdfPage page, double scale = 1.5)
    {
        ArgumentNullException.ThrowIfNull(page);
        return new PdfRenderer().RenderInternal(page, scale);
    }

    private RenderedImage RenderInternal(PdfPage page, double scale)
    {
        var box = page.MediaBox;
        double x0 = box[0], y0 = box[1];
        var wPts = Math.Abs(box[2] - box[0]);
        var hPts = Math.Abs(box[3] - box[1]);

        var pw = Math.Max(1, (int)Math.Round(wPts * scale));
        var ph = Math.Max(1, (int)Math.Round(hPts * scale));

        _raster = new Raster(pw, ph);
        _raster.Clear(PdfColor.White);

        // Map user space (origin bottom-left, y up) to device pixels (top-left, y down).
        var baseMatrix = new PdfMatrix(scale, 0, 0, -scale, -scale * x0, scale * (hPts + y0));
        _baseMatrix = baseMatrix;
        _state = new GraphicsState { Ctm = baseMatrix };

        try
        {
            _doc = page.Document;
            _resources.Push(page.Resources);
            Interpret(page.GetContentBytes());
        }
        catch
        {
            // Render whatever succeeded; partial output beats a hard failure.
        }

        var result = _raster.Rotated(page.Rotation);
        return result.ToImage();
    }

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

    private PdfDictionary? CurrentResources => _resources.Count > 0 ? _resources.Peek() : null;

    private PdfObject? LookupResource(string category, string name)
    {
        var res = _doc.Resolve(CurrentResources?.Get(category)) as PdfDictionary;
        return res is null ? null : _doc.Resolve(res.Get(name));
    }

    private PdfFont? ResolveFont(string name)
    {
        if (LookupResource("Font", name) is not PdfDictionary fontDict) return null;
        if (_fontCache.TryGetValue(fontDict, out var cached)) return cached;

        try
        {
            var font = PdfFont.Load(_doc, fontDict);
            _fontCache[fontDict] = font;
            return font;
        }
        catch
        {
            return null;
        }
    }

    private static double Num(List<PdfObject> ops, int index) =>
        index >= 0 && index < ops.Count && ops[index] is PdfNumber n ? n.Value : 0;

    private void Execute(string op, List<PdfObject> ops, PdfLexer lexer)
    {
        switch (op)
        {
            // Graphics state.
            case "q": _stack.Push(_state.Clone()); break;
            case "Q": if (_stack.Count > 0) _state = _stack.Pop(); break;
            case "cm":
                _state.Ctm = new PdfMatrix(Num(ops, 0), Num(ops, 1), Num(ops, 2), Num(ops, 3), Num(ops, 4), Num(ops, 5)).Multiply(_state.Ctm);
                break;
            case "w": _state.LineWidth = Num(ops, 0); break;
            case "gs":
                if (ops.OfType<PdfName>().LastOrDefault() is { } gsName) ApplyExtGState(gsName.Value);
                break;

            // Path construction.
            case "m": MoveTo(Num(ops, 0), Num(ops, 1)); break;
            case "l": LineTo(Num(ops, 0), Num(ops, 1)); break;
            case "c": CurveTo(Num(ops, 0), Num(ops, 1), Num(ops, 2), Num(ops, 3), Num(ops, 4), Num(ops, 5)); break;
            case "v": CurveTo(_cursorUser.X, _cursorUser.Y, Num(ops, 0), Num(ops, 1), Num(ops, 2), Num(ops, 3)); break;
            case "y": CurveTo(Num(ops, 0), Num(ops, 1), Num(ops, 2), Num(ops, 3), Num(ops, 2), Num(ops, 3)); break;
            case "re": Rectangle(Num(ops, 0), Num(ops, 1), Num(ops, 2), Num(ops, 3)); break;
            case "h": ClosePath(); break;

            // Path painting.
            case "f" or "F": FillCurrent(false); EndPath(); break;
            case "f*": FillCurrent(true); EndPath(); break;
            case "S": StrokeCurrent(); EndPath(); break;
            case "s": ClosePath(); StrokeCurrent(); EndPath(); break;
            case "B" or "B*": FillCurrent(op == "B*"); StrokeCurrent(); EndPath(); break;
            case "b" or "b*": ClosePath(); FillCurrent(op == "b*"); StrokeCurrent(); EndPath(); break;
            case "n": EndPath(); break;
            case "W": _pendingClip = true; _pendingClipEvenOdd = false; break;
            case "W*": _pendingClip = true; _pendingClipEvenOdd = true; break;

            // Color.
            case "g": SetFill(PdfColor.FromGray(Num(ops, 0)), 1); break;
            case "G": _state.StrokeColor = PdfColor.FromGray(Num(ops, 0)); _state.StrokeComponents = 1; break;
            case "rg": SetFill(PdfColor.FromRgb(Num(ops, 0), Num(ops, 1), Num(ops, 2)), 3); break;
            case "RG": _state.StrokeColor = PdfColor.FromRgb(Num(ops, 0), Num(ops, 1), Num(ops, 2)); _state.StrokeComponents = 3; break;
            case "k": SetFill(PdfColor.FromCmyk(Num(ops, 0), Num(ops, 1), Num(ops, 2), Num(ops, 3)), 4); break;
            case "K": _state.StrokeColor = PdfColor.FromCmyk(Num(ops, 0), Num(ops, 1), Num(ops, 2), Num(ops, 3)); _state.StrokeComponents = 4; break;
            case "cs": _state.FillComponents = ComponentsForSpace(ops); _fillPatternMode = IsPatternSpace(ops); ClearFillPattern(); break;
            case "CS": _state.StrokeComponents = ComponentsForSpace(ops); break;
            case "sc" or "scn": SetFillScn(ops); break;
            case "SC" or "SCN": _state.StrokeColor = ColorFromComponents(ops, _state.StrokeComponents); break;

            // Text.
            case "BT": _tm = PdfMatrix.Identity; _tlm = PdfMatrix.Identity; _penX = 0; break;
            case "ET": break;
            case "Td": TextMove(Num(ops, 0), Num(ops, 1)); break;
            case "TD": _state.Leading = -Num(ops, 1); TextMove(Num(ops, 0), Num(ops, 1)); break;
            case "Tm":
                _tlm = new PdfMatrix(Num(ops, 0), Num(ops, 1), Num(ops, 2), Num(ops, 3), Num(ops, 4), Num(ops, 5));
                _tm = _tlm; _penX = 0;
                break;
            case "T*": TextMove(0, -_state.Leading); break;
            case "Tc": _state.CharSpacing = Num(ops, 0); break;
            case "Tw": _state.WordSpacing = Num(ops, 0); break;
            case "Tz": _state.HorizontalScale = Num(ops, 0) / 100.0; break;
            case "TL": _state.Leading = Num(ops, 0); break;
            case "Ts": _state.TextRise = Num(ops, 0); break;
            case "Tf":
                _currentFont = ops.OfType<PdfName>().LastOrDefault() is { } fn ? ResolveFont(fn.Value) : null;
                _state.FontSize = Num(ops, ops.Count - 1);
                break;
            case "Tr": _state.TextRenderMode = (int)Num(ops, 0); break;
            case "Tj": if (LastString(ops) is { } s) ShowText(s.Bytes); break;
            case "TJ": if (LastArray(ops) is { } a) ShowTextArray(a); break;
            case "'": TextMove(0, -_state.Leading); if (LastString(ops) is { } s1) ShowText(s1.Bytes); break;
            case "\"":
                _state.WordSpacing = Num(ops, 0);
                _state.CharSpacing = Num(ops, 1);
                TextMove(0, -_state.Leading);
                if (LastString(ops) is { } s2) ShowText(s2.Bytes);
                break;

            // XObjects (images and forms).
            case "Do":
                if (ops.OfType<PdfName>().LastOrDefault() is { } xname) DoXObject(xname.Value);
                break;

            // Shading fill over the current clip region.
            case "sh":
                if (ops.OfType<PdfName>().LastOrDefault() is { } shName &&
                    LookupResource("Shading", shName.Value) is { } shObj)
                {
                    var shDict = shObj as PdfDictionary ?? (shObj as PdfStream)?.Dictionary;
                    if (shDict is not null) PaintShading(shDict, _state.Ctm, _state.Clip);
                }
                break;

            // Inline images: skip the binary payload up to EI.
            case "BI": SkipInlineImage(lexer); break;
        }
    }

    private static PdfString? LastString(List<PdfObject> ops) =>
        ops.Count > 0 && ops[^1] is PdfString s ? s : null;

    private static PdfArray? LastArray(List<PdfObject> ops) =>
        ops.Count > 0 && ops[^1] is PdfArray a ? a : null;

    private static int ComponentsForSpace(List<PdfObject> ops)
    {
        if (ops.Count > 0 && ops[^1] is PdfName name)
        {
            return name.Value switch
            {
                "DeviceGray" or "CalGray" or "G" => 1,
                "DeviceCMYK" or "CMYK" => 4,
                "DeviceRGB" or "CalRGB" or "Lab" or "RGB" => 3,
                _ => 3,
            };
        }
        return 3;
    }

    private static PdfColor ColorFromComponents(List<PdfObject> ops, int components)
    {
        var nums = ops.OfType<PdfNumber>().Select(n => n.Value).ToArray();
        return nums.Length switch
        {
            >= 4 => PdfColor.FromCmyk(nums[^4], nums[^3], nums[^2], nums[^1]),
            3 => PdfColor.FromRgb(nums[0], nums[1], nums[2]),
            1 => PdfColor.FromGray(nums[0]),
            _ => components == 4 && nums.Length >= 4
                ? PdfColor.FromCmyk(nums[0], nums[1], nums[2], nums[3])
                : PdfColor.Black,
        };
    }

    // --- Path helpers (operate in user space, store device-space points) ---

    private (double X, double Y) Device(double ux, double uy) => _state.Ctm.Apply(ux, uy);

    private void MoveTo(double x, double y)
    {
        _current = [Device(x, y)];
        _path.Add(_current);
        _start = (x, y);
        _cursorUser = (x, y);
    }

    private void LineTo(double x, double y)
    {
        _current ??= StartImplicit();
        _current.Add(Device(x, y));
        _cursorUser = (x, y);
    }

    private List<(double X, double Y)> StartImplicit()
    {
        var list = new List<(double, double)> { Device(_cursorUser.X, _cursorUser.Y) };
        _path.Add(list);
        return list;
    }

    private void CurveTo(double x1, double y1, double x2, double y2, double x3, double y3)
    {
        _current ??= StartImplicit();
        var (p0x, p0y) = _cursorUser;
        for (var i = 1; i <= BezierSegments; i++)
        {
            var t = (double)i / BezierSegments;
            var mt = 1 - t;
            var bx = mt * mt * mt * p0x + 3 * mt * mt * t * x1 + 3 * mt * t * t * x2 + t * t * t * x3;
            var by = mt * mt * mt * p0y + 3 * mt * mt * t * y1 + 3 * mt * t * t * y2 + t * t * t * y3;
            _current.Add(Device(bx, by));
        }
        _cursorUser = (x3, y3);
    }

    private void Rectangle(double x, double y, double w, double h)
    {
        // A closed subpath: repeat the start point so stroking draws all four edges.
        _current = [Device(x, y), Device(x + w, y), Device(x + w, y + h), Device(x, y + h), Device(x, y)];
        _path.Add(_current);
        _start = (x, y);
        _cursorUser = (x, y);
    }

    private void ClosePath()
    {
        // Append the subpath start so a stroked closed path draws its closing edge.
        if (_current is { Count: > 0 }) _current.Add(Device(_start.X, _start.Y));
        _current = null;
        _cursorUser = _start;
    }

    private void FillCurrent(bool evenOdd)
    {
        if (_state.FillShading is { } shading)
        {
            var mask = IntersectClip(_state.Clip, _raster.ComputeMask(_path, evenOdd));
            PaintShading(shading, _state.FillShadingMatrix.Multiply(_baseMatrix), mask);
            return;
        }

        SyncRaster();
        var color = _state.FillIsTilingPattern ? new PdfColor(128, 128, 128) : _state.FillColor;
        _raster.FillPolygons(_path, color, evenOdd);
    }

    private void SyncRaster(bool stroke = false)
    {
        _raster.Clip = _state.Clip;
        _raster.GlobalAlpha = stroke ? _state.StrokeAlpha : _state.FillAlpha;
        _raster.Mode = _state.Blend;
    }

    private void ApplyExtGState(string name)
    {
        if (LookupResource("ExtGState", name) is not PdfDictionary g) return;

        if (_doc.Resolve(g.Get("ca")) is PdfNumber ca) _state.FillAlpha = Math.Clamp(ca.Value, 0, 1);
        if (_doc.Resolve(g.Get("CA")) is PdfNumber bigCa) _state.StrokeAlpha = Math.Clamp(bigCa.Value, 0, 1);
        if (_doc.Resolve(g.Get("LW")) is PdfNumber lw) _state.LineWidth = lw.Value;

        var bm = _doc.Resolve(g.Get("BM"));
        var bmName = (bm as PdfName)?.Value
            ?? (bm is PdfArray { Count: > 0 } a ? (_doc.Resolve(a[0]) as PdfName)?.Value : null);
        if (bmName is not null) _state.Blend = ParseBlend(bmName);
    }

    private static BlendMode ParseBlend(string name) => name switch
    {
        "Multiply" => BlendMode.Multiply,
        "Screen" => BlendMode.Screen,
        "Overlay" => BlendMode.Overlay,
        "Darken" => BlendMode.Darken,
        "Lighten" => BlendMode.Lighten,
        "HardLight" => BlendMode.HardLight,
        "SoftLight" => BlendMode.SoftLight,
        "Difference" => BlendMode.Difference,
        "Exclusion" => BlendMode.Exclusion,
        _ => BlendMode.Normal,
    };

    private void SetFill(PdfColor color, int components)
    {
        _state.FillColor = color;
        _state.FillComponents = components;
        ClearFillPattern();
    }

    private void ClearFillPattern()
    {
        _state.FillShading = null;
        _state.FillIsTilingPattern = false;
    }

    private static bool IsPatternSpace(List<PdfObject> ops) =>
        ops.OfType<PdfName>().LastOrDefault()?.Value == "Pattern";

    private void SetFillScn(List<PdfObject> ops)
    {
        if (_fillPatternMode && ops.OfType<PdfName>().LastOrDefault() is { } name)
        {
            ResolveFillPattern(name.Value);
        }
        else
        {
            _state.FillColor = ColorFromComponents(ops, _state.FillComponents);
            ClearFillPattern();
        }
    }

    private void ResolveFillPattern(string name)
    {
        ClearFillPattern();
        var pattern = LookupResource("Pattern", name);
        var dict = pattern as PdfDictionary ?? (pattern as PdfStream)?.Dictionary;
        if (dict is null) return;

        var patternType = (_doc.Resolve(dict.Get("PatternType")) as PdfNumber)?.AsInt ?? 0;
        var matrix = ReadMatrix(dict.Get("Matrix"));

        if (patternType == 2 && _doc.Resolve(dict.Get("Shading")) is { } shObj)
        {
            var shDict = shObj as PdfDictionary ?? (shObj as PdfStream)?.Dictionary;
            if (shDict is not null)
            {
                _state.FillShading = shDict;
                _state.FillShadingMatrix = matrix;
            }
        }
        else
        {
            _state.FillIsTilingPattern = true;
        }
    }

    private PdfMatrix ReadMatrix(PdfObject? obj)
    {
        return _doc.Resolve(obj) is PdfArray { Count: >= 6 } m
            ? new PdfMatrix(AsD(m[0]), AsD(m[1]), AsD(m[2]), AsD(m[3]), AsD(m[4]), AsD(m[5]))
            : PdfMatrix.Identity;
    }

    private void PaintShading(PdfDictionary sh, PdfMatrix toDevice, byte[]? clip)
    {
        var type = (_doc.Resolve(sh.Get("ShadingType")) as PdfNumber)?.AsInt ?? 0;
        if (type is not (2 or 3)) return; // axial / radial only

        var func = PdfFunction.Parse(_doc, sh.Get("Function"));
        if (func is null) return;

        var coords = ReadDoubles(sh.Get("Coords"));
        if (coords is null || coords.Length < (type == 2 ? 4 : 6)) return;

        var cs = ColorSpaceInfo.Parse(_doc, _doc.Resolve(sh.Get("ColorSpace")));

        double t0 = 0, t1 = 1;
        if (_doc.Resolve(sh.Get("Domain")) is PdfArray { Count: >= 2 } dom)
        {
            t0 = AsD(dom[0]); t1 = AsD(dom[1]);
        }

        bool e0 = false, e1 = false;
        if (_doc.Resolve(sh.Get("Extend")) is PdfArray { Count: >= 2 } ext)
        {
            e0 = (_doc.Resolve(ext[0]) as PdfBoolean)?.Value ?? false;
            e1 = (_doc.Resolve(ext[1]) as PdfBoolean)?.Value ?? false;
        }

        // Precompute a 256-entry color ramp.
        var lut = new PdfColor[256];
        for (var i = 0; i < 256; i++)
        {
            var s = i / 255.0;
            lut[i] = cs.ToColor(func.Eval(t0 + s * (t1 - t0)));
        }

        var inv = toDevice.Invert();
        var (bx0, by0, bx1, by1) = ClipBounds(clip);
        _raster.Clip = clip;
        _raster.GlobalAlpha = _state.FillAlpha;
        _raster.Mode = _state.Blend;

        for (var py = by0; py <= by1; py++)
        {
            for (var px = bx0; px <= bx1; px++)
            {
                if (clip is not null && clip[py * _raster.Width + px] == 0) continue;

                var (sx, sy) = inv.Apply(px + 0.5, py + 0.5);
                double s;
                var ok = type == 2 ? Axial(coords, sx, sy, e0, e1, out s) : Radial(coords, sx, sy, e0, e1, out s);
                if (!ok) continue;

                var idx = (int)Math.Clamp(s * 255.0, 0, 255);
                _raster.BlendPixel(px, py, lut[idx], 1.0);
            }
        }
    }

    private static bool Axial(double[] c, double x, double y, bool e0, bool e1, out double s)
    {
        double x0 = c[0], y0 = c[1], x1 = c[2], y1 = c[3];
        double dx = x1 - x0, dy = y1 - y0;
        var den = dx * dx + dy * dy;
        if (den < 1e-9) { s = 0; return true; }

        var t = ((x - x0) * dx + (y - y0) * dy) / den;
        if (t < 0) { if (!e0) { s = 0; return false; } t = 0; }
        if (t > 1) { if (!e1) { s = 0; return false; } t = 1; }
        s = t;
        return true;
    }

    private static bool Radial(double[] c, double x, double y, bool e0, bool e1, out double s)
    {
        double x0 = c[0], y0 = c[1], r0 = c[2], x1 = c[3], y1 = c[4], r1 = c[5];
        double dx = x1 - x0, dy = y1 - y0, dr = r1 - r0;
        double pdx = x - x0, pdy = y - y0;

        var a = dx * dx + dy * dy - dr * dr;
        var beta = pdx * dx + pdy * dy + r0 * dr;
        var cc = pdx * pdx + pdy * pdy - r0 * r0;

        double s1, s2;
        if (Math.Abs(a) < 1e-9)
        {
            if (Math.Abs(beta) < 1e-12) { s = 0; return false; }
            s1 = s2 = cc / (2 * beta);
        }
        else
        {
            var disc = beta * beta - a * cc;
            if (disc < 0) { s = 0; return false; }
            var sq = Math.Sqrt(disc);
            s1 = (beta + sq) / a;
            s2 = (beta - sq) / a;
        }

        foreach (var cand in new[] { Math.Max(s1, s2), Math.Min(s1, s2) })
        {
            if (r0 + cand * dr < 0) continue;
            var cl = cand;
            if (cl < 0) { if (!e0) continue; cl = 0; }
            if (cl > 1) { if (!e1) continue; cl = 1; }
            s = cl;
            return true;
        }

        s = 0;
        return false;
    }

    private (int X0, int Y0, int X1, int Y1) ClipBounds(byte[]? clip)
    {
        if (clip is null) return (0, 0, _raster.Width - 1, _raster.Height - 1);

        int minX = _raster.Width, minY = _raster.Height, maxX = -1, maxY = -1;
        for (var y = 0; y < _raster.Height; y++)
        {
            var row = y * _raster.Width;
            for (var x = 0; x < _raster.Width; x++)
            {
                if (clip[row + x] == 0) continue;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
        }
        return maxX < 0 ? (0, 0, -1, -1) : (minX, minY, maxX, maxY);
    }

    private double[]? ReadDoubles(PdfObject? obj)
    {
        if (_doc.Resolve(obj) is not PdfArray arr) return null;
        var result = new double[arr.Count];
        for (var i = 0; i < arr.Count; i++) result[i] = AsD(arr[i]);
        return result;
    }

    private void StrokeCurrent()
    {
        SyncRaster(stroke: true);
        var width = Math.Max(0.7, _state.LineWidth * _state.Ctm.ScaleFactor);
        foreach (var sub in _path)
        {
            _raster.StrokePolyline(sub, width, _state.StrokeColor);
        }
    }

    private void EndPath()
    {
        if (_pendingClip && _path.Count > 0)
        {
            var mask = _raster.ComputeMask(_path, _pendingClipEvenOdd);
            _state.Clip = IntersectClip(_state.Clip, mask);
        }
        _pendingClip = false;
        _path.Clear();
        _current = null;
    }

    private static byte[] IntersectClip(byte[]? existing, byte[] mask)
    {
        if (existing is null) return mask;
        for (var i = 0; i < mask.Length; i++)
        {
            mask[i] = (byte)(existing[i] * mask[i] / 255);
        }
        return mask;
    }

    // --- Text helpers ---

    private void TextMove(double tx, double ty)
    {
        _tlm = PdfMatrix.Translate(tx, ty).Multiply(_tlm);
        _tm = _tlm;
        _penX = 0;
    }

    private void ShowTextArray(PdfArray array)
    {
        foreach (var item in array.Items)
        {
            switch (item)
            {
                case PdfString s:
                    ShowText(s.Bytes);
                    break;
                case PdfNumber n:
                    _penX -= n.Value / 1000.0 * _state.FontSize * _state.HorizontalScale;
                    break;
            }
        }
    }

    private void ShowText(byte[] bytes)
    {
        SyncRaster();
        var invisible = _state.TextRenderMode == 3;
        var fs = _state.FontSize;
        var th = _state.HorizontalScale;
        var combined = _tm.Multiply(_state.Ctm);
        var font = _currentFont;

        if (font is not null)
        {
            foreach (var code in font.Decode(bytes))
            {
                if (!invisible) DrawGlyph(font, code, fs, th, combined);
                var w = font.GetWidth(code) / 1000.0 * fs + _state.CharSpacing;
                if (font.IsSpace(code)) w += _state.WordSpacing;
                _penX += w * th;
            }
            return;
        }

        // No font resource: use the built-in vector font.
        foreach (var b in bytes)
        {
            if (!invisible) DrawVectorGlyph((char)b, fs, th, combined);
            AdvanceChar((char)b, fs, th);
        }
    }

    private void DrawGlyph(PdfFont font, int code, double fs, double th, PdfMatrix combined)
    {
        var contours = font.GetContours(code);
        if (contours is null || contours.Count == 0)
        {
            // No outline (e.g., standard-14 font): approximate with the vector glyph.
            if (!font.TwoByte) DrawVectorGlyph((char)code, fs, th, combined);
            return;
        }

        var polygons = new List<List<(double X, double Y)>>(contours.Count);
        foreach (var contour in contours)
        {
            var poly = new List<(double X, double Y)>(contour.Count);
            foreach (var (gx, gy) in contour)
            {
                var textX = _penX + gx / 1000.0 * fs * th;
                var textY = gy / 1000.0 * fs + _state.TextRise;
                poly.Add(combined.Apply(textX, textY));
            }
            polygons.Add(poly);
        }

        // TrueType outlines use nonzero winding.
        _raster.FillPolygons(polygons, _state.FillColor, evenOdd: false);
    }

    private void DrawVectorGlyph(char ch, double fs, double th, PdfMatrix combined)
    {
        // Glyph polylines are in glyph space (1/1000 em), placed exactly like real outlines.
        var glyphWidth = Math.Max(0.5, fs * combined.ScaleFactor * 0.055);
        foreach (var poly in StrokeFont.Get(ch))
        {
            var pts = new List<(double X, double Y)>(poly.Length / 2);
            for (var i = 0; i < poly.Length; i += 2)
            {
                var ex = poly[i] / 1000.0;
                var ey = poly[i + 1] / 1000.0;
                var textX = _penX + ex * fs * th;
                var textY = ey * fs + _state.TextRise;
                pts.Add(combined.Apply(textX, textY));
            }
            _raster.StrokePolyline(pts, glyphWidth, _state.FillColor);
        }
    }

    private void AdvanceChar(char ch, double fs, double th)
    {
        var advance = StrokeFont.Width(ch) / 1000.0 * fs + _state.CharSpacing;
        if (ch == ' ') advance += _state.WordSpacing;
        _penX += advance * th;
    }

    private void DoXObject(string name)
    {
        if (LookupResource("XObject", name) is not PdfStream stream) return;
        var subtype = (_doc.Resolve(stream.Dictionary.Get("Subtype")) as PdfName)?.Value;

        if (subtype == "Form")
        {
            RunForm(stream);
        }
        else
        {
            DrawImage(stream);
        }
    }

    private void RunForm(PdfStream form)
    {
        if (_formDepth >= 12) return;
        _formDepth++;
        _stack.Push(_state.Clone());

        if (_doc.Resolve(form.Dictionary.Get("Matrix")) is PdfArray { Count: >= 6 } m)
        {
            _state.Ctm = new PdfMatrix(
                AsD(m[0]), AsD(m[1]), AsD(m[2]), AsD(m[3]), AsD(m[4]), AsD(m[5])).Multiply(_state.Ctm);
        }

        var formResources = _doc.Resolve(form.Dictionary.Get("Resources")) as PdfDictionary;
        _resources.Push(formResources ?? CurrentResources);
        try
        {
            Interpret(PdfFilters.Decode(form, _doc));
        }
        finally
        {
            _resources.Pop();
            if (_stack.Count > 0) _state = _stack.Pop();
            _formDepth--;
        }
    }

    private double AsD(PdfObject o) => _doc.Resolve(o) is PdfNumber n ? n.Value : 0;

    private void DrawImage(PdfStream stream)
    {
        var image = PdfImage.Decode(_doc, stream);
        if (image is null) return;

        SyncRaster();

        // The image fills the unit square in user space, transformed by the CTM.
        var ctm = _state.Ctm;
        var inv = ctm.Invert();

        // Device-space bounds of the unit square.
        Span<(double X, double Y)> corners =
        [
            ctm.Apply(0, 0), ctm.Apply(1, 0), ctm.Apply(1, 1), ctm.Apply(0, 1)
        ];
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var (cx, cy) in corners)
        {
            minX = Math.Min(minX, cx); maxX = Math.Max(maxX, cx);
            minY = Math.Min(minY, cy); maxY = Math.Max(maxY, cy);
        }

        var x0 = Math.Max(0, (int)Math.Floor(minX));
        var x1 = Math.Min(_raster.Width - 1, (int)Math.Ceiling(maxX));
        var y0 = Math.Max(0, (int)Math.Floor(minY));
        var y1 = Math.Min(_raster.Height - 1, (int)Math.Ceiling(maxY));

        for (var py = y0; py <= y1; py++)
        {
            for (var px = x0; px <= x1; px++)
            {
                var (u, v) = inv.Apply(px + 0.5, py + 0.5);
                if (u < 0 || u >= 1 || v < 0 || v >= 1) continue;

                var ix = Math.Clamp((int)(u * image.Width), 0, image.Width - 1);
                var iy = Math.Clamp((int)((1 - v) * image.Height), 0, image.Height - 1);

                if (image.IsStencil)
                {
                    if (image.StencilPaint![iy * image.Width + ix])
                    {
                        _raster.BlendPixel(px, py, _state.FillColor, 1.0);
                    }
                }
                else
                {
                    var o = (iy * image.Width + ix) * 4;
                    var rgba = image.Rgba!;
                    var color = new PdfColor(rgba[o], rgba[o + 1], rgba[o + 2]);
                    _raster.BlendPixel(px, py, color, rgba[o + 3] / 255.0);
                }
            }
        }
    }

    private static void SkipInlineImage(PdfLexer lexer)
    {
        var data = lexer.Data;
        var idx = PdfParser.IndexOf(data, "EI", lexer.Position);
        lexer.Position = idx < 0 ? data.Length : idx + 2;
    }
}
