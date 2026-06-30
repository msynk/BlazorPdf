namespace BlazorPdf;

/// <summary>
/// Identifies how a <see cref="PdfSource"/> delivers its document to the viewer.
/// </summary>
public enum PdfSourceKind
{
    /// <summary>No source set.</summary>
    None = 0,

    /// <summary>A URL (absolute or app-relative) the browser fetches directly.</summary>
    Url,

    /// <summary>Raw bytes that are turned into an in-browser blob URL.</summary>
    Bytes,
}

/// <summary>
/// Represents a PDF document to be displayed by <see cref="PdfViewer"/>.
/// Create instances via the static factory methods.
/// </summary>
public sealed class PdfSource
{
    private PdfSource() { }

    /// <summary>How the document is delivered to the browser.</summary>
    public PdfSourceKind Kind { get; private init; }

    /// <summary>The URL when <see cref="Kind"/> is <see cref="PdfSourceKind.Url"/>.</summary>
    public string? Url { get; private init; }

    /// <summary>The raw bytes when <see cref="Kind"/> is <see cref="PdfSourceKind.Bytes"/>.</summary>
    public byte[]? Bytes { get; private init; }

    /// <summary>Suggested file name used for downloads and printing.</summary>
    public string FileName { get; private init; } = "document.pdf";

    /// <summary>Creates a source that points the viewer at a URL.</summary>
    /// <param name="url">An absolute or application-relative URL to a PDF.</param>
    /// <param name="fileName">Optional file name used when downloading.</param>
    public static PdfSource FromUrl(string url, string? fileName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        return new PdfSource
        {
            Kind = PdfSourceKind.Url,
            Url = url,
            FileName = fileName ?? DeriveFileName(url),
        };
    }

    /// <summary>Creates a source from raw PDF bytes.</summary>
    /// <param name="bytes">The PDF file contents.</param>
    /// <param name="fileName">Optional file name used when downloading.</param>
    public static PdfSource FromBytes(byte[] bytes, string? fileName = null)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        return new PdfSource
        {
            Kind = PdfSourceKind.Bytes,
            Bytes = bytes,
            FileName = fileName ?? "document.pdf",
        };
    }

    /// <summary>Creates a source from a base64-encoded PDF string.</summary>
    /// <param name="base64">Base64 text, with or without a <c>data:</c> URI prefix.</param>
    /// <param name="fileName">Optional file name used when downloading.</param>
    public static PdfSource FromBase64(string base64, string? fileName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(base64);
        var comma = base64.IndexOf(',');
        if (base64.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && comma >= 0)
        {
            base64 = base64[(comma + 1)..];
        }

        return FromBytes(Convert.FromBase64String(base64), fileName);
    }

    /// <summary>Creates a source by reading all bytes from a stream.</summary>
    /// <param name="stream">A readable stream positioned at the start of the PDF.</param>
    /// <param name="fileName">Optional file name used when downloading.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    public static async Task<PdfSource> FromStreamAsync(
        Stream stream,
        string? fileName = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory, cancellationToken).ConfigureAwait(false);
        return FromBytes(memory.ToArray(), fileName);
    }

    private static string DeriveFileName(string url)
    {
        try
        {
            var path = url.Split('?', '#')[0].TrimEnd('/');
            var name = path[(path.LastIndexOf('/') + 1)..];
            return string.IsNullOrWhiteSpace(name) ? "document.pdf" : name;
        }
        catch
        {
            return "document.pdf";
        }
    }
}
