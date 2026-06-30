namespace BlazorPdf.Engine.Fonts;

/// <summary>
/// A minimal TrueType (sfnt) font parser: reads <c>head</c>, <c>maxp</c>, <c>hhea</c>,
/// <c>hmtx</c>, <c>loca</c>, <c>glyf</c> and <c>cmap</c> to expose glyph advance widths
/// and flattened glyph outlines. Pure C#, no external dependency.
/// </summary>
internal sealed class TrueTypeFont : IGlyphSource
{
    private const int QuadSegments = 8;

    private byte[] _data = [];
    private int _glyfOffset;
    private int[] _loca = [];
    private int[] _advances = [];

    /// <summary>Units per em from the <c>head</c> table.</summary>
    public int UnitsPerEm { get; private set; } = 1000;

    /// <summary>Number of glyphs.</summary>
    public int GlyphCount { get; private set; }

    /// <summary>Best available character-code to glyph-id map.</summary>
    public Dictionary<int, int> Cmap { get; } = [];

    /// <summary>True when the chosen cmap is a (3,0) symbol table.</summary>
    public bool IsSymbol { get; private set; }

    public static TrueTypeFont? Parse(byte[] data)
    {
        try
        {
            var font = new TrueTypeFont { _data = data };
            return font.ParseInternal() ? font : null;
        }
        catch
        {
            return null;
        }
    }

    private bool ParseInternal()
    {
        var r = new ByteReader(_data);
        var sfnt = r.U32();
        // 0x00010000 (TrueType), 'true', or 'OTTO' (CFF outlines - unsupported here).
        if (sfnt == 0x4F54544F) return false; // OTTO/CFF
        var numTables = r.U16();
        r.Position += 6;

        var tables = new Dictionary<string, (int Offset, int Length)>(StringComparer.Ordinal);
        for (var i = 0; i < numTables; i++)
        {
            var tag = r.Tag();
            r.U32(); // checksum
            var offset = (int)r.U32();
            var length = (int)r.U32();
            tables[tag] = (offset, length);
        }

        if (!tables.TryGetValue("head", out var head) ||
            !tables.TryGetValue("maxp", out var maxp) ||
            !tables.TryGetValue("loca", out var locaT) ||
            !tables.TryGetValue("glyf", out var glyf))
        {
            return false;
        }

        UnitsPerEm = new ByteReader(_data, head.Offset + 18).U16();
        if (UnitsPerEm == 0) UnitsPerEm = 1000;
        var indexToLocFormat = new ByteReader(_data, head.Offset + 50).I16();

        GlyphCount = new ByteReader(_data, maxp.Offset + 4).U16();
        _glyfOffset = glyf.Offset;

        ParseLoca(locaT.Offset, indexToLocFormat);

        if (tables.TryGetValue("hhea", out var hhea) && tables.TryGetValue("hmtx", out var hmtx))
        {
            ParseHmtx(hhea.Offset, hmtx.Offset);
        }

        if (tables.TryGetValue("cmap", out var cmap))
        {
            ParseCmap(cmap.Offset);
        }

        return true;
    }

    private void ParseLoca(int offset, int format)
    {
        var count = GlyphCount + 1;
        _loca = new int[count];
        var r = new ByteReader(_data, offset);
        for (var i = 0; i < count; i++)
        {
            _loca[i] = format == 0 ? r.U16() * 2 : (int)r.U32();
        }
    }

    private void ParseHmtx(int hheaOffset, int hmtxOffset)
    {
        var numHMetrics = new ByteReader(_data, hheaOffset + 34).U16();
        _advances = new int[GlyphCount];
        var r = new ByteReader(_data, hmtxOffset);
        var last = 0;
        for (var i = 0; i < GlyphCount; i++)
        {
            if (i < numHMetrics)
            {
                last = r.U16();
                r.I16(); // left side bearing
            }
            _advances[i] = last;
        }
    }

