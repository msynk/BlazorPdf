// Tagged-PDF logical structure tree (/StructTreeRoot) exposure. Assistive
// technology and reflow tools use this to recover reading order, headings and
// alternate text. This parses the structure hierarchy into a simple tree; it
// does not resolve marked-content (MCID) leaves, which reference page content.

namespace BlazorPdf.Core;

/// <summary>A node in the tagged-PDF logical structure tree.</summary>
public sealed class StructElement
{
    /// <summary>The structure type (e.g. "Document", "H1", "P", "Figure").</summary>
    public required string Type { get; init; }

    /// <summary>Alternate description text (<c>/Alt</c>), when supplied.</summary>
    public string? Alt { get; init; }

    /// <summary>Replacement text (<c>/ActualText</c>), when supplied.</summary>
    public string? ActualText { get; init; }

    /// <summary>Child structure elements (marked-content leaves are omitted).</summary>
    public IReadOnlyList<StructElement> Children { get; init; } = Array.Empty<StructElement>();
}

internal static class PdfStructTree
{
    public static IReadOnlyList<StructElement> Build(IXRef xref, Dict catalog)
    {
        if (xref.FetchIfRef(catalog.Get("StructTreeRoot")) is not Dict root)
        {
            return Array.Empty<StructElement>();
        }
        var visited = new HashSet<int>();
        return ReadKids(xref, root.Get("K"), visited, 0);
    }

    private static List<StructElement> ReadKids(IXRef xref, object? kids, HashSet<int> visited, int depth)
    {
        var result = new List<StructElement>();
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

    private static void AddNode(IXRef xref, object? item, List<StructElement> result, HashSet<int> visited, int depth)
    {
        // Marked-content leaves are plain integers or MCR/OBJR dicts; skip them —
        // the structure tree API exposes the element hierarchy, not content refs.
        if (item is double)
        {
            return;
        }
        if (item is Ref r && !visited.Add(r.Num))
        {
            return; // cycle guard
        }
        if (xref.FetchIfRef(item) is not Dict elem)
        {
            return;
        }

        string? sType = (elem.Get("S") as Name)?.Value;
        if (sType is null)
        {
            // A grouping node without /S (e.g. an MCR/OBJR container): recurse.
            result.AddRange(ReadKids(xref, elem.Get("K"), visited, depth + 1));
            return;
        }

        result.Add(new StructElement
        {
            Type = sType,
            Alt = (elem.Get("Alt") as PdfString)?.AsText(),
            ActualText = (elem.Get("ActualText") as PdfString)?.AsText(),
            Children = ReadKids(xref, elem.Get("K"), visited, depth + 1),
        });
    }
}
