using System.Globalization;
using System.Text;

namespace BlazorPdf.Engine;

internal enum PdfTokenKind
{
    Eof,
    Number,
    Name,
    String,
    ArrayStart,
    ArrayEnd,
    DictStart,
    DictEnd,
    Keyword,
}

internal readonly struct PdfToken(PdfTokenKind kind, double number = 0, string? text = null, byte[]? bytes = null)
{
    public PdfTokenKind Kind { get; } = kind;
    public double Number { get; } = number;
    public string? Text { get; } = text;
    public byte[]? Bytes { get; } = bytes;

    public bool IsKeyword(string value) => Kind == PdfTokenKind.Keyword && Text == value;
}

/// <summary>
/// Tokenizes PDF syntax from a byte buffer. Shared by the document parser and the
/// content-stream interpreter.
/// </summary>
internal sealed class PdfLexer(byte[] data, int position = 0)
{
    private readonly byte[] _data = data;

    public int Position { get; set; } = position;

    public byte[] Data => _data;

    public int Length => _data.Length;

    private static bool IsWhite(byte b) => b is 0 or 9 or 10 or 12 or 13 or 32;

    private static bool IsDelimiter(byte b) => b is
        (byte)'(' or (byte)')' or (byte)'<' or (byte)'>' or
        (byte)'[' or (byte)']' or (byte)'{' or (byte)'}' or
        (byte)'/' or (byte)'%';

    private static bool IsRegular(byte b) => !IsWhite(b) && !IsDelimiter(b);

    public void SkipWhitespace()
    {
        while (Position < _data.Length)
        {
            var b = _data[Position];
            if (b == (byte)'%')
            {
                while (Position < _data.Length && _data[Position] != 10 && _data[Position] != 13)
                {
                    Position++;
                }
            }
            else if (IsWhite(b))
            {
                Position++;
            }
            else
            {
                break;
            }
        }
    }

    public PdfToken Next()
    {
        SkipWhitespace();
        if (Position >= _data.Length)
        {
            return new PdfToken(PdfTokenKind.Eof);
        }

        var b = _data[Position];
        switch (b)
        {
            case (byte)'[':
                Position++;
                return new PdfToken(PdfTokenKind.ArrayStart);
            case (byte)']':
                Position++;
                return new PdfToken(PdfTokenKind.ArrayEnd);
            case (byte)'<':
                if (Position + 1 < _data.Length && _data[Position + 1] == (byte)'<')
                {
                    Position += 2;
                    return new PdfToken(PdfTokenKind.DictStart);
                }
                return ReadHexString();
            case (byte)'>':
                if (Position + 1 < _data.Length && _data[Position + 1] == (byte)'>')
                {
                    Position += 2;
                    return new PdfToken(PdfTokenKind.DictEnd);
                }
                Position++; // stray '>'; skip
                return Next();
            case (byte)'(':
                return ReadLiteralString();
            case (byte)'/':
                return ReadName();
        }

        if (b == (byte)'+' || b == (byte)'-' || b == (byte)'.' || (b >= (byte)'0' && b <= (byte)'9'))
        {
            return ReadNumber();
        }

        return ReadKeyword();
    }

