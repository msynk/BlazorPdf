// Canvas rendering: the display-list front end over the shared content engine.

namespace BlazorPdf.Core.Render;

/// <summary>The output of a <see cref="CanvasRenderer"/> page render.</summary>
/// <param name="TextLayerHtml">
/// The page's DOM part: the positioned page <c>div</c> containing the
/// <c>&lt;canvas&gt;</c> placeholder, the selectable text layer, and link/
/// annotation overlays — everything except the painted content.
/// </param>
/// <param name="OpsJson">
/// The display list to replay onto the canvas (JSON array of drawing ops),
/// consumed by the viewer's JavaScript interpreter.
/// </param>
/// <param name="Width">Page width in CSS pixels (device space).</param>
/// <param name="Height">Page height in CSS pixels (device space).</param>
public sealed record CanvasRenderResult(string TextLayerHtml, string OpsJson, double Width, double Height);

/// <summary>
/// Renders a page for canvas display: the same engine as <see cref="HtmlRenderer"/>
/// (one content walk, identical state interpretation), but painted output is
/// emitted as a compact display list instead of DOM, while the selectable text
/// layer and link overlays remain HTML. Replaying the ops onto a
/// <c>&lt;canvas&gt;</c> is the viewer's JavaScript side
/// (<c>paintCanvasPages</c> in <c>blazor-pdf.js</c>).
/// </summary>
public sealed class CanvasRenderer
{
    private readonly HtmlRenderer _inner;
    private readonly PdfPage _page;

    public CanvasRenderer(PdfPage page, IXRef xref, PdfFontStore? fontStore = null, int rotationOffset = 0)
    {
        _page = page;
        _inner = new HtmlRenderer(page, xref, fontStore, rotationOffset) { EmitCanvasOps = true };
    }

    /// <summary>See <see cref="HtmlRenderer.DestinationResolver"/>.</summary>
    public Func<object?, int?>? DestinationResolver
    {
        get => _inner.DestinationResolver;
        set => _inner.DestinationResolver = value;
    }

    /// <summary>Renders the page into a DOM shell plus a canvas display list.</summary>
    public CanvasRenderResult Render()
    {
        string html = _inner.Render();
        return new CanvasRenderResult(html, _inner.CanvasOpsJson ?? "[]", _inner.ViewWidth, _inner.ViewHeight);
    }
}
