// Clean-room C# port of pdf.js `src/core/primitives.js`.
// Original: Copyright (c) Mozilla Foundation, Apache-2.0. See NOTICE.

using System.Collections.Concurrent;

namespace BlazorPdf.Core;

/// <summary>
/// Resolves indirect references (<see cref="Ref"/>) into concrete PDF objects.
/// Implemented by the cross-reference table reader. Defined here so that
/// <see cref="Dict"/> can resolve references lazily, exactly as pdf.js does.
/// </summary>
public interface IXRef
{
    /// <summary>Fetch the object a reference points to.</summary>
    object? Fetch(Ref reference, bool suppressEncryption = false);

    /// <summary>Resolve <paramref name="value"/> if it is a <see cref="Ref"/>, otherwise return it unchanged.</summary>
    object? FetchIfRef(object? value, bool suppressEncryption = false);
}

/// <summary>
/// A PDF name object (e.g. <c>/Type</c>). Instances are interned so that name
/// equality can be checked by reference, mirroring pdf.js's <c>Name.get</c> cache.
/// </summary>
public sealed class Name
{
    private static readonly ConcurrentDictionary<string, Name> Cache = new(StringComparer.Ordinal);

    /// <summary>The raw name without the leading slash.</summary>
    public string Value { get; }

    private Name(string value) => Value = value;

    /// <summary>Returns the interned <see cref="Name"/> for <paramref name="value"/>.</summary>
    public static Name Get(string value) => Cache.GetOrAdd(value, static v => new Name(v));

    public override string ToString() => "/" + Value;
    public override int GetHashCode() => Value.GetHashCode();
    public override bool Equals(object? obj) => ReferenceEquals(this, obj);

    internal static void ClearCache() => Cache.Clear();
}

/// <summary>
/// A PDF command / keyword token produced by the lexer (e.g. <c>obj</c>, <c>BT</c>,
/// <c>stream</c>). Interned like <see cref="Name"/>.
/// </summary>
public sealed class Cmd
{
    private static readonly ConcurrentDictionary<string, Cmd> Cache = new(StringComparer.Ordinal);

    /// <summary>The command keyword.</summary>
    public string Value { get; }

    private Cmd(string value) => Value = value;

    /// <summary>Returns the interned <see cref="Cmd"/> for <paramref name="value"/>.</summary>
    public static Cmd Get(string value) => Cache.GetOrAdd(value, static v => new Cmd(v));

    public override string ToString() => Value;
    public override int GetHashCode() => Value.GetHashCode();
    public override bool Equals(object? obj) => ReferenceEquals(this, obj);

    internal static void ClearCache() => Cache.Clear();
}

/// <summary>
/// An indirect reference, e.g. <c>12 0 R</c>. Value type with structural equality.
/// </summary>
public readonly struct Ref : IEquatable<Ref>
{
    /// <summary>Object number.</summary>
    public int Num { get; }

    /// <summary>Generation number.</summary>
    public int Gen { get; }

    public Ref(int num, int gen)
    {
        Num = num;
        Gen = gen;
    }

    /// <summary>A stable string key (<c>"num gen R"</c>) usable in dictionaries.</summary>
    public string ToRefString() => Gen == 0 ? $"{Num}R" : $"{Num}R{Gen}";

    public bool Equals(Ref other) => Num == other.Num && Gen == other.Gen;
    public override bool Equals(object? obj) => obj is Ref r && Equals(r);
    public override int GetHashCode() => HashCode.Combine(Num, Gen);
    public override string ToString() => $"{Num} {Gen} R";

    public static bool operator ==(Ref left, Ref right) => left.Equals(right);
    public static bool operator !=(Ref left, Ref right) => !left.Equals(right);
}

/// <summary>
/// A PDF dictionary. Keys are name strings (without the leading slash); values
/// are PDF objects. When an <see cref="IXRef"/> is attached, <see cref="Get(string)"/>
/// transparently resolves indirect references, matching pdf.js behaviour.
/// </summary>
public sealed class Dict
{
    private readonly Dictionary<string, object?> _map = new(StringComparer.Ordinal);

    /// <summary>The cross-reference table used to resolve indirect values, if any.</summary>
    public IXRef? XRef { get; set; }

    /// <summary>An immutable, empty dictionary.</summary>
    public static readonly Dict Empty = new();

    public Dict(IXRef? xref = null) => XRef = xref;

