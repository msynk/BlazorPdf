using BlazorPdf.Engine.Rendering;

namespace BlazorPdf.Engine.Svg;

/// <summary>Graphics state for the SVG backend, cloned on the q/Q stack.</summary>
internal sealed class SvgGraphicsState
{
    public PdfMatrix Ctm = PdfMatrix.Identity;
    public string FillColor = "#000000";
    public string StrokeColor = "#000000";
    public double LineWidth = 1.0;
    public double FillAlpha = 1.0;
    public double StrokeAlpha = 1.0;
    public int FillComponents = 1;
    public int StrokeComponents = 1;

    public string? ClipId;

    public PdfDictionary? FillShading;
    public PdfMatrix FillShadingMatrix = PdfMatrix.Identity;

    // Text state.
    public double FontSize;
    public double CharSpacing;
    public double WordSpacing;
    public double HorizontalScale = 1.0;
    public double Leading;
    public double TextRise;
    public int TextRenderMode;

    public SvgGraphicsState Clone() => (SvgGraphicsState)MemberwiseClone();
}
