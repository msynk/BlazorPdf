namespace BlazorPdf.Engine.Fonts;

/// <summary>
/// Executes a Type2 charstring, producing flattened glyph contours in 1/1000 em.
/// Implements the path, subroutine, hint and width operators; ignores hint data but
/// correctly skips hintmask bytes. Flex and seac-style endchar accents are not built.
/// </summary>
internal sealed class Type2Interpreter
{
    private const int QuadSegments = 8;

    private readonly byte[][] _gsubrs;
    private readonly byte[][] _lsubrs;
    private readonly int _gbias;
    private readonly int _lbias;
    private readonly double _scale;

    private readonly List<double> _stack = new(48);
    private readonly List<List<(double X, double Y)>> _contours = [];
    private List<(double X, double Y)>? _current;

    private double _x, _y;
    private int _nStems;
    private bool _haveWidth;
    private bool _open;

    public Type2Interpreter(byte[][] gsubrs, byte[][] lsubrs, int gbias, int lbias, double scale)
    {
        _gsubrs = gsubrs;
        _lsubrs = lsubrs;
        _gbias = gbias;
        _lbias = lbias;
        _scale = scale;
    }

    public List<List<(double X, double Y)>> Run(byte[] charString)
    {
        Execute(charString, 0);
        CloseContour();
        return _contours;
    }

    private bool Execute(byte[] cs, int depth)
    {
        if (depth > 10) return true;
        var i = 0;
        while (i < cs.Length)
        {
            int b = cs[i++];
            if (b >= 32 || b == 28)
            {
                // Operand.
                double v;
                if (b == 28) { v = (short)((cs[i] << 8) | cs[i + 1]); i += 2; }
                else if (b < 247) v = b - 139;
                else if (b < 251) { v = (b - 247) * 256 + cs[i++] + 108; }
                else if (b < 255) { v = -(b - 251) * 256 - cs[i++] - 108; }
                else { v = ((cs[i] << 24) | (cs[i + 1] << 16) | (cs[i + 2] << 8) | cs[i + 3]) / 65536.0; i += 4; }
                _stack.Add(v);
                continue;
            }

            switch (b)
            {
                case 1: case 3: case 18: case 23: // h/v stem (hm)
                    CountStems();
                    break;

                case 19: case 20: // hintmask / cntrmask
                    CountStems();
                    i += (_nStems + 7) / 8;
                    break;

                case 21: // rmoveto
                    TakeWidth(2);
                    MoveTo(_x + _stack[0], _y + _stack[1]);
                    _stack.Clear();
                    break;
                case 22: // hmoveto
                    TakeWidth(1);
                    MoveTo(_x + _stack[0], _y);
                    _stack.Clear();
                    break;
                case 4: // vmoveto
                    TakeWidth(1);
                    MoveTo(_x, _y + _stack[0]);
                    _stack.Clear();
                    break;

                case 5: // rlineto
                    for (var k = 0; k + 1 < _stack.Count; k += 2) LineTo(_x + _stack[k], _y + _stack[k + 1]);
                    _stack.Clear();
                    break;
                case 6: // hlineto
                    AlternatingLines(horizontalFirst: true);
                    break;
                case 7: // vlineto
                    AlternatingLines(horizontalFirst: false);
                    break;

                case 8: // rrcurveto
                    for (var k = 0; k + 5 < _stack.Count; k += 6) RCurve(k);
                    _stack.Clear();
                    break;
                case 24: // rcurveline
                    {
                        var k = 0;
                        for (; k + 5 < _stack.Count - 2; k += 6) RCurve(k);
                        if (k + 1 < _stack.Count) LineTo(_x + _stack[k], _y + _stack[k + 1]);
                        _stack.Clear();
                        break;
                    }
                case 25: // rlinecurve
                    {
                        var k = 0;
                        for (; k + 1 < _stack.Count - 6; k += 2) LineTo(_x + _stack[k], _y + _stack[k + 1]);
                        if (k + 5 < _stack.Count) RCurve(k);
                        _stack.Clear();
                        break;
                    }
                case 26: // vvcurveto
                    VvCurve();
                    break;
                case 27: // hhcurveto
                    HhCurve();
                    break;
                case 30: // vhcurveto
                    AlternatingCurves(startHorizontal: false);
                    break;
                case 31: // hvcurveto
                    AlternatingCurves(startHorizontal: true);
                    break;

                case 10: // callsubr
                    {
                        var idx = (int)_stack[^1] + _lbias;
                        _stack.RemoveAt(_stack.Count - 1);
                        if (idx >= 0 && idx < _lsubrs.Length && Execute(_lsubrs[idx], depth + 1)) return true;
                        break;
                    }
                case 29: // callgsubr
                    {
                        var idx = (int)_stack[^1] + _gbias;
                        _stack.RemoveAt(_stack.Count - 1);
                        if (idx >= 0 && idx < _gsubrs.Length && Execute(_gsubrs[idx], depth + 1)) return true;
                        break;
                    }
                case 11: // return
                    return false;
                case 14: // endchar
                    TakeWidth(0);
                    CloseContour();
                    return true;

                case 12: // escape (two-byte ops: flex etc.)
                    i = HandleEscape(cs, i);
                    break;

                default:
                    _stack.Clear();
                    break;
            }
        }
        return false;
    }

