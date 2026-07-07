// Content-stream preprocessing into operations.

namespace BlazorPdf.Core.Content;

/// <summary>
/// Parses a decoded page content stream into a flat list of
/// <see cref="Operation"/>s. Operands are accumulated until an operator keyword
/// is read; inline images (<c>BI … ID … EI</c>) are captured as a single op.
/// </summary>
public sealed class ContentParser
{
    private readonly Lexer _lexer;

    public ContentParser(byte[] content) => _lexer = new Lexer(new PdfStream(content));

    /// <summary>Reads all operations from the content stream.</summary>
    public List<Operation> Parse()
    {
        var operations = new List<Operation>();
        var operands = new List<object?>();

        while (true)
        {
            object token = _lexer.GetObj();
            if (ReferenceEquals(token, Primitives.EOF))
            {
                break;
            }

            if (token is Cmd cmd)
            {
                string op = cmd.Value;

                // Structural delimiters can appear as operands (arrays/dicts).
                if (op == "[")
                {
                    operands.Add(ReadArray());
                    continue;
                }
                if (op == "<<")
                {
                    operands.Add(ReadDict());
                    continue;
                }

                if (op == "BI")
                {
                    operations.Add(ReadInlineImage());
                    operands.Clear();
                    continue;
                }

                operations.Add(new Operation(op, operands));
                operands = new List<object?>();
                continue;
            }

            // An operand: number, name, string, bool, or null.
            operands.Add(token);
            if (operands.Count > 64)
            {
                // Guard against runaway operand accumulation in malformed content.
                operands.Clear();
            }
        }

        return operations;
    }

    private List<object?> ReadArray()
    {
        var array = new List<object?>();
        while (true)
        {
            object token = _lexer.GetObj();
            if (ReferenceEquals(token, Primitives.EOF) || (token is Cmd { Value: "]" }))
            {
                break;
            }
            if (token is Cmd { Value: "[" })
            {
                array.Add(ReadArray());
            }
            else
            {
                array.Add(token);
            }
        }
        return array;
    }

    private Dict ReadDict()
    {
        var dict = new Dict();
        while (true)
        {
            object token = _lexer.GetObj();
            if (ReferenceEquals(token, Primitives.EOF) || token is Cmd { Value: ">>" })
            {
                break;
            }
            if (token is not Name key)
            {
                continue;
            }
            object value = _lexer.GetObj();
            if (value is Cmd { Value: "[" })
            {
                dict.Set(key.Value, ReadArray());
            }
            else if (value is Cmd { Value: "<<" })
            {
                dict.Set(key.Value, ReadDict());
            }
            else
            {
                dict.Set(key.Value, value);
            }
        }
        return dict;
    }

    private Operation ReadInlineImage()
    {
        // Parse the inline-image dictionary (key/value pairs up to ID), then
        // capture the raw image bytes up to the EI marker.
        var dict = new Dict();
        while (true)
        {
            object token = _lexer.GetObj();
            if (ReferenceEquals(token, Primitives.EOF) || token is Cmd { Value: "ID" })
            {
                break;
            }
            if (token is not Name key)
            {
                continue;
            }
            object value = _lexer.GetObj();
            if (value is Cmd { Value: "[" })
            {
                dict.Set(key.Value, ReadArray());
            }
            else
            {
                dict.Set(key.Value, value);
            }
        }

        byte[] data = ReadInlineImageData();
        return new Operation("INLINE_IMAGE", new List<object?> { dict, data });
    }

    private byte[] ReadInlineImageData()
    {
        // After "ID" exactly one whitespace byte separates the keyword from data.
        BaseStream stream = _lexer.Stream;
        var buffer = ((PdfStream)stream).Buffer;
        int pos = _lexer.Pos - 1; // current char position

        // Skip the single whitespace after ID.
        if (pos < buffer.Length && (buffer[pos] is 0x20 or 0x0A or 0x0D))
        {
            pos++;
        }

        int start = pos;
        // Scan for the "EI" delimiter surrounded by whitespace.
        for (int i = pos; i + 1 < buffer.Length; i++)
        {
            if (buffer[i] == (byte)'E' && buffer[i + 1] == (byte)'I'
                && (i == 0 || IsWhitespaceByte(buffer[i - 1]))
                && (i + 2 >= buffer.Length || IsWhitespaceByte(buffer[i + 2])))
            {
                int end = i;
                _lexer.Seek(i + 2);
                return buffer[start..end];
            }
        }

        _lexer.Seek(buffer.Length);
        return buffer[start..];
    }

    private static bool IsWhitespaceByte(byte b) => b is 0x00 or 0x09 or 0x0A or 0x0C or 0x0D or 0x20;
}
