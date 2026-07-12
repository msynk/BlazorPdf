// Core PDF object primitives: names, commands, references and dictionaries.

using System.Collections.Concurrent;

namespace BlazorPdf;

/// <summary>
/// A PDF command / keyword token produced by the lexer (e.g. <c>obj</c>, <c>BT</c>,
/// <c>stream</c>). Interned like <see cref="BlazorPdfName"/>.
/// </summary>
public sealed class BlazorPdfCmd
{
    private static readonly ConcurrentDictionary<string, BlazorPdfCmd> Cache = new(StringComparer.Ordinal);

    /// <summary>The command keyword.</summary>
    public string Value { get; }

    private BlazorPdfCmd(string value) => Value = value;

    /// <summary>Returns the interned <see cref="BlazorPdfCmd"/> for <paramref name="value"/>.</summary>
    public static BlazorPdfCmd Get(string value) => Cache.GetOrAdd(value, static v => new BlazorPdfCmd(v));

    public override string ToString() => Value;
    public override int GetHashCode() => Value.GetHashCode();
    public override bool Equals(object? obj) => ReferenceEquals(this, obj);

    internal static void ClearCache() => Cache.Clear();
}
