// Glyph-name to Unicode resolution, a focused port of the Adobe Glyph List
// logic used by pdf.js `src/core/glyphlist.js` and the name-parsing rules in
// `src/core/fonts_utils.js`. Covers the glyph names referenced by the standard
// Latin encodings plus the algorithmic "uniXXXX"/"uXXXXXX" forms. See NOTICE.

using System.Globalization;

namespace BlazorPdf.Core.Fonts;

/// <summary>
/// Maps PostScript glyph names (e.g. "A", "quoteright", "eacute") to their
/// Unicode string. Falls back to the algorithmic Adobe rules for "uniXXXX",
/// "uXXXXXX" and single-character names.
/// </summary>
internal static class GlyphList
{
    /// <summary>
    /// Resolves a glyph name to a Unicode string, or <c>string.Empty</c> when
    /// it cannot be mapped.
    /// </summary>
    public static string ToUnicode(string? name)
    {
        if (string.IsNullOrEmpty(name) || name == ".notdef")
        {
            return string.Empty;
        }

        // Strip a trailing ".variant" suffix (e.g. "a.sc", "f_f.alt").
        int dot = name.IndexOf('.');
        if (dot > 0)
        {
            name = name[..dot];
        }

        // A ligature/composite name is a sequence joined by "_".
        if (name.IndexOf('_') >= 0)
        {
            var parts = name.Split('_');
            var sb = new System.Text.StringBuilder();
            foreach (var part in parts)
            {
                sb.Append(ToUnicode(part));
            }
            return sb.ToString();
        }

        if (Map.TryGetValue(name, out string? mapped))
        {
            return mapped;
        }

        // Single ASCII letters are their own glyph names in the Adobe Glyph List
        // (e.g. "A" -> U+0041); without this a /Differences remap to a letter
        // would be lost and fall back to the wrong base-encoding character.
        if (name.Length == 1)
        {
            char ch = name[0];
            if (ch is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z'))
            {
                return name;
            }
        }

        // "uniXXXX" — one or more 4-hex-digit UTF-16 code units.
        if (name.Length >= 7 && name.StartsWith("uni", StringComparison.Ordinal)
            && (name.Length - 3) % 4 == 0)
        {
            var sb = new System.Text.StringBuilder();
            bool ok = true;
            for (int i = 3; i + 4 <= name.Length; i += 4)
            {
                if (int.TryParse(name.AsSpan(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int cu))
                {
                    sb.Append((char)cu);
                }
                else
                {
                    ok = false;
                    break;
                }
            }
            if (ok)
            {
                return sb.ToString();
            }
        }

        // "uXXXX".."uXXXXXX" — a single code point of 4 to 6 hex digits.
        if (name.Length is >= 5 and <= 7 && name[0] == 'u'
            && int.TryParse(name.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int cp)
            && cp <= 0x10FFFF)
        {
            return char.ConvertFromUtf32(cp);
        }

        // "gXX" / "cidXX" / "indexXX" carry no Unicode meaning.
        return string.Empty;
    }

    // The subset of the Adobe Glyph List covering every name used by the
    // Standard, WinAnsi and MacRoman encodings (PDF 32000-1:2008 Annex D).
    private static readonly Dictionary<string, string> Map = new(StringComparer.Ordinal)
    {
        ["space"] = " ", ["exclam"] = "!", ["quotedbl"] = "\"", ["numbersign"] = "#",
        ["dollar"] = "$", ["percent"] = "%", ["ampersand"] = "&", ["quotesingle"] = "'",
        ["quoteright"] = "\u2019", ["parenleft"] = "(", ["parenright"] = ")", ["asterisk"] = "*",
        ["plus"] = "+", ["comma"] = ",", ["hyphen"] = "-", ["period"] = ".", ["slash"] = "/",
        ["zero"] = "0", ["one"] = "1", ["two"] = "2", ["three"] = "3", ["four"] = "4",
        ["five"] = "5", ["six"] = "6", ["seven"] = "7", ["eight"] = "8", ["nine"] = "9",
        ["colon"] = ":", ["semicolon"] = ";", ["less"] = "<", ["equal"] = "=", ["greater"] = ">",
        ["question"] = "?", ["at"] = "@",
        ["bracketleft"] = "[", ["backslash"] = "\\", ["bracketright"] = "]",
        ["asciicircum"] = "^", ["underscore"] = "_", ["grave"] = "`", ["quoteleft"] = "\u2018",
        ["braceleft"] = "{", ["bar"] = "|", ["braceright"] = "}", ["asciitilde"] = "~",
        ["exclamdown"] = "\u00A1", ["cent"] = "\u00A2", ["sterling"] = "\u00A3",
        ["fraction"] = "\u2044", ["yen"] = "\u00A5", ["florin"] = "\u0192",
        ["section"] = "\u00A7", ["currency"] = "\u00A4", ["quotedblleft"] = "\u201C",
        ["guillemotleft"] = "\u00AB", ["guilsinglleft"] = "\u2039", ["guilsinglright"] = "\u203A",
        ["fi"] = "fi", ["fl"] = "fl", ["endash"] = "\u2013", ["dagger"] = "\u2020",
        ["daggerdbl"] = "\u2021", ["periodcentered"] = "\u00B7", ["paragraph"] = "\u00B6",
        ["bullet"] = "\u2022", ["quotesinglbase"] = "\u201A", ["quotedblbase"] = "\u201E",
        ["quotedblright"] = "\u201D", ["guillemotright"] = "\u00BB", ["ellipsis"] = "\u2026",
        ["perthousand"] = "\u2030", ["questiondown"] = "\u00BF", ["acute"] = "\u00B4",
        ["circumflex"] = "\u02C6", ["tilde"] = "\u02DC", ["macron"] = "\u00AF",
        ["breve"] = "\u02D8", ["dotaccent"] = "\u02D9", ["dieresis"] = "\u00A8",
        ["ring"] = "\u02DA", ["cedilla"] = "\u00B8", ["hungarumlaut"] = "\u02DD",
        ["ogonek"] = "\u02DB", ["caron"] = "\u02C7", ["emdash"] = "\u2014",
        ["AE"] = "\u00C6", ["ordfeminine"] = "\u00AA", ["Lslash"] = "\u0141",
        ["Oslash"] = "\u00D8", ["OE"] = "\u0152", ["ordmasculine"] = "\u00BA",
        ["ae"] = "\u00E6", ["dotlessi"] = "\u0131", ["lslash"] = "\u0142",
        ["oslash"] = "\u00F8", ["oe"] = "\u0153", ["germandbls"] = "\u00DF",
        ["brokenbar"] = "\u00A6", ["copyright"] = "\u00A9", ["logicalnot"] = "\u00AC",
        ["registered"] = "\u00AE", ["degree"] = "\u00B0", ["plusminus"] = "\u00B1",
        ["twosuperior"] = "\u00B2", ["threesuperior"] = "\u00B3", ["mu"] = "\u00B5",
        ["onesuperior"] = "\u00B9", ["onequarter"] = "\u00BC", ["onehalf"] = "\u00BD",
        ["threequarters"] = "\u00BE", ["trademark"] = "\u2122", ["multiply"] = "\u00D7",
        ["divide"] = "\u00F7", ["Euro"] = "\u20AC", ["euro"] = "\u20AC",
        ["minus"] = "\u2212", ["partialdiff"] = "\u2202", ["infinity"] = "\u221E",
        ["lozenge"] = "\u25CA", ["notequal"] = "\u2260", ["lessequal"] = "\u2264",
        ["greaterequal"] = "\u2265", ["summation"] = "\u2211", ["product"] = "\u220F",
        ["pi"] = "\u03C0", ["integral"] = "\u222B", ["Omega"] = "\u03A9",
        ["radical"] = "\u221A", ["approxequal"] = "\u2248", ["Delta"] = "\u2206",
        // Accented Latin letters (WinAnsi / MacRoman high range).
        ["Agrave"] = "\u00C0", ["Aacute"] = "\u00C1", ["Acircumflex"] = "\u00C2",
        ["Atilde"] = "\u00C3", ["Adieresis"] = "\u00C4", ["Aring"] = "\u00C5",
        ["Ccedilla"] = "\u00C7", ["Egrave"] = "\u00C8", ["Eacute"] = "\u00C9",
        ["Ecircumflex"] = "\u00CA", ["Edieresis"] = "\u00CB", ["Igrave"] = "\u00CC",
        ["Iacute"] = "\u00CD", ["Icircumflex"] = "\u00CE", ["Idieresis"] = "\u00CF",
        ["Eth"] = "\u00D0", ["Ntilde"] = "\u00D1", ["Ograve"] = "\u00D2",
        ["Oacute"] = "\u00D3", ["Ocircumflex"] = "\u00D4", ["Otilde"] = "\u00D5",
        ["Odieresis"] = "\u00D6", ["Ugrave"] = "\u00D9", ["Uacute"] = "\u00DA",
        ["Ucircumflex"] = "\u00DB", ["Udieresis"] = "\u00DC", ["Yacute"] = "\u00DD",
        ["Thorn"] = "\u00DE", ["agrave"] = "\u00E0", ["aacute"] = "\u00E1",
        ["acircumflex"] = "\u00E2", ["atilde"] = "\u00E3", ["adieresis"] = "\u00E4",
        ["aring"] = "\u00E5", ["ccedilla"] = "\u00E7", ["egrave"] = "\u00E8",
        ["eacute"] = "\u00E9", ["ecircumflex"] = "\u00EA", ["edieresis"] = "\u00EB",
        ["igrave"] = "\u00EC", ["iacute"] = "\u00ED", ["icircumflex"] = "\u00EE",
        ["idieresis"] = "\u00EF", ["eth"] = "\u00F0", ["ntilde"] = "\u00F1",
        ["ograve"] = "\u00F2", ["oacute"] = "\u00F3", ["ocircumflex"] = "\u00F4",
        ["otilde"] = "\u00F5", ["odieresis"] = "\u00F6", ["ugrave"] = "\u00F9",
        ["uacute"] = "\u00FA", ["ucircumflex"] = "\u00FB", ["udieresis"] = "\u00FC",
        ["yacute"] = "\u00FD", ["thorn"] = "\u00FE", ["ydieresis"] = "\u00FF",
        ["Scaron"] = "\u0160", ["scaron"] = "\u0161", ["Zcaron"] = "\u017D",
        ["zcaron"] = "\u017E", ["Ydieresis"] = "\u0178",
    };
}
