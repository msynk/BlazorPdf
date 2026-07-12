using System.Net.Http;
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
    private bool _correctWidthsPending; // run the JS text width-correction after render
    private string?[]? _pageText; // lazily-built per-page text index for search
    private int _loadVersion; // bumped per load; guards against a superseded load committing
    private string _status = "Idle.";
    private bool _loading;

    // One slot per page. A null slot is a not-yet-rendered page shown as a
    // light placeholder; it is rendered on demand when it nears the viewport.
    private readonly List<MarkupString?> _pages = new();
    private readonly List<double> _pageWidths = new();  // points, display orientation
    private readonly List<double> _pageHeights = new();

    // The thumbnail sidebar owns its own render slots, decoupled from _pages, so
    // it can lazy-render only the thumbnails scrolled into the sidebar viewport
    // (like pdf.js) instead of mirroring whatever the main surface happens to
    // have rendered. A null slot is a not-yet-rendered thumbnail placeholder.
    private readonly List<MarkupString?> _thumbs = new();

    private int _currentPage = 1;
    private double _zoom = 1.0;
    private PdfZoomMode _zoomMode = PdfZoomMode.FitWidth;
    private PdfTextCoalescing _textCoalescing; // last applied; changes re-render pages
    private int _rotation;
    private bool _showThumbnails;
    private bool _showOutline;
    private IReadOnlyList<OutlineItem> _outline = Array.Empty<OutlineItem>();

    private bool _showSearch;
    private string _searchQuery = "";
    private int _searchTotal;
    private int _searchIndex = -1;

    // Cache-busts the JS module import. The module ships inside this assembly and
    // is served from a fixed URL with no content hash, so browsers would cache it
    // across rebuilds — leaving a stale script that lacks newly added functions.
    // The module version id changes on every compilation, so a rebuilt library
    // always loads its matching script.
    private static readonly string AssetVersion =
        typeof(BlazorPdfViewer).Assembly.ManifestModule.ModuleVersionId.ToString("N");

    private IJSObjectReference? _module;
    private DotNetObjectReference<BlazorPdfViewer>? _dotNetRef;
    private ElementReference _containerRef;
    private ElementReference _thumbsRef;
    private ElementReference _viewerRef;
    private bool _spyPending;
    private bool _thumbSpyPending; // (re)attach the sidebar's lazy-render spy after render

    /// <summary>The document to display.</summary>
    [Parameter] public PdfSource? Source { get; set; }

    /// <summary>CSS height of the viewer container.</summary>
    [Parameter] public string Height { get; set; } = "780px";

    /// <summary>Whether the toolbar is shown.</summary>
    [Parameter] public bool ShowToolbar { get; set; } = true;

    /// <summary>The initial zoom behavior.</summary>
    [Parameter] public PdfZoomMode InitialZoomMode { get; set; } = PdfZoomMode.FitWidth;

    /// <summary>
    /// How painted text is emitted. <see cref="PdfTextCoalescing.Compact"/> merges
    /// same-line, same-style runs into one span per visual line — far fewer DOM
    /// nodes on per-glyph PDFs, with small intra-line position drift (explicit
    /// kerning between runs is approximated). Rotated text always stays exact.
    /// Default is <see cref="PdfTextCoalescing.Exact"/>.
    /// </summary>
    [Parameter] public PdfTextCoalescing TextCoalescing { get; set; } = PdfTextCoalescing.Exact;

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

    /// <summary>The document-defined label for the current page (e.g. "iv", "A-1")
    /// when it differs from the plain page number; otherwise <c>null</c>.</summary>
    private string? CurrentPageLabel
    {
        get
        {
            if (_document is null)
            {
                return null;
            }
            try
            {
                var labels = _document.PageLabels;
                int i = _currentPage - 1;
                if (i < 0 || i >= labels.Count)
                {
                    return null;
                }
                string label = labels[i];
                return label == _currentPage.ToString(System.Globalization.CultureInfo.InvariantCulture) ? null : label;
            }
            catch
            {
                return null;
            }
        }
    }

    protected override void OnInitialized() => _zoomMode = InitialZoomMode;

    protected override async Task OnParametersSetAsync()
    {
        if (!ReferenceEquals(_source, Source))
        {
            _source = Source;
            _rotation = 0;
            _currentPage = 1;
            _textCoalescing = TextCoalescing;
            await LoadAsync();
            return;
        }
        // Same document but the text-emission mode changed: invalidate and
        // re-render the page fragments (same mechanism as rotation) so the new
        // mode takes effect without reloading the document.
        if (_textCoalescing != TextCoalescing)
        {
            _textCoalescing = TextCoalescing;
            PreparePages();
        }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            _module = await JS.InvokeAsync<IJSObjectReference>(
                "import", $"./_content/BlazorPdf/blazor-pdf.js?v={AssetVersion}");
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

        // Attach the sidebar's own lazy-render spy once its element exists in the
        // DOM (it is only present while the thumbnail panel is open). The spy
        // fills the visible thumbnails on its own scroll, independent of the main
        // surface. Registration is idempotent, so re-running it after a reload or
        // rotation simply re-fills the freshly reset slots.
        if (_thumbSpyPending && _showThumbnails && _module is not null && _dotNetRef is not null)
        {
            _thumbSpyPending = false;
            try
            {
                await _module.InvokeVoidAsync("registerThumbSpy", _thumbsRef, _dotNetRef);
                await _module.InvokeVoidAsync("scrollThumbIntoView", _thumbsRef, _currentPage);
            }
            catch (JSDisconnectedException)
            {
                // Circuit gone mid-render; ignore.
            }
            catch (JSException)
            {
                // A stale/cached script may not expose the sidebar functions yet;
                // the panel still shows the eagerly-rendered thumbnails. A refresh
                // (new script) restores lazy loading.
            }
        }

        // After any render that produced new page content, correct each text run's
        // width to its PDF advance (fixes spacing when a substitute font is used).
        if (_correctWidthsPending && _module is not null)
        {
            _correctWidthsPending = false;
            try
            {
                await _module.InvokeVoidAsync("correctTextWidths", _containerRef);
            }
            catch (JSDisconnectedException)
            {
                // Circuit gone mid-render; ignore.
            }
            catch (JSException)
            {
                // Optional enhancement: a stale/cached script may not expose the
                // function yet. Never let width correction break rendering.
            }
        }
    }

    [Inject] private IJSRuntime JS { get; set; } = default!;

    [Inject] private IServiceProvider Services { get; set; } = default!;

    // Parse off the UI thread on Blazor Server so a large document doesn't freeze
    // the circuit; on single-threaded WASM this runs inline (Task.Run offers no
    // parallelism there, and the surrounding Task.Yield already lets the bar paint).
    private static Task<PdfDocument> ParseAsync(byte[] bytes, string? password)
        => OperatingSystem.IsBrowser()
            ? Task.FromResult(PdfDocument.Load(bytes, password))
            : Task.Run(() => PdfDocument.Load(bytes, password));

    private async Task LoadAsync()
    {
        int version = ++_loadVersion; // supersedes any load still in flight
        _pages.Clear();
        _pageWidths.Clear();
        _pageHeights.Clear();
        _document = null;
        _fontStore = null; // fresh embedded-font store per document
        _pageText = null;  // invalidate the search text index
        _searchTotal = 0;
        _searchIndex = -1;
        _outline = Array.Empty<OutlineItem>();

        if (_source is null)
        {
            _status = "No document loaded.";
            return;
        }

        // Show the progress bar and let it paint before the synchronous parse
        // work begins. The bar animates on the compositor so it keeps moving
        // even while the WASM thread is busy parsing.
        _loading = true;
        StateHasChanged();
        await Task.Yield();

        // A newer Source arrived while we yielded: abandon this stale load.
        if (version != _loadVersion)
        {
            return;
        }

        // Resolve the bytes: an in-memory buffer, or a URL fetched via HttpClient.
        byte[]? bytes = _source.Bytes;
        if (bytes is null && _source.Url is not null)
        {
            var http = Services.GetService(typeof(HttpClient)) as HttpClient;
            if (http is null)
            {
                _status = "URL sources require a registered HttpClient.";
                _loading = false;
                await OnError.InvokeAsync(_status);
                return;
            }
            try
            {
                bytes = await http.GetByteArrayAsync(_source.Url);
            }
            catch (Exception ex)
            {
                _status = $"Failed to fetch document: {ex.Message}";
                _loading = false;
                await OnError.InvokeAsync(_status);
                return;
            }
            if (version != _loadVersion)
            {
                return;
            }
        }
        if (bytes is null)
        {
            _status = "No document loaded.";
            _loading = false;
            return;
        }

        try
        {
            try
            {
                _document = await ParseAsync(bytes, _source.Password);
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
                _document = await ParseAsync(bytes, entered);
            }
            // A password prompt may have awaited long enough for a newer Source.
            if (version != _loadVersion)
            {
                return;
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
        _thumbs.Clear();
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
            _thumbs.Add(null);
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

        // If the sidebar is open, its slots were just reset; let its spy re-fill
        // the visible thumbnails on the next render.
        if (_showThumbnails)
        {
            _thumbSpyPending = true;
        }
    }

    /// <summary>Renders a single page to its HTML fragment.</summary>
    /// <summary>The document-wide embedded-font <c>@font-face</c> stylesheet,
    /// rendered in a persistent element so it survives page eviction.</summary>
    private MarkupString FontFaceStyleMarkup => new(_fontStore?.FontFaceStyle ?? string.Empty);

    private MarkupString RenderPageContent(int index)
    {
        var page = _document!.Pages[index];
        _fontStore ??= new Core.Render.PdfFontStore();
        _correctWidthsPending = true; // measure/scale text runs after this render
        var renderer = new Core.Render.HtmlRenderer(page, _document.XRef, _fontStore, _rotation)
        {
            DestinationResolver = dest => _document.ResolveDestinationPage(dest),
            TextCoalescing = TextCoalescing,
        };
        return new MarkupString(renderer.Render());
    }

    /// <summary>Renders a single page (1-based) to self-contained HTML, or an
    /// empty string when no document is loaded or the number is out of range.</summary>
    public string RenderPageHtml(int pageNumber)
    {
        if (_document is null || pageNumber < 1 || pageNumber > _document.PageCount)
        {
            return string.Empty;
        }
        return new Core.Render.HtmlRenderer(_document.Pages[pageNumber - 1], _document.XRef, _rotation)
        {
            TextCoalescing = TextCoalescing,
        }.Render();
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
            EvictDistantPages();
            StateHasChanged();
        }
    }

    // Cap how many pages stay materialized so a large document does not grow the
    // DOM (and Blazor Server circuit memory) unbounded. Evicted pages revert to
    // placeholders and are re-rendered lazily when scrolled back into view.
    private const int MaxRenderedPages = 24;

    private void EvictDistantPages()
    {
        int rendered = 0;
        foreach (var p in _pages)
        {
            if (p is not null)
            {
                rendered++;
            }
        }
        if (rendered <= MaxRenderedPages)
        {
            return;
        }

        // Keep a window centered on the current page; drop everything outside it.
        int half = MaxRenderedPages / 2;
        int keepLo = Math.Max(0, _currentPage - 1 - half);
        int keepHi = Math.Min(_pages.Count - 1, _currentPage - 1 + half);
        for (int i = 0; i < _pages.Count; i++)
        {
            if ((i < keepLo || i > keepHi) && _pages[i] is not null)
            {
                _pages[i] = null;
            }
        }
    }

    /// <summary>
    /// Renders the fragment shown in a thumbnail. The markup is identical to the
    /// full page (only the enclosing <c>--bp-scale</c> differs), so when the main
    /// surface has already rendered this page we reuse its immutable fragment;
    /// otherwise we render one just for the sidebar. Either way the thumbnail is
    /// cached in its own slot and survives the main page's eviction.
    /// </summary>
    private MarkupString RenderThumbContent(int index)
        => _pages[index] ?? RenderPageContent(index);

    /// <summary>
    /// Invoked from JavaScript as thumbnails approach the sidebar viewport.
    /// Renders any requested thumbnails that are still placeholders. This is the
    /// sidebar's counterpart to <see cref="EnsurePagesRendered"/> and runs on the
    /// sidebar's own scroll, so opening the panel on a 500-page document renders
    /// only the handful of thumbnails on screen.
    /// </summary>
    [JSInvokable]
    public void EnsureThumbsRendered(int[] pageNumbers)
    {
        if (_document is null || pageNumbers is null)
        {
            return;
        }

        bool changed = false;
        int lo = int.MaxValue, hi = int.MinValue;
        foreach (int n in pageNumbers)
        {
            int idx = n - 1;
            if (idx >= 0 && idx < _thumbs.Count)
            {
                lo = Math.Min(lo, idx);
                hi = Math.Max(hi, idx);
                if (_thumbs[idx] is null)
                {
                    _thumbs[idx] = RenderThumbContent(idx);
                    changed = true;
                }
            }
        }

        if (changed)
        {
            // Evict around the range just requested (what is visible in the
            // sidebar), not the current page — scrolling the sidebar leaves the
            // current page put, so centering on it would blank the very
            // thumbnails the user just scrolled to.
            EvictDistantThumbs(lo, hi);
            StateHasChanged();
        }
    }

    // Bound how many thumbnails stay materialized. A thumbnail fragment is as
    // heavy as a full page, so a large document scrolled end-to-end in the
    // sidebar would otherwise pin every page's markup in memory.
    private const int MaxRenderedThumbs = 40;

    private void EvictDistantThumbs(int visibleLo, int visibleHi)
    {
        int rendered = 0;
        foreach (var t in _thumbs)
        {
            if (t is not null)
            {
                rendered++;
            }
        }
        if (rendered <= MaxRenderedThumbs)
        {
            return;
        }

        // Keep the visible range plus an equal margin on each side, so nearby
        // thumbnails are already warm when the user keeps scrolling.
        int margin = Math.Max(0, (MaxRenderedThumbs - (visibleHi - visibleLo + 1)) / 2);
        int keepLo = Math.Max(0, visibleLo - margin);
        int keepHi = Math.Min(_thumbs.Count - 1, visibleHi + margin);
        for (int i = 0; i < _thumbs.Count; i++)
        {
            if ((i < keepLo || i > keepHi) && _thumbs[i] is not null)
            {
                _thumbs[i] = null;
            }
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
            if (_showThumbnails)
            {
                await ScrollActiveThumbIntoViewAsync();
            }
        }
    }

    // Keeps the sidebar's active thumbnail in view, tolerating a stale cached
    // script that predates the sidebar functions (degrades to no auto-follow).
    private async Task ScrollActiveThumbIntoViewAsync()
    {
        if (_module is null)
        {
            return;
        }
        try
        {
            await _module.InvokeVoidAsync("scrollThumbIntoView", _thumbsRef, _currentPage);
        }
        catch (JSDisconnectedException)
        {
        }
        catch (JSException)
        {
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
            // Keep the sidebar's active thumbnail in view as the main surface
            // scrolls, so lazy-loaded thumbnails follow the reader.
            if (_showThumbnails && _module is not null)
            {
                _ = ScrollActiveThumbIntoViewAsync();
            }
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

    /// <summary>Invoked from JavaScript on Ctrl+wheel / pinch to zoom.</summary>
    [JSInvokable]
    public async Task OnWheelZoom(double deltaY)
    {
        await SetCustomZoom(deltaY < 0 ? _zoom * 1.1 : _zoom / 1.1);
        StateHasChanged();
    }

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
        // Stream the bytes as a Blob rather than pushing a base64 data: URI (which
        // on Blazor Server would traverse SignalR as one huge string).
        using var stream = new MemoryStream(_source.Bytes, writable: false);
        using var streamRef = new DotNetStreamReference(stream, leaveOpen: true);
        try
        {
            await _module.InvokeVoidAsync("downloadStream", _source.FileName ?? "document.pdf", streamRef);
        }
        catch (JSException)
        {
            // Fall back to the base64 path if the streaming import is unavailable.
            await _module.InvokeVoidAsync("download", _source.FileName ?? "document.pdf",
                Convert.ToBase64String(_source.Bytes));
        }
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

    private async Task ToggleThumbnails()
    {
        _showThumbnails = !_showThumbnails;
        if (_showThumbnails)
        {
            _showOutline = false;
            // Attach the sidebar spy after its element renders; it fills the
            // visible thumbnails on its own.
            _thumbSpyPending = true;
        }
        else if (_module is not null)
        {
            // The sidebar element is leaving the DOM; drop its scroll listener.
            try
            {
                await _module.InvokeVoidAsync("disposeThumbSpy", _thumbsRef);
            }
            catch (JSException)
            {
                // Element already gone / module unavailable; nothing to clean up.
            }
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

    /// <summary>Activates a control on Enter or Space, so keyboard users can
    /// operate the thumbnail list and outline tree like buttons.</summary>
    private async Task OnActivateKey(KeyboardEventArgs e, Func<Task> action)
    {
        if (e.Key is "Enter" or " " or "Spacebar")
        {
            await action();
        }
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
        if (_module is null || _document is null)
        {
            return;
        }
        if (string.IsNullOrEmpty(_searchQuery))
        {
            await ClearSearchAsync();
            return;
        }

        _loading = true;
        StateHasChanged();
        await Task.Yield();

        // Search a per-page extracted-text index (built lazily) rather than the
        // rendered DOM, so we only render the pages that actually contain matches
        // — a 500-page document with matches on 3 pages renders 3, not 500.
        _pageText ??= new string?[_document.PageCount];
        string needle = _searchQuery;
        bool rendered = false;
        for (int i = 0; i < _document.PageCount; i++)
        {
            _pageText[i] ??= _document.Pages[i].ExtractText();
            if (_pageText[i]!.Contains(needle, StringComparison.OrdinalIgnoreCase)
                && i < _pages.Count && _pages[i] is null)
            {
                _pages[i] = RenderPageContent(i);
                rendered = true;
            }
        }

        _loading = false;
        if (rendered)
        {
            StateHasChanged();
            await Task.Yield(); // let the freshly rendered pages paint before highlighting
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
                try
                {
                    await _module.InvokeVoidAsync("disposeThumbSpy", _thumbsRef);
                }
                catch (JSException)
                {
                    // Stale cached script without the sidebar functions; nothing
                    // to tear down.
                }
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
