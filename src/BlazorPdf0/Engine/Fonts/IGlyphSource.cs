namespace BlazorPdf.Engine.Fonts;

/// <summary>
/// A source of glyph outlines and metrics, implemented by the TrueType and CFF font
/// parsers. Outlines are returned as flattened contours in 1/1000 em units (y up).
/// </summary>
internal interface IGlyphSource
{
    /// <summary>Flattened contours for a glyph id, or null when empty/missing.</summary>
    List<List<(double X, double Y)>>? GetGlyphContours(int gid);

    /// <summary>Advance width in 1/1000 em, or -1 when unknown.</summary>
    double GetAdvance1000(int gid);
}
