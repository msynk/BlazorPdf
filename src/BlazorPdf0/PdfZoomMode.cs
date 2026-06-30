namespace BlazorPdf;

/// <summary>
/// Initial zoom behaviour passed to the browser's built-in PDF viewer via
/// PDF Open Parameters. Support varies by browser; Chromium honours these most fully.
/// </summary>
public enum PdfZoomMode
{
    /// <summary>Let the browser choose (no zoom parameter emitted).</summary>
    Auto = 0,

    /// <summary>Fit the whole page in the viewport.</summary>
    PageFit,

    /// <summary>Fit the page width to the viewport.</summary>
    PageWidth,

    /// <summary>Use an explicit percentage (see <c>PdfViewer.ZoomPercent</c>).</summary>
    Custom,
}
