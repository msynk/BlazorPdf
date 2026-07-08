// The font model: the parts needed to position and extract text.

using BlazorPdf.Core.Filters;
using BlazorPdf.Core.Geometry;

namespace BlazorPdf.Core.Fonts;

/// <summary>
/// The information required to lay out and extract text for one PDF font:
/// glyph widths, the code-to-Unicode mapping, and the number of bytes per
/// character code. Supports simple (single-byte) fonts and Type0 composite
/// fonts using two-byte codes (Identity and CMap-based encodings).
/// </summary>
public sealed class PdfFont
{
    private readonly bool _isType0;
    private readonly int _firstChar;
    private readonly double[] _widths;          // simple-font widths, 1000-em
    private readonly Dictionary<int, double> _cidWidths; // Type0 widths, 1000-em
    private readonly double _defaultWidth;      // /DW or /MissingWidth, 1000-em
    private readonly ToUnicodeCMap? _toUnicode;
    private readonly Func<int, double>? _standardWidth; // Core-14 fallback metrics
    private readonly string[]? _encoding;       // simple-font code -> glyph name
    private CMap _cidEncoding = CMap.Identity;   // Type0 code -> CID mapping
    private double _glyphWidthScale = 1.0;      // maps raw widths to 1000-em (Type3)

    /// <summary>Type3 glyph data (glyph procedures), when the font is a Type3 font.</summary>
    public Type3FontData? Type3 { get; private set; }

    /// <summary><c>true</c> for a Type3 font whose glyphs are content-stream procedures.</summary>
    public bool IsType3 => Type3 is not null;

    /// <summary>The PostScript base font name, when available.</summary>
    public string BaseFont { get; }

    /// <summary>The embedded font program bytes (TrueType/OpenType), if any.</summary>
    public byte[]? EmbeddedProgram { get; private set; }

    /// <summary>The CSS <c>@font-face</c> format of <see cref="EmbeddedProgram"/> ("truetype"/"opentype").</summary>
    public string? EmbeddedFormat { get; private set; }

    /// <summary>Whether the font is bold (from the base-font name or descriptor flags).</summary>
    public bool Bold { get; private set; }

    /// <summary>Whether the font is italic/oblique.</summary>
    public bool Italic { get; private set; }

    /// <summary><c>true</c> when an embeddable font program is available.</summary>
    public bool HasEmbedded => EmbeddedProgram is { Length: > 0 } && EmbeddedFormat is not null;

    private string? _fontFaceFamily;

    /// <summary>
    /// A stable CSS family name derived from a content hash of the embedded font
    /// program. Using the program bytes (not <c>string.GetHashCode</c>, which is
    /// randomized per process) keeps <c>@font-face</c> names deterministic and
    /// avoids cross-font collisions.
    /// </summary>
    public string FontFaceFamily => _fontFaceFamily ??= $"bpf{StableFontHash():x8}";

    private uint StableFontHash()
    {
        // FNV-1a over the embedded program (or the base-font name when not
        // embedded). Deterministic across processes and WASM-safe.
        uint h = 2166136261;
        if (EmbeddedProgram is { Length: > 0 } p)
        {
            foreach (byte b in p)
            {
                h = (h ^ b) * 16777619;
            }
        }
        else
        {
            foreach (char c in BaseFont)
            {
                h = (h ^ (byte)c) * 16777619;
            }
        }
        return h;
    }

    /// <summary>A generic CSS family ("serif"/"sans-serif"/"monospace") inferred from the base font.</summary>
    public string GenericFamily => InferGenericFamily(BaseFont);

    /// <summary>Number of bytes per character code (1 for simple, 2 for Type0).</summary>
    public int BytesPerCode => _isType0 ? 2 : 1;

    private PdfFont(
        bool isType0, string baseFont, int firstChar, double[] widths,
        Dictionary<int, double> cidWidths, double defaultWidth, ToUnicodeCMap? toUnicode,
        Func<int, double>? standardWidth = null, string[]? encoding = null)
    {
        _isType0 = isType0;
        BaseFont = baseFont;
        _firstChar = firstChar;
        _widths = widths;
        _cidWidths = cidWidths;
        _defaultWidth = defaultWidth;
        _toUnicode = toUnicode;
        _standardWidth = standardWidth;
        _encoding = encoding;
    }

