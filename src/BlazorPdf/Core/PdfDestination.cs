// Explicit-destination parsing and normalization.
// A destination array is [pageRef /Type args...]; see PDF 32000-1:2008 §12.3.2.

namespace BlazorPdf.Core;

/// <summary>The view-fit modes a destination can request (PDF §12.3.2.2).</summary>
public enum DestinationFit
{
    /// <summary>The destination array could not be interpreted.</summary>
    Unknown,

    /// <summary>[/XYZ left top zoom] - position the given point with a zoom level.</summary>
    XYZ,

    /// <summary>[/Fit] - fit the whole page in the window.</summary>
    Fit,

    /// <summary>[/FitH top] - fit the page width, with the given top edge.</summary>
    FitH,

    /// <summary>[/FitV left] - fit the page height, with the given left edge.</summary>
    FitV,

    /// <summary>[/FitR left bottom right top] - fit the given rectangle.</summary>
    FitR,

    /// <summary>[/FitB] - fit the page's bounding box.</summary>
    FitB,

    /// <summary>[/FitBH top] - fit the bounding-box width.</summary>
    FitBH,

    /// <summary>[/FitBV left] - fit the bounding-box height.</summary>
    FitBV,
}

/// <summary>
/// A resolved explicit destination: the target page plus the view parameters
/// (fit mode, position, zoom). Unspecified numeric parameters are <c>null</c>.
/// </summary>
public sealed class PdfDestination
{
    /// <summary>The target page (1-based), or <c>null</c> if it could not be resolved.</summary>
    public int? PageNumber { get; init; }

    /// <summary>The requested view-fit mode.</summary>
    public DestinationFit Fit { get; init; } = DestinationFit.Unknown;

    /// <summary>Left edge / X coordinate (XYZ, FitV, FitR, FitBV), when specified.</summary>
    public double? Left { get; init; }

    /// <summary>Top edge / Y coordinate (XYZ, FitH, FitR, FitBH), when specified.</summary>
    public double? Top { get; init; }

    /// <summary>Right edge (FitR), when specified.</summary>
    public double? Right { get; init; }

    /// <summary>Bottom edge (FitR), when specified.</summary>
    public double? Bottom { get; init; }

    /// <summary>Zoom factor (XYZ); <c>0</c>/<c>null</c> means "retain current zoom".</summary>
    public double? Zoom { get; init; }

    /// <summary>Builds a destination from the tail of a destination array (the fit name and its args).</summary>
    internal static PdfDestination FromArray(int? pageNumber, List<object?> arr, IXRef xref)
    {
        // arr[0] is the page target; arr[1] is the fit name; the rest are args.
        string fitName = arr.Count > 1 && xref.FetchIfRef(arr[1]) is Name n ? n.Value : "";

        double? Arg(int index)
        {
            if (index >= arr.Count)
            {
                return null;
            }
            object? v = xref.FetchIfRef(arr[index]);
            return v is double d ? d : null; // PDF null => "unchanged"
        }

        return fitName switch
        {
            "XYZ" => new PdfDestination
            {
                PageNumber = pageNumber,
                Fit = DestinationFit.XYZ,
                Left = Arg(2),
                Top = Arg(3),
                Zoom = Arg(4),
            },
            "Fit" => new PdfDestination { PageNumber = pageNumber, Fit = DestinationFit.Fit },
            "FitB" => new PdfDestination { PageNumber = pageNumber, Fit = DestinationFit.FitB },
            "FitH" => new PdfDestination { PageNumber = pageNumber, Fit = DestinationFit.FitH, Top = Arg(2) },
            "FitBH" => new PdfDestination { PageNumber = pageNumber, Fit = DestinationFit.FitBH, Top = Arg(2) },
            "FitV" => new PdfDestination { PageNumber = pageNumber, Fit = DestinationFit.FitV, Left = Arg(2) },
            "FitBV" => new PdfDestination { PageNumber = pageNumber, Fit = DestinationFit.FitBV, Left = Arg(2) },
            "FitR" => new PdfDestination
            {
                PageNumber = pageNumber,
                Fit = DestinationFit.FitR,
                Left = Arg(2),
                Bottom = Arg(3),
                Right = Arg(4),
                Top = Arg(5),
            },
            _ => new PdfDestination { PageNumber = pageNumber, Fit = DestinationFit.Unknown },
        };
    }
}