    /// <summary>Advance width of a glyph in 1/1000 em, or -1 when unknown.</summary>
    public double GetAdvance1000(int gid)
    {
        if (gid < 0 || gid >= _advances.Length || _advances.Length == 0) return -1;
        return _advances[gid] * 1000.0 / UnitsPerEm;
    }

    /// <summary>Glyph id for a unicode code point via the cmap, or 0 when absent.</summary>
    public int GidForUnicode(int unicode)
    {
        if (Cmap.TryGetValue(unicode, out var g)) return g;
        if (Cmap.TryGetValue(0xF000 + unicode, out var gf)) return gf;
        return 0;
    }

    private void ParseCmap(int cmapOffset)
    {
        var r = new ByteReader(_data, cmapOffset);
        r.U16(); // version
        var numSub = r.U16();

        var best = -1;
        var bestScore = -1;
        var bestSymbol = false;

        for (var i = 0; i < numSub; i++)
        {
            var platform = r.U16();
            var encoding = r.U16();
            var subOffset = (int)r.U32();

            // Prefer Unicode tables; fall back to symbol and Mac Roman.
            var (score, symbol) = (platform, encoding) switch
            {
                (3, 10) => (5, false),
                (3, 1) => (4, false),
                (0, _) => (3, false),
                (3, 0) => (2, true),
                (1, 0) => (1, false),
                _ => (0, false),
            };

            if (score > bestScore)
            {
                bestScore = score;
                best = cmapOffset + subOffset;
                bestSymbol = symbol;
            }
        }

        if (best < 0) return;
        IsSymbol = bestSymbol;
        ParseCmapSubtable(best);
    }

    private void ParseCmapSubtable(int offset)
    {
        var r = new ByteReader(_data, offset);
        var format = r.U16();

        switch (format)
        {
            case 0:
                {
                    r.U16(); r.U16(); // length, language
                    for (var c = 0; c < 256; c++) Cmap[c] = r.U8();
                    break;
                }
            case 6:
                {
                    r.U16(); r.U16(); // length, language
                    var first = r.U16();
                    var count = r.U16();
                    for (var i = 0; i < count; i++) Cmap[first + i] = r.U16();
                    break;
                }
            case 4:
                ParseCmapFormat4(r);
                break;
            case 12:
                {
                    r.U16(); r.U32(); r.U32(); // reserved, length, language
                    var nGroups = (int)r.U32();
                    for (var i = 0; i < nGroups; i++)
                    {
                        var start = (int)r.U32();
                        var end = (int)r.U32();
                        var startGid = (int)r.U32();
                        for (var c = start; c <= end && c - start < 70000; c++)
                        {
                            Cmap[c] = startGid + (c - start);
                        }
                    }
                    break;
                }
        }
    }

    private void ParseCmapFormat4(ByteReader r)
    {
        r.U16(); // length
        r.U16(); // language
        var segX2 = r.U16();
        var segCount = segX2 / 2;
        r.U16(); r.U16(); r.U16(); // searchRange, entrySelector, rangeShift

        var endCode = new int[segCount];
        for (var i = 0; i < segCount; i++) endCode[i] = r.U16();
        r.U16(); // reservedPad
        var startCode = new int[segCount];
        for (var i = 0; i < segCount; i++) startCode[i] = r.U16();
        var idDelta = new int[segCount];
        for (var i = 0; i < segCount; i++) idDelta[i] = r.I16();

        var idRangeOffsetPos = r.Position;
        var idRangeOffset = new int[segCount];
        for (var i = 0; i < segCount; i++) idRangeOffset[i] = r.U16();

        for (var i = 0; i < segCount; i++)
        {
            for (var c = startCode[i]; c <= endCode[i] && c != 0xFFFF; c++)
            {
                int gid;
                if (idRangeOffset[i] == 0)
                {
                    gid = (c + idDelta[i]) & 0xFFFF;
                }
                else
                {
                    var addr = idRangeOffsetPos + i * 2 + idRangeOffset[i] + (c - startCode[i]) * 2;
                    if (addr + 1 >= _data.Length) continue;
                    var g = (_data[addr] << 8) | _data[addr + 1];
                    gid = g == 0 ? 0 : (g + idDelta[i]) & 0xFFFF;
                }
                if (gid != 0) Cmap[c] = gid;
            }
        }
    }