    /// <summary>Builds a <see cref="PdfFont"/> from a font dictionary.</summary>
    public static PdfFont Create(Dict fontDict, IXRef xref)
    {
        string subtype = (fontDict.Get("Subtype") as Name)?.Value ?? "";
        string baseFont = (fontDict.Get("BaseFont") as Name)?.Value ?? "";
        ToUnicodeCMap? toUnicode = ReadToUnicode(fontDict);

        PdfFont font = subtype switch
        {
            "Type0" => CreateType0(fontDict, xref, baseFont, toUnicode),
            "Type3" => CreateType3(fontDict, xref, toUnicode),
            _ => CreateSimple(fontDict, baseFont, toUnicode),
        };

        // Locate the font descriptor (on the font, or its descendant for Type0).
        Dict? descriptor = fontDict.Get("FontDescriptor") as Dict;
        if (descriptor is null && fontDict.Get("DescendantFonts") is List<object?> desc && desc.Count > 0
            && xref.FetchIfRef(desc[0]) is Dict cidFont)
        {
            descriptor = cidFont.Get("FontDescriptor") as Dict;
        }

        (byte[]? program, string? format) = ExtractEmbedded(descriptor);
        bool bold = InferBold(baseFont, descriptor);
        bool italic = InferItalic(baseFont, descriptor);

        font.EmbeddedProgram = program;
        font.EmbeddedFormat = format;
        font.Bold = bold;
        font.Italic = italic;
        return font;
    }

    private static (byte[]?, string?) ExtractEmbedded(Dict? descriptor)
    {
        if (descriptor is null)
        {
            return (null, null);
        }
        try
        {
            // TrueType / CIDFontType2 program. Normalize the sfnt structure so a
            // subset font with an unsorted directory or bad checksums still loads
            // in the browser; keep the raw bytes if it isn't a parseable sfnt.
            if (descriptor.Get("FontFile2") is PdfStream ttf)
            {
                byte[] raw = StreamDecoder.Decode(ttf);
                return (TrueTypeSanitizer.Sanitize(raw) ?? raw, "truetype");
            }
            // OpenType program (FontFile3 with Subtype OpenType).
            if (descriptor.Get("FontFile3") is PdfStream ot && ot.Dict is not null
                && (ot.Dict.Get("Subtype") as Name)?.Value == "OpenType")
            {
                return (StreamDecoder.Decode(ot), "opentype");
            }
            // Bare CFF / Type1 programs need wrapping the browser can't do directly.
        }
        catch
        {
            // Ignore malformed font programs and fall back to substitute fonts.
        }
        return (null, null);
    }

    private static PdfFont CreateSimple(Dict fontDict, string baseFont, ToUnicodeCMap? toUnicode)
    {
        int firstChar = ToInt(fontDict.Get("FirstChar"), 0);
        var widths = new List<double>();
        if (fontDict.Get("Widths") is List<object?> widthArr)
        {
            foreach (var w in widthArr)
            {
                widths.Add(w is double d ? d : 0);
            }
        }

        double missingWidth = 0;
        bool symbolic = false;
        if (fontDict.Get("FontDescriptor") is Dict descriptor)
        {
            missingWidth = ToDouble(descriptor.Get("MissingWidth"), 0);
            // /Flags bit 3 (value 4) marks a symbolic font.
            symbolic = descriptor.Get("Flags") is double flags && ((int)flags & 0x4) != 0;
        }

        // Resolve Core-14 metrics whenever the base font is a standard font, so
        // they can fill in codes outside the explicit /Widths range too (not only
        // when /Widths is entirely absent).
        Func<int, double>? standardWidth = StandardFonts.Resolve(baseFont);

        string[]? encoding = BuildSimpleEncoding(fontDict, baseFont, symbolic);

        return new PdfFont(
            isType0: false,
            baseFont,
            firstChar,
            widths.ToArray(),
            new Dictionary<int, double>(),
            missingWidth,
            toUnicode,
            standardWidth,
            encoding);
    }