    private int HandleEscape(byte[] cs, int i)
    {
        var op2 = cs[i++];
        switch (op2)
        {
            case 34: // hflex
                if (_stack.Count >= 7)
                {
                    var y0 = _y;
                    Curve(_stack[0], 0, _stack[1], _stack[2], _stack[3], 0);
                    Curve(_stack[4], 0, _stack[5], y0 - _y, _stack[6], 0);
                }
                break;
            case 36: // hflex1
                if (_stack.Count >= 9)
                {
                    var y0 = _y;
                    Curve(_stack[0], _stack[1], _stack[2], _stack[3], _stack[4], 0);
                    Curve(_stack[5], 0, _stack[6], _stack[7], _stack[8], y0 - _y);
                }
                break;
            case 35: // flex
                if (_stack.Count >= 12)
                {
                    Curve(_stack[0], _stack[1], _stack[2], _stack[3], _stack[4], _stack[5]);
                    Curve(_stack[6], _stack[7], _stack[8], _stack[9], _stack[10], _stack[11]);
                }
                break;
            case 37: // flex1
                if (_stack.Count >= 11)
                {
                    var x0 = _x; var y0 = _y;
                    Curve(_stack[0], _stack[1], _stack[2], _stack[3], _stack[4], _stack[5]);
                    var dx = _stack[6]; var dy = _stack[7];
                    var dx2 = _stack[8]; var dy2 = _stack[9]; var d6 = _stack[10];
                    var sumX = _stack[0] + _stack[2] + _stack[4] + dx + dx2;
                    var sumY = _stack[1] + _stack[3] + _stack[5] + dy + dy2;
                    if (Math.Abs(sumX) > Math.Abs(sumY)) Curve(dx, dy, dx2, dy2, d6, y0 - (_y + dy + dy2));
                    else Curve(dx, dy, dx2, dy2, x0 - (_x + dx + dx2), d6);
                }
                break;
        }
        _stack.Clear();
        return i;
    }

    private void CountStems()
    {
        if (!_haveWidth && (_stack.Count % 2) == 1)
        {
            _haveWidth = true; // first element is the width
        }
        _nStems += _stack.Count / 2;
        _stack.Clear();
    }

    private void TakeWidth(int expectedArgs)
    {
        if (_haveWidth) return;
        if ((expectedArgs == 0 && _stack.Count > 0) || _stack.Count > expectedArgs)
        {
            _stack.RemoveAt(0);
        }
        _haveWidth = true;
    }

    private void MoveTo(double x, double y)
    {
        CloseContour();
        _x = x; _y = y;
        _current = [Pt(x, y)];
        _open = true;
    }

    private void LineTo(double x, double y)
    {
        _current ??= [Pt(_x, _y)];
        _x = x; _y = y;
        _current.Add(Pt(x, y));
    }

    private void RCurve(int k) =>
        Curve(_stack[k], _stack[k + 1], _stack[k + 2], _stack[k + 3], _stack[k + 4], _stack[k + 5]);

    private void Curve(double dx1, double dy1, double dx2, double dy2, double dx3, double dy3)
    {
        var x0 = _x; var y0 = _y;
        var x1 = x0 + dx1; var y1 = y0 + dy1;
        var x2 = x1 + dx2; var y2 = y1 + dy2;
        var x3 = x2 + dx3; var y3 = y2 + dy3;
        _current ??= [Pt(x0, y0)];
        for (var s = 1; s <= QuadSegments; s++)
        {
            var t = (double)s / QuadSegments;
            var mt = 1 - t;
            var bx = mt * mt * mt * x0 + 3 * mt * mt * t * x1 + 3 * mt * t * t * x2 + t * t * t * x3;
            var by = mt * mt * mt * y0 + 3 * mt * mt * t * y1 + 3 * mt * t * t * y2 + t * t * t * y3;
            _current.Add(Pt(bx, by));
        }
        _x = x3; _y = y3;
    }

    private void AlternatingLines(bool horizontalFirst)
    {
        var horizontal = horizontalFirst;
        for (var k = 0; k < _stack.Count; k++)
        {
            if (horizontal) LineTo(_x + _stack[k], _y);
            else LineTo(_x, _y + _stack[k]);
            horizontal = !horizontal;
        }
        _stack.Clear();
    }

    private void HhCurve()
    {
        var k = 0;
        double dy1 = 0;
        if ((_stack.Count % 4) == 1) { dy1 = _stack[0]; k = 1; }
        for (; k + 3 < _stack.Count; k += 4)
        {
            Curve(_stack[k], dy1, _stack[k + 1], _stack[k + 2], _stack[k + 3], 0);
            dy1 = 0;
        }
        _stack.Clear();
    }

    private void VvCurve()
    {
        var k = 0;
        double dx1 = 0;
        if ((_stack.Count % 4) == 1) { dx1 = _stack[0]; k = 1; }
        for (; k + 3 < _stack.Count; k += 4)
        {
            Curve(dx1, _stack[k], _stack[k + 1], _stack[k + 2], 0, _stack[k + 3]);
            dx1 = 0;
        }
        _stack.Clear();
    }

    private void AlternatingCurves(bool startHorizontal)
    {
        var horizontal = startHorizontal;
        var k = 0;
        var n = _stack.Count;
        while (n - k >= 4)
        {
            var last = (n - k) == 5;
            var df = last ? _stack[k + 4] : 0;
            if (horizontal)
            {
                Curve(_stack[k], 0, _stack[k + 1], _stack[k + 2], df, _stack[k + 3]);
            }
            else
            {
                Curve(0, _stack[k], _stack[k + 1], _stack[k + 2], _stack[k + 3], df);
            }
            k += 4;
            horizontal = !horizontal;
        }
        _stack.Clear();
    }

    private void CloseContour()
    {
        if (_open && _current is { Count: >= 2 })
        {
            _contours.Add(_current);
        }
        _current = null;
        _open = false;
    }

    private (double X, double Y) Pt(double x, double y) => (x * _scale, y * _scale);
}