    /// <summary>
    /// Returns flattened glyph contours in 1/1000 em units (y up), or null when the
    /// glyph is empty/missing. Composite glyphs are expanded recursively.
    /// </summary>
    public List<List<(double X, double Y)>>? GetGlyphContours(int gid)
    {
        var contours = new List<List<(double X, double Y)>>();
        if (!AppendGlyph(gid, 1.0, 0, 0, 1.0, 0, 0, contours, 0)) return null;
        if (contours.Count == 0) return null;

        var scale = 1000.0 / UnitsPerEm;
        foreach (var c in contours)
        {
            for (var i = 0; i < c.Count; i++)
            {
                c[i] = (c[i].X * scale, c[i].Y * scale);
            }
        }
        return contours;
    }

    private bool AppendGlyph(int gid, double a, double b, double c, double d,
        double e, double f, List<List<(double X, double Y)>> output, int depth)
    {
        if (depth > 6 || gid < 0 || gid + 1 >= _loca.Length) return false;
        var start = _glyfOffset + _loca[gid];
        var end = _glyfOffset + _loca[gid + 1];
        if (end <= start) return true; // empty glyph (e.g., space)

        var r = new ByteReader(_data, start);
        var numContours = r.I16();
        r.I16(); r.I16(); r.I16(); r.I16(); // bounding box

        if (numContours < 0)
        {
            return AppendComposite(r, a, b, c, d, e, f, output, depth);
        }

        var endPts = new int[numContours];
        for (var i = 0; i < numContours; i++) endPts[i] = r.U16();
        var numPoints = numContours == 0 ? 0 : endPts[^1] + 1;

        var instrLen = r.U16();
        r.Position += instrLen;

        // Flags with repeat compression.
        var flags = new byte[numPoints];
        for (var i = 0; i < numPoints;)
        {
            var fl = r.U8();
            flags[i++] = fl;
            if ((fl & 0x08) != 0)
            {
                var repeat = r.U8();
                for (var k = 0; k < repeat && i < numPoints; k++) flags[i++] = fl;
            }
        }

        var xs = ReadCoordinates(r, flags, numPoints, 0x02, 0x10);
        var ys = ReadCoordinates(r, flags, numPoints, 0x04, 0x20);

        var startPt = 0;
        for (var ci = 0; ci < numContours; ci++)
        {
            var endPt = endPts[ci];
            BuildContour(flags, xs, ys, startPt, endPt, a, b, c, d, e, f, output);
            startPt = endPt + 1;
        }
        return true;
    }

    private static int[] ReadCoordinates(ByteReader r, byte[] flags, int n, int shortBit, int sameBit)
    {
        var coords = new int[n];
        var value = 0;
        for (var i = 0; i < n; i++)
        {
            var fl = flags[i];
            if ((fl & shortBit) != 0)
            {
                var delta = r.U8();
                value += (fl & sameBit) != 0 ? delta : -delta;
            }
            else if ((fl & sameBit) == 0)
            {
                value += r.I16();
            }
            coords[i] = value;
        }
        return coords;
    }

