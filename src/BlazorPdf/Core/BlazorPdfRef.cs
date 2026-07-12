// Core PDF object primitives: names, commands, references and dictionaries.

using System.Collections.Concurrent;

namespace BlazorPdf;

/// <summary>
/// An indirect reference, e.g. <c>12 0 R</c>. Value type with structural equality.
/// </summary>
public readonly struct BlazorPdfRef : IEquatable<BlazorPdfRef>
{
    /// <summary>Object number.</summary>
    public int Num { get; }

    /// <summary>Generation number.</summary>
    public int Gen { get; }

    public BlazorPdfRef(int num, int gen)
    {
        Num = num;
        Gen = gen;
    }

    /// <summary>A stable string key (<c>"num gen R"</c>) usable in dictionaries.</summary>
    public string ToRefString() => Gen == 0 ? $"{Num}R" : $"{Num}R{Gen}";

    public bool Equals(BlazorPdfRef other) => Num == other.Num && Gen == other.Gen;
    public override bool Equals(object? obj) => obj is BlazorPdfRef r && Equals(r);
    public override int GetHashCode() => HashCode.Combine(Num, Gen);
    public override string ToString() => $"{Num} {Gen} R";

    public static bool operator ==(BlazorPdfRef left, BlazorPdfRef right) => left.Equals(right);
    public static bool operator !=(BlazorPdfRef left, BlazorPdfRef right) => !left.Equals(right);
}
