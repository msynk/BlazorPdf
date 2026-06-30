using System.Text;

namespace BlazorPdf.Engine;

/// <summary>
/// A parsed PDF document. Provides access to pages, metadata, text extraction and
/// search. Loading is resilient: the object table is rebuilt by scanning the file,
/// which tolerates broken cross-reference tables and incremental updates.
/// </summary>
public sealed class PdfDocument
{
    private readonly byte[] _data;
    private readonly Dictionary<int, int> _offsets = [];
    private readonly Dictionary<int, PdfObject?> _cache = [];
    private PdfDictionary _trailer = new();
    private List<PdfPage> _pages = [];

    private PdfDocument(byte[] data) => _data = data;

    /// <summary>The document trailer dictionary.</summary>
    public PdfDictionary Trailer => _trailer;

    /// <summary>The document catalog (<c>/Type /Catalog</c>), if found.</summary>
    public PdfDictionary? Catalog { get; private set; }

    /// <summary>The pages in document order.</summary>
    public IReadOnlyList<PdfPage> Pages => _pages;

    /// <summary>The number of pages.</summary>
    public int PageCount => _pages.Count;

    /// <summary>True when the document declares encryption (not yet supported for decoding).</summary>
    public bool IsEncrypted { get; private set; }

    /// <summary>Parses a PDF document from bytes.</summary>
    public static PdfDocument Load(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length < 5 || PdfParser.IndexOf(data, "%PDF-", 0) < 0)
        {
            throw new PdfParseException("The data does not begin with a PDF header.");
        }

