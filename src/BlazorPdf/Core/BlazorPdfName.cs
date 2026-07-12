// Core PDF object primitives: names, commands, references and dictionaries.

using System.Collections.Concurrent;

namespace BlazorPdf;

/// <summary>
/// A PDF name object (e.g. <c>/Type</c>). Instances are interned so that name
/// equality can be checked by reference.
/// </summary>
public sealed class BlazorPdfName
{
    private static readonly ConcurrentDictionary<string, BlazorPdfName> Cache = new(StringComparer.Ordinal);

    /// <summary>The raw name without the leading slash.</summary>
    public string Value { get; }

    private BlazorPdfName(string value) => Value = value;

    /// <summary>Returns the interned <see cref="BlazorPdfName"/> for <paramref name="value"/>.</summary>
    public static BlazorPdfName Get(string value) => Cache.GetOrAdd(value, static v => new BlazorPdfName(v));

    public override string ToString() => "/" + Value;
    public override int GetHashCode() => Value.GetHashCode();
    public override bool Equals(object? obj) => ReferenceEquals(this, obj);

    internal static void ClearCache() => Cache.Clear();
}