    /// <summary>
    /// Builds the code-to-glyph-name table for a simple font from its
    /// <c>/Encoding</c> (a base-encoding name and/or a <c>/Differences</c> array).
    /// </summary>
    private static string[]? BuildSimpleEncoding(Dict fontDict, string baseFont, bool symbolic = false)
    {
        object? enc = fontDict.Get("Encoding");

        // The default base encoding: the named symbolic fonts (Symbol /
        // ZapfDingbats) carry their own built-in encoding; a font flagged
        // Symbolic in its descriptor uses the program's built-in encoding (which
        // we don't decode here), so start from an empty table rather than
        // imposing WinAnsi glyph names on codes that aren't WinAnsi. WinAnsi is
        // the pragmatic default for ordinary non-symbolic text.
        string[] baseTable = symbolic && !IsNamedSymbolFont(baseFont)
            ? EmptyEncodingTable()
            : DefaultBaseEncoding(baseFont);
        List<object?>? differences = null;

        switch (enc)
        {
            case Name name:
                baseTable = Encodings.ByName(name.Value) ?? baseTable;
                break;
            case Dict dict:
                if (dict.Get("BaseEncoding") is Name baseName)
                {
                    baseTable = Encodings.ByName(baseName.Value) ?? baseTable;
                }
                differences = dict.Get("Differences") as List<object?>;
                break;
            case null:
                // No /Encoding: nothing to override; keep the default table.
                return (string[])baseTable.Clone();
        }

        var table = (string[])baseTable.Clone();
        if (differences is not null)
        {
            int code = 0;
            foreach (var item in differences)
            {
                if (item is double d)
                {
                    code = (int)d;
                }
                else if (item is Name glyphName && code is >= 0 and < 256)
                {
                    table[code] = glyphName.Value;
                    code++;
                }
            }
        }
        return table;
    }

