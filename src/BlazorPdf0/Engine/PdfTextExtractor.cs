using System.Text;

namespace BlazorPdf.Engine;

/// <summary>
/// Extracts text from a decoded content stream by interpreting the text-showing
/// operators (<c>Tj</c>, <c>TJ</c>, <c>'</c>, <c>"</c>) and basic positioning.
/// </summary>
/// <remarks>
/// String bytes are decoded with the font's simple (single-byte) encoding treated as
/// Latin-1, which is correct for the common StandardEncoding/WinAnsiEncoding case.
/// CID fonts and <c>/ToUnicode</c> CMaps are not yet applied.
/// </remarks>
internal static class PdfTextExtractor
{
    public static string Extract(byte[] content)
    {
        if (content.Length == 0) return string.Empty;

        var lexer = new PdfLexer(content);
        var operands = new List<PdfObject>();
        var sb = new StringBuilder();
        var parser = new PdfParser(lexer);

        while (true)
        {
            var save = lexer.Position;
            var token = lexer.Next();
            if (token.Kind == PdfTokenKind.Eof) break;

            switch (token.Kind)
            {
                case PdfTokenKind.Number:
                    operands.Add(new PdfNumber(token.Number, token.Text == "int"));
                    break;
                case PdfTokenKind.String:
                    operands.Add(new PdfString(token.Bytes!));
                    break;
                case PdfTokenKind.Name:
                    operands.Add(new PdfName(token.Text!));
                    break;
                case PdfTokenKind.ArrayStart:
                    lexer.Position = save;
                    operands.Add(parser.ParseObject());
                    break;
                case PdfTokenKind.DictStart:
                    lexer.Position = save;
                    operands.Add(parser.ParseObject());
                    break;
                case PdfTokenKind.Keyword:
                    HandleOperator(token.Text!, operands, sb);
                    operands.Clear();
                    break;
                default:
                    operands.Clear();
                    break;
            }
        }

        return Normalize(sb.ToString());
    }

    private static void HandleOperator(string op, List<PdfObject> operands, StringBuilder sb)
    {
        switch (op)
        {
            case "Tj":
                if (Last(operands) is PdfString s) Append(sb, s);
                break;

            case "TJ":
                if (Last(operands) is PdfArray array) AppendArray(sb, array);
                break;

            case "'": // move to next line and show
                sb.Append('\n');
                if (Last(operands) is PdfString s1) Append(sb, s1);
                break;

            case "\"": // aw ac string ' : set spacing, next line, show
                sb.Append('\n');
                if (Last(operands) is PdfString s2) Append(sb, s2);
                break;

            case "Td":
            case "TD":
                // Vertical movement implies a new line.
                if (operands.Count >= 2 && operands[^1] is PdfNumber ty && Math.Abs(ty.Value) > 0.01)
                {
                    sb.Append('\n');
                }
                break;

            case "T*":
                sb.Append('\n');
                break;

            case "BT":
                if (sb.Length > 0 && sb[^1] != '\n') sb.Append('\n');
                break;
        }
    }

    private static void Append(StringBuilder sb, PdfString s) => sb.Append(s.AsText());

    private static void AppendArray(StringBuilder sb, PdfArray array)
    {
        foreach (var item in array.Items)
        {
            switch (item)
            {
                case PdfString str:
                    sb.Append(str.AsText());
                    break;
                case PdfNumber num when num.Value < -150:
                    // A large negative adjustment usually denotes inter-word space.
                    sb.Append(' ');
                    break;
            }
        }
    }

    private static PdfObject? Last(List<PdfObject> operands) => operands.Count > 0 ? operands[^1] : null;

    private static string Normalize(string text)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var sb = new StringBuilder();
        foreach (var line in lines)
        {
            var trimmed = line.TrimEnd();
            if (trimmed.Length == 0 && (sb.Length == 0 || sb[^1] == '\n')) continue;
            sb.Append(trimmed).Append('\n');
        }
        return sb.ToString().TrimEnd('\n');
    }
}
