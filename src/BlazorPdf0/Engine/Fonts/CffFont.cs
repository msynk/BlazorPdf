namespace BlazorPdf.Engine.Fonts;

/// <summary>
/// A Compact Font Format (CFF / Type1C / CIDFontType0C) parser with a Type2 charstring
/// interpreter that produces glyph outlines. Handles bare CFF and OpenType-CFF
/// (<c>OTTO</c>) wrappers, charsets, encodings, global/local subrs, and CID fonts
/// (FDArray/FDSelect). Pure C#, no external dependency.
/// </summary>
internal sealed class CffFont : IGlyphSource
{
    private byte[] _data = [];
    private byte[][] _charStrings = [];
    private byte[][] _globalSubrs = [];
    private byte[][] _localSubrs = [];          // non-CID
    private byte[][][] _fdLocalSubrs = [];      // CID: per-FD local subrs
    private int[] _fdSelect = [];               // CID: gid -> fd index
    private int _globalBias, _localBias;
    private double _fontScale = 1.0;            // design units -> 1/1000 em
    private bool _isCid;

    private int[] _charsetGidToSid = [];        // gid -> SID (or CID for CID fonts)
    private readonly Dictionary<int, int> _cidToGid = [];
    private readonly Dictionary<int, int> _codeToGid = []; // simple Encoding

    public int GlyphCount => _charStrings.Length;
    public bool IsCid => _isCid;

    public static CffFont? Parse(byte[] fontBytes)
    {
        try
        {
            var cff = ExtractCff(fontBytes);
            if (cff is null) return null;
            var font = new CffFont { _data = cff };
            return font.ParseInternal() ? font : null;
        }
        catch
        {
            return null;
        }
    }

    private static byte[]? ExtractCff(byte[] data)
    {
        if (data.Length < 4) return null;

        // OpenType (OTTO) wrapper: find the 'CFF ' table.
        if (data[0] == 'O' && data[1] == 'T' && data[2] == 'T' && data[3] == 'O')
        {
            var r = new ByteReader(data, 4);
            var numTables = r.U16();
            r.Position += 6;
            for (var i = 0; i < numTables; i++)
            {
                var tag = r.Tag();
                r.U32();
                var offset = (int)r.U32();
                var length = (int)r.U32();
                if (tag == "CFF ")
                {
                    var slice = new byte[length];
                    Array.Copy(data, offset, slice, 0, length);
                    return slice;
                }
            }
            return null;
        }

        return data; // assume bare CFF
    }

    private bool ParseInternal()
    {
        var hdrSize = _data[2];
        int pos = hdrSize;

        pos = ReadIndex(pos, out _);              // Name INDEX
        pos = ReadIndex(pos, out var topDicts);   // Top DICT INDEX
        pos = ReadIndex(pos, out var strings);    // String INDEX (unused names here)
        _ = strings;
        pos = ReadIndex(pos, out var gsubrs);     // Global Subr INDEX
        _globalSubrs = gsubrs;
        _globalBias = Bias(gsubrs.Length);

        if (topDicts.Length == 0) return false;
        var top = ParseDict(topDicts[0]);

        if (top.TryGetValue(1207, out var fm) && fm.Count >= 1 && fm[0] != 0)
        {
            _fontScale = fm[0] * 1000.0;
        }

        if (!top.TryGetValue(17, out var csOff)) return false;
        ReadIndex((int)csOff[0], out _charStrings);
        var nGlyphs = _charStrings.Length;

        _isCid = top.ContainsKey(1230); // ROS

        ParseCharset(top, nGlyphs);

        if (_isCid)
        {
            ParseCidPrivate(top, nGlyphs);
        }
        else
        {
            ParseSimplePrivate(top);
            ParseEncoding(top, nGlyphs);
        }

        return nGlyphs > 0;
    }

    private void ParseSimplePrivate(Dictionary<int, List<double>> top)
    {
        if (top.TryGetValue(18, out var priv) && priv.Count >= 2)
        {
            var size = (int)priv[0];
            var offset = (int)priv[1];
            var dict = ParseDict(Slice(offset, size));
            if (dict.TryGetValue(19, out var subrsRel))
            {
                ReadIndex(offset + (int)subrsRel[0], out _localSubrs);
            }
        }
        _localBias = Bias(_localSubrs.Length);
    }

