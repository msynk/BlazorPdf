// Core PDF object primitives: names, commands, references and dictionaries.

using System.Collections.Concurrent;

namespace BlazorPdf;

/// <summary>A cache keyed by <see cref="BlazorPdfRef"/>.</summary>
public sealed class BlazorPdfRefSetCache<TValue>
{
    private readonly Dictionary<BlazorPdfRef, TValue> _map = new();

    public int Size => _map.Count;
    public bool Has(BlazorPdfRef reference) => _map.ContainsKey(reference);
    public TValue? Get(BlazorPdfRef reference) => _map.TryGetValue(reference, out var v) ? v : default;
    public void Put(BlazorPdfRef reference, TValue value) => _map[reference] = value;
    public void Clear() => _map.Clear();
}
