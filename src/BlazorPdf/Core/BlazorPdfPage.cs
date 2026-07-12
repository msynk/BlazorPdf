// The page model: inherited attributes, boxes, rotation and content streams.


namespace BlazorPdf;

/// <summary>
/// A single page of a PDF document. Exposes the geometry (media box, rotation)
/// and the resources/content needed to render it.
/// </summary>
public sealed class BlazorPdfPage
{
    private readonly IBlazorPdfXRef _xref;

    /// <summary>The page's dictionary (<c>/Type /Page</c>).</summary>
    public BlazorPdfDict Dict { get; }

    /// <summary>One-based page number within the document.</summary>
    public int Number { get; }

    /// <summary>The effective media box [llx, lly, urx, ury], with inheritance resolved.</summary>
    public double[] MediaBox { get; }

    /// <summary>
    /// The effective crop box [llx, lly, urx, ury], with inheritance resolved.
    /// Defaults to the <see cref="MediaBox"/> when no <c>/CropBox</c> is present.
    /// </summary>
    public double[] CropBox { get; }

    /// <summary>The bleed box; defaults to the <see cref="CropBox"/> when absent.</summary>
    public double[] BleedBox { get; }

    /// <summary>The trim box; defaults to the <see cref="CropBox"/> when absent.</summary>
    public double[] TrimBox { get; }

    /// <summary>The art box; defaults to the <see cref="CropBox"/> when absent.</summary>
    public double[] ArtBox { get; }

    /// <summary>Page rotation in degrees (0, 90, 180, 270), with inheritance resolved.</summary>
    public int Rotate { get; }

    /// <summary>The page's resource dictionary, with inheritance resolved.</summary>
    public BlazorPdfDict? Resources { get; }

    internal BlazorPdfPage(IBlazorPdfXRef xref, BlazorPdfDict dict, int number, double[] mediaBox, BlazorPdfDict? resources, int rotate,
        double[]? cropBox = null)
    {
        _xref = xref;
        Dict = dict;
        Number = number;
        MediaBox = mediaBox;
        Resources = resources;
        Rotate = rotate;

        // CropBox is inheritable and clipped to the MediaBox; the other boxes are
        // page-level and default to the CropBox.
        CropBox = Intersect(cropBox ?? ReadRect(dict.Get("CropBox")) ?? mediaBox, mediaBox);
        BleedBox = ReadRect(dict.Get("BleedBox")) ?? CropBox;
        TrimBox = ReadRect(dict.Get("TrimBox")) ?? CropBox;
        ArtBox = ReadRect(dict.Get("ArtBox")) ?? CropBox;
    }

    private static double[]? ReadRect(object? value)
    {
        if (value is not List<object?> arr || arr.Count < 4)
        {
            return null;
        }
        var rect = new double[4];
        for (int i = 0; i < 4; i++)
        {
            if (arr[i] is not double d)
            {
                return null;
            }
            rect[i] = d;
        }
        // Normalise so that [x0,y0] is the lower-left and [x1,y1] the upper-right.
        return
        [
            Math.Min(rect[0], rect[2]), Math.Min(rect[1], rect[3]),
            Math.Max(rect[0], rect[2]), Math.Max(rect[1], rect[3]),
        ];
    }

    private static double[] Intersect(double[] box, double[] bounds) =>
    [
        Math.Max(box[0], bounds[0]), Math.Max(box[1], bounds[1]),
        Math.Min(box[2], bounds[2]), Math.Min(box[3], bounds[3]),
    ];

    /// <summary>Page width in PDF units (points), accounting for rotation.</summary>
    public double Width => IsRotatedQuarter ? RawHeight : RawWidth;

    /// <summary>Page height in PDF units (points), accounting for rotation.</summary>
    public double Height => IsRotatedQuarter ? RawWidth : RawHeight;

    private double RawWidth => Math.Abs(MediaBox[2] - MediaBox[0]);
    private double RawHeight => Math.Abs(MediaBox[3] - MediaBox[1]);
    private bool IsRotatedQuarter => ((Rotate % 360 + 360) % 360) is 90 or 270;

    /// <summary>
    /// Returns the concatenated, decoded content stream bytes for the page.
    /// <c>/Contents</c> may be a single stream or an array of streams.
    /// </summary>
    public byte[] GetContentBytes()
    {
        object? contents = Dict.Get("Contents");
        using var output = new MemoryStream();

        switch (contents)
        {
            case BlazorPdfStream stream:
                Append(output, stream);
                break;
            case List<object?> array:
                foreach (var item in array)
                {
                    if (_xref.FetchIfRef(item) is BlazorPdfStream s)
                    {
                        Append(output, s);
                        output.WriteByte((byte)'\n');
                    }
                }
                break;
        }

        return output.ToArray();
    }

    private static void Append(MemoryStream output, BlazorPdfStream stream)
    {
        byte[] decoded = BlazorPdfStreamDecoder.Decode(stream);
        output.Write(decoded, 0, decoded.Length);
    }

    /// <summary>
    /// Extracts the page's visible text (for search or copy) without producing
    /// HTML. Positioning is approximated, so this is not a layout-faithful dump.
    /// </summary>
    public string ExtractText() => BlazorPdfTextExtractor.Extract(this, _xref);
}
