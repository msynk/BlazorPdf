using BlazorPdf.Core;
using BlazorPdf.Core.Render;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace BlazorPdf;

/// <summary>
/// A pure-C# PDF viewer component with a full toolbar (page navigation, zoom,
/// fit modes, rotation, download, fullscreen and an optional thumbnail sidebar).
/// The rendering pipeline is a clean-room port of pdf.js that emits plain SVG
/// DOM per page (vector graphics as &lt;path&gt;, selectable text as &lt;text&gt;).
/// </summary>
public partial class BlazorPdfViewer : ComponentBase, IAsyncDisposable
{
    private PdfSource? _source;
    private PdfDocument? _document;
    private string _status = "Idle.";

    private readonly List<MarkupString> _pages = new();
    private readonly List<double> _pageWidths = new();  // points, display orientation
    private readonly List<double> _pageHeights = new();

    private int _currentPage = 1;
    private double _zoom = 1.0;
    private PdfZoomMode _zoomMode = PdfZoomMode.FitWidth;
    private int _rotation;
    private bool _showThumbnails;

    private IJSObjectReference? _module;
    private DotNetObjectReference<BlazorPdfViewer>? _dotNetRef;
    private ElementReference _containerRef;
    private ElementReference _viewerRef;
    private bool _spyPending;

    /// <summary>The document to display.</summary>
    [Parameter] public PdfSource? Source { get; set; }

    /// <summary>CSS height of the viewer container.</summary>
    [Parameter] public string Height { get; set; } = "780px";

    /// <summary>Whether the toolbar is shown.</summary>
    [Parameter] public bool ShowToolbar { get; set; } = true;

    /// <summary>The initial zoom behavior.</summary>
    [Parameter] public PdfZoomMode InitialZoomMode { get; set; } = PdfZoomMode.FitWidth;

    /// <summary>Raised when a document has finished loading.</summary>
    [Parameter] public EventCallback OnDocumentLoaded { get; set; }

    /// <summary>Raised when loading or rendering fails, with the error message.</summary>
    [Parameter] public EventCallback<string> OnError { get; set; }

    /// <summary>The number of pages currently rendered.</summary>
    public int PageCount => _pages.Count;

    /// <summary>The currently focused page (1-based).</summary>
    public int CurrentPage => _currentPage;

    protected override void OnInitialized() => _zoomMode = InitialZoomMode;

    protected override async Task OnParametersSetAsync()
    {
        if (ReferenceEquals(_source, Source))
        {
            return;
        }
        _source = Source;
        _rotation = 0;
        _currentPage = 1;
        await LoadAsync();
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            _module = await JS.InvokeAsync<IJSObjectReference>(
                "import", "./_content/BlazorPdf/blazorPdfViewer.js");
            _dotNetRef = DotNetObjectReference.Create(this);
        }

