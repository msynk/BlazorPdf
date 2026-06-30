using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace BlazorPdf;

/// <summary>
/// A dependency-free PDF viewer for Blazor. Renders documents using the browser's
/// built-in PDF engine and wraps them in a customizable Blazor toolbar.
/// </summary>
public partial class PdfViewer : IAsyncDisposable
{
    private const string ModulePath = "./_content/BlazorPdf/pdfViewer.js";

    [Inject] private IJSRuntime JS { get; set; } = default!;

    private IJSObjectReference? _module;
    private DotNetObjectReference<PdfViewer>? _selfRef;
    private ElementReference _container;
    private ElementReference _iframe;
    private string? _instanceId;
    private PdfSource? _currentSource;
    private bool _sourceDirty;
    private int _currentPage = 1;

    /// <summary>The document to display. Setting this reloads the viewer.</summary>
    [Parameter] public PdfSource? Source { get; set; }

    /// <summary>Convenience shortcut: a URL to display. Ignored when <see cref="Source"/> is set.</summary>
    [Parameter] public string? Url { get; set; }

    /// <summary>Convenience shortcut: raw bytes to display. Ignored when <see cref="Source"/> or <see cref="Url"/> is set.</summary>
    [Parameter] public byte[]? Data { get; set; }

    /// <summary>CSS width of the viewer. Default <c>100%</c>.</summary>
    [Parameter] public string Width { get; set; } = "100%";

    /// <summary>CSS height of the viewer. Default <c>600px</c>.</summary>
    [Parameter] public string Height { get; set; } = "600px";

    /// <summary>Shows the custom Blazor toolbar. Default <c>true</c>.</summary>
    [Parameter] public bool ShowToolbar { get; set; } = true;

    /// <summary>Shows the browser's own PDF toolbar inside the frame. Default <c>true</c>.</summary>
    [Parameter] public bool ShowNativeToolbar { get; set; } = true;

    /// <summary>Shows the download button in the toolbar.</summary>
    [Parameter] public bool ShowDownloadButton { get; set; } = true;

    /// <summary>Shows the print button in the toolbar.</summary>
    [Parameter] public bool ShowPrintButton { get; set; } = true;

    /// <summary>Shows the open-in-new-tab button in the toolbar.</summary>
    [Parameter] public bool ShowOpenInNewTabButton { get; set; } = true;

    /// <summary>Shows the fullscreen button in the toolbar.</summary>
    [Parameter] public bool ShowFullscreenButton { get; set; } = true;

    /// <summary>Shows the page navigation control in the toolbar.</summary>
    [Parameter] public bool ShowPageNavigation { get; set; } = true;

    /// <summary>The page to open initially (1-based). Default <c>1</c>.</summary>
    [Parameter] public int InitialPage { get; set; } = 1;

    /// <summary>Initial zoom behaviour. Default <see cref="PdfZoomMode.Auto"/>.</summary>
    [Parameter] public PdfZoomMode ZoomMode { get; set; } = PdfZoomMode.Auto;

    /// <summary>Zoom percentage used when <see cref="ZoomMode"/> is <see cref="PdfZoomMode.Custom"/>.</summary>
    [Parameter] public int ZoomPercent { get; set; } = 100;

    /// <summary>Extra CSS class applied to the root element.</summary>
    [Parameter] public string? CssClass { get; set; }

    /// <summary>Inline style appended to the root element.</summary>
    [Parameter] public string? Style { get; set; }

    /// <summary>Custom toolbar content rendered to the right of the built-in buttons.</summary>
    [Parameter] public RenderFragment? ToolbarContent { get; set; }

    /// <summary>Captures additional attributes splatted onto the root element.</summary>
    [Parameter(CaptureUnmatchedValues = true)]
    public IReadOnlyDictionary<string, object>? AdditionalAttributes { get; set; }

    /// <summary>Raised when the browser finishes loading the document.</summary>
    [Parameter] public EventCallback OnDocumentLoaded { get; set; }

    /// <summary>Raised when an error occurs while loading or rendering.</summary>
    [Parameter] public EventCallback<string> OnError { get; set; }

    /// <summary>The current 1-based page number (best-effort; reflects navigation requests).</summary>
    public int CurrentPage => _currentPage;

    private PdfSource? ResolveSource()
    {
        if (Source is not null) return Source;
        if (!string.IsNullOrWhiteSpace(Url)) return PdfSource.FromUrl(Url);
        if (Data is { Length: > 0 }) return PdfSource.FromBytes(Data);
        return null;
    }

    /// <inheritdoc />
    protected override void OnParametersSet()
    {
        var resolved = ResolveSource();
        if (!ReferenceEquals(resolved, _currentSource))
        {
            _currentSource = resolved;
            _sourceDirty = true;
            _currentPage = InitialPage < 1 ? 1 : InitialPage;
        }
    }

