// Part of the clean-room pdf.js port. See NOTICE.

using System.Text;

namespace BlazorPdf.Core;

/// <summary>
/// A PDF string object (literal <c>(...)</c> or hexadecimal <c>&lt;...&gt;</c>).
/// PDF strings are byte sequences, not Unicode text, so the raw bytes are kept
/// and interpreted by callers (e.g. for PDFDocEncoding or UTF-16BE text).
/// </summary>
public sealed class PdfString
{
    /// <summary>The raw decoded bytes of the string.</summary>
    public byte[] Bytes { get; }

    public PdfString(byte[] bytes) => Bytes = bytes ?? [];

    public int Length => Bytes.Length;

    /// <summary>Interprets the bytes as Latin-1, matching pdf.js's binary-string model.</summary>
    public string AsLatin1() => Encoding.Latin1.GetString(Bytes);

    /// <summary>
    /// Decodes the string as PDF text: UTF-16BE when a BOM (<c>FE FF</c>) is
    /// present, otherwise PDFDocEncoding is approximated via Latin-1.
    /// </summary>
    public string AsText()
    {
        if (Bytes.Length >= 2 && Bytes[0] == 0xFE && Bytes[1] == 0xFF)
        {
            return Encoding.BigEndianUnicode.GetString(Bytes, 2, Bytes.Length - 2);
        }
        return AsLatin1();
    }

    public override string ToString() => AsText();
}
