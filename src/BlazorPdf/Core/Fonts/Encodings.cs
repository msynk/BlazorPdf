// Standard PDF base encodings (code -> glyph name) per PDF 32000-1:2008 Annex D,
// a clean-room C# port of the tables in pdf.js `src/core/encodings.js`. Used to
// resolve simple-font character codes to glyph names (and thence to Unicode)
// when a font supplies a base encoding and/or a /Differences array. See NOTICE.

namespace BlazorPdf.Core.Fonts;

/// <summary>
/// The named base encodings used by simple fonts. Each array maps a byte code
/// (0..255) to a PostScript glyph name; empty entries are <c>.notdef</c>.
/// </summary>
internal static class Encodings
{
    /// <summary>Returns the base encoding table for a named encoding, or <c>null</c>.</summary>
    public static string[]? ByName(string? name) => name switch
    {
        "StandardEncoding" => Standard,
        "WinAnsiEncoding" => WinAnsi,
        "MacRomanEncoding" => MacRoman,
        "PDFDocEncoding" => WinAnsi, // close enough for text extraction
        _ => null,
    };

    // Glyph name for the printable ASCII range 32..126, shared by all Latin
    // base encodings (with a couple of position-specific overrides applied below).
    private static readonly string[] Ascii =
    [
        "space", "exclam", "quotedbl", "numbersign", "dollar", "percent", "ampersand", "quotesingle",
        "parenleft", "parenright", "asterisk", "plus", "comma", "hyphen", "period", "slash",
        "zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine",
        "colon", "semicolon", "less", "equal", "greater", "question", "at",
        "A", "B", "C", "D", "E", "F", "G", "H", "I", "J", "K", "L", "M",
        "N", "O", "P", "Q", "R", "S", "T", "U", "V", "W", "X", "Y", "Z",
        "bracketleft", "backslash", "bracketright", "asciicircum", "underscore", "grave",
        "a", "b", "c", "d", "e", "f", "g", "h", "i", "j", "k", "l", "m",
        "n", "o", "p", "q", "r", "s", "t", "u", "v", "w", "x", "y", "z",
        "braceleft", "bar", "braceright", "asciitilde",
    ];

    private static string[] NewLatinBase()
    {
        var enc = new string[256];
        for (int i = 0; i < 256; i++)
        {
            enc[i] = "";
        }
        for (int i = 0; i < Ascii.Length; i++)
        {
            enc[32 + i] = Ascii[i];
        }
        return enc;
    }

    public static readonly string[] Standard = BuildStandard();
    public static readonly string[] WinAnsi = BuildWinAnsi();
    public static readonly string[] MacRoman = BuildMacRoman();

    private static string[] BuildStandard()
    {
        var e = NewLatinBase();
        // StandardEncoding differs from ASCII in the quote glyphs.
        e[0x27] = "quoteright";
        e[0x60] = "quoteleft";
        // High range (PDF Annex D.2; codes shown in hexadecimal).
        e[0xA1] = "exclamdown"; e[0xA2] = "cent"; e[0xA3] = "sterling"; e[0xA4] = "fraction";
        e[0xA5] = "yen"; e[0xA6] = "florin"; e[0xA7] = "section"; e[0xA8] = "currency";
        e[0xA9] = "quotesingle"; e[0xAA] = "quotedblleft"; e[0xAB] = "guillemotleft";
        e[0xAC] = "guilsinglleft"; e[0xAD] = "guilsinglright"; e[0xAE] = "fi"; e[0xAF] = "fl";
        e[0xB1] = "endash"; e[0xB2] = "dagger"; e[0xB3] = "daggerdbl"; e[0xB4] = "periodcentered";
        e[0xB6] = "paragraph"; e[0xB7] = "bullet"; e[0xB8] = "quotesinglbase";
        e[0xB9] = "quotedblbase"; e[0xBA] = "quotedblright"; e[0xBB] = "guillemotright";
        e[0xBC] = "ellipsis"; e[0xBD] = "perthousand"; e[0xBF] = "questiondown";
        e[0xC1] = "grave"; e[0xC2] = "acute"; e[0xC3] = "circumflex"; e[0xC4] = "tilde";
        e[0xC5] = "macron"; e[0xC6] = "breve"; e[0xC7] = "dotaccent"; e[0xC8] = "dieresis";
        e[0xCA] = "ring"; e[0xCB] = "cedilla"; e[0xCD] = "hungarumlaut"; e[0xCE] = "ogonek";
        e[0xCF] = "caron"; e[0xD0] = "emdash"; e[0xE1] = "AE"; e[0xE3] = "ordfeminine";
        e[0xE8] = "Lslash"; e[0xE9] = "Oslash"; e[0xEA] = "OE"; e[0xEB] = "ordmasculine";
        e[0xF1] = "ae"; e[0xF5] = "dotlessi"; e[0xF8] = "lslash"; e[0xF9] = "oslash";
        e[0xFA] = "oe"; e[0xFB] = "germandbls";
        return e;
    }