        if (_spyPending && _module is not null && _dotNetRef is not null)
        {
            _spyPending = false;
            await _module.InvokeVoidAsync("registerScrollSpy", _containerRef, _dotNetRef);
            await ApplyFitAsync();
        }
    }

    [Inject] private IJSRuntime JS { get; set; } = default!;

    private async Task LoadAsync()
    {
        _pages.Clear();
        _pageWidths.Clear();
        _pageHeights.Clear();
        _document = null;

        if (_source is null)
        {
            _status = "No document loaded.";
            return;
        }
        if (!_source.IsBytes || _source.Bytes is null)
        {
            _status = "This build renders in-memory byte sources only.";
            await OnError.InvokeAsync(_status);
            return;
        }

        try
        {
            _document = PdfDocument.Load(_source.Bytes);
            RenderPages();
            _status = $"{_document.PageCount} page(s).";
            _spyPending = true;
            await OnDocumentLoaded.InvokeAsync();
        }
        catch (Exception ex)
        {
            _status = $"Error: {ex.Message}";
            await OnError.InvokeAsync(ex.Message);
        }
    }

    private void RenderPages()
    {
        _pages.Clear();
        _pageWidths.Clear();
        _pageHeights.Clear();
        if (_document is null)
        {
            return;
        }

        bool swap = _rotation % 180 == 90;
        foreach (var page in _document.Pages)
        {
            string svg = new SvgRenderer(page, _document.XRef, _rotation).Render();
            _pages.Add(new MarkupString(svg));
            _pageWidths.Add(swap ? page.Height : page.Width);
            _pageHeights.Add(swap ? page.Width : page.Height);
        }
    }

    // ----- Navigation -----

    private Task NextPage() => GoToPage(_currentPage + 1);

    private Task PrevPage() => GoToPage(_currentPage - 1);

    private async Task GoToPage(int pageNumber)
    {
        if (_pages.Count == 0)
        {
            return;
        }
        _currentPage = Math.Clamp(pageNumber, 1, _pages.Count);
        if (_module is not null)
        {
            await _module.InvokeVoidAsync("scrollToPage", _containerRef, _currentPage);
        }
    }

    private async Task OnPageInput(ChangeEventArgs e)
    {
        if (int.TryParse(e.Value?.ToString(), out int n))
        {
            await GoToPage(n);
        }
    }

    /// <summary>Invoked from JavaScript when the most-visible page changes.</summary>
    [JSInvokable]
    public void OnPageVisible(int pageNumber)
    {
        if (pageNumber != _currentPage && pageNumber >= 1 && pageNumber <= _pages.Count)
        {
            _currentPage = pageNumber;
            StateHasChanged();
        }
    }

    /// <summary>Invoked from JavaScript when the viewport size changes.</summary>
    [JSInvokable]
    public async Task OnViewportResized()
    {
        if (_zoomMode != PdfZoomMode.Custom)
        {
            await ApplyFitAsync();
            StateHasChanged();
        }
    }

    // ----- Zoom -----

    private async Task ZoomIn() => await SetCustomZoom(_zoom * 1.2);

    private async Task ZoomOut() => await SetCustomZoom(_zoom / 1.2);

    private Task SetCustomZoom(double zoom)
    {
        _zoomMode = PdfZoomMode.Custom;
        _zoom = Math.Clamp(zoom, 0.1, 8.0);
        return Task.CompletedTask;
    }

    private async Task SetZoomMode(PdfZoomMode mode)
    {
        _zoomMode = mode;
        if (mode == PdfZoomMode.ActualSize)
        {
            _zoom = 1.0;
        }
        else
        {
            await ApplyFitAsync();
        }
    }

    private async Task ApplyFitAsync()
    {
        if (_module is null || _pages.Count == 0 || _zoomMode is PdfZoomMode.Custom or PdfZoomMode.ActualSize)
        {
            return;
        }

        var vp = await _module.InvokeAsync<Viewport>("getViewport", _containerRef);
        if (vp.Width <= 0)
        {
            return;
        }

        double maxW = _pageWidths.Count > 0 ? _pageWidths.Max() : 612;
        double maxH = _pageHeights.Count > 0 ? _pageHeights.Max() : 792;
        const double padding = 32; // surface padding + page margin

        double fitWidth = (vp.Width - padding) / maxW;
        _zoom = _zoomMode == PdfZoomMode.FitPage
            ? Math.Min(fitWidth, (vp.Height - padding) / maxH)
            : fitWidth;
        _zoom = Math.Clamp(_zoom, 0.1, 8.0);
    }

    // ----- Rotation, download, fullscreen, sidebar -----

    private async Task RotateClockwise()
    {
        _rotation = (_rotation + 90) % 360;
        RenderPages();
        _spyPending = true;
        await Task.CompletedTask;
    }

    private async Task DownloadAsync()
    {
        if (_module is null || _source?.Bytes is null)
        {
            return;
        }
        string base64 = Convert.ToBase64String(_source.Bytes);
        await _module.InvokeVoidAsync("download", _source.FileName ?? "document.pdf", base64);
    }

    private async Task ToggleFullscreen()
    {
        if (_module is not null)
        {
            await _module.InvokeVoidAsync("toggleFullscreen", _viewerRef);
        }
    }

    private void ToggleThumbnails() => _showThumbnails = !_showThumbnails;

    private string PageStyle(int index)
    {
        double w = (index < _pageWidths.Count ? _pageWidths[index] : 612) * _zoom;
        return string.Create(System.Globalization.CultureInfo.InvariantCulture, $"width:{w:0.#}px");
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_module is not null)
            {
                await _module.InvokeVoidAsync("disposeScrollSpy", _containerRef);
                await _module.DisposeAsync();
            }
        }
        catch (JSDisconnectedException)
        {
            // Circuit already gone; nothing to clean up.
        }
        _dotNetRef?.Dispose();
    }

    private readonly record struct Viewport(double Width, double Height);
}
