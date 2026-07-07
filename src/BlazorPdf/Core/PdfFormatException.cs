namespace BlazorPdf.Core;

/// <summary>
/// Thrown when the PDF byte stream cannot be parsed according to the PDF
/// specification (malformed tokens, structural errors, etc.).
/// </summary>
public sealed class PdfFormatException : Exception
{
    public PdfFormatException(string message) : base(message) { }
    public PdfFormatException(string message, Exception inner) : base(message, inner) { }
}
