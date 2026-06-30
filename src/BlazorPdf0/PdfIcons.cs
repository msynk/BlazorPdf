using Microsoft.AspNetCore.Components;

namespace BlazorPdf;

/// <summary>
/// Inline SVG icons used by the toolbar. Kept as markup so the package ships
/// without any external icon font or image dependency.
/// </summary>
internal static class PdfIcons
{
    private static MarkupString Svg(string path) => new(
        $"<svg class=\"nbp-icon\" viewBox=\"0 0 24 24\" width=\"18\" height=\"18\" " +
        $"fill=\"none\" stroke=\"currentColor\" stroke-width=\"2\" " +
        $"stroke-linecap=\"round\" stroke-linejoin=\"round\" aria-hidden=\"true\">{path}</svg>");

    public static MarkupString ChevronLeft => Svg("<polyline points=\"15 18 9 12 15 6\"/>");

    public static MarkupString ChevronRight => Svg("<polyline points=\"9 18 15 12 9 6\"/>");

    public static MarkupString Download => Svg(
        "<path d=\"M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4\"/>" +
        "<polyline points=\"7 10 12 15 17 10\"/><line x1=\"12\" y1=\"15\" x2=\"12\" y2=\"3\"/>");

    public static MarkupString Print => Svg(
        "<polyline points=\"6 9 6 2 18 2 18 9\"/>" +
        "<path d=\"M6 18H4a2 2 0 0 1-2-2v-5a2 2 0 0 1 2-2h16a2 2 0 0 1 2 2v5a2 2 0 0 1-2 2h-2\"/>" +
        "<rect x=\"6\" y=\"14\" width=\"12\" height=\"8\"/>");

    public static MarkupString NewTab => Svg(
        "<path d=\"M18 13v6a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V8a2 2 0 0 1 2-2h6\"/>" +
        "<polyline points=\"15 3 21 3 21 9\"/><line x1=\"10\" y1=\"14\" x2=\"21\" y2=\"3\"/>");

    public static MarkupString Fullscreen => Svg(
        "<path d=\"M8 3H5a2 2 0 0 0-2 2v3m18 0V5a2 2 0 0 0-2-2h-3m0 18h3a2 2 0 0 0 2-2v-3M3 16v3a2 2 0 0 0 2 2h3\"/>");
}