    private PdfToken ReadNumber()
    {
        var start = Position;
        while (Position < _data.Length)
        {
            var b = _data[Position];
            if (b == (byte)'+' || b == (byte)'-' || b == (byte)'.' || b == (byte)'e' || b == (byte)'E' ||
                (b >= (byte)'0' && b <= (byte)'9'))
            {
                Position++;
            }
            else
            {
                break;
            }
        }

        var text = Encoding.ASCII.GetString(_data, start, Position - start);
        var isInteger = text.IndexOf('.') < 0 && text.IndexOf('e') < 0 && text.IndexOf('E') < 0;
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value);
        return new PdfToken(PdfTokenKind.Number, value, isInteger ? "int" : null);
    }

    private PdfToken ReadName()
    {
        Position++; // consume '/'
        var sb = new StringBuilder();
        while (Position < _data.Length && IsRegular(_data[Position]))
        {
            var b = _data[Position];
            if (b == (byte)'#' && Position + 2 < _data.Length &&
                IsHex(_data[Position + 1]) && IsHex(_data[Position + 2]))
            {
                var hi = HexValue(_data[Position + 1]);
                var lo = HexValue(_data[Position + 2]);
                sb.Append((char)((hi << 4) | lo));
                Position += 3;
            }
            else
            {
                sb.Append((char)b);
                Position++;
            }
        }

        return new PdfToken(PdfTokenKind.Name, text: sb.ToString());
    }

    private PdfToken ReadKeyword()
    {
        var start = Position;
        while (Position < _data.Length && IsRegular(_data[Position]))
        {
            Position++;
        }

        if (Position == start)
        {
            Position++; // avoid infinite loop on an unexpected byte
            return Next();
        }

        return new PdfToken(PdfTokenKind.Keyword, text: Encoding.ASCII.GetString(_data, start, Position - start));
    }

    private PdfToken ReadLiteralString()
    {
        Position++; // consume '('
        var bytes = new List<byte>();
        var depth = 1;

        while (Position < _data.Length)
        {
            var b = _data[Position++];
            if (b == (byte)'\\')
            {
                if (Position >= _data.Length)
                {
                    break;
                }
                var e = _data[Position++];
                switch (e)
                {
                    case (byte)'n': bytes.Add((byte)'\n'); break;
                    case (byte)'r': bytes.Add((byte)'\r'); break;
                    case (byte)'t': bytes.Add((byte)'\t'); break;
                    case (byte)'b': bytes.Add((byte)'\b'); break;
                    case (byte)'f': bytes.Add((byte)'\f'); break;
                    case (byte)'(': bytes.Add((byte)'('); break;
                    case (byte)')': bytes.Add((byte)')'); break;
                    case (byte)'\\': bytes.Add((byte)'\\'); break;
                    case 13: // line continuation: \<CR>[<LF>]
                        if (Position < _data.Length && _data[Position] == 10) Position++;
                        break;
                    case 10:
                        break;
                    default:
                        if (e >= (byte)'0' && e <= (byte)'7')
                        {
                            var val = e - (byte)'0';
                            for (var i = 0; i < 2 && Position < _data.Length; i++)
                            {
                                var c = _data[Position];
                                if (c < (byte)'0' || c > (byte)'7') break;
                                val = (val << 3) + (c - (byte)'0');
                                Position++;
                            }
                            bytes.Add((byte)val);
                        }
                        else
                        {
                            bytes.Add(e);
                        }
                        break;
                }
            }
            else if (b == (byte)'(')
            {
                depth++;
                bytes.Add(b);
            }
            else if (b == (byte)')')
            {
                depth--;
                if (depth == 0) break;
                bytes.Add(b);
            }
            else
            {
                bytes.Add(b);
            }
        }

        return new PdfToken(PdfTokenKind.String, bytes: [.. bytes]);
    }

    private PdfToken ReadHexString()
    {
        Position++; // consume '<'
        var bytes = new List<byte>();
        var hi = -1;
        while (Position < _data.Length)
        {
            var b = _data[Position++];
            if (b == (byte)'>') break;
            if (!IsHex(b)) continue;
            var v = HexValue(b);
            if (hi < 0)
            {
                hi = v;
            }
            else
            {
                bytes.Add((byte)((hi << 4) | v));
                hi = -1;
            }
        }

        if (hi >= 0)
        {
            bytes.Add((byte)(hi << 4)); // odd digit count: pad low nibble with 0
        }

        return new PdfToken(PdfTokenKind.String, bytes: [.. bytes]);
    }

    private static bool IsHex(byte b) =>
        (b >= (byte)'0' && b <= (byte)'9') ||
        (b >= (byte)'a' && b <= (byte)'f') ||
        (b >= (byte)'A' && b <= (byte)'F');

    private static int HexValue(byte b) => b switch
    {
        >= (byte)'0' and <= (byte)'9' => b - (byte)'0',
        >= (byte)'a' and <= (byte)'f' => b - (byte)'a' + 10,
        _ => b - (byte)'A' + 10,
    };
}
