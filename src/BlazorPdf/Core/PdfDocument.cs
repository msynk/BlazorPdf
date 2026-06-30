// Clean-room C# port combining pdf.js `src/core/document.js` (PDFDocument) and
// `src/core/catalog.js` page-tree traversal. See NOTICE.

namespace BlazorPdf.Core;

/// <summary>
/// A parsed PDF document: cross-reference resolution, the catalog, and the
/// flattened list of pages. This is the entry point of the C# engine and the
/// equivalent of pdf.js's <c>PDFDocument</c>.
/// </summary>
public sealed class PdfDocument
{
    private readonly XRef _xref;
    private List<PdfPage>? _pages;

    private PdfDocument(XRef xref) => _xref = xref;

    /// <summary>The cross-reference reader for this document.</summary>
    public XRef XRef => _xref;

    /// <summary>The document catalog (<c>/Root</c>).</summary>
    public Dict Catalog { get; private set; } = Dict.Empty;

    /// <summary>The PDF version declared in the header (e.g. "1.7"), if present.</summary>
    public string? Version { get; private set; }

    /// <summary><c>true</c> when the trailer declares an <c>/Encrypt</c> dictionary.</summary>
    public bool IsEncrypted => _xref.Trailer?.Has("Encrypt") == true;

    /// <summary>The document's pages in order.</summary>
    public IReadOnlyList<PdfPage> Pages => _pages ??= BuildPages();

    /// <summary>Number of pages.</summary>
    public int PageCount => Pages.Count;

    /// <summary>Parses <paramref name="bytes"/> into a document model.</summary>
    public static PdfDocument Load(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        var xref = new XRef(bytes);
        xref.Parse();

        var document = new PdfDocument(xref)
        {
            Version = ReadHeaderVersion(bytes),
        };

        document.Catalog = xref.Root
            ?? throw new PdfFormatException("Document catalog (/Root) not found.");

        return document;
    }

    private List<PdfPage> BuildPages()
    {
        var pages = new List<PdfPage>();
        if (Catalog.Get("Pages") is not Dict root)
        {
            return pages;
        }

        var inherited = new InheritedAttributes();
        var visited = new HashSet<int>();
        Traverse(root, inherited, pages, visited, depth: 0);
        return pages;
    }

    private readonly struct InheritedAttributes
    {
        public double[]? MediaBox { get; init; }
        public Dict? Resources { get; init; }
        public int? Rotate { get; init; }

        public InheritedAttributes With(Dict node)
        {
            return new InheritedAttributes
            {
                MediaBox = ReadRectangle(node.Get("MediaBox")) ?? MediaBox,
                Resources = node.Get("Resources") as Dict ?? Resources,
                Rotate = node.Get("Rotate") is double r ? NormalizeRotation((int)r) : Rotate,
            };
        }
    }

    private void Traverse(Dict node, InheritedAttributes inherited,
        List<PdfPage> pages, HashSet<int> visited, int depth)
    {
        if (depth > 64)
        {
            throw new PdfFormatException("Page tree nesting too deep.");
        }

        InheritedAttributes current = inherited.With(node);
        object? typeObj = node.Get("Type");

        // A node with /Kids is an interior /Pages node; otherwise it's a leaf page.
        if (node.Get("Kids") is List<object?> kids && !Primitives.IsName(typeObj, "Page"))
        {
            foreach (var kid in kids)
            {
                if (_xref.FetchIfRef(kid) is Dict child)
                {
                    Traverse(child, current, pages, visited, depth + 1);
                }
            }
            return;
        }

        double[] mediaBox = current.MediaBox ?? [0, 0, 612, 792]; // US Letter default
        pages.Add(new PdfPage(
            _xref,
            node,
            pages.Count + 1,
            mediaBox,
            current.Resources,
            current.Rotate ?? 0));
    }

    private static double[]? ReadRectangle(object? value)
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
        return rect;
    }

    private static int NormalizeRotation(int rotate)
    {
        int r = rotate % 360;
        if (r < 0)
        {
            r += 360;
        }
        return r;
    }

    private static string? ReadHeaderVersion(byte[] bytes)
    {
        // Header looks like "%PDF-1.7" within the first bytes of the file.
        int limit = Math.Min(bytes.Length, 1024);
        ReadOnlySpan<byte> prefix = "%PDF-"u8;
        for (int i = 0; i + prefix.Length < limit; i++)
        {
            bool match = true;
            for (int k = 0; k < prefix.Length; k++)
            {
                if (bytes[i + k] != prefix[k])
                {
                    match = false;
                    break;
                }
            }
            if (!match)
            {
                continue;
            }
            int start = i + prefix.Length;
            int end = start;
            while (end < limit && bytes[end] is not (0x0D or 0x0A or 0x20))
            {
                end++;
            }
            return System.Text.Encoding.ASCII.GetString(bytes, start, end - start);
        }
        return null;
    }
}
