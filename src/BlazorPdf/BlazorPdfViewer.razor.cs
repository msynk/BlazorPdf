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
    private Core.Render.PdfFontStore? _fontStore;
    private string _status = "Idle.";
    private bool _loading;

    // One slot per page. A null slot is a not-yet-rendered page shown as a
    // light placeholder; it is rendered on demand when it nears the viewport.
    private readonly List<MarkupString?> _pages = new();
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

    /// <summary>Raised (with the 1-based page number) when the focused page changes.</summary>
    [Parameter] public EventCallback<int> OnPageChanged { get; set; }

    /// <summary>Raised when loading or rendering fails, with the error message.</summary>
    [Parameter] public EventCallback<string> OnError { get; set; }

    /// <summary>
    /// Raised after a document loads with any non-fatal diagnostics (e.g. the
    /// file was damaged and its cross-reference table had to be rebuilt).
    /// </summary>
    [Parameter] public EventCallback<IReadOnlyList<string>> OnWarnings { get; set; }

    /// <summary>
    /// Invoked when an encrypted document needs a password. Return the password to
    /// retry, or <c>null</c>/empty to cancel. If unset, a password error surfaces
    /// through <see cref="OnError"/> instead.
    /// </summary>
    [Parameter] public Func<Task<string?>>? OnPasswordRequested { get; set; }

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
        _fontStore = null; // fresh embedded-font store per document
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

        // Show the progress bar and let it paint before the synchronous parse
        // work begins. The bar animates on the compositor so it keeps moving
        // even while the WASM thread is busy parsing.
        _loading = true;
        StateHasChanged();
        await Task.Yield();

        try
        {
            try
            {
                _document = PdfDocument.Load(_source.Bytes, _source.Password);
            }
            catch (PdfPasswordException) when (OnPasswordRequested is not null)
            {
                // Ask the host for a password and retry once. The callback returns
                // null to cancel.
                string? entered = await OnPasswordRequested();
                if (string.IsNullOrEmpty(entered))
                {
                    throw;
                }
                _document = PdfDocument.Load(_source.Bytes, entered);
            }
            PreparePages();
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
            if (_document.Warnings.Count > 0 && OnWarnings.HasDelegate)
            {
                await OnWarnings.InvokeAsync(_document.Warnings);
            }
            await OnDocumentLoaded.InvokeAsync();
        }
        catch (Exception ex)
        {
            _status = $"Error: {ex.Message}";
            await OnError.InvokeAsync(ex.Message);
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>
    /// Measures every page (cheap) and creates an empty render slot for each so
    /// the document surface, scrollbar and page count are correct immediately.
    /// Only a small window around the current page is rendered up front; the
    /// rest are rendered on demand as they approach the viewport.
    /// </summary>
    private void PreparePages()
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
            _pages.Add(null);
            _pageWidths.Add(swap ? page.Height : page.Width);
            _pageHeights.Add(swap ? page.Width : page.Height);
        }

        // Eagerly render a small window around the current page so something is
        // visible instantly (page 1 on load, or the viewed page after rotation).
        int center = Math.Clamp(_currentPage - 1, 0, _pages.Count - 1);
        for (int i = Math.Max(0, center - 1); i <= Math.Min(_pages.Count - 1, center + 1); i++)
        {
            _pages[i] = RenderPageContent(i);
        }
    }

    /// <summary>Renders a single page to its HTML fragment.</summary>
    private MarkupString RenderPageContent(int index)
    {
        var page = _document!.Pages[index];
        _fontStore ??= new Core.Render.PdfFontStore();
        string html = new Core.Render.HtmlRenderer(page, _document.XRef, _fontStore, _rotation).Render();
        return new MarkupString(html);
    }

    /// <summary>Renders a single page (1-based) to self-contained HTML, or an
    /// empty string when no document is loaded or the number is out of range.</summary>
    public string RenderPageHtml(int pageNumber)
    {
        if (_document is null || pageNumber < 1 || pageNumber > _document.PageCount)
        {
            return string.Empty;
        }
        return new Core.Render.HtmlRenderer(_document.Pages[pageNumber - 1], _document.XRef, _rotation).Render();
    }

    /// <summary>Extracts the visible text of a single page (1-based) for search or
    /// copy, or an empty string when unavailable.</summary>
    public string ExtractPageText(int pageNumber)
    {
        if (_document is null || pageNumber < 1 || pageNumber > _document.PageCount)
        {
            return string.Empty;
        }
        return _document.Pages[pageNumber - 1].ExtractText();
    }

    /// <summary>
    /// Invoked from JavaScript as pages approach the viewport. Renders any of
    /// the requested pages that have not been rendered yet.
    /// </summary>
    [JSInvokable]
    public void EnsurePagesRendered(int[] pageNumbers)
    {
        if (_document is null || pageNumbers is null)
        {
            return;
        }

        bool changed = false;
        foreach (int n in pageNumbers)
        {
            int idx = n - 1;
            if (idx >= 0 && idx < _pages.Count && _pages[idx] is null)
            {
                _pages[idx] = RenderPageContent(idx);
                changed = true;
            }
        }

        if (changed)
        {
            StateHasChanged();
        }
    }

    /// <summary>
    /// Renders every page that is still a placeholder. Used before a full-text
    /// search so matches on not-yet-viewed pages are found.
    /// </summary>
    private async Task RenderAllPagesAsync()
    {
        if (_document is null)
        {
            return;
        }

        bool any = false;
        for (int i = 0; i < _pages.Count; i++)
        {
            if (_pages[i] is null)
            {
                _pages[i] = RenderPageContent(i);
                any = true;
                if (i % 4 == 0)
                {
                    StateHasChanged();
                    await Task.Yield();
                }
            }
        }

        if (any)
        {
            StateHasChanged();
            await Task.Yield();
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
        int target = Math.Clamp(pageNumber, 1, _pages.Count);
        if (target != _currentPage)
        {
            _currentPage = target;
            await OnPageChanged.InvokeAsync(_currentPage);
        }
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
            _ = OnPageChanged.InvokeAsync(pageNumber);
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
        PreparePages();
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
        if (_module is null || _document is null || _pages.Count == 0)
        {
            return;
        }

        // Render every page before printing so the output includes all pages, not
        // just the ones scrolled into view. Show progress while catching up.
        bool rendered = false;
        for (int i = 0; i < _pages.Count; i++)
        {
            if (_pages[i] is null)
            {
                if (!rendered)
                {
                    _loading = true;
                    _status = "Preparing all pages for printing…";
                    StateHasChanged();
                    await Task.Yield();
                    rendered = true;
                }
                _pages[i] = RenderPageContent(i);
            }
        }
        if (rendered)
        {
            _loading = false;
            StateHasChanged();
            await Task.Yield(); // let the DOM paint the freshly rendered pages
        }

        await _module.InvokeVoidAsync("printDocument", _containerRef);
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

        // Full-text search reads the rendered DOM, so ensure every page is
        // rendered first. Show the progress bar while catching up.
        bool needsRender = _pages.Any(p => p is null);
        if (needsRender)
        {
            _loading = true;
            StateHasChanged();
            await Task.Yield();
            await RenderAllPagesAsync();
            _loading = false;
            StateHasChanged();
            await Task.Yield();
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
        catch (TaskCanceledException)
        {
            // Disposal raced an in-flight interop call; safe to ignore.
        }
        catch (ObjectDisposedException)
        {
            // The module or its reference was already disposed.
        }
        _dotNetRef?.Dispose();
    }

    private readonly record struct Viewport(double Width, double Height);
}
