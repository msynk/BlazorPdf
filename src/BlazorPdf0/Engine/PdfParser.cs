using System.Text;

namespace BlazorPdf.Engine;

/// <summary>
/// Parses PDF objects from a <see cref="PdfLexer"/>, including indirect references
/// and streams.
/// </summary>
internal sealed class PdfParser(PdfLexer lexer)
{
    private readonly PdfLexer _lexer = lexer;

    public PdfLexer Lexer => _lexer;

    /// <summary>Parses the next object starting at the current lexer position.</summary>
    public PdfObject ParseObject()
    {
        var token = _lexer.Next();
        return ParseFromToken(token);
    }

    private PdfObject ParseFromToken(PdfToken token)
    {
        switch (token.Kind)
        {
            case PdfTokenKind.Number:
                return ParseNumberOrReference(token);

            case PdfTokenKind.Name:
                return new PdfName(token.Text!);

            case PdfTokenKind.String:
                return new PdfString(token.Bytes!);

            case PdfTokenKind.ArrayStart:
                return ParseArray();

            case PdfTokenKind.DictStart:
                return ParseDictionaryOrStream();

            case PdfTokenKind.Keyword:
                return token.Text switch
                {
                    "true" => new PdfBoolean(true),
                    "false" => new PdfBoolean(false),
                    "null" => PdfNull.Instance,
                    _ => PdfNull.Instance,
                };

            default:
                return PdfNull.Instance;
        }
    }

    private PdfObject ParseNumberOrReference(PdfToken first)
    {
        if (first.Text != "int")
        {
            return new PdfNumber(first.Number, isInteger: false);
        }

        // Could be "N G R" (reference) or "N G obj" (indirect definition).
        var afterFirst = _lexer.Position;
        var second = _lexer.Next();
        if (second.Kind == PdfTokenKind.Number && second.Text == "int")
        {
            var third = _lexer.Next();
            if (third.IsKeyword("R"))
            {
                return new PdfReference((int)first.Number, (int)second.Number);
            }
            if (third.IsKeyword("obj"))
            {
                // Indirect object body follows; the caller handles definitions, so
                // just return the inner object.
                return ParseObject();
            }

            // Not a reference: rewind so the second number is parsed next.
            _lexer.Position = afterFirst;
            return new PdfNumber(first.Number, isInteger: true);
        }

        _lexer.Position = afterFirst;
        return new PdfNumber(first.Number, isInteger: true);
    }

    private PdfArray ParseArray()
    {
        var array = new PdfArray();
        while (true)
        {
            var token = _lexer.Next();
            if (token.Kind is PdfTokenKind.ArrayEnd or PdfTokenKind.Eof)
            {
                break;
            }
            array.Items.Add(ParseFromToken(token));
        }
        return array;
    }

    private PdfObject ParseDictionaryOrStream()
    {
        var dict = new PdfDictionary();
        while (true)
        {
            var keyToken = _lexer.Next();
            if (keyToken.Kind is PdfTokenKind.DictEnd or PdfTokenKind.Eof)
            {
                break;
            }
            if (keyToken.Kind != PdfTokenKind.Name)
            {
                continue; // malformed entry; skip to keep going
            }

            var value = ParseObject();
            dict.Items[keyToken.Text!] = value;
        }

        // A stream may immediately follow the dictionary.
        var save = _lexer.Position;
        var next = _lexer.Next();
        if (next.IsKeyword("stream"))
        {
            return ReadStream(dict);
        }

        _lexer.Position = save;
        return dict;
    }

    private PdfStream ReadStream(PdfDictionary dict)
    {
        var data = _lexer.Data;
        var pos = _lexer.Position;

        // The 'stream' keyword is followed by CRLF or a single LF.
        if (pos < data.Length && data[pos] == 13) pos++;
        if (pos < data.Length && data[pos] == 10) pos++;

        var start = pos;
        int end;

        if (dict.Get("Length") is PdfNumber lengthNum && lengthNum.IsInteger &&
            start + lengthNum.AsInt <= data.Length)
        {
            end = start + lengthNum.AsInt;

            // Validate that 'endstream' follows; otherwise fall back to scanning.
            var probe = end;
            while (probe < data.Length && (data[probe] is 13 or 10 or 32 or 9)) probe++;
            if (!MatchesAt(data, probe, "endstream"))
            {
                end = FindEndstream(data, start);
            }
        }
        else
        {
            end = FindEndstream(data, start);
        }

        var length = Math.Max(0, end - start);
        var raw = new byte[length];
        Array.Copy(data, start, raw, 0, length);

        // Advance the lexer past 'endstream'.
        var after = end;
        while (after < data.Length && (data[after] is 13 or 10 or 32 or 9)) after++;
        if (MatchesAt(data, after, "endstream")) after += "endstream".Length;
        _lexer.Position = after;

        return new PdfStream(dict, raw);
    }

    private static int FindEndstream(byte[] data, int start)
    {
        var idx = IndexOf(data, "endstream", start);
        if (idx < 0) return data.Length;

        // Trim a single trailing EOL that precedes 'endstream'.
        var end = idx;
        if (end > start && data[end - 1] == 10) end--;
        if (end > start && data[end - 1] == 13) end--;
        return end;
    }

    private static bool MatchesAt(byte[] data, int pos, string token)
    {
        if (pos + token.Length > data.Length) return false;
        for (var i = 0; i < token.Length; i++)
        {
            if (data[pos + i] != (byte)token[i]) return false;
        }
        return true;
    }

    internal static int IndexOf(byte[] data, string token, int start)
    {
        var needle = Encoding.ASCII.GetBytes(token);
        var last = data.Length - needle.Length;
        for (var i = start; i <= last; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (data[i + j] != needle[j])
                {
                    match = false;
                    break;
                }
            }
            if (match) return i;
        }
        return -1;
    }
}
