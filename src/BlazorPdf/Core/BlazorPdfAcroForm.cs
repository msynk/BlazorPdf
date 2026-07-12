// AcroForm field extraction. Exposes the interactive form fields (/AcroForm
// /Fields) as a flat list of name/type/value so a consumer can build form UI or
// read submitted data. This does not yet render widgets as live inputs.

namespace BlazorPdf;

internal static class BlazorPdfAcroForm
{
    public static IReadOnlyList<BlazorPdfFormField> Build(IBlazorPdfXRef xref, BlazorPdfDict catalog)
    {
        if (xref.FetchIfRef(catalog.Get("AcroForm")) is not BlazorPdfDict form
            || xref.FetchIfRef(form.Get("Fields")) is not List<object?> fields)
        {
            return Array.Empty<BlazorPdfFormField>();
        }

        var result = new List<BlazorPdfFormField>();
        var visited = new HashSet<int>();
        foreach (var f in fields)
        {
            Walk(xref, f, parentName: "", parentType: null, result, visited, 0);
        }
        return result;
    }

    private static void Walk(IBlazorPdfXRef xref, object? fieldObj, string parentName, string? parentType,
        List<BlazorPdfFormField> result, HashSet<int> visited, int depth)
    {
        if (depth > 50)
        {
            return;
        }
        if (fieldObj is BlazorPdfRef r && !visited.Add(r.Num))
        {
            return;
        }
        if (xref.FetchIfRef(fieldObj) is not BlazorPdfDict field)
        {
            return;
        }

        // Field type is inheritable from the parent.
        string? ft = (field.Get("FT") as BlazorPdfName)?.Value ?? parentType;

        // Build the qualified name from the partial name /T.
        string partial = (field.Get("T") as BlazorPdfString)?.AsText() ?? "";
        string name = partial.Length == 0
            ? parentName
            : parentName.Length == 0 ? partial : parentName + "." + partial;

        object? kids = xref.FetchIfRef(field.Get("Kids"));

        // A node with child *fields* (kids that carry their own /T) is a
        // non-terminal; recurse. Kids without /T are the field's widgets.
        bool hasChildFields = false;
        if (kids is List<object?> kidArr)
        {
            foreach (var kid in kidArr)
            {
                if (xref.FetchIfRef(kid) is BlazorPdfDict kd && kd.Get("T") is BlazorPdfString)
                {
                    hasChildFields = true;
                    Walk(xref, kid, name, ft, result, visited, depth + 1);
                }
            }
        }

        // A terminal field has a type (or a value) and no child fields.
        if (!hasChildFields && (ft is not null || field.Has("V")))
        {
            result.Add(new BlazorPdfFormField
            {
                Name = name,
                Type = ft ?? "",
                Value = ValueToText(xref.FetchIfRef(field.Get("V"))),
            });
        }
    }

    private static string? ValueToText(object? v) => v switch
    {
        BlazorPdfString s => s.AsText(),
        BlazorPdfName n => n.Value,
        double d => d.ToString(System.Globalization.CultureInfo.InvariantCulture),
        _ => null,
    };
}