    /// <summary>Number of entries.</summary>
    public int Count => _map.Count;

    /// <summary>All keys present in the dictionary.</summary>
    public IEnumerable<string> Keys => _map.Keys;

    /// <summary>The raw (unresolved) entries.</summary>
    public IReadOnlyDictionary<string, object?> RawEntries => _map;

    public void Set(string key, object? value) => _map[key] = value;
    public void Set(Name key, object? value) => _map[key.Value] = value;

    public bool Has(string key) => _map.ContainsKey(key);

    /// <summary>Gets a value, resolving an indirect reference through <see cref="XRef"/> if present.</summary>
    public object? Get(string key)
    {
        if (!_map.TryGetValue(key, out var value))
        {
            return null;
        }
        return value is Ref r && XRef is not null ? XRef.Fetch(r) : value;
    }

    /// <summary>
    /// Gets the first present value among <paramref name="key1"/>, <paramref name="key2"/>
    /// (e.g. abbreviated inline-image keys). Mirrors pdf.js's multi-key <c>Dict.get</c>.
    /// </summary>
    public object? Get(string key1, string key2)
        => _map.ContainsKey(key1) ? Get(key1) : Get(key2);

    public object? Get(string key1, string key2, string key3)
        => _map.ContainsKey(key1) ? Get(key1)
         : _map.ContainsKey(key2) ? Get(key2)
         : Get(key3);

    /// <summary>Gets a value without resolving references.</summary>
    public object? GetRaw(string key) => _map.TryGetValue(key, out var v) ? v : null;

    /// <summary>Gets a strongly typed value, or <c>default</c> if absent / wrong type.</summary>
    public T? GetValue<T>(string key) => Get(key) is T t ? t : default;

    public override string ToString() => $"<< {string.Join(" ", _map.Keys.Select(k => "/" + k))} >>";
}

/// <summary>A set of <see cref="Ref"/>s, used for cycle detection while walking the object graph.</summary>
public sealed class RefSet
{
    private readonly HashSet<Ref> _set;

    public RefSet(RefSet? parent = null)
        => _set = parent is null ? new HashSet<Ref>() : new HashSet<Ref>(parent._set);

    public bool Has(Ref reference) => _set.Contains(reference);
    public void Put(Ref reference) => _set.Add(reference);
    public void Remove(Ref reference) => _set.Remove(reference);
}

/// <summary>A cache keyed by <see cref="Ref"/>, mirroring pdf.js's <c>RefSetCache</c>.</summary>
public sealed class RefSetCache<TValue>
{
    private readonly Dictionary<Ref, TValue> _map = new();

    public int Size => _map.Count;
    public bool Has(Ref reference) => _map.ContainsKey(reference);
    public TValue? Get(Ref reference) => _map.TryGetValue(reference, out var v) ? v : default;
    public void Put(Ref reference, TValue value) => _map[reference] = value;
    public void Clear() => _map.Clear();
}

/// <summary>
/// Singleton sentinel objects used throughout parsing, equivalent to the
/// frozen singletons declared in pdf.js primitives.js.
/// </summary>
public static class Primitives
{
    /// <summary>The PDF <c>null</c> object (distinct from a missing key / CLR null).</summary>
    public static readonly object Null = new NullObject();

    /// <summary>End-of-file / end-of-stream marker emitted by the lexer.</summary>
    public static readonly object EOF = new EofObject();

    /// <summary>Marks a circular indirect reference encountered during fetch.</summary>
    public static readonly object CircularRef = new CircularRefObject();

    public static bool IsName(object? obj) => obj is Name;
    public static bool IsName(object? obj, string value) => obj is Name n && n.Value == value;
    public static bool IsCmd(object? obj) => obj is Cmd;
    public static bool IsCmd(object? obj, string value) => obj is Cmd c && c.Value == value;
    public static bool IsDict(object? obj) => obj is Dict;
    public static bool IsDict(object? obj, string type)
        => obj is Dict d && IsName(d.Get("Type"), type);

    /// <summary>Clears the interned <see cref="Name"/> and <see cref="Cmd"/> caches.</summary>
    public static void ClearPrimitiveCaches()
    {
        Name.ClearCache();
        Cmd.ClearCache();
    }

    private sealed class NullObject { public override string ToString() => "null"; }
    private sealed class EofObject { public override string ToString() => "EOF"; }
    private sealed class CircularRefObject { public override string ToString() => "<circular>"; }
}
