namespace BlazorPdf.Engine.Rendering;

/// <summary>Mutable graphics state, cloned on the <c>q</c>/<c>Q</c> stack.</summary>
internal sealed class GraphicsState
{
    public PdfMatrix Ctm = PdfMatrix.Identity;
    public PdfColor FillColor = PdfColor.Black;
    public PdfColor StrokeColor = PdfColor.Black;
    public double LineWidth = 1.0;
    public int FillComponents = 1;
    public int StrokeComponents = 1;

    /// <summary>Constant fill alpha (ExtGState <c>ca</c>).</summary>
    public double FillAlpha = 1.0;

    /// <summary>Constant stroke alpha (ExtGState <c>CA</c>).</summary>
    public double StrokeAlpha = 1.0;

    /// <summary>Active separable blend mode (ExtGState <c>BM</c>).</summary>
    public BlendMode Blend = BlendMode.Normal;

    /// <summary>Active clip coverage mask (length Width*Height, 0..255), or null for none.</summary>
    public byte[]? Clip;

    /// <summary>When set, fills use this shading instead of <see cref="FillColor"/>.</summary>
    public PdfDictionary? FillShading;
    public PdfMatrix FillShadingMatrix = PdfMatrix.Identity;

    /// <summary>When true, fills use a tiling pattern (approximated by a neutral gray).</summary>
    public bool FillIsTilingPattern;

    // Text state.
    public double FontSize;
    public double CharSpacing;
    public double WordSpacing;
    public double HorizontalScale = 1.0;
    public double Leading;
    public double TextRise;
    public int TextRenderMode;

    public GraphicsState Clone() => (GraphicsState)MemberwiseClone();
}