    private static string[] BuildWinAnsi()
    {
        var e = NewLatinBase();
        e[0x80] = "Euro"; e[0x82] = "quotesinglbase"; e[0x83] = "florin"; e[0x84] = "quotedblbase";
        e[0x85] = "ellipsis"; e[0x86] = "dagger"; e[0x87] = "daggerdbl"; e[0x88] = "circumflex";
        e[0x89] = "perthousand"; e[0x8A] = "Scaron"; e[0x8B] = "guilsinglleft"; e[0x8C] = "OE";
        e[0x8E] = "Zcaron"; e[0x91] = "quoteleft"; e[0x92] = "quoteright"; e[0x93] = "quotedblleft";
        e[0x94] = "quotedblright"; e[0x95] = "bullet"; e[0x96] = "endash"; e[0x97] = "emdash";
        e[0x98] = "tilde"; e[0x99] = "trademark"; e[0x9A] = "scaron"; e[0x9B] = "guilsinglright";
        e[0x9C] = "oe"; e[0x9E] = "zcaron"; e[0x9F] = "Ydieresis"; e[0xA0] = "space";
        e[0xA1] = "exclamdown"; e[0xA2] = "cent"; e[0xA3] = "sterling"; e[0xA4] = "currency";
        e[0xA5] = "yen"; e[0xA6] = "brokenbar"; e[0xA7] = "section"; e[0xA8] = "dieresis";
        e[0xA9] = "copyright"; e[0xAA] = "ordfeminine"; e[0xAB] = "guillemotleft";
        e[0xAC] = "logicalnot"; e[0xAD] = "hyphen"; e[0xAE] = "registered"; e[0xAF] = "macron";
        e[0xB0] = "degree"; e[0xB1] = "plusminus"; e[0xB2] = "twosuperior"; e[0xB3] = "threesuperior";
        e[0xB4] = "acute"; e[0xB5] = "mu"; e[0xB6] = "paragraph"; e[0xB7] = "periodcentered";
        e[0xB8] = "cedilla"; e[0xB9] = "onesuperior"; e[0xBA] = "ordmasculine";
        e[0xBB] = "guillemotright"; e[0xBC] = "onequarter"; e[0xBD] = "onehalf";
        e[0xBE] = "threequarters"; e[0xBF] = "questiondown"; e[0xC0] = "Agrave"; e[0xC1] = "Aacute";
        e[0xC2] = "Acircumflex"; e[0xC3] = "Atilde"; e[0xC4] = "Adieresis"; e[0xC5] = "Aring";
        e[0xC6] = "AE"; e[0xC7] = "Ccedilla"; e[0xC8] = "Egrave"; e[0xC9] = "Eacute";
        e[0xCA] = "Ecircumflex"; e[0xCB] = "Edieresis"; e[0xCC] = "Igrave"; e[0xCD] = "Iacute";
        e[0xCE] = "Icircumflex"; e[0xCF] = "Idieresis"; e[0xD0] = "Eth"; e[0xD1] = "Ntilde";
        e[0xD2] = "Ograve"; e[0xD3] = "Oacute"; e[0xD4] = "Ocircumflex"; e[0xD5] = "Otilde";
        e[0xD6] = "Odieresis"; e[0xD7] = "multiply"; e[0xD8] = "Oslash"; e[0xD9] = "Ugrave";
        e[0xDA] = "Uacute"; e[0xDB] = "Ucircumflex"; e[0xDC] = "Udieresis"; e[0xDD] = "Yacute";
        e[0xDE] = "Thorn"; e[0xDF] = "germandbls"; e[0xE0] = "agrave"; e[0xE1] = "aacute";
        e[0xE2] = "acircumflex"; e[0xE3] = "atilde"; e[0xE4] = "adieresis"; e[0xE5] = "aring";
        e[0xE6] = "ae"; e[0xE7] = "ccedilla"; e[0xE8] = "egrave"; e[0xE9] = "eacute";
        e[0xEA] = "ecircumflex"; e[0xEB] = "edieresis"; e[0xEC] = "igrave"; e[0xED] = "iacute";
        e[0xEE] = "icircumflex"; e[0xEF] = "idieresis"; e[0xF0] = "eth"; e[0xF1] = "ntilde";
        e[0xF2] = "ograve"; e[0xF3] = "oacute"; e[0xF4] = "ocircumflex"; e[0xF5] = "otilde";
        e[0xF6] = "odieresis"; e[0xF7] = "divide"; e[0xF8] = "oslash"; e[0xF9] = "ugrave";
        e[0xFA] = "uacute"; e[0xFB] = "ucircumflex"; e[0xFC] = "udieresis"; e[0xFD] = "yacute";
        e[0xFE] = "thorn"; e[0xFF] = "ydieresis";
        return e;
    }

