// Tagged-PDF logical structure tree (/StructTreeRoot) exposure. Assistive
// technology and reflow tools use this to recover reading order, headings and
// alternate text. This parses the structure hierarchy into a simple tree; it
// does not resolve marked-content (MCID) leaves, which reference page content.

namespace BlazorPdf;

internal static class BlazorPdfStructTree
{
    public static IReadOnlyList<BlazorPdfStructElement> Build(IBlazorPdfXRef xref, BlazorPdfDict catalog)
    {
        if (xref.FetchIfRef(catalog.Get("StructTreeRoot")) is not BlazorPdfDict root)
        {
            return Array.Empty<BlazorPdfStructElement>();
        }
        var visited = new HashSet<int>();
        return ReadKids(xref, root.Get("K"), visited, 0);
    }

    private static List<BlazorPdfStructElement> ReadKids(IBlazorPdfXRef xref, object? kids, HashSet<int> visited, int depth)
    {
        var result = new List<BlazorPdfStructElement>();
        if (depth > 50)
        {
            return result;
        }

        switch (kids)
        {
            case List<object?> arr:
                foreach (var item in arr)
                {
                    AddNode(xref, item, result, visited, depth);
                }
                break;
            case not null:
                AddNode(xref, kids, result, visited, depth);
                break;
        }
        return result;
    }

    private static void AddNode(IBlazorPdfXRef xref, object? item, List<BlazorPdfStructElement> result, HashSet<int> visited, int depth)
    {
        // Marked-content leaves are plain integers or MCR/OBJR dicts; skip them —
        // the structure tree API exposes the element hierarchy, not content refs.
        if (item is double)
        {
            return;
        }
        if (item is BlazorPdfRef r && !visited.Add(r.Num))
        {
            return; // cycle guard
        }
        if (xref.FetchIfRef(item) is not BlazorPdfDict elem)
        {
            return;
        }

        string? sType = (elem.Get("S") as BlazorPdfName)?.Value;
        if (sType is null)
        {
            // A grouping node without /S (e.g. an MCR/OBJR container): recurse.
            result.AddRange(ReadKids(xref, elem.Get("K"), visited, depth + 1));
            return;
        }

        result.Add(new BlazorPdfStructElement
        {
            Type = sType,
            Alt = (elem.Get("Alt") as BlazorPdfString)?.AsText(),
            ActualText = (elem.Get("ActualText") as BlazorPdfString)?.AsText(),
            Children = ReadKids(xref, elem.Get("K"), visited, depth + 1),
        });
    }
}
