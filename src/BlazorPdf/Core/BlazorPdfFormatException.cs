namespace BlazorPdf;

/// <summary>
/// Thrown when the PDF byte stream cannot be parsed according to the PDF
/// specification (malformed tokens, structural errors, etc.).
/// </summary>
public sealed class BlazorPdfFormatException : Exception
{
    public BlazorPdfFormatException(string message) : base(message) { }
    public BlazorPdfFormatException(string message, Exception inner) : base(message, inner) { }
}