    private void ParseCidPrivate(Dictionary<int, List<double>> top, int nGlyphs)
    {
        // FDArray (12 36) and FDSelect (12 37).
        if (top.TryGetValue(1236, out var fdArrOff))
        {
            ReadIndex((int)fdArrOff[0], out var fdDicts);
            _fdLocalSubrs = new byte[fdDicts.Length][][];
            for (var i = 0; i < fdDicts.Length; i++)
            {
                var fd = ParseDict(fdDicts[i]);
                byte[][] local = [];
                if (fd.TryGetValue(18, out var priv) && priv.Count >= 2)
                {
                    var size = (int)priv[0];
                    var offset = (int)priv[1];
                    var pdict = ParseDict(Slice(offset, size));
                    if (pdict.TryGetValue(19, out var subrsRel))
                    {
                        ReadIndex(offset + (int)subrsRel[0], out local);
                    }
                }
                _fdLocalSubrs[i] = local;
            }
        }

        _fdSelect = new int[nGlyphs];
        if (top.TryGetValue(1237, out var fdSelOff))
        {
            ParseFdSelect((int)fdSelOff[0], nGlyphs);
        }
    }

    private void ParseFdSelect(int offset, int nGlyphs)
    {
        var r = new ByteReader(_data, offset);
        var format = r.U8();
        if (format == 0)
        {
            for (var i = 0; i < nGlyphs; i++) _fdSelect[i] = r.U8();
        }
        else if (format == 3)
        {
            var nRanges = r.U16();
            var first = r.U16();
            for (var i = 0; i < nRanges; i++)
            {
                var fd = r.U8();
                var next = r.U16();
                for (var g = first; g < next && g < nGlyphs; g++) _fdSelect[g] = fd;
                first = next;
            }
        }
    }

    private void ParseCharset(Dictionary<int, List<double>> top, int nGlyphs)
    {
        _charsetGidToSid = new int[nGlyphs];
        var offset = top.TryGetValue(15, out var cs) ? (int)cs[0] : 0;

        if (offset is 0 or 1 or 2)
        {
            // Predefined charsets: approximate as identity SID == gid.
            for (var g = 0; g < nGlyphs; g++) _charsetGidToSid[g] = g;
        }
        else
        {
            var r = new ByteReader(_data, offset);
            var format = r.U8();
            _charsetGidToSid[0] = 0; // .notdef
            var gid = 1;
            switch (format)
            {
                case 0:
                    while (gid < nGlyphs) _charsetGidToSid[gid++] = r.U16();
                    break;
                case 1:
                    while (gid < nGlyphs)
                    {
                        var first = r.U16();
                        var nLeft = r.U8();
                        for (var i = 0; i <= nLeft && gid < nGlyphs; i++) _charsetGidToSid[gid++] = first + i;
                    }
                    break;
                case 2:
                    while (gid < nGlyphs)
                    {
                        var first = r.U16();
                        var nLeft = r.U16();
                        for (var i = 0; i <= nLeft && gid < nGlyphs; i++) _charsetGidToSid[gid++] = first + i;
                    }
                    break;
            }
        }

        for (var g = 0; g < nGlyphs; g++) _cidToGid[_charsetGidToSid[g]] = g;
    }

    private void ParseEncoding(Dictionary<int, List<double>> top, int nGlyphs)
    {
        var offset = top.TryGetValue(16, out var enc) ? (int)enc[0] : 0;
        if (offset is 0 or 1)
        {
            // Standard/Expert encoding: not reconstructed here (callers fall back).
            return;
        }

        var r = new ByteReader(_data, offset);
        var format = r.U8();
        var baseFormat = format & 0x7F;

        if (baseFormat == 0)
        {
            var nCodes = r.U8();
            for (var i = 1; i <= nCodes; i++)
            {
                var code = r.U8();
                _codeToGid[code] = i;
            }
        }
        else if (baseFormat == 1)
        {
            var nRanges = r.U8();
            var gid = 1;
            for (var i = 0; i < nRanges; i++)
            {
                var first = r.U8();
                var nLeft = r.U8();
                for (var c = first; c <= first + nLeft; c++) _codeToGid[c] = gid++;
            }
        }
    }

    public int GidForCid(int cid) => _cidToGid.TryGetValue(cid, out var g) ? g : (cid < GlyphCount ? cid : 0);

