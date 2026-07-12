// Core PDF object primitives: names, commands, references and dictionaries.

using System.Collections.Concurrent;

namespace BlazorPdf;

/// <summary>A set of <see cref="BlazorPdfRef"/>s, used for cycle detection while walking the object graph.</summary>
public sealed class BlazorPdfRefSet
{
    private readonly HashSet<BlazorPdfRef> _set;

    public BlazorPdfRefSet(BlazorPdfRefSet? parent = null)
        => _set = parent is null ? new HashSet<BlazorPdfRef>() : new HashSet<BlazorPdfRef>(parent._set);

    public bool Has(BlazorPdfRef reference) => _set.Contains(reference);
    public void Put(BlazorPdfRef reference) => _set.Add(reference);
    public void Remove(BlazorPdfRef reference) => _set.Remove(reference);
}
