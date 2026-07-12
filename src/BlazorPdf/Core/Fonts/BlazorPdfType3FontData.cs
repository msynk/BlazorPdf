// The font model: the parts needed to position and extract text.


namespace BlazorPdf;

/// <summary>
/// The data needed to draw a Type3 font's glyphs: the font matrix (glyph space
/// to text space), the <c>/CharProcs</c> content streams, the glyph resources,
/// and the code-to-glyph-name encoding. Type3 glyphs are rendered by executing
/// each glyph's content stream, unlike other fonts whose glyphs are drawn from
/// outlines or substituted.
/// </summary>
public sealed class BlazorPdfType3FontData
{
    private readonly BlazorPdfDict? _charProcs;
    private readonly string[]? _encoding;
    private readonly IBlazorPdfXRef _xref;

    /// <summary>The font matrix mapping glyph space to text space.</summary>
    public BlazorPdfMatrix FontMatrix { get; }

    /// <summary>The glyph resource dictionary, if the font supplies one.</summary>
    public BlazorPdfDict? Resources { get; }

    internal BlazorPdfType3FontData(BlazorPdfMatrix fontMatrix, BlazorPdfDict? charProcs, BlazorPdfDict? resources,
        string[]? encoding, IBlazorPdfXRef xref)
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
    public BlazorPdfStream? GetGlyphProcedure(int code)
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
        return _xref.FetchIfRef(_charProcs.Get(name)) as BlazorPdfStream;
    }
}