    /// <inheritdoc />
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            try
            {
                _module = await JS.InvokeAsync<IJSObjectReference>("import", ModulePath);
                _selfRef = DotNetObjectReference.Create(this);
                _instanceId = await _module.InvokeAsync<string>("create", _container, _iframe, _selfRef);
            }
            catch (Exception ex)
            {
                await RaiseErrorAsync($"Failed to initialize viewer: {ex.Message}");
                return;
            }
        }

        if (_sourceDirty && _module is not null && _instanceId is not null)
        {
            _sourceDirty = false;
            await ApplySourceAsync();
        }
    }

    private Dictionary<string, object?> BuildFragment()
    {
        var fragment = new Dictionary<string, object?>
        {
            ["page"] = _currentPage > 1 ? _currentPage : null,
            ["toolbar"] = ShowNativeToolbar ? null : 0,
            ["navpanes"] = ShowNativeToolbar ? null : 0,
        };

        fragment["zoom"] = ZoomMode switch
        {
            PdfZoomMode.PageFit => "page-fit",
            PdfZoomMode.PageWidth => "page-width",
            PdfZoomMode.Custom => ZoomPercent.ToString(),
            _ => null,
        };

        return fragment;
    }

    private async Task ApplySourceAsync()
    {
        if (_module is null || _instanceId is null) return;

        try
        {
            if (_currentSource is null)
            {
                await _module.InvokeVoidAsync("setUrl", _instanceId, "about:blank", null);
                return;
            }

            var fragment = BuildFragment();

            switch (_currentSource.Kind)
            {
                case PdfSourceKind.Url:
                    await _module.InvokeVoidAsync("setUrl", _instanceId, _currentSource.Url, fragment);
                    break;
                case PdfSourceKind.Bytes:
                    var base64 = Convert.ToBase64String(_currentSource.Bytes!);
                    await _module.InvokeVoidAsync("setBytes", _instanceId, base64, fragment);
                    break;
            }
        }
        catch (Exception ex)
        {
            await RaiseErrorAsync($"Failed to load document: {ex.Message}");
        }
    }

    private async Task RaiseErrorAsync(string message)
    {
        if (OnError.HasDelegate)
        {
            await OnError.InvokeAsync(message);
        }
    }

    /// <summary>JS callback invoked when the iframe finishes loading a document.</summary>
    [JSInvokable]
    public async Task OnDocumentLoadedInternal()
    {
        if (OnDocumentLoaded.HasDelegate)
        {
            await OnDocumentLoaded.InvokeAsync();
        }
    }

    /// <summary>Loads a new document into the viewer.</summary>
    public async Task LoadAsync(PdfSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        _currentSource = source;
        _currentPage = InitialPage < 1 ? 1 : InitialPage;
        await ApplySourceAsync();
    }

    /// <summary>Navigates the built-in viewer to the given 1-based page.</summary>
    public async Task GoToPageAsync(int page)
    {
        if (page < 1) page = 1;
        _currentPage = page;
        if (_module is not null && _instanceId is not null)
        {
            await _module.InvokeVoidAsync("goToPage", _instanceId, page);
        }
        StateHasChanged();
    }

    private Task NextPageAsync() => GoToPageAsync(_currentPage + 1);

    private Task PreviousPageAsync() => GoToPageAsync(Math.Max(1, _currentPage - 1));

    /// <summary>Downloads the current document.</summary>
    public async Task DownloadAsync()
    {
        if (_module is not null && _instanceId is not null)
        {
            await _module.InvokeVoidAsync("download", _instanceId, _currentSource?.FileName ?? "document.pdf");
        }
    }

    /// <summary>Prints the current document.</summary>
    public async Task PrintAsync()
    {
        if (_module is not null && _instanceId is not null)
        {
            await _module.InvokeVoidAsync("print", _instanceId);
        }
    }

    /// <summary>Opens the current document in a new browser tab.</summary>
    public async Task OpenInNewTabAsync()
    {
        if (_module is not null && _instanceId is not null)
        {
            await _module.InvokeVoidAsync("openInNewTab", _instanceId);
        }
    }

    /// <summary>Toggles fullscreen display of the viewer.</summary>
    public async Task ToggleFullscreenAsync()
    {
        if (_module is not null && _instanceId is not null)
        {
            await _module.InvokeVoidAsync("toggleFullscreen", _instanceId);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_module is not null && _instanceId is not null)
            {
                await _module.InvokeVoidAsync("dispose", _instanceId);
            }
            if (_module is not null)
            {
                await _module.DisposeAsync();
            }
        }
        catch (JSDisconnectedException)
        {
            // Circuit already gone; nothing to clean up.
        }
        catch (Exception)
        {
            // Best-effort cleanup.
        }
        finally
        {
            _selfRef?.Dispose();
        }
    }
}
