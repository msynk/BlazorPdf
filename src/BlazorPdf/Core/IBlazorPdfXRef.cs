// Core PDF object primitives: names, commands, references and dictionaries.

using System.Collections.Concurrent;

namespace BlazorPdf;

/// <summary>
/// Resolves indirect references (<see cref="BlazorPdfRef"/>) into concrete PDF objects.
/// Implemented by the cross-reference table reader. Defined here so that
/// <see cref="BlazorPdfDict"/> can resolve references lazily.
/// </summary>
public interface IBlazorPdfXRef
{
    /// <summary>Fetch the object a reference points to.</summary>
    object? Fetch(BlazorPdfRef reference, bool suppressEncryption = false);

    /// <summary>Resolve <paramref name="value"/> if it is a <see cref="BlazorPdfRef"/>, otherwise return it unchanged.</summary>
    object? FetchIfRef(object? value, bool suppressEncryption = false);
}
