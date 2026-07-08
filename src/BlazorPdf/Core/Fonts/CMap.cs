// Parsing of embedded CMap streams used as the /Encoding of Type0 (composite)
// fonts: maps multi-byte character codes to CIDs. Identity encodings map code
// to CID directly; an embedded CMap supplies explicit codespace ranges and
// cidrange/cidchar mappings.

namespace BlazorPdf.Core.Fonts;

/// <summary>
/// A character-code → CID mapping for a composite font, built from an embedded
/// CMap stream (or the Identity mapping for Identity-H/Identity-V).
/// </summary>
public sealed class CMap
{
    private readonly List<(long Lo, long Hi, int Cid)> _ranges = new();
    private readonly Dictionary<long, int> _chars = new();

    /// <summary>The byte length of a character code (1 or 2 for the common cases).</summary>
    public int CodeLength { get; private set; } = 2;

    /// <summary><c>true</c> for an Identity mapping (CID == code).</summary>
    public bool IsIdentity { get; private set; }

    /// <summary>The Identity mapping (2-byte codes, CID == code).</summary>
    public static CMap Identity { get; } = new() { IsIdentity = true, CodeLength = 2 };

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

    /// <summary>Parses an embedded CMap stream into a <see cref="CMap"/>.</summary>
    public static CMap Parse(byte[] data)
    {
        var cmap = new CMap();
        var lexer = new Lexer(new PdfStream(data));
        int codeLen = 0;

        // Two-token queue so we can read "value value operator" style groups.
        object prev2 = Primitives.EOF;
        object prev1 = Primitives.EOF;

        while (true)
        {
            object tok = lexer.GetObj();
            if (ReferenceEquals(tok, Primitives.EOF))
            {
                break;
            }

            if (tok is Cmd cmd)
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

    private static int ReadCodespace(Lexer lexer, ref CMap cmap)
    {
        int len = 0;
        while (true)
        {
            object tok = lexer.GetObj();
            if (ReferenceEquals(tok, Primitives.EOF) || (tok is Cmd { Value: "endcodespacerange" }))
            {
                break;
            }
            if (tok is PdfString lo)
            {
                _ = lexer.GetObj(); // the high bound of the range
                len = Math.Max(len, lo.Bytes.Length);
            }
        }
        return len;
    }

    private static void ReadCidRanges(Lexer lexer, CMap cmap)
    {
        while (true)
        {
            object a = lexer.GetObj();
            if (ReferenceEquals(a, Primitives.EOF) || a is Cmd)
            {
                break; // endcidrange / endbfrange
            }
            object b = lexer.GetObj();
            object c = lexer.GetObj();
            if (a is PdfString lo && b is PdfString hi)
            {
                long loCode = ToCode(lo.Bytes);
                long hiCode = ToCode(hi.Bytes);
                int cid = c switch
                {
                    double d => (int)d,
                    PdfString s => (int)ToCode(s.Bytes),
                    _ => 0,
                };
                cmap._ranges.Add((loCode, hiCode, cid));
            }
        }
    }

    private static void ReadCidChars(Lexer lexer, CMap cmap)
    {
        while (true)
        {
            object a = lexer.GetObj();
            if (ReferenceEquals(a, Primitives.EOF) || a is Cmd)
            {
                break; // endcidchar / endbfchar
            }
            object b = lexer.GetObj();
            if (a is PdfString code)
            {
                int cid = b switch
                {
                    double d => (int)d,
                    PdfString s => (int)ToCode(s.Bytes),
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
