
namespace BlazorPdf;

public class EncodingsTests
{
    [Fact]
    public void ByName_resolves_known_encodings()
    {
        Assert.NotNull(BlazorPdfEncodings.ByName("StandardEncoding"));
        Assert.NotNull(BlazorPdfEncodings.ByName("WinAnsiEncoding"));
        Assert.NotNull(BlazorPdfEncodings.ByName("MacRomanEncoding"));
        Assert.NotNull(BlazorPdfEncodings.ByName("PDFDocEncoding"));
        Assert.Null(BlazorPdfEncodings.ByName("BogusEncoding"));
        Assert.Null(BlazorPdfEncodings.ByName(null));
    }

    [Fact]
    public void Shared_ascii_range_is_consistent()
    {
        // The printable ASCII range is identical across the Latin encodings.
        Assert.Equal("A", BlazorPdfEncodings.WinAnsi[0x41]);
        Assert.Equal("A", BlazorPdfEncodings.MacRoman[0x41]);
        Assert.Equal("A", BlazorPdfEncodings.Standard[0x41]);
        Assert.Equal("space", BlazorPdfEncodings.WinAnsi[0x20]);
        Assert.Equal("zero", BlazorPdfEncodings.WinAnsi[0x30]);
    }

    [Fact]
    public void Standard_encoding_uses_quote_glyphs()
    {
        Assert.Equal("quoteright", BlazorPdfEncodings.Standard[0x27]);
        Assert.Equal("quoteleft", BlazorPdfEncodings.Standard[0x60]);
    }

    [Fact]
    public void WinAnsi_high_range()
    {
        Assert.Equal("Euro", BlazorPdfEncodings.WinAnsi[0x80]);
        Assert.Equal("bullet", BlazorPdfEncodings.WinAnsi[0x95]);
        Assert.Equal("Adieresis", BlazorPdfEncodings.WinAnsi[0xC4]);
        Assert.Equal("ydieresis", BlazorPdfEncodings.WinAnsi[0xFF]);
    }

    [Fact]
    public void MacRoman_high_range_differs_from_winansi()
    {
        Assert.Equal("Adieresis", BlazorPdfEncodings.MacRoman[0x80]);
        Assert.Equal("endash", BlazorPdfEncodings.MacRoman[0xD0]);
        Assert.Equal("space", BlazorPdfEncodings.MacRoman[0xCA]); // non-breaking space slot
    }

    [Fact]
    public void Tables_are_full_length()
    {
        Assert.Equal(256, BlazorPdfEncodings.Standard.Length);
        Assert.Equal(256, BlazorPdfEncodings.WinAnsi.Length);
        Assert.Equal(256, BlazorPdfEncodings.MacRoman.Length);
        Assert.Equal(256, BlazorPdfEncodings.Symbol.Length);
        Assert.Equal(256, BlazorPdfEncodings.ZapfDingbats.Length);
    }

    [Fact]
    public void ByName_resolves_symbolic_encodings()
    {
        Assert.Same(BlazorPdfEncodings.Symbol, BlazorPdfEncodings.ByName("Symbol"));
        Assert.Same(BlazorPdfEncodings.ZapfDingbats, BlazorPdfEncodings.ByName("ZapfDingbatsEncoding"));
    }

    [Fact]
    public void Symbol_encoding_maps_greek_and_math()
    {
        Assert.Equal("space", BlazorPdfEncodings.Symbol[0x20]);
        Assert.Equal("Alpha", BlazorPdfEncodings.Symbol[0x41]);   // 'A' position -> Greek Alpha
        Assert.Equal("beta", BlazorPdfEncodings.Symbol[0x62]);    // 'b' position -> Greek beta
        Assert.Equal("universal", BlazorPdfEncodings.Symbol[0x22]);
        Assert.Equal("infinity", BlazorPdfEncodings.Symbol[0xA5]);
        Assert.Equal("integral", BlazorPdfEncodings.Symbol[0xF2]);
    }

    [Fact]
    public void ZapfDingbats_encoding_maps_ornaments()
    {
        Assert.Equal("space", BlazorPdfEncodings.ZapfDingbats[0x20]);
        Assert.Equal("a1", BlazorPdfEncodings.ZapfDingbats[0x21]);
        Assert.Equal("a10", BlazorPdfEncodings.ZapfDingbats[0x41]);
        Assert.Equal("a191", BlazorPdfEncodings.ZapfDingbats[0xFE]);
    }
}
