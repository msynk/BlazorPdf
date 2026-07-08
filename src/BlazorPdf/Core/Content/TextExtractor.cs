// Plain-text extraction from a page's content stream, without emitting HTML.
// Used for search indexing and the public text-extraction API.

using System.Text;
using BlazorPdf.Core.Fonts;

namespace BlazorPdf.Core.Content;

/// <summary>
/// Extracts the visible text of a page by replaying its content stream and
/// decoding show-text operators through each selected font. Positioning is
/// approximated with simple space/newline heuristics — enough for search and
/// copy, not a layout-faithful reconstruction.
/// </summary>
public static class TextExtractor
{
    public static string Extract(PdfPage page, IXRef xref)
    {
        var sb = new StringBuilder();
        var fontCache = new Dictionary<object, PdfFont>();

        void Run(byte[] content, Dict? resources, int depth)
        {
            if (depth > 8)
            {
                return;
            }
            List<Operation> ops;
            try
            {
                ops = new ContentParser(content).Parse();
            }
            catch
            {
                return;
            }

            PdfFont? font = null;
            foreach (var op in ops)
            {
                switch (op.Operator)
                {
                    case "Tf":
                        if (op.Operands.Count >= 1 && op.Operands[0] is Name fn)
                        {
                            font = ResolveFont(fn.Value, resources, xref, fontCache);
                        }
                        break;
                    case "Tj":
                        AppendShow(sb, font, op.Operands.Count > 0 ? op.Operands[0] : null);
                        break;
                    case "TJ":
                        if (op.Operands.Count > 0 && op.Operands[0] is List<object?> arr)
                        {
                            foreach (var item in arr)
                            {
                                if (item is PdfString)
                                {
                                    AppendShow(sb, font, item);
                                }
                                else if (item is double adj && adj < -100)
                                {
                                    sb.Append(' '); // a large negative adjustment is a word gap
                                }
                            }
                        }
                        break;
                    case "'":
                        sb.Append('\n');
                        AppendShow(sb, font, op.Operands.Count > 0 ? op.Operands[^1] : null);
                        break;
                    case "\"":
                        sb.Append('\n');
                        AppendShow(sb, font, op.Operands.Count > 0 ? op.Operands[^1] : null);
                        break;
                    case "Td":
                    case "TD":
                        // A vertical move implies a new line; a purely horizontal move a space.
                        if (Math.Abs(op.Num(1)) > 0.01)
                        {
                            sb.Append('\n');
                        }
                        else if (op.Num(0) > 0)
                        {
                            sb.Append(' ');
                        }
                        break;
                    case "T*":
                        sb.Append('\n');
                        break;
                    case "Do":
                        // Recurse into a form XObject so its text is captured too.
                        if (op.Operands.Count > 0 && op.Operands[0] is Name xn
                            && resources?.Get("XObject") is Dict xobjs
                            && xobjs.Get(xn.Value) is PdfStream xs && xs.Dict is not null
                            && (xs.Dict.Get("Subtype") as Name)?.Value == "Form")
                        {
                            try
                            {
                                Run(Filters.StreamDecoder.Decode(xs), xs.Dict.Get("Resources") as Dict ?? resources, depth + 1);
                            }
                            catch
                            {
                                // ignore malformed form content
                            }
                        }
                        break;
                }
            }
        }

        Run(page.GetContentBytes(), page.Resources, 0);
        return sb.ToString();
    }

    private static void AppendShow(StringBuilder sb, PdfFont? font, object? operand)
    {
        if (operand is not PdfString s || font is null)
        {
            return;
        }
        foreach (var g in font.Decode(s.Bytes))
        {
            sb.Append(g.Unicode);
        }
    }

    private static PdfFont? ResolveFont(string name, Dict? resources, IXRef xref, Dictionary<object, PdfFont> cache)
    {
        if (resources?.Get("Font") is not Dict fonts)
        {
            return null;
        }
        object? raw = fonts.GetRaw(name);
        object key = raw is Ref r ? r : xref.FetchIfRef(raw) ?? name;
        if (cache.TryGetValue(key, out var cached))
        {
            return cached;
        }
        if (xref.FetchIfRef(raw) is Dict fontDict)
        {
            var font = PdfFont.Create(fontDict, xref);
            cache[key] = font;
            return font;
        }
        return null;
    }
}
