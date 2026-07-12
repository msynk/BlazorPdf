
namespace BlazorPdf;

public class PdfFontTests
{
    private static readonly IBlazorPdfXRef Xref = new InlineXRef();

    private static BlazorPdfFont SimpleFont(object? encoding)
    {
        var dict = new BlazorPdfDict();
        dict.Set("Subtype", BlazorPdfName.Get("Type1"));
        dict.Set("BaseFont", BlazorPdfName.Get("Helvetica"));
        if (encoding is not null)
        {
            dict.Set("Encoding", encoding);
        }
        return BlazorPdfFont.Create(dict, Xref);
    }

    private static string Decode(BlazorPdfFont font, params byte[] codes)
        => string.Concat(font.Decode(codes).Select(g => g.Unicode));

    [Fact]
    public void Default_encoding_maps_ascii()
    {
        var font = SimpleFont(null);
        Assert.Equal("Hello", Decode(font, (byte)'H', (byte)'e', (byte)'l', (byte)'l', (byte)'o'));
    }

    [Fact]
    public void Default_encoding_maps_winansi_high_range()
    {
        var font = SimpleFont(null);
        Assert.Equal("\u20AC", Decode(font, 0x80)); // Euro
    }

    [Fact]
    public void Named_base_encoding_macroman()
    {
        var font = SimpleFont(BlazorPdfName.Get("MacRomanEncoding"));
        Assert.Equal("\u2013", Decode(font, 0xD0)); // endash in MacRoman
    }

    [Fact]
    public void Differences_override_base_encoding()
    {
        var enc = new BlazorPdfDict();
        enc.Set("Type", BlazorPdfName.Get("Encoding"));
        // code 65 -> eacute, code 66 -> B (an explicit letter remap)
        enc.Set("Differences", new List<object?> { 65.0, BlazorPdfName.Get("eacute"), BlazorPdfName.Get("B") });

        var font = SimpleFont(enc);
        // 65 -> é, 66 -> B (remapped), 67 -> C (from base encoding)
        Assert.Equal("\u00E9BC", Decode(font, 65, 66, 67));
    }

    [Fact]
    public void Differences_remap_to_letter_is_not_lost()
    {
        // Regression: a /Differences entry mapping a code to a single-letter
        // glyph name must win over the base-encoding character at that code.
        var enc = new BlazorPdfDict();
        enc.Set("Differences", new List<object?> { 65.0, BlazorPdfName.Get("Z") });
        var font = SimpleFont(enc);
        Assert.Equal("Z", Decode(font, 65)); // not the base "A"
    }

    [Fact]
    public void Simple_font_is_single_byte()
        => Assert.Equal(1, SimpleFont(null).BytesPerCode);
}
