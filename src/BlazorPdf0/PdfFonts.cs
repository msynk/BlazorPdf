using BlazorPdf.Engine.Fonts;

namespace BlazorPdf;

/// <summary>
/// Configures the fallback font used by the native C# renderer for PDF text that has no
/// embedded font program (the standard-14 families such as Helvetica). By default the
/// renderer auto-discovers a common sans-serif system font at runtime when the platform
/// allows file access (Blazor Server, desktop). On platforms without file access (Blazor
/// WebAssembly) register a TrueType font explicitly so text renders as real filled
/// outlines instead of the built-in monoline approximation.
/// </summary>
public static class PdfFonts
{
    /// <summary>
    /// Registers TrueType font bytes used to draw non-embedded text. Provide a regular
    /// weight and, optionally, a bold weight. Overrides system auto-discovery.
    /// </summary>
    /// <param name="regular">TrueType (.ttf) bytes for the regular weight.</param>
    /// <param name="bold">Optional TrueType (.ttf) bytes for the bold weight.</param>
    public static void RegisterFallbackFont(byte[] regular, byte[]? bold = null)
    {
        ArgumentNullException.ThrowIfNull(regular);
        FallbackFont.Register(regular, bold);
    }
}