    /// <summary>
    /// Chooses the default base encoding for a simple font from its base-font
    /// name: the built-in encoding for Symbol/ZapfDingbats, WinAnsi otherwise.
    /// </summary>
    private static string[] DefaultBaseEncoding(string baseFont)
    {
        // Strip a subset prefix such as "ABCDEF+Symbol".
        int plus = baseFont.IndexOf('+');
        string name = plus >= 0 ? baseFont[(plus + 1)..] : baseFont;

        if (name.Contains("ZapfDingbats", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Dingbats", StringComparison.OrdinalIgnoreCase))
        {
            return Encodings.ZapfDingbats;
        }
        if (name.Contains("Symbol", StringComparison.OrdinalIgnoreCase))
        {
            return Encodings.Symbol;
        }
        return Encodings.WinAnsi;
    }

    /// <summary>True when the base-font name is one of the built-in symbol fonts
    /// (Symbol / ZapfDingbats), which have their own well-known encoding table.</summary>
    private static bool IsNamedSymbolFont(string baseFont)
    {
        int plus = baseFont.IndexOf('+');
        string name = plus >= 0 ? baseFont[(plus + 1)..] : baseFont;
        return name.Contains("Symbol", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Dingbats", StringComparison.OrdinalIgnoreCase);
    }

    private static string[] EmptyEncodingTable()
    {
        var table = new string[256];
        Array.Fill(table, string.Empty);
        return table;
    }

    private static PdfFont CreateType3(Dict fontDict, IXRef xref, ToUnicodeCMap? toUnicode)
    {
        int firstChar = ToInt(fontDict.Get("FirstChar"), 0);
        var widths = new List<double>();
        if (fontDict.Get("Widths") is List<object?> widthArr)
        {
            foreach (var w in widthArr)
            {
                widths.Add(w is double d ? d : 0);
            }
        }

        // Type3 glyphs live in glyph space; /FontMatrix maps them to text space.
        Matrix fontMatrix = ReadFontMatrix(fontDict);
        // A Type3 font defines its own glyphs via /CharProcs keyed by the names in
        // /Encoding /Differences; there is no standard base encoding, so start from
        // an empty table (symbolic) rather than imposing WinAnsi names.
        string[]? encoding = BuildSimpleEncoding(fontDict, "", symbolic: true);

        var font = new PdfFont(
            isType0: false,
            baseFont: "",
            firstChar,
            widths.ToArray(),
            new Dictionary<int, double>(),
            defaultWidth: 0,
            toUnicode,
            standardWidth: null,
            encoding)
        {
            // Widths are in glyph space; scale them to the 1000-em text space the
            // layout code expects (advance_text = width_glyph * fontMatrix.a).
            _glyphWidthScale = fontMatrix.A * 1000.0,
        };

        Dict? charProcs = fontDict.Get("CharProcs") as Dict;
        Dict? resources = fontDict.Get("Resources") as Dict;
        font.Type3 = new Type3FontData(fontMatrix, charProcs, resources, encoding, xref);
        return font;
    }

    private static Matrix ReadFontMatrix(Dict fontDict)
    {
        if (fontDict.Get("FontMatrix") is List<object?> m && m.Count >= 6)
        {
            return new Matrix(ToDouble(m[0], 0.001), ToDouble(m[1], 0), ToDouble(m[2], 0),
                ToDouble(m[3], 0.001), ToDouble(m[4], 0), ToDouble(m[5], 0));
        }
        return new Matrix(0.001, 0, 0, 0.001, 0, 0);
    }

    private static PdfFont CreateType0(Dict fontDict, IXRef xref, string baseFont, ToUnicodeCMap? toUnicode)
    {
        var cidWidths = new Dictionary<int, double>();
        double defaultWidth = 1000;

        if (fontDict.Get("DescendantFonts") is List<object?> descendants && descendants.Count > 0
            && xref.FetchIfRef(descendants[0]) is Dict cidFont)
        {
            defaultWidth = ToDouble(cidFont.Get("DW"), 1000);
            if (cidFont.Get("W") is List<object?> w)
            {
                ReadCidWidths(w, cidWidths, xref);
            }
        }

        var font = new PdfFont(
            isType0: true,
            baseFont,
            firstChar: 0,
            [],
            cidWidths,
            defaultWidth,
            toUnicode);

        // The Type0 /Encoding maps character codes to CIDs: Identity-H/V map
        // directly, an embedded CMap stream supplies explicit ranges.
        object? enc = fontDict.Get("Encoding");
        if (enc is PdfStream cmapStream)
        {
            try
            {
                font._cidEncoding = CMap.Parse(StreamDecoder.Decode(cmapStream));
            }
            catch
            {
                font._cidEncoding = CMap.Identity;
            }
        }
        return font;
    }

    private static void ReadCidWidths(List<object?> w, Dictionary<int, double> cidWidths, IXRef xref)
    {
        // Two forms: "c [w1 w2 ...]" and "cFirst cLast w". Array elements (and the
        // inner width list) may be indirect references, so resolve each.
        int i = 0;
        while (i < w.Count)
        {
            if (xref.FetchIfRef(w[i]) is not double first)
            {
                break;
            }
            object? second = i + 1 < w.Count ? xref.FetchIfRef(w[i + 1]) : null;
            if (second is List<object?> list)
            {
                int cid = (int)first;
                foreach (var item in list)
                {
                    if (xref.FetchIfRef(item) is double width)
                    {
                        cidWidths[cid] = width;
                    }
                    cid++;
                }
                i += 2;
            }
            else if (second is double last && i + 2 < w.Count && xref.FetchIfRef(w[i + 2]) is double width)
            {
                for (int cid = (int)first; cid <= (int)last; cid++)
                {
                    cidWidths[cid] = width;
                }
                i += 3;
            }
            else
            {
                break;
            }
        }
    }

    private static ToUnicodeCMap? ReadToUnicode(Dict fontDict)
    {
        if (fontDict.Get("ToUnicode") is PdfStream stream)
        {
            try
            {
                return ToUnicodeCMap.Parse(StreamDecoder.Decode(stream));
            }
            catch
            {
                return null;
            }
        }
        return null;
    }

    /// <summary>Decodes a show-text operand into a sequence of glyphs.</summary>
    public IEnumerable<Glyph> Decode(byte[] bytes)
    {
        if (_isType0)
        {
            int step = _cidEncoding.CodeLength >= 1 ? _cidEncoding.CodeLength : 2;
            for (int i = 0; i + step <= bytes.Length; i += step)
            {
                long code = 0;
                for (int k = 0; k < step; k++)
                {
                    code = (code << 8) | bytes[i + k];
                }
                int cid = _cidEncoding.Lookup(code);
                double width = _cidWidths.TryGetValue(cid, out var w) ? w : _defaultWidth;
                // Text is keyed by the original code for ToUnicode lookup.
                yield return new Glyph((int)code, UnicodeFor((int)code), width, isSpace: false);
            }
        }
        else
        {
            foreach (byte b in bytes)
            {
                int code = b;
                double width = WidthFor(code);
                yield return new Glyph(code, UnicodeFor(code), width, isSpace: code == 0x20);
            }
        }
    }

    private double WidthFor(int code)
    {
        int index = code - _firstChar;
        if (index >= 0 && index < _widths.Length)
        {
            // An explicit width of 0 is valid (e.g. combining marks) — use it
            // rather than falling through to a substitute metric.
            return _widths[index] * _glyphWidthScale;
        }
        // Code outside [FirstChar, FirstChar+len): prefer Core-14 metrics, then
        // the /MissingWidth default (never silently 0 when metrics exist).
        if (_standardWidth is not null)
        {
            return _standardWidth(code);
        }
        return _defaultWidth * _glyphWidthScale;
    }

    private string UnicodeFor(int code)
    {
        string? mapped = _toUnicode?.Lookup(code);
        if (!string.IsNullOrEmpty(mapped))
        {
            return mapped;
        }
        if (_isType0)
        {
            // Without a ToUnicode map a CID cannot be reliably mapped; emit nothing.
            return string.Empty;
        }
        // Resolve via the font's encoding (base encoding + /Differences).
        if (_encoding is not null && code is >= 0 and < 256 && _encoding[code].Length > 0)
        {
            string viaName = GlyphList.ToUnicode(_encoding[code]);
            if (!string.IsNullOrEmpty(viaName))
            {
                return viaName;
            }
        }
        return WinAnsiEncoding.CodeToUnicode(code);
    }

    private static string InferGenericFamily(string baseFont)
    {
        int plus = baseFont.IndexOf('+');
        string name = (plus >= 0 ? baseFont[(plus + 1)..] : baseFont).ToLowerInvariant();
        if (name.Contains("courier") || name.Contains("mono"))
        {
            return "monospace";
        }
        if (name.Contains("times") || name.Contains("serif") || name.Contains("roman")
            || name.Contains("georgia") || name.Contains("minion") || name.Contains("garamond"))
        {
            return "serif";
        }
        return "sans-serif";
    }

    private static bool InferBold(string baseFont, Dict? descriptor)
    {
        if (baseFont.Contains("Bold", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        if (descriptor?.Get("StemV") is double stemV && stemV >= 140)
        {
            return true;
        }
        // FontDescriptor /Flags bit 19 (0x40000) marks ForceBold.
        return descriptor?.Get("Flags") is double flags && ((int)flags & 0x40000) != 0;
    }

    private static bool InferItalic(string baseFont, Dict? descriptor)
    {
        if (baseFont.Contains("Italic", StringComparison.OrdinalIgnoreCase)
            || baseFont.Contains("Oblique", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        if (descriptor?.Get("ItalicAngle") is double angle && Math.Abs(angle) > 0.5)
        {
            return true;
        }
        // FontDescriptor /Flags bit 7 (0x40) marks Italic.
        return descriptor?.Get("Flags") is double flags && ((int)flags & 0x40) != 0;
    }

    private static int ToInt(object? value, int fallback) => value is double d ? (int)d : fallback;
    private static double ToDouble(object? value, double fallback) => value is double d ? d : fallback;
}

/// <summary>
/// The data needed to draw a Type3 font's glyphs: the font matrix (glyph space
/// to text space), the <c>/CharProcs</c> content streams, the glyph resources,
/// and the code-to-glyph-name encoding. Type3 glyphs are rendered by executing
/// each glyph's content stream, unlike other fonts whose glyphs are drawn from
/// outlines or substituted.
/// </summary>
public sealed class Type3FontData
{
    private readonly Dict? _charProcs;
    private readonly string[]? _encoding;
    private readonly IXRef _xref;

    /// <summary>The font matrix mapping glyph space to text space.</summary>
    public Matrix FontMatrix { get; }

    /// <summary>The glyph resource dictionary, if the font supplies one.</summary>
    public Dict? Resources { get; }

    internal Type3FontData(Matrix fontMatrix, Dict? charProcs, Dict? resources,
        string[]? encoding, IXRef xref)
    {
        FontMatrix = fontMatrix;
        _charProcs = charProcs;
        Resources = resources;
        _encoding = encoding;
        _xref = xref;
    }

    /// <summary>
    /// Returns the glyph procedure (a content stream) for a character code, or
    /// <c>null</c> when the code has no glyph name or matching <c>/CharProcs</c> entry.
    /// </summary>
    public PdfStream? GetGlyphProcedure(int code)
    {
        if (_charProcs is null || _encoding is null || code is < 0 or > 255)
        {
            return null;
        }
        string name = _encoding[code];
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }
        return _xref.FetchIfRef(_charProcs.Get(name)) as PdfStream;
    }
}