    private static void BuildContour(byte[] flags, int[] xs, int[] ys, int start, int end,
        double a, double b, double c, double d, double e, double f,
        List<List<(double X, double Y)>> output)
    {
        var count = end - start + 1;
        if (count < 2) return;

        (double X, double Y, bool On) Pt(int idx)
        {
            var i = start + ((idx % count) + count) % count;
            return (xs[i], ys[i], (flags[i] & 0x01) != 0);
        }

        // Find a starting on-curve point (synthesize one if all are off-curve).
        var startOn = -1;
        for (var i = 0; i < count; i++)
        {
            if (Pt(i).On) { startOn = i; break; }
        }

        (double X, double Y) firstOn;
        var indexOffset = 0;
        if (startOn < 0)
        {
            var p0 = Pt(0);
            var pl = Pt(count - 1);
            firstOn = ((p0.X + pl.X) / 2, (p0.Y + pl.Y) / 2);
            indexOffset = 0;
        }
        else
        {
            var p = Pt(startOn);
            firstOn = (p.X, p.Y);
            indexOffset = startOn;
        }

        var poly = new List<(double X, double Y)> { Transform(firstOn.X, firstOn.Y) };
        var current = firstOn;

        for (var k = 1; k <= count; k++)
        {
            var p = Pt(indexOffset + k);
            if (p.On)
            {
                poly.Add(Transform(p.X, p.Y));
                current = (p.X, p.Y);
            }
            else
            {
                var next = Pt(indexOffset + k + 1);
                (double X, double Y) nextOn = next.On
                    ? (next.X, next.Y)
                    : ((p.X + next.X) / 2, (p.Y + next.Y) / 2);

                FlattenQuad(poly, current, (p.X, p.Y), nextOn);
                current = nextOn;
                if (next.On) k++;
            }
        }

        output.Add(poly);

        (double X, double Y) Transform(double x, double y) =>
            (a * x + c * y + e, b * x + d * y + f);

        void FlattenQuad(List<(double X, double Y)> dst, (double X, double Y) p0,
            (double X, double Y) ctrl, (double X, double Y) p1)
        {
            for (var i = 1; i <= QuadSegments; i++)
            {
                var t = (double)i / QuadSegments;
                var mt = 1 - t;
                var x = mt * mt * p0.X + 2 * mt * t * ctrl.X + t * t * p1.X;
                var y = mt * mt * p0.Y + 2 * mt * t * ctrl.Y + t * t * p1.Y;
                dst.Add(Transform(x, y));
            }
        }
    }

    private bool AppendComposite(ByteReader r, double pa, double pb, double pc, double pd,
        double pe, double pf, List<List<(double X, double Y)>> output, int depth)
    {
        const int ArgsAreWords = 0x0001;
        const int ArgsAreXy = 0x0002;
        const int HaveScale = 0x0008;
        const int MoreComponents = 0x0020;
        const int HaveXYScale = 0x0040;
        const int Have2x2 = 0x0080;

        while (true)
        {
            var flags = r.U16();
            var glyphIndex = r.U16();

            double dx, dy;
            if ((flags & ArgsAreWords) != 0)
            {
                dx = (flags & ArgsAreXy) != 0 ? r.I16() : r.U16();
                dy = (flags & ArgsAreXy) != 0 ? r.I16() : r.U16();
            }
            else
            {
                dx = (flags & ArgsAreXy) != 0 ? r.I8() : r.U8();
                dy = (flags & ArgsAreXy) != 0 ? r.I8() : r.U8();
            }

            double a = 1, b = 0, cc = 0, d = 1;
            if ((flags & HaveScale) != 0)
            {
                a = d = F2Dot14(r);
            }
            else if ((flags & HaveXYScale) != 0)
            {
                a = F2Dot14(r);
                d = F2Dot14(r);
            }
            else if ((flags & Have2x2) != 0)
            {
                a = F2Dot14(r);
                b = F2Dot14(r);
                cc = F2Dot14(r);
                d = F2Dot14(r);
            }

            // Compose child transform with parent.
            var na = a * pa + b * pc;
            var nb = a * pb + b * pd;
            var nc = cc * pa + d * pc;
            var nd = cc * pb + d * pd;
            var ne = dx * pa + dy * pc + pe;
            var nf = dx * pb + dy * pd + pf;

            AppendGlyph(glyphIndex, na, nb, nc, nd, ne, nf, output, depth + 1);

            if ((flags & MoreComponents) == 0) break;
        }
        return true;
    }

    private static double F2Dot14(ByteReader r) => r.I16() / 16384.0;
}
