using BlazorPdf.Engine;
using BlazorPdf.Engine.Svg;
using Microsoft.AspNetCore.Components;

namespace BlazorPdf;

/// <summary>
/// A PDF viewer that renders pages as <b>SVG</b> (standard DOM elements) rather than a
/// canvas raster. Text is selectable, output is resolution-independent, and zooming is
/// pure vector scaling — no JavaScript interop is required.
/// </summary>
public partial class PdfSvgViewer
{
    private PdfDocument? _document;
    private byte[]? _loadedBytes;
    private MarkupString _svg;
    private int _pageIndex;
    private double _zoom = 1.0;
    private double _pageWidth = 612;
    private double _pageHeight = 792;
    private string? _error;

    /// <summary>The PDF document bytes to render.</summary>
    [Parameter] public byte[]? Data { get; set; }

    /// <summary>A byte-based <see cref="PdfSource"/> to render. Takes precedence over <see cref="Data"/>.</summary>
    [Parameter] public PdfSource? Source { get; set; }

    /// <summary>Initial zoom factor (1.0 = 100%). Default 1.0.</summary>
    [Parameter] public double Zoom { get; set; } = 1.0;

    /// <summary>Initial page (1-based). Default 1.</summary>
    [Parameter] public int InitialPage { get; set; } = 1;

    /// <summary>Shows the navigation/zoom toolbar. Default true.</summary>
    [Parameter] public bool ShowToolbar { get; set; } = true;

    /// <summary>CSS height of the scroll area. Default 720px.</summary>
    [Parameter] public string Height { get; set; } = "720px";

    /// <summary>Extra CSS class on the root element.</summary>
    [Parameter] public string? CssClass { get; set; }

    /// <summary>Raised after a document is parsed, with the page count.</summary>
    [Parameter] public EventCallback<int> OnDocumentParsed { get; set; }

    /// <summary>The current 1-based page number.</summary>
    public int CurrentPage => _pageIndex + 1;

    /// <summary>The number of pages in the loaded document.</summary>
    public int PageCount => _document?.PageCount ?? 0;

    private byte[]? ResolveBytes() =>
        Source is { Kind: PdfSourceKind.Bytes, Bytes: { } b } ? b : Data;

    /// <inheritdoc />
    protected override void OnParametersSet()
    {
        _zoom = Zoom <= 0 ? 1.0 : Zoom;
        var bytes = ResolveBytes();
        if (!ReferenceEquals(bytes, _loadedBytes))
        {
            _loadedBytes = bytes;
            ParseAndRender();
        }
    }

    private void ParseAndRender()
    {
        _error = null;
        _document = null;
        _pageIndex = Math.Max(0, InitialPage - 1);

        if (_loadedBytes is null || _loadedBytes.Length == 0)
        {
            _svg = default;
            return;
        }

        try
        {
            _document = PdfDocument.Load(_loadedBytes);
            if (_pageIndex >= _document.PageCount) _pageIndex = 0;
            _ = OnDocumentParsed.InvokeAsync(_document.PageCount);
            RenderCurrent();
        }
        catch (Exception ex)
        {
            _error = ex.Message;
        }
    }

    private void RenderCurrent()
    {
        if (_document is null || _document.PageCount == 0) return;
        try
        {
            var page = _document.Pages[_pageIndex];
            _pageWidth = page.Width;
            _pageHeight = page.Height;
            _svg = new MarkupString(SvgRenderer.Render(page));
        }
        catch (Exception ex)
        {
            _error = ex.Message;
            _svg = default;
        }
    }

    private void GoTo(int pageIndex)
    {
        if (_document is null) return;
        _pageIndex = Math.Clamp(pageIndex, 0, _document.PageCount - 1);
        RenderCurrent();
    }

    /// <summary>Navigates to a 1-based page number.</summary>
    public void GoToPage(int page) => GoTo(page - 1);

    private void Next() => GoTo(_pageIndex + 1);
    private void Previous() => GoTo(_pageIndex - 1);

    // Zoom only resizes the container; the SVG scales as vectors (no re-render).
    private void ZoomIn() => _zoom = Math.Min(8.0, _zoom * 1.25);
    private void ZoomOut() => _zoom = Math.Max(0.1, _zoom / 1.25);
}