    private static string[] BuildMacRoman()
    {
        var e = NewLatinBase();
        e[0x80] = "Adieresis"; e[0x81] = "Aring"; e[0x82] = "Ccedilla"; e[0x83] = "Eacute";
        e[0x84] = "Ntilde"; e[0x85] = "Odieresis"; e[0x86] = "Udieresis"; e[0x87] = "aacute";
        e[0x88] = "agrave"; e[0x89] = "acircumflex"; e[0x8A] = "adieresis"; e[0x8B] = "atilde";
        e[0x8C] = "aring"; e[0x8D] = "ccedilla"; e[0x8E] = "eacute"; e[0x8F] = "egrave";
        e[0x90] = "ecircumflex"; e[0x91] = "edieresis"; e[0x92] = "iacute"; e[0x93] = "igrave";
        e[0x94] = "icircumflex"; e[0x95] = "idieresis"; e[0x96] = "ntilde"; e[0x97] = "oacute";
        e[0x98] = "ograve"; e[0x99] = "ocircumflex"; e[0x9A] = "odieresis"; e[0x9B] = "otilde";
        e[0x9C] = "uacute"; e[0x9D] = "ugrave"; e[0x9E] = "ucircumflex"; e[0x9F] = "udieresis";
        e[0xA0] = "dagger"; e[0xA1] = "degree"; e[0xA2] = "cent"; e[0xA3] = "sterling";
        e[0xA4] = "section"; e[0xA5] = "bullet"; e[0xA6] = "paragraph"; e[0xA7] = "germandbls";
        e[0xA8] = "registered"; e[0xA9] = "copyright"; e[0xAA] = "trademark"; e[0xAB] = "acute";
        e[0xAC] = "dieresis"; e[0xAD] = "notequal"; e[0xAE] = "AE"; e[0xAF] = "Oslash";
        e[0xB0] = "infinity"; e[0xB1] = "plusminus"; e[0xB2] = "lessequal"; e[0xB3] = "greaterequal";
        e[0xB4] = "yen"; e[0xB5] = "mu"; e[0xB6] = "partialdiff"; e[0xB7] = "summation";
        e[0xB8] = "product"; e[0xB9] = "pi"; e[0xBA] = "integral"; e[0xBB] = "ordfeminine";
        e[0xBC] = "ordmasculine"; e[0xBD] = "Omega"; e[0xBE] = "ae"; e[0xBF] = "oslash";
        e[0xC0] = "questiondown"; e[0xC1] = "exclamdown"; e[0xC2] = "logicalnot"; e[0xC3] = "radical";
        e[0xC4] = "florin"; e[0xC5] = "approxequal"; e[0xC6] = "Delta"; e[0xC7] = "guillemotleft";
        e[0xC8] = "guillemotright"; e[0xC9] = "ellipsis"; e[0xCA] = "space"; e[0xCB] = "Agrave";
        e[0xCC] = "Atilde"; e[0xCD] = "Otilde"; e[0xCE] = "OE"; e[0xCF] = "oe"; e[0xD0] = "endash";
        e[0xD1] = "emdash"; e[0xD2] = "quotedblleft"; e[0xD3] = "quotedblright"; e[0xD4] = "quoteleft";
        e[0xD5] = "quoteright"; e[0xD6] = "divide"; e[0xD7] = "lozenge"; e[0xD8] = "ydieresis";
        e[0xD9] = "Ydieresis"; e[0xDA] = "fraction"; e[0xDB] = "currency"; e[0xDC] = "guilsinglleft";
        e[0xDD] = "guilsinglright"; e[0xDE] = "fi"; e[0xDF] = "fl"; e[0xE0] = "daggerdbl";
        e[0xE1] = "periodcentered"; e[0xE2] = "quotesinglbase"; e[0xE3] = "quotedblbase";
        e[0xE4] = "perthousand"; e[0xE5] = "Acircumflex"; e[0xE6] = "Ecircumflex"; e[0xE7] = "Aacute";
        e[0xE8] = "Edieresis"; e[0xE9] = "Egrave"; e[0xEA] = "Iacute"; e[0xEB] = "Icircumflex";
        e[0xEC] = "Idieresis"; e[0xED] = "Igrave"; e[0xEE] = "Oacute"; e[0xEF] = "Ocircumflex";
        e[0xF1] = "Ograve"; e[0xF2] = "Uacute"; e[0xF3] = "Ucircumflex"; e[0xF4] = "Ugrave";
        e[0xF5] = "dotlessi"; e[0xF6] = "circumflex"; e[0xF7] = "tilde"; e[0xF8] = "macron";
        e[0xF9] = "breve"; e[0xFA] = "dotaccent"; e[0xFB] = "ring"; e[0xFC] = "cedilla";
        e[0xFD] = "hungarumlaut"; e[0xFE] = "ogonek"; e[0xFF] = "caron";
        return e;
    }
}
