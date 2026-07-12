// Parsing of embedded CMap streams used as the /Encoding of Type0 (composite)
// fonts: maps multi-byte character codes to CIDs. Identity encodings map code
// to CID directly; an embedded CMap supplies explicit codespace ranges and
// cidrange/cidchar mappings.

namespace BlazorPdf;

/// <summary>
/// A character-code → CID mapping for a composite font, built from an embedded
/// CMap stream (or the Identity mapping for Identity-H/Identity-V).
/// </summary>
public sealed class BlazorPdfCMap
{
    private readonly List<(long Lo, long Hi, int Cid)> _ranges = new();
    private readonly Dictionary<long, int> _chars = new();

    /// <summary>The byte length of a character code (1 or 2 for the common cases).</summary>
    public int CodeLength { get; private set; } = 2;

    /// <summary><c>true</c> for an Identity mapping (CID == code).</summary>
    public bool IsIdentity { get; private set; }

    /// <summary>The Identity mapping (2-byte codes, CID == code).</summary>
    public static BlazorPdfCMap Identity { get; } = new() { IsIdentity = true, CodeLength = 2 };

    /// <summary>Maps a character code to a CID.</summary>
    public int Lookup(long code)
    {
        if (IsIdentity)
        {
            return (int)code;
        }
        if (_chars.TryGetValue(code, out int cid))
        {
            return cid;
        }
        foreach (var (lo, hi, start) in _ranges)
        {
            if (code >= lo && code <= hi)
            {
                return (int)(start + (code - lo));
            }
        }
        return (int)code; // fall back to identity for unmapped codes
    }

    /// <summary>Parses an embedded CMap stream into a <see cref="BlazorPdfCMap"/>.</summary>
    public static BlazorPdfCMap Parse(byte[] data)
    {
        var cmap = new BlazorPdfCMap();
        var lexer = new BlazorPdfLexer(new BlazorPdfStream(data));
        int codeLen = 0;

        // Two-token queue so we can read "value value operator" style groups.
        object prev2 = BlazorPdfPrimitives.EOF;
        object prev1 = BlazorPdfPrimitives.EOF;

        while (true)
        {
            object tok = lexer.GetObj();
            if (ReferenceEquals(tok, BlazorPdfPrimitives.EOF))
            {
                break;
            }

            if (tok is BlazorPdfCmd cmd)
            {
                switch (cmd.Value)
                {
                    case "begincodespacerange":
                        codeLen = ReadCodespace(lexer, ref cmap);
                        if (codeLen > 0)
                        {
                            cmap.CodeLength = codeLen;
                        }
                        break;
                    case "begincidrange":
                    case "beginbfrange":
                        ReadCidRanges(lexer, cmap);
                        break;
                    case "begincidchar":
                    case "beginbfchar":
                        ReadCidChars(lexer, cmap);
                        break;
                }
            }

            prev2 = prev1;
            prev1 = tok;
            _ = prev2; // reserved for usecmap handling
        }

        return cmap;
    }

    private static int ReadCodespace(BlazorPdfLexer lexer, ref BlazorPdfCMap cmap)
    {
        int len = 0;
        while (true)
        {
            object tok = lexer.GetObj();
            if (ReferenceEquals(tok, BlazorPdfPrimitives.EOF) || (tok is BlazorPdfCmd { Value: "endcodespacerange" }))
            {
                break;
            }
            if (tok is BlazorPdfString lo)
            {
                _ = lexer.GetObj(); // the high bound of the range
                len = Math.Max(len, lo.Bytes.Length);
            }
        }
        return len;
    }

    private static void ReadCidRanges(BlazorPdfLexer lexer, BlazorPdfCMap cmap)
    {
        while (true)
        {
            object a = lexer.GetObj();
            if (ReferenceEquals(a, BlazorPdfPrimitives.EOF) || a is BlazorPdfCmd)
            {
                break; // endcidrange / endbfrange
            }
            object b = lexer.GetObj();
            object c = lexer.GetObj();
            if (a is BlazorPdfString lo && b is BlazorPdfString hi)
            {
                long loCode = ToCode(lo.Bytes);
                long hiCode = ToCode(hi.Bytes);
                int cid = c switch
                {
                    double d => (int)d,
                    BlazorPdfString s => (int)ToCode(s.Bytes),
                    _ => 0,
                };
                cmap._ranges.Add((loCode, hiCode, cid));
            }
        }
    }

    private static void ReadCidChars(BlazorPdfLexer lexer, BlazorPdfCMap cmap)
    {
        while (true)
        {
            object a = lexer.GetObj();
            if (ReferenceEquals(a, BlazorPdfPrimitives.EOF) || a is BlazorPdfCmd)
            {
                break; // endcidchar / endbfchar
            }
            object b = lexer.GetObj();
            if (a is BlazorPdfString code)
            {
                int cid = b switch
                {
                    double d => (int)d,
                    BlazorPdfString s => (int)ToCode(s.Bytes),
                    _ => 0,
                };
                cmap._chars[ToCode(code.Bytes)] = cid;
            }
        }
    }

    private static long ToCode(byte[] bytes)
    {
        long value = 0;
        foreach (byte b in bytes)
        {
            value = (value << 8) | b;
        }
        return value;
    }
}
