// Plain-text extraction from a page's content stream, without emitting HTML.
// Used for search indexing and the public text-extraction API.

using System.Text;

namespace BlazorPdf;

/// <summary>
/// Extracts the visible text of a page by replaying its content stream and
/// decoding show-text operators through each selected font. Positioning is
/// approximated with simple space/newline heuristics — enough for search and
/// copy, not a layout-faithful reconstruction.
/// </summary>
public static class BlazorPdfTextExtractor
{
    public static string Extract(BlazorPdfPage page, IBlazorPdfXRef xref)
    {
        var sb = new StringBuilder();
        var fontCache = new Dictionary<object, BlazorPdfFont>();

        void Run(byte[] content, BlazorPdfDict? resources, int depth)
        {
            if (depth > 8)
            {
                return;
            }
            List<BlazorPdfOperation> ops;
            try
            {
                ops = new BlazorPdfContentParser(content).Parse();
            }
            catch
            {
                return;
            }

            BlazorPdfFont? font = null;
            foreach (var op in ops)
            {
                switch (op.Operator)
                {
                    case "Tf":
                        if (op.Operands.Count >= 1 && op.Operands[0] is BlazorPdfName fn)
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
                                if (item is BlazorPdfString)
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
                        if (op.Operands.Count > 0 && op.Operands[0] is BlazorPdfName xn
                            && resources?.Get("XObject") is BlazorPdfDict xobjs
                            && xobjs.Get(xn.Value) is BlazorPdfStream xs && xs.Dict is not null
                            && (xs.Dict.Get("Subtype") as BlazorPdfName)?.Value == "Form")
                        {
                            try
                            {
                                Run(BlazorPdfStreamDecoder.Decode(xs), xs.Dict.Get("Resources") as BlazorPdfDict ?? resources, depth + 1);
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

    private static void AppendShow(StringBuilder sb, BlazorPdfFont? font, object? operand)
    {
        if (operand is not BlazorPdfString s || font is null)
        {
            return;
        }
        foreach (var g in font.Decode(s.Bytes))
        {
            sb.Append(g.Unicode);
        }
    }

    private static BlazorPdfFont? ResolveFont(string name, BlazorPdfDict? resources, IBlazorPdfXRef xref, Dictionary<object, BlazorPdfFont> cache)
    {
        if (resources?.Get("Font") is not BlazorPdfDict fonts)
        {
            return null;
        }
        object? raw = fonts.GetRaw(name);
        object key = raw is BlazorPdfRef r ? r : xref.FetchIfRef(raw) ?? name;
        if (cache.TryGetValue(key, out var cached))
        {
            return cached;
        }
        if (xref.FetchIfRef(raw) is BlazorPdfDict fontDict)
        {
            var font = BlazorPdfFont.Create(fontDict, xref);
            cache[key] = font;
            return font;
        }
        return null;
    }
}
