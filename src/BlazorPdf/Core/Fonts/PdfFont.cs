// Clean-room C# port of the font model from pdf.js `src/core/fonts.js` /
// `src/core/evaluator.js` (the parts needed to position and extract text).
// See NOTICE.

using BlazorPdf.Core.Filters;

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

    /// <summary>A stable CSS family name for the embedded font program.</summary>
    public string FontFaceFamily => $"bpf{(uint)(BaseFont.GetHashCode() ^ (EmbeddedProgram?.Length ?? 0)):x8}";

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

        PdfFont font = subtype == "Type0"
            ? CreateType0(fontDict, xref, baseFont, toUnicode)
            : CreateSimple(fontDict, baseFont, toUnicode);

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
            // TrueType / CIDFontType2 program.
            if (descriptor.Get("FontFile2") is PdfStream ttf)
            {
                return (StreamDecoder.Decode(ttf), "truetype");
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
        if (fontDict.Get("FontDescriptor") is Dict descriptor)
        {
            missingWidth = ToDouble(descriptor.Get("MissingWidth"), 0);
        }

        // When no /Widths are supplied (common for the Standard 14 fonts), fall
        // back to the built-in Core-14 advance metrics.
        Func<int, double>? standardWidth = widths.Count == 0 ? StandardFonts.Resolve(baseFont) : null;

        string[]? encoding = BuildSimpleEncoding(fontDict, baseFont);

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
    private static string[]? BuildSimpleEncoding(Dict fontDict, string baseFont)
    {
        object? enc = fontDict.Get("Encoding");

        // The default base encoding: Standard for the symbolic core fonts,
        // WinAnsi otherwise (a pragmatic choice that maximizes correct text).
        string[] baseTable = Encodings.WinAnsi;
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
                ReadCidWidths(w, cidWidths);
            }
        }

        return new PdfFont(
            isType0: true,
            baseFont,
            firstChar: 0,
            [],
            cidWidths,
            defaultWidth,
            toUnicode);
    }

    private static void ReadCidWidths(List<object?> w, Dictionary<int, double> cidWidths)
    {
        // Two forms: "c [w1 w2 ...]" and "cFirst cLast w".
        int i = 0;
        while (i < w.Count)
        {
            if (w[i] is not double first)
            {
                break;
            }
            if (i + 1 < w.Count && w[i + 1] is List<object?> list)
            {
                int cid = (int)first;
                foreach (var item in list)
                {
                    if (item is double width)
                    {
                        cidWidths[cid] = width;
                    }
                    cid++;
                }
                i += 2;
            }
            else if (i + 2 < w.Count && w[i + 1] is double last && w[i + 2] is double width)
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
            for (int i = 0; i + 1 < bytes.Length; i += 2)
            {
                int code = (bytes[i] << 8) | bytes[i + 1];
                double width = _cidWidths.TryGetValue(code, out var w) ? w : _defaultWidth;
                yield return new Glyph(code, UnicodeFor(code), width, isSpace: false);
            }
            // Handle a trailing odd byte defensively.
            if (bytes.Length % 2 == 1)
            {
                int code = bytes[^1];
                yield return new Glyph(code, UnicodeFor(code), _defaultWidth, false);
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
        if (index >= 0 && index < _widths.Length && _widths[index] != 0)
        {
            return _widths[index];
        }
        if (_widths.Length == 0 && _standardWidth is not null)
        {
            return _standardWidth(code);
        }
        return _defaultWidth;
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
