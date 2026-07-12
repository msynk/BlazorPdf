// Canvas rendering: the display-list front end over the shared content engine.

namespace BlazorPdf;

/// <summary>
/// Renders a page for canvas display: the same engine as <see cref="BlazorPdfHtmlRenderer"/>
/// (one content walk, identical state interpretation), but painted output is
/// emitted as a compact display list instead of DOM, while the selectable text
/// layer and link overlays remain HTML. Replaying the ops onto a
/// <c>&lt;canvas&gt;</c> is the viewer's JavaScript side
/// (<c>paintCanvasPages</c> in <c>blazor-pdf.js</c>).
/// </summary>
public sealed class BlazorPdfCanvasRenderer
{
    private readonly BlazorPdfHtmlRenderer _inner;
    private readonly BlazorPdfPage _page;

    public BlazorPdfCanvasRenderer(BlazorPdfPage page, IBlazorPdfXRef xref, BlazorPdfFontStore? fontStore = null, int rotationOffset = 0)
    {
        _page = page;
        _inner = new BlazorPdfHtmlRenderer(page, xref, fontStore, rotationOffset) { EmitCanvasOps = true };
    }

    /// <summary>See <see cref="BlazorPdfHtmlRenderer.DestinationResolver"/>.</summary>
    public Func<object?, int?>? DestinationResolver
    {
        get => _inner.DestinationResolver;
        set => _inner.DestinationResolver = value;
    }

    /// <summary>Renders the page into a DOM shell plus a canvas display list.</summary>
    public BlazorPdfCanvasRenderResult Render()
    {
        string html = _inner.Render();
        return new BlazorPdfCanvasRenderResult(html, _inner.CanvasOpsJson ?? "[]", _inner.ViewWidth, _inner.ViewHeight);
    }
}