    public int GidForCode(int code) => _codeToGid.TryGetValue(code, out var g) ? g : 0;

    public double GetAdvance1000(int gid) => -1; // PDF supplies widths

    public List<List<(double X, double Y)>>? GetGlyphContours(int gid)
    {
        if (gid < 0 || gid >= _charStrings.Length) return null;

        var local = _localSubrs;
        if (_isCid && _fdSelect.Length > gid && _fdLocalSubrs.Length > 0)
        {
            var fd = _fdSelect[gid];
            if (fd < _fdLocalSubrs.Length) local = _fdLocalSubrs[fd];
        }

        var interp = new Type2Interpreter(_globalSubrs, local, _globalBias, Bias(local.Length), _fontScale);
        var contours = interp.Run(_charStrings[gid]);
        return contours.Count == 0 ? null : contours;
    }

    // --- INDEX / DICT / slicing helpers ---

    private int ReadIndex(int pos, out byte[][] objects)
    {
        var r = new ByteReader(_data, pos);
        var count = r.U16();
        if (count == 0)
        {
            objects = [];
            return pos + 2;
        }

        var offSize = r.U8();
        var offsets = new int[count + 1];
        for (var i = 0; i <= count; i++) offsets[i] = ReadOffset(r, offSize);

        var dataBase = r.Position - 1; // offsets are 1-based from here
        objects = new byte[count][];
        for (var i = 0; i < count; i++)
        {
            var start = dataBase + offsets[i];
            var end = dataBase + offsets[i + 1];
            var obj = new byte[Math.Max(0, end - start)];
            if (obj.Length > 0) Array.Copy(_data, start, obj, 0, obj.Length);
            objects[i] = obj;
        }

        return dataBase + offsets[count];
    }

    private static int ReadOffset(ByteReader r, int offSize)
    {
        var v = 0;
        for (var i = 0; i < offSize; i++) v = (v << 8) | r.U8();
        return v;
    }

    private byte[] Slice(int offset, int length)
    {
        var s = new byte[Math.Max(0, Math.Min(length, _data.Length - offset))];
        if (s.Length > 0) Array.Copy(_data, offset, s, 0, s.Length);
        return s;
    }

    private static Dictionary<int, List<double>> ParseDict(byte[] data)
    {
        var dict = new Dictionary<int, List<double>>();
        var operands = new List<double>();
        var i = 0;
        while (i < data.Length)
        {
            var b = data[i];
            if (b <= 21)
            {
                int op = b;
                i++;
                if (op == 12) { op = 1200 + data[i]; i++; }
                dict[op] = [.. operands];
                operands.Clear();
            }
            else if (b == 28) { operands.Add((short)((data[i + 1] << 8) | data[i + 2])); i += 3; }
            else if (b == 29) { operands.Add((data[i + 1] << 24) | (data[i + 2] << 16) | (data[i + 3] << 8) | data[i + 4]); i += 5; }
            else if (b == 30) { operands.Add(ReadReal(data, ref i)); }
            else if (b is >= 32 and <= 246) { operands.Add(b - 139); i++; }
            else if (b is >= 247 and <= 250) { operands.Add((b - 247) * 256 + data[i + 1] + 108); i += 2; }
            else if (b is >= 251 and <= 254) { operands.Add(-(b - 251) * 256 - data[i + 1] - 108); i += 2; }
            else { i++; }
        }
        return dict;
    }

    private static double ReadReal(byte[] data, ref int i)
    {
        i++; // skip 30
        var sb = new System.Text.StringBuilder();
        var done = false;
        while (i < data.Length && !done)
        {
            var b = data[i++];
            foreach (var nib in new[] { b >> 4, b & 0x0F })
            {
                switch (nib)
                {
                    case <= 9: sb.Append((char)('0' + nib)); break;
                    case 0xa: sb.Append('.'); break;
                    case 0xb: sb.Append('E'); break;
                    case 0xc: sb.Append("E-"); break;
                    case 0xe: sb.Append('-'); break;
                    case 0xf: done = true; break;
                }
                if (done) break;
            }
        }
        return double.TryParse(sb.ToString(), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;
    }

    private static int Bias(int n) => n < 1240 ? 107 : n < 33900 ? 1131 : 32768;
}
