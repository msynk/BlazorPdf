using BlazorPdf.Core;
using BlazorPdf.Core.Render;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;

namespace BlazorPdf;

/// <summary>
/// A pure-C# PDF viewer component with a full toolbar (page navigation, zoom,
/// fit modes, rotation, download, fullscreen and an optional thumbnail sidebar).
/// The rendering pipeline emits plain HTML DOM per page (vector graphics as
/// &lt;div&gt; clip-paths, selectable text as &lt;span&gt;, rasters as &lt;img&gt;).
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
    private bool _showOutline;
    private IReadOnlyList<OutlineItem> _outline = Array.Empty<OutlineItem>();

    private bool _showSearch;
    private string _searchQuery = "";
    private int _searchTotal;
    private int _searchIndex = -1;

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
                "import", "./_content/BlazorPdf/blazor-pdf.js");
            _dotNetRef = DotNetObjectReference.Create(this);
        }

        if (_spyPending && _module is not null && _dotNetRef is not null)
        {
            _spyPending = false;
            await _module.InvokeVoidAsync("registerScrollSpy", _containerRef, _dotNetRef);
            await ApplyFitAsync();
            if (!string.IsNullOrEmpty(_searchQuery))
            {
                await RunSearchAsync();
            }
        }
    }

    [Inject] private IJSRuntime JS { get; set; } = default!;

    private async Task LoadAsync()
    {
        _pages.Clear();
        _pageWidths.Clear();
        _pageHeights.Clear();
        _document = null;
        _searchTotal = 0;
        _searchIndex = -1;
        _outline = Array.Empty<OutlineItem>();

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
            try
            {
                _outline = _document.Outline;
            }
            catch
            {
                _outline = Array.Empty<OutlineItem>();
            }
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
            string html = new Core.Render.HtmlRenderer(page, _document.XRef, _rotation).Render();
            _pages.Add(new MarkupString(html));
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

    private async Task PrintAsync()
    {
        if (_module is not null && _pages.Count > 0)
        {
            await _module.InvokeVoidAsync("printDocument", _containerRef);
        }
    }

    private void ToggleThumbnails()
    {
        _showThumbnails = !_showThumbnails;
        if (_showThumbnails)
        {
            _showOutline = false;
        }
    }

    private void ToggleOutline()
    {
        _showOutline = !_showOutline;
        if (_showOutline)
        {
            _showThumbnails = false;
        }
    }

    /// <summary>Whether the document exposes any bookmarks.</summary>
    public bool HasOutline => _outline.Count > 0;

    private async Task OnOutlineClick(OutlineItem item)
    {
        if (item.PageNumber is int pageNo)
        {
            await GoToPage(pageNo);
        }
    }

    // ----- Search -----

    private string SearchLabel => _searchTotal switch
    {
        < 0 => "n/a",
        0 => string.IsNullOrEmpty(_searchQuery) ? "" : "0/0",
        _ => $"{_searchIndex + 1}/{_searchTotal}",
    };

    private async Task ToggleSearch()
    {
        _showSearch = !_showSearch;
        if (!_showSearch)
        {
            _searchQuery = "";
            await ClearSearchAsync();
        }
    }

    private async Task OnSearchInput(ChangeEventArgs e)
    {
        _searchQuery = e.Value?.ToString() ?? "";
        await RunSearchAsync();
    }

    private async Task OnSearchKeyDown(KeyboardEventArgs e)
    {
        if (e.Key == "Enter")
        {
            if (_searchTotal > 0)
            {
                await (e.ShiftKey ? SearchPrev() : SearchNext());
            }
        }
        else if (e.Key == "Escape")
        {
            await ToggleSearch();
        }
    }

    private async Task RunSearchAsync()
    {
        if (_module is null)
        {
            return;
        }
        if (string.IsNullOrEmpty(_searchQuery))
        {
            await ClearSearchAsync();
            return;
        }

        _searchTotal = await _module.InvokeAsync<int>("searchAll", _containerRef, _searchQuery);
        _searchIndex = _searchTotal > 0 ? 0 : -1;
        if (_searchTotal > 0)
        {
            await _module.InvokeVoidAsync("gotoMatch", _containerRef, _searchIndex);
        }
    }

    private Task SearchNext() => GotoMatch(_searchIndex + 1);

    private Task SearchPrev() => GotoMatch(_searchIndex - 1);

    private async Task GotoMatch(int index)
    {
        if (_module is null || _searchTotal <= 0)
        {
            return;
        }
        _searchIndex = ((index % _searchTotal) + _searchTotal) % _searchTotal;
        await _module.InvokeVoidAsync("gotoMatch", _containerRef, _searchIndex);
    }

    private async Task ClearSearchAsync()
    {
        _searchTotal = 0;
        _searchIndex = -1;
        if (_module is not null)
        {
            await _module.InvokeVoidAsync("clearSearch", _containerRef);
        }
    }

    private string PageStyle(int index)
    {
        double pw = index < _pageWidths.Count ? _pageWidths[index] : 612;
        double ph = index < _pageHeights.Count ? _pageHeights[index] : 792;
        return string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"width:{pw * _zoom:0.#}px;height:{ph * _zoom:0.#}px;--bp-scale:{_zoom:0.####}");
    }

    private string ThumbStyle(int index)
    {
        const double target = 130.0; // thumbnail content width in px
        double pw = index < _pageWidths.Count ? _pageWidths[index] : 612;
        double ph = index < _pageHeights.Count ? _pageHeights[index] : 792;
        double scale = pw > 0 ? target / pw : 0.2;
        return string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"position:relative;width:{pw * scale:0.#}px;height:{ph * scale:0.#}px;overflow:hidden;--bp-scale:{scale:0.####}");
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
