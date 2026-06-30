namespace BlazorPdf.Engine;

/// <summary>
/// Attributes that pages inherit from their ancestors in the page tree
/// (<c>MediaBox</c>, <c>CropBox</c>, <c>Resources</c>, <c>Rotate</c>).
/// </summary>
internal readonly struct InheritedAttributes
{
    public PdfArray? MediaBox { get; private init; }
    public PdfArray? CropBox { get; private init; }
    public PdfDictionary? Resources { get; private init; }
    public int Rotate { get; private init; }

    public InheritedAttributes MergeFrom(PdfDictionary node, PdfDocument doc)
    {
        var rotate = Rotate;
        if (doc.Resolve(node.Get("Rotate")) is PdfNumber r)
        {
            rotate = ((r.AsInt % 360) + 360) % 360;
        }

        return new InheritedAttributes
        {
            MediaBox = doc.ResolveAs<PdfArray>(node.Get("MediaBox")) ?? MediaBox,
            CropBox = doc.ResolveAs<PdfArray>(node.Get("CropBox")) ?? CropBox,
            Resources = doc.ResolveAs<PdfDictionary>(node.Get("Resources")) ?? Resources,
            Rotate = rotate,
        };
    }
}

/// <summary>A single page of a <see cref="PdfDocument"/>.</summary>
public sealed class PdfPage
{
    private readonly PdfDocument _document;
    private readonly PdfDictionary _dict;
    private readonly InheritedAttributes _inherited;
    private string? _cachedText;

    internal PdfPage(PdfDocument document, PdfDictionary dict, InheritedAttributes inherited)
    {
        _document = document;
        _dict = dict;
        _inherited = inherited;
    }

    /// <summary>The page dictionary.</summary>
    public PdfDictionary Dictionary => _dict;

    internal PdfDocument Document => _document;

    internal PdfDictionary? Resources => _inherited.Resources;

    /// <summary>The page rotation in degrees (0, 90, 180 or 270).</summary>
    public int Rotation => _inherited.Rotate;

    /// <summary>The media box as <c>[llx, lly, urx, ury]</c> in PDF points (1/72 inch).</summary>
    public double[] MediaBox => ReadBox(_inherited.MediaBox) ?? [0, 0, 612, 792];

    /// <summary>The page width in points, accounting for rotation.</summary>
    public double Width
    {
        get
        {
            var box = MediaBox;
            var w = Math.Abs(box[2] - box[0]);
            var h = Math.Abs(box[3] - box[1]);
            return Rotation is 90 or 270 ? h : w;
        }
    }

    /// <summary>The page height in points, accounting for rotation.</summary>
    public double Height
    {
        get
        {
            var box = MediaBox;
            var w = Math.Abs(box[2] - box[0]);
            var h = Math.Abs(box[3] - box[1]);
            return Rotation is 90 or 270 ? w : h;
        }
    }

    private double[]? ReadBox(PdfArray? array)
    {
        if (array is null || array.Count < 4) return null;
        var box = new double[4];
        for (var i = 0; i < 4; i++)
        {
            if (_document.Resolve(array[i]) is PdfNumber n) box[i] = n.Value;
            else return null;
        }
        return box;
    }

    /// <summary>Returns the decoded, concatenated content-stream bytes for this page.</summary>
    public byte[] GetContentBytes()
    {
        var contents = _document.Resolve(_dict.Get("Contents"));
        using var output = new MemoryStream();

        switch (contents)
        {
            case PdfStream stream:
                output.Write(PdfFilters.Decode(stream, _document));
                break;
            case PdfArray array:
                foreach (var item in array.Items)
                {
                    if (_document.Resolve(item) is PdfStream s)
                    {
                        output.Write(PdfFilters.Decode(s, _document));
                        output.WriteByte((byte)'\n');
                    }
                }
                break;
        }

        return output.ToArray();
    }

    /// <summary>Extracts the visible text of this page (best-effort, cached).</summary>
    public string ExtractText()
    {
        return _cachedText ??= PdfTextExtractor.Extract(GetContentBytes());
    }
}
