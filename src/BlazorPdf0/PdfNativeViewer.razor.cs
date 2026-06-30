using BlazorPdf.Engine;
using BlazorPdf.Engine.Rendering;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace BlazorPdf;

/// <summary>
/// A fully native PDF viewer that rasterizes pages with the pure-C#
/// <see cref="PdfRenderer"/> and paints them onto a canvas. It does not use the
/// browser's built-in PDF engine or any external library.
/// </summary>
public partial class PdfNativeViewer : IAsyncDisposable
{
    private const string ModulePath = "./_content/BlazorPdf/pdfRender.js";

    [Inject] private IJSRuntime JS { get; set; } = default!;

    private IJSObjectReference? _module;
    private ElementReference _canvas;
    private PdfDocument? _document;
    private byte[]? _loadedBytes;
    private int _pageIndex;
    private double _scale = 1.5;
    private bool _rendering;
    private string? _error;

    /// <summary>The PDF document bytes to render.</summary>
    [Parameter] public byte[]? Data { get; set; }

    /// <summary>A byte-based <see cref="PdfSource"/> to render. Takes precedence over <see cref="Data"/>.</summary>
    [Parameter] public PdfSource? Source { get; set; }

    /// <summary>Initial render scale (1.0 = 72 DPI). Default 1.5.</summary>
    [Parameter] public double Scale { get; set; } = 1.5;

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

    /// <summary>Raised when parsing or rendering fails.</summary>
    [Parameter] public EventCallback<string> OnError { get; set; }

    /// <summary>The current 1-based page number.</summary>
    public int CurrentPage => _pageIndex + 1;

    /// <summary>The number of pages in the loaded document.</summary>
    public int PageCount => _document?.PageCount ?? 0;

    private byte[]? ResolveBytes()
    {
        if (Source is { Kind: PdfSourceKind.Bytes, Bytes: { } b }) return b;
        return Data;
    }

    /// <inheritdoc />
    protected override void OnParametersSet()
    {
        _scale = Scale <= 0 ? 1.5 : Scale;
        var bytes = ResolveBytes();
        if (!ReferenceEquals(bytes, _loadedBytes))
        {
            _loadedBytes = bytes;
            ParseDocument();
        }
    }

    private void ParseDocument()
    {
        _error = null;
        _document = null;
        _pageIndex = Math.Max(0, InitialPage - 1);

        if (_loadedBytes is null || _loadedBytes.Length == 0)
        {
            return;
        }

        try
        {
            _document = PdfDocument.Load(_loadedBytes);
            if (_pageIndex >= _document.PageCount) _pageIndex = 0;
            _ = OnDocumentParsed.InvokeAsync(_document.PageCount);
        }
        catch (Exception ex)
        {
            _error = ex.Message;
            _ = OnError.InvokeAsync(ex.Message);
        }
    }

    /// <inheritdoc />
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            _module = await JS.InvokeAsync<IJSObjectReference>("import", ModulePath);
        }
        await RenderCurrentAsync();
    }

    private async Task RenderCurrentAsync()
    {
        if (_module is null || _document is null || _document.PageCount == 0 || _rendering)
        {
            return;
        }

        _rendering = true;
        try
        {
            var page = _document.Pages[_pageIndex];
            // Rasterize off the UI thread; this can be CPU-heavy for complex pages.
            var image = await Task.Run(() => PdfRenderer.Render(page, _scale));
            await _module.InvokeVoidAsync("draw", _canvas, image.Width, image.Height, image.ToBase64());
        }
        catch (Exception ex)
        {
            _error = ex.Message;
            if (OnError.HasDelegate) await OnError.InvokeAsync(ex.Message);
        }
        finally
        {
            _rendering = false;
        }
    }

    private async Task GoToAsync(int pageIndex)
    {
        if (_document is null) return;
        _pageIndex = Math.Clamp(pageIndex, 0, _document.PageCount - 1);
        await RenderCurrentAsync();
        StateHasChanged();
    }

    /// <summary>Navigates to a 1-based page number.</summary>
    public Task GoToPageAsync(int page) => GoToAsync(page - 1);

    private Task NextAsync() => GoToAsync(_pageIndex + 1);

    private Task PreviousAsync() => GoToAsync(_pageIndex - 1);

    private async Task ZoomAsync(double factor)
    {
        _scale = Math.Clamp(_scale * factor, 0.25, 6.0);
        await RenderCurrentAsync();
        StateHasChanged();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_module is not null) await _module.DisposeAsync();
        }
        catch (JSDisconnectedException) { }
        catch (Exception) { }
    }
}
