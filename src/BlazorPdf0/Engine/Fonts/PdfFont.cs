namespace BlazorPdf.Engine.Fonts;

/// <summary>
/// A loaded PDF font that exposes per-code advance widths (in 1/1000 em) and, when an
/// embedded TrueType program is present, real glyph outlines. Supports simple
/// single-byte fonts and Identity-encoded Type0/CIDFontType2 (2-byte) fonts; other
/// cases fall back to width-only metrics with the renderer's built-in vector glyphs.
/// </summary>
internal sealed class PdfFont
{
    private TrueTypeFont? _ttf;
    private CffFont? _cff;
    private readonly Dictionary<int, double> _widths = [];
    private double _defaultWidth = 500;
    private bool _twoByte;
    private bool _identityCid;
    private bool _symbolic;
    private bool _simple;
    private string _baseFont = "";
    private TrueTypeFont? _fallback;
    private bool _useFallback;
    private bool _fallbackBold;

    /// <summary>True for 2-byte (composite) fonts.</summary>
    public bool TwoByte => _twoByte;

    /// <summary>The font's BaseFont name (used by the SVG backend to pick a CSS family).</summary>
    public string BaseFont => _baseFont;

    /// <summary>Decodes a shown string into character codes (CIDs for composite fonts).</summary>
    public IEnumerable<int> Decode(byte[] bytes)
    {
        if (_twoByte)
        {
            for (var i = 0; i + 1 < bytes.Length; i += 2)
            {
                yield return (bytes[i] << 8) | bytes[i + 1];
            }
        }
        else
        {
            foreach (var b in bytes) yield return b;
        }
    }

    /// <summary>True when the code is an ASCII space (word-spacing applies).</summary>
    public bool IsSpace(int code) => !_twoByte && code == 32;

    /// <summary>Advance width of a code in 1/1000 em.</summary>
    public double GetWidth(int code)
    {
        if (_widths.TryGetValue(code, out var w)) return w;
        if (_ttf is not null)
        {
            var gid = MapToGid(code);
            var adv = _ttf.GetAdvance1000(gid);
            if (adv >= 0) return adv;
        }
        if (_simple)
        {
            var std = Standard14Metrics.GetWidth(_baseFont, code);
            if (std >= 0) return std;
        }
        return _defaultWidth;
    }

    /// <summary>Glyph outline contours in 1/1000 em (y up), or null when unavailable.</summary>
    public List<List<(double X, double Y)>>? GetContours(int code)
    {
        if (_cff is not null)
        {
            var gid = _cff.IsCid || _twoByte ? _cff.GidForCid(code) : _cff.GidForCode(code);
            return gid <= 0 ? null : _cff.GetGlyphContours(gid);
        }
        if (_ttf is not null)
        {
            var gid = MapToGid(code);
            return gid <= 0 ? null : _ttf.GetGlyphContours(gid);
        }
        if (_useFallback)
        {
            var fb = _fallback ??= FallbackFont.Get(_fallbackBold);
            if (fb is not null)
            {
                var gid = fb.GidForUnicode(WinAnsiEncoding.ToUnicode(code));
                if (gid > 0) return fb.GetGlyphContours(gid);
            }
        }
        return null;
    }

    private int MapToGid(int code)
    {
        if (_ttf is null) return 0;
        if (_identityCid) return code; // Identity CIDToGIDMap: CID == GID

        if (_symbolic && _ttf.IsSymbol)
        {
            if (_ttf.Cmap.TryGetValue(0xF000 + code, out var sg)) return sg;
            if (_ttf.Cmap.TryGetValue(code, out var sc)) return sc;
        }

        var unicode = WinAnsiEncoding.ToUnicode(code);
        if (_ttf.Cmap.TryGetValue(unicode, out var g)) return g;
        if (_ttf.Cmap.TryGetValue(code, out var gc)) return gc;
        if (_ttf.Cmap.TryGetValue(0xF000 + code, out var gf)) return gf;
        return 0;
    }

    // --- Loading ---

    public static PdfFont Load(PdfDocument doc, PdfDictionary dict)
    {
        var font = new PdfFont();
        var subtype = (doc.Resolve(dict.Get("Subtype")) as PdfName)?.Value ?? "";
        var baseFont = (doc.Resolve(dict.Get("BaseFont")) as PdfName)?.Value ?? "";
        font._baseFont = baseFont;

        if (baseFont.Contains("Courier", StringComparison.OrdinalIgnoreCase))
        {
            font._defaultWidth = 600;
        }

        if (subtype == "Type0")
        {
            font.LoadType0(doc, dict);
        }
        else
        {
            font.LoadSimple(doc, dict);
        }

        return font;
    }

