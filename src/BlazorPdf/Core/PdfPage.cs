// Clean-room C# port of the page model from pdf.js `src/core/page.js`. See NOTICE.

using BlazorPdf.Core.Filters;

namespace BlazorPdf.Core;

/// <summary>
/// A single page of a PDF document. Exposes the geometry (media box, rotation)
/// and the resources/content needed to render it.
/// </summary>
public sealed class PdfPage
{
    private readonly IXRef _xref;

    /// <summary>The page's dictionary (<c>/Type /Page</c>).</summary>
    public Dict Dict { get; }

    /// <summary>One-based page number within the document.</summary>
    public int Number { get; }

    /// <summary>The effective media box [llx, lly, urx, ury], with inheritance resolved.</summary>
    public double[] MediaBox { get; }

    /// <summary>Page rotation in degrees (0, 90, 180, 270), with inheritance resolved.</summary>
    public int Rotate { get; }

    /// <summary>The page's resource dictionary, with inheritance resolved.</summary>
    public Dict? Resources { get; }

    internal PdfPage(IXRef xref, Dict dict, int number, double[] mediaBox, Dict? resources, int rotate)
    {
        _xref = xref;
        Dict = dict;
        Number = number;
        MediaBox = mediaBox;
        Resources = resources;
        Rotate = rotate;
    }

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
            case PdfStream stream:
                Append(output, stream);
                break;
            case List<object?> array:
                foreach (var item in array)
                {
                    if (_xref.FetchIfRef(item) is PdfStream s)
                    {
                        Append(output, s);
                        output.WriteByte((byte)'\n');
                    }
                }
                break;
        }

        return output.ToArray();
    }

    private static void Append(MemoryStream output, PdfStream stream)
    {
        byte[] decoded = StreamDecoder.Decode(stream);
        output.Write(decoded, 0, decoded.Length);
    }
}
