using System.Globalization;
using System.Text;

namespace BlazorPdf.Engine;

/// <summary>Base type for all parsed PDF objects.</summary>
public abstract class PdfObject
{
}

/// <summary>The PDF <c>null</c> object.</summary>
public sealed class PdfNull : PdfObject
{
    /// <summary>Shared singleton instance.</summary>
    public static readonly PdfNull Instance = new();
    private PdfNull() { }
}

/// <summary>A PDF boolean.</summary>
public sealed class PdfBoolean(bool value) : PdfObject
{
    /// <summary>The boolean value.</summary>
    public bool Value { get; } = value;
}

/// <summary>A PDF numeric object (integer or real).</summary>
public sealed class PdfNumber(double value, bool isInteger) : PdfObject
{
    /// <summary>The numeric value.</summary>
    public double Value { get; } = value;

    /// <summary>True when the token was an integer literal.</summary>
    public bool IsInteger { get; } = isInteger;

    /// <summary>The value truncated to an <see cref="int"/>.</summary>
    public int AsInt => (int)Value;

    /// <inheritdoc />
    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>A PDF string object (literal or hexadecimal). Stored as raw bytes.</summary>
public sealed class PdfString(byte[] bytes) : PdfObject
{
    /// <summary>The raw bytes of the string.</summary>
    public byte[] Bytes { get; } = bytes;

    /// <summary>
    /// Decodes the string as text. Handles UTF-16 BE byte-order marks (used by
    /// document metadata); otherwise falls back to Latin-1/PdfDocEncoding.
    /// </summary>
    public string AsText()
    {
        if (Bytes.Length >= 2 && Bytes[0] == 0xFE && Bytes[1] == 0xFF)
        {
            return Encoding.BigEndianUnicode.GetString(Bytes, 2, Bytes.Length - 2);
        }
        return Encoding.Latin1.GetString(Bytes);
    }

    /// <inheritdoc />
    public override string ToString() => AsText();
}

/// <summary>A PDF name object (without the leading slash).</summary>
public sealed class PdfName(string value) : PdfObject
{
    /// <summary>The name text, slash excluded.</summary>
    public string Value { get; } = value;

    /// <inheritdoc />
    public override string ToString() => "/" + Value;
}

/// <summary>A PDF array.</summary>
public sealed class PdfArray : PdfObject
{
    /// <summary>The array elements.</summary>
    public List<PdfObject> Items { get; } = [];

    /// <summary>The number of elements.</summary>
    public int Count => Items.Count;

    /// <summary>Gets the element at the given index.</summary>
    public PdfObject this[int index] => Items[index];
}

/// <summary>A PDF dictionary keyed by name (slash excluded).</summary>
public sealed class PdfDictionary : PdfObject
{
    /// <summary>The dictionary entries.</summary>
    public Dictionary<string, PdfObject> Items { get; } = new(StringComparer.Ordinal);

    /// <summary>Gets a raw (possibly indirect) value, or null when absent.</summary>
    public PdfObject? Get(string key) => Items.TryGetValue(key, out var v) ? v : null;

    /// <summary>True when the dictionary contains the given key.</summary>
    public bool Has(string key) => Items.ContainsKey(key);
}

/// <summary>A PDF stream: a dictionary followed by raw (still-encoded) bytes.</summary>
public sealed class PdfStream(PdfDictionary dictionary, byte[] rawData) : PdfObject
{
    /// <summary>The stream dictionary.</summary>
    public PdfDictionary Dictionary { get; } = dictionary;

    /// <summary>The raw, undecoded stream bytes.</summary>
    public byte[] RawData { get; } = rawData;
}

/// <summary>An indirect reference (<c>N G R</c>).</summary>
public sealed class PdfReference(int number, int generation) : PdfObject
{
    /// <summary>The referenced object number.</summary>
    public int Number { get; } = number;

    /// <summary>The referenced generation number.</summary>
    public int Generation { get; } = generation;

    /// <inheritdoc />
    public override string ToString() => $"{Number} {Generation} R";
}