    private void LoadSimple(PdfDocument doc, PdfDictionary dict)
    {
        _simple = true;
        var firstChar = (doc.Resolve(dict.Get("FirstChar")) as PdfNumber)?.AsInt ?? 0;
        if (doc.Resolve(dict.Get("Widths")) is PdfArray widths)
        {
            for (var i = 0; i < widths.Count; i++)
            {
                if (doc.Resolve(widths[i]) is PdfNumber n) _widths[firstChar + i] = n.Value;
            }
        }

        var descriptor = doc.Resolve(dict.Get("FontDescriptor")) as PdfDictionary;
        ReadFlags(doc, descriptor);
        LoadGlyphSource(doc, descriptor);

        // No embedded program: render with a real fallback outline font when one is
        // available, otherwise the renderer uses its built-in vector glyphs.
        if (_ttf is null && _cff is null)
        {
            _useFallback = true;
            _fallbackBold = _baseFont.Contains("Bold", StringComparison.OrdinalIgnoreCase);
        }
    }

    private void LoadType0(PdfDocument doc, PdfDictionary dict)
    {
        _twoByte = true;

        var encoding = doc.Resolve(dict.Get("Encoding"));
        var encName = (encoding as PdfName)?.Value ?? "";
        _identityCid = encName.StartsWith("Identity", StringComparison.Ordinal);

        if (doc.Resolve(dict.Get("DescendantFonts")) is not PdfArray { Count: > 0 } descendants ||
            doc.Resolve(descendants[0]) is not PdfDictionary cidFont)
        {
            return;
        }

        _defaultWidth = (doc.Resolve(cidFont.Get("DW")) as PdfNumber)?.Value ?? 1000;
        ReadCidWidths(doc, cidFont);

        var descriptor = doc.Resolve(cidFont.Get("FontDescriptor")) as PdfDictionary;
        ReadFlags(doc, descriptor);
        LoadGlyphSource(doc, descriptor);

        // A non-Identity CIDToGIDMap stream is uncommon; Identity is assumed otherwise.
        if (doc.Resolve(cidFont.Get("CIDToGIDMap")) is PdfName { Value: "Identity" } or null)
        {
            _identityCid = true;
        }
    }

    private void ReadCidWidths(PdfDocument doc, PdfDictionary cidFont)
    {
        if (doc.Resolve(cidFont.Get("W")) is not PdfArray w) return;

        var i = 0;
        while (i < w.Count)
        {
            if (doc.Resolve(w[i]) is not PdfNumber first) break;
            if (i + 1 >= w.Count) break;

            var next = doc.Resolve(w[i + 1]);
            if (next is PdfArray arr)
            {
                for (var k = 0; k < arr.Count; k++)
                {
                    if (doc.Resolve(arr[k]) is PdfNumber wn) _widths[first.AsInt + k] = wn.Value;
                }
                i += 2;
            }
            else if (next is PdfNumber last && i + 2 < w.Count && doc.Resolve(w[i + 2]) is PdfNumber width)
            {
                for (var cid = first.AsInt; cid <= last.AsInt; cid++) _widths[cid] = width.Value;
                i += 3;
            }
            else
            {
                break;
            }
        }
    }

    private void ReadFlags(PdfDocument doc, PdfDictionary? descriptor)
    {
        if (descriptor is null) return;
        var flags = (doc.Resolve(descriptor.Get("Flags")) as PdfNumber)?.AsInt ?? 0;
        _symbolic = (flags & 4) != 0 && (flags & 32) == 0;
    }

    private void LoadGlyphSource(PdfDocument doc, PdfDictionary? descriptor)
    {
        if (descriptor is null) return;

        // Embedded TrueType outlines.
        if (doc.Resolve(descriptor.Get("FontFile2")) is PdfStream ff2)
        {
            var bytes = PdfFilters.Decode(ff2, doc);
            if (bytes.Length > 0) _ttf = TrueTypeFont.Parse(bytes);
            if (_ttf is not null) return;
        }

        // Embedded CFF / OpenType-CFF outlines (Type1C, CIDFontType0C).
        if (doc.Resolve(descriptor.Get("FontFile3")) is PdfStream ff3)
        {
            var bytes = PdfFilters.Decode(ff3, doc);
            if (bytes.Length > 0)
            {
                _cff = CffFont.Parse(bytes);
                if (_cff is not null) return;
                _ttf = TrueTypeFont.Parse(bytes); // OpenType with TrueType outlines
            }
        }
    }
}