        var doc = new PdfDocument(data);
        doc.BuildObjectMap();
        doc.FindTrailer();
        doc.IsEncrypted = doc.Resolve(doc._trailer.Get("Encrypt")) is not null;
        doc.BuildPages();
        return doc;
    }

    /// <summary>Resolves indirect references to their direct object.</summary>
    public PdfObject? Resolve(PdfObject? obj)
    {
        var guard = 0;
        while (obj is PdfReference reference && guard++ < 100)
        {
            obj = GetObject(reference.Number);
        }
        return obj;
    }

    /// <summary>Resolves and casts a dictionary entry.</summary>
    public T? ResolveAs<T>(PdfObject? obj) where T : PdfObject => Resolve(obj) as T;

    private PdfObject? GetObject(int number)
    {
        if (_cache.TryGetValue(number, out var cached))
        {
            return cached;
        }

        _cache[number] = PdfNull.Instance; // guard against cycles during parse

        if (!_offsets.TryGetValue(number, out var offset))
        {
            return null;
        }

        var lexer = new PdfLexer(_data, offset);
        var num = lexer.Next();   // object number
        var gen = lexer.Next();   // generation
        var keyword = lexer.Next(); // 'obj'
        if (!keyword.IsKeyword("obj"))
        {
            return null;
        }

        var parser = new PdfParser(lexer);
        var result = parser.ParseObject();
        _cache[number] = result;
        return result;
    }

    private void BuildObjectMap()
    {
        // Scan for "<int> <int> obj" definitions. The last definition of a given
        // object number wins (handles incremental updates).
        var index = 0;
        while (true)
        {
            var objPos = PdfParser.IndexOf(_data, "obj", index);
            if (objPos < 0) break;
            index = objPos + 3;

            // 'obj' must be a standalone keyword.
            if (objPos + 3 < _data.Length && IsRegular(_data[objPos + 3]))
            {
                continue;
            }

            if (TryReadDefinitionHeader(objPos, out var number, out var start))
            {
                _offsets[number] = start;
            }
        }
    }

    private bool TryReadDefinitionHeader(int objPos, out int number, out int start)
    {
        number = 0;
        start = 0;

        var p = objPos - 1;
        p = SkipWhitespaceBackward(p);
        var genEnd = p + 1;
        p = SkipDigitsBackward(p);
        var genStart = p + 1;
        if (genStart >= genEnd) return false;

        p = SkipWhitespaceBackward(p);
        var numEnd = p + 1;
        p = SkipDigitsBackward(p);
        var numStart = p + 1;
        if (numStart >= numEnd) return false;

        // Ensure the character before the number is not a regular char (token boundary).
        if (numStart > 0 && IsRegular(_data[numStart - 1]) && _data[numStart - 1] != (byte)'>')
        {
            // tolerate; many files have whitespace, but guard against digit run-ons
        }

        var text = Encoding.ASCII.GetString(_data, numStart, numEnd - numStart);
        if (!int.TryParse(text, out number)) return false;

        start = numStart;
        return true;
    }

    private int SkipWhitespaceBackward(int p)
    {
        while (p >= 0 && _data[p] is 0 or 9 or 10 or 12 or 13 or 32) p--;
        return p;
    }

    private int SkipDigitsBackward(int p)
    {
        while (p >= 0 && _data[p] >= (byte)'0' && _data[p] <= (byte)'9') p--;
        return p;
    }

    private static bool IsRegular(byte b) =>
        b is not (0 or 9 or 10 or 12 or 13 or 32) &&
        b is not ((byte)'(' or (byte)')' or (byte)'<' or (byte)'>' or
                  (byte)'[' or (byte)']' or (byte)'{' or (byte)'}' or
                  (byte)'/' or (byte)'%');

    private void FindTrailer()
    {
        // Prefer a classic trailer dictionary containing /Root. Scan all of them and
        // keep the one that resolves to a catalog.
        var index = 0;
        PdfDictionary? best = null;

        while (true)
        {
            var pos = PdfParser.IndexOf(_data, "trailer", index);
            if (pos < 0) break;
            index = pos + 7;

            var lexer = new PdfLexer(_data, pos + 7);
            var parser = new PdfParser(lexer);
            if (parser.ParseObject() is PdfDictionary dict)
            {
                MergeTrailer(dict);
                if (dict.Has("Root")) best = dict;
            }
        }

        if (best is not null && Resolve(best.Get("Root")) is PdfDictionary catalog)
        {
            Catalog = catalog;
            return;
        }

        // Cross-reference streams (PDF 1.5+) keep /Root in an /XRef stream dict.
        foreach (var (number, _) in _offsets)
        {
            if (GetObject(number) is PdfStream { Dictionary: var d } &&
                d.Get("Type") is PdfName { Value: "XRef" })
            {
                MergeTrailer(d);
                if (Resolve(d.Get("Root")) is PdfDictionary cat)
                {
                    Catalog = cat;
                    return;
                }
            }
        }

        // Last resort: locate any object that is itself a catalog.
        foreach (var (number, _) in _offsets)
        {
            if (Resolve(new PdfReference(number, 0)) is PdfDictionary dict &&
                dict.Get("Type") is PdfName { Value: "Catalog" })
            {
                Catalog = dict;
                _trailer.Items["Root"] = new PdfReference(number, 0);
                return;
            }
        }
    }

    private void MergeTrailer(PdfDictionary dict)
    {
        foreach (var (key, value) in dict.Items)
        {
            _trailer.Items.TryAdd(key, value);
        }
    }

    private void BuildPages()
    {
        _pages = [];
        if (Catalog?.Get("Pages") is not { } pagesRef)
        {
            return;
        }

        var root = Resolve(pagesRef) as PdfDictionary;
        if (root is null) return;

        var visited = new HashSet<int>();
        WalkPageTree(root, new InheritedAttributes(), visited);
    }

    private void WalkPageTree(PdfDictionary node, InheritedAttributes inherited, HashSet<int> visited)
    {
        if (_pages.Count > 50000) return; // safety cap

        var merged = inherited.MergeFrom(node, this);
        var type = (node.Get("Type") as PdfName)?.Value;

        if (type == "Page" || (type is null && !node.Has("Kids")))
        {
            _pages.Add(new PdfPage(this, node, merged));
            return;
        }

        if (Resolve(node.Get("Kids")) is not PdfArray kids)
        {
            return;
        }

        foreach (var kidRef in kids.Items)
        {
            if (kidRef is PdfReference r && !visited.Add(r.Number))
            {
                continue; // cycle guard
            }
            if (Resolve(kidRef) is PdfDictionary kid)
            {
                WalkPageTree(kid, merged, visited);
            }
        }
    }

    /// <summary>Extracts the visible text of every page, separated by form feeds.</summary>
    public string ExtractText()
    {
        var sb = new StringBuilder();
        for (var i = 0; i < _pages.Count; i++)
        {
            if (i > 0) sb.Append('\f');
            sb.Append(_pages[i].ExtractText());
        }
        return sb.ToString();
    }

    /// <summary>
    /// Searches every page for <paramref name="query"/> and returns one result per
    /// matching page with the number of occurrences.
    /// </summary>
    public IReadOnlyList<PdfSearchResult> Search(string query, bool caseSensitive = false)
    {
        var results = new List<PdfSearchResult>();
        if (string.IsNullOrEmpty(query)) return results;

        var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        for (var i = 0; i < _pages.Count; i++)
        {
            var text = _pages[i].ExtractText();
            var count = 0;
            var from = 0;
            while (true)
            {
                var at = text.IndexOf(query, from, comparison);
                if (at < 0) break;
                count++;
                from = at + query.Length;
            }
            if (count > 0)
            {
                results.Add(new PdfSearchResult(i + 1, count));
            }
        }
        return results;
    }
}

/// <summary>A single page search result.</summary>
/// <param name="PageNumber">The 1-based page number.</param>
/// <param name="Occurrences">How many times the query appears on the page.</param>
public readonly record struct PdfSearchResult(int PageNumber, int Occurrences);

/// <summary>Raised when a document cannot be parsed.</summary>
public sealed class PdfParseException(string message) : Exception(message);
