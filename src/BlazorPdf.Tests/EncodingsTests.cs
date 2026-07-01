using BlazorPdf.Core.Fonts;

namespace BlazorPdf.Tests;

public class EncodingsTests
{
    [Fact]
    public void ByName_resolves_known_encodings()
    {
        Assert.NotNull(Encodings.ByName("StandardEncoding"));
        Assert.NotNull(Encodings.ByName("WinAnsiEncoding"));
        Assert.NotNull(Encodings.ByName("MacRomanEncoding"));
        Assert.NotNull(Encodings.ByName("PDFDocEncoding"));
        Assert.Null(Encodings.ByName("BogusEncoding"));
        Assert.Null(Encodings.ByName(null));
    }

    [Fact]
    public void Shared_ascii_range_is_consistent()
    {
        // The printable ASCII range is identical across the Latin encodings.
        Assert.Equal("A", Encodings.WinAnsi[0x41]);
        Assert.Equal("A", Encodings.MacRoman[0x41]);
        Assert.Equal("A", Encodings.Standard[0x41]);
        Assert.Equal("space", Encodings.WinAnsi[0x20]);
        Assert.Equal("zero", Encodings.WinAnsi[0x30]);
    }

    [Fact]
    public void Standard_encoding_uses_quote_glyphs()
    {
        Assert.Equal("quoteright", Encodings.Standard[0x27]);
        Assert.Equal("quoteleft", Encodings.Standard[0x60]);
    }

    [Fact]
    public void WinAnsi_high_range()
    {
        Assert.Equal("Euro", Encodings.WinAnsi[0x80]);
        Assert.Equal("bullet", Encodings.WinAnsi[0x95]);
        Assert.Equal("Adieresis", Encodings.WinAnsi[0xC4]);
        Assert.Equal("ydieresis", Encodings.WinAnsi[0xFF]);
    }

    [Fact]
    public void MacRoman_high_range_differs_from_winansi()
    {
        Assert.Equal("Adieresis", Encodings.MacRoman[0x80]);
        Assert.Equal("endash", Encodings.MacRoman[0xD0]);
        Assert.Equal("space", Encodings.MacRoman[0xCA]); // non-breaking space slot
    }

    [Fact]
    public void Tables_are_full_length()
    {
        Assert.Equal(256, Encodings.Standard.Length);
        Assert.Equal(256, Encodings.WinAnsi.Length);
        Assert.Equal(256, Encodings.MacRoman.Length);
        Assert.Equal(256, Encodings.Symbol.Length);
        Assert.Equal(256, Encodings.ZapfDingbats.Length);
    }

    [Fact]
    public void ByName_resolves_symbolic_encodings()
    {
        Assert.Same(Encodings.Symbol, Encodings.ByName("Symbol"));
        Assert.Same(Encodings.ZapfDingbats, Encodings.ByName("ZapfDingbatsEncoding"));
    }

    [Fact]
    public void Symbol_encoding_maps_greek_and_math()
    {
        Assert.Equal("space", Encodings.Symbol[0x20]);
        Assert.Equal("Alpha", Encodings.Symbol[0x41]);   // 'A' position -> Greek Alpha
        Assert.Equal("beta", Encodings.Symbol[0x62]);    // 'b' position -> Greek beta
        Assert.Equal("universal", Encodings.Symbol[0x22]);
        Assert.Equal("infinity", Encodings.Symbol[0xA5]);
        Assert.Equal("integral", Encodings.Symbol[0xF2]);
    }

    [Fact]
    public void ZapfDingbats_encoding_maps_ornaments()
    {
        Assert.Equal("space", Encodings.ZapfDingbats[0x20]);
        Assert.Equal("a1", Encodings.ZapfDingbats[0x21]);
        Assert.Equal("a10", Encodings.ZapfDingbats[0x41]);
        Assert.Equal("a191", Encodings.ZapfDingbats[0xFE]);
    }
}
