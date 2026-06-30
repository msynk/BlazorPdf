namespace BlazorPdf.Engine.Fonts;

/// <summary>
/// Advance-width metrics (in 1/1000 em) for the PDF standard-14 fonts. Used when a
/// simple font has no embedded program and no <c>Widths</c> array, so text laid out
/// with the built-in vector glyphs still gets proportional spacing instead of a flat
/// default. Helvetica metrics also serve as the generic proportional fallback; Courier
/// is treated as a 600-unit monospace; Times has its own roman/bold tables.
/// </summary>
internal static class Standard14Metrics
{
    private const double DefaultWidth = 500;
    private const double CourierWidth = 600;

    // AFM advance widths for the printable ASCII range (code 32..126).
    private static readonly short[] Helvetica =
    [
        278, 278, 355, 556, 556, 889, 667, 191, 333, 333, 389, 584, 278, 333, 278, 278, // 32..47
        556, 556, 556, 556, 556, 556, 556, 556, 556, 556, 278, 278, 584, 584, 584, 556, // 48..63
        1015, 667, 667, 722, 722, 667, 611, 778, 722, 278, 500, 667, 556, 833, 722, 778, // 64..79
        667, 778, 722, 667, 611, 722, 667, 944, 667, 667, 611, 278, 278, 278, 469, 556, // 80..95
        333, 556, 556, 500, 556, 556, 278, 556, 556, 222, 222, 500, 222, 833, 556, 556, // 96..111
        556, 556, 333, 500, 278, 556, 500, 722, 500, 500, 500, 334, 260, 334, 584,       // 112..126
    ];

    private static readonly short[] TimesRoman =
    [
        250, 333, 408, 500, 500, 833, 778, 180, 333, 333, 500, 564, 250, 333, 250, 278, // 32..47
        500, 500, 500, 500, 500, 500, 500, 500, 500, 500, 278, 278, 564, 564, 564, 444, // 48..63
        921, 722, 667, 667, 722, 611, 556, 722, 722, 333, 389, 722, 611, 889, 722, 722, // 64..79
        556, 722, 667, 556, 611, 722, 722, 944, 722, 722, 611, 333, 278, 333, 469, 500, // 80..95
        333, 444, 500, 444, 500, 444, 333, 500, 500, 278, 278, 500, 278, 778, 500, 500, // 96..111
        500, 500, 333, 389, 278, 500, 500, 722, 500, 500, 444, 480, 200, 480, 541,       // 112..126
    ];

    private static readonly short[] TimesBold =
    [
        250, 333, 555, 500, 500, 1000, 833, 278, 333, 333, 500, 570, 250, 333, 250, 278, // 32..47
        500, 500, 500, 500, 500, 500, 500, 500, 500, 500, 333, 333, 570, 570, 570, 500,  // 48..63
        930, 722, 667, 722, 722, 667, 611, 778, 778, 389, 500, 778, 667, 944, 722, 778,  // 64..79
        611, 778, 722, 556, 667, 722, 722, 1000, 722, 722, 667, 333, 278, 333, 581, 500, // 80..95
        333, 500, 556, 444, 556, 444, 333, 500, 556, 278, 333, 556, 278, 833, 556, 500,  // 96..111
        556, 556, 444, 389, 333, 556, 500, 722, 500, 500, 444, 394, 220, 394, 520,        // 112..126
    ];

    /// <summary>Removes a font subset prefix such as "ABCDEF+" from a base-font name.</summary>
    private static string Normalize(string baseFont)
    {
        if (string.IsNullOrEmpty(baseFont)) return "";
        var plus = baseFont.IndexOf('+');
        return plus == 6 ? baseFont[(plus + 1)..] : baseFont;
    }

    /// <summary>True when the base-font name is one of the standard-14 families.</summary>
    public static bool IsStandardFont(string baseFont)
    {
        var bf = Normalize(baseFont);
        if (bf.Length == 0) return false;
        return bf.Contains("Helvetica", StringComparison.OrdinalIgnoreCase)
            || bf.Contains("Arial", StringComparison.OrdinalIgnoreCase)
            || bf.Contains("Times", StringComparison.OrdinalIgnoreCase)
            || bf.Contains("Courier", StringComparison.OrdinalIgnoreCase)
            || bf.Contains("Symbol", StringComparison.OrdinalIgnoreCase)
            || bf.Contains("ZapfDingbats", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True when the base font is monospaced (Courier family).</summary>
    public static bool IsMonospace(string baseFont)
    {
        var bf = Normalize(baseFont);
        return bf.Contains("Courier", StringComparison.OrdinalIgnoreCase)
            || bf.Contains("Mono", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Advance width (1/1000 em) for a unicode character in the given family.</summary>
    public static double Width(string baseFont, int unicode)
    {
        var bf = Normalize(baseFont);
        if (IsMonospace(bf)) return unicode == 0 ? 0 : CourierWidth;
        var table = TableFor(bf);
        return unicode is >= 32 and <= 126 ? table[unicode - 32] : DefaultWidth;
    }

    /// <summary>
    /// Advance width (1/1000 em) for a single-byte WinAnsi code in the given family, or
    /// -1 when the code is outside the supported printable range (let the caller fall
    /// back to its default width).
    /// </summary>
    public static double GetWidth(string baseFont, int code)
    {
        var unicode = WinAnsiEncoding.ToUnicode(code);
        if (unicode is < 32 or > 126) return -1;
        return Width(baseFont, unicode);
    }

    /// <summary>Helvetica advance width (1/1000 em) for a unicode character.</summary>
    public static double HelveticaWidth(int unicode) =>
        unicode is >= 32 and <= 126 ? Helvetica[unicode - 32] : DefaultWidth;

    private static short[] TableFor(string baseFont)
    {
        if (baseFont.Contains("Times", StringComparison.OrdinalIgnoreCase))
        {
            return baseFont.Contains("Bold", StringComparison.OrdinalIgnoreCase) ? TimesBold : TimesRoman;
        }
        return Helvetica;
    }
}
