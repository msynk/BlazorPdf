// Document outline (bookmarks) parsing. Resolves the /Outlines tree and maps
// each item's destination to a page index.

namespace BlazorPdf.Core;

/// <summary>A single entry in the document outline (a bookmark).</summary>
public sealed class OutlineItem
{
    /// <summary>The bookmark label.</summary>
    public string Title { get; init; } = "";

    /// <summary>The target page (1-based), or <c>null</c> if it could not be resolved.</summary>
    public int? PageNumber { get; init; }

    /// <summary>
    /// The full resolved destination (page plus view parameters), or <c>null</c>
    /// when the bookmark has no usable destination. <see cref="PageNumber"/> is a
    /// convenience shortcut for <c>Destination?.PageNumber</c>.
    /// </summary>
    public PdfDestination? Destination { get; init; }

    /// <summary>Nested child bookmarks.</summary>
    public IReadOnlyList<OutlineItem> Children { get; init; } = Array.Empty<OutlineItem>();
}

/// <summary>
/// Builds the outline (bookmark) tree from a document catalog, resolving both
/// explicit and named destinations to page numbers.
/// </summary>
internal sealed class OutlineBuilder
{
    private readonly IXRef _xref;
    private readonly Dict _catalog;
    private readonly IReadOnlyDictionary<Dict, int> _pageIndex;

    public OutlineBuilder(IXRef xref, Dict catalog, IReadOnlyDictionary<Dict, int> pageIndex)
    {
        _xref = xref;
        _catalog = catalog;
        _pageIndex = pageIndex;
    }

    public IReadOnlyList<OutlineItem> Build()
    {
        if (_xref.FetchIfRef(_catalog.Get("Outlines")) is not Dict outlines)
        {
            return Array.Empty<OutlineItem>();
        }
        return BuildSiblings(outlines.GetRaw("First"), new HashSet<int>(), depth: 0);
    }

    private List<OutlineItem> BuildSiblings(object? firstRaw, HashSet<int> visited, int depth)
    {
        var items = new List<OutlineItem>();
        if (depth > 32)
        {
            // Very deep outline: stop descending but record why, rather than
            // silently dropping the deeper bookmarks.
            (_xref as XRef)?.Warnings.Add("Outline nesting deeper than 32 levels; deeper bookmarks omitted.");
            return items;
        }

        // Walk the linked list of siblings. Entries are normally indirect
        // references (tracked for cycle safety); a direct dictionary is tolerated.
        object? current = firstRaw;
        int guard = 0;
        while (current is not null && guard++ < 8192)
        {
            Dict? node;
            if (current is Ref r)
            {
                if (!visited.Add(r.Num))
                {
                    break;
                }
                node = _xref.Fetch(r) as Dict;
            }
            else
            {
                node = current as Dict;
            }
            if (node is null)
            {
                break;
            }

            string title = (node.Get("Title") as PdfString)?.AsText() ?? "";
            PdfDestination? dest = ResolveDestinationInfo(node);
            var children = BuildSiblings(node.GetRaw("First"), visited, depth + 1);

            items.Add(new OutlineItem
            {
                Title = title.Trim(),
                PageNumber = dest?.PageNumber,
                Destination = dest,
                Children = children,
            });

            current = node.GetRaw("Next");
        }
        return items;
    }

    private PdfDestination? ResolveDestinationInfo(Dict node)
    {
        // A bookmark may carry an explicit /Dest or a GoTo action /A with /D.
        object? dest = node.Get("Dest");
        if (dest is null && _xref.FetchIfRef(node.Get("A")) is Dict action
            && Primitives.IsName(action.Get("S"), "GoTo"))
        {
            dest = action.Get("D");
        }
        return ResolveDestination(dest);
    }

    internal PdfDestination? ResolveDestination(object? dest)
    {
        dest = _xref.FetchIfRef(dest);

        // Named destination: look it up in the name tree / dests dictionary.
        if (dest is Name name)
        {
            dest = LookupNamedDestination(name.Value);
        }
        else if (dest is PdfString s)
        {
            dest = LookupNamedDestination(s.AsLatin1());
        }
        dest = _xref.FetchIfRef(dest);

        // A destination dictionary may wrap the array under /D.
        if (dest is Dict destDict)
        {
            dest = _xref.FetchIfRef(destDict.Get("D"));
        }

        if (dest is List<object?> arr && arr.Count > 0)
        {
            int? page = ResolvePageOfTarget(arr[0]);
            return PdfDestination.FromArray(page, arr, _xref);
        }
        return null;
    }

    private int? ResolvePageOfTarget(object? target)
    {
        if (target is Ref pageRef && _xref.Fetch(pageRef) is Dict pageDict
            && _pageIndex.TryGetValue(pageDict, out int idx))
        {
            return idx + 1;
        }
        // Some destinations encode a 0-based page index directly.
        if (target is double d)
        {
            return (int)d + 1;
        }
        return null;
    }

    private object? LookupNamedDestination(string name)
    {
        // PDF 1.1 style: catalog /Dests dictionary keyed by name.
        if (_xref.FetchIfRef(_catalog.Get("Dests")) is Dict dests && dests.Has(name))
        {
            return dests.Get(name);
        }
        // PDF 1.2+ style: /Names /Dests name tree (keyed by string).
        if (_xref.FetchIfRef(_catalog.Get("Names")) is Dict names
            && _xref.FetchIfRef(names.Get("Dests")) is Dict destsTree)
        {
            return SearchNameTree(destsTree, name, depth: 0);
        }
        return null;
    }

    private object? SearchNameTree(Dict node, string key, int depth)
    {
        if (depth > 32)
        {
            return null;
        }

        // Leaf: /Names is a flat [key1 value1 key2 value2 ...] array.
        if (_xref.FetchIfRef(node.Get("Names")) is List<object?> pairs)
        {
            for (int i = 0; i + 1 < pairs.Count; i += 2)
            {
                if (_xref.FetchIfRef(pairs[i]) is PdfString s && s.AsLatin1() == key)
                {
                    return pairs[i + 1];
                }
            }
        }

        // Interior: descend into /Kids whose /Limits bracket the key.
        if (_xref.FetchIfRef(node.Get("Kids")) is List<object?> kids)
        {
            foreach (var kidObj in kids)
            {
                if (_xref.FetchIfRef(kidObj) is not Dict kid)
                {
                    continue;
                }
                if (kid.Get("Limits") is List<object?> limits && limits.Count >= 2
                    && _xref.FetchIfRef(limits[0]) is PdfString lo && _xref.FetchIfRef(limits[1]) is PdfString hi)
                {
                    if (string.CompareOrdinal(key, lo.AsLatin1()) < 0
                        || string.CompareOrdinal(key, hi.AsLatin1()) > 0)
                    {
                        continue;
                    }
                }
                object? found = SearchNameTree(kid, key, depth + 1);
                if (found is not null)
                {
                    return found;
                }
            }
        }
        return null;
    }
}
