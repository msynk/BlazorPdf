using BlazorPdf.Core.Fonts;

namespace BlazorPdf.Tests;

public class GlyphListTests
{
    [Theory]
    [InlineData("space", " ")]
    [InlineData("A", "A")]
    [InlineData("z", "z")]
    [InlineData("eacute", "\u00E9")]
    [InlineData("Euro", "\u20AC")]
    [InlineData("endash", "\u2013")]
    [InlineData("quoteright", "\u2019")]
    [InlineData("fi", "fi")]
    public void Maps_named_glyphs(string name, string expected)
        => Assert.Equal(expected, GlyphList.ToUnicode(name));

    [Theory]
    [InlineData("uni0041", "A")]
    [InlineData("uni20AC", "\u20AC")]
    [InlineData("uni00410042", "AB")]   // multiple UTF-16 code units
    [InlineData("u00E9", "\u00E9")]
    [InlineData("u1F600", "\U0001F600")] // astral plane
    public void Resolves_algorithmic_names(string name, string expected)
        => Assert.Equal(expected, GlyphList.ToUnicode(name));

    [Fact]
    public void Strips_variant_suffix()
        => Assert.Equal("a", GlyphList.ToUnicode("a.sc"));

    [Fact]
    public void Joins_ligature_components()
        => Assert.Equal("ff", GlyphList.ToUnicode("f_f"));

    [Theory]
    [InlineData("Alpha", "\u0391")]
    [InlineData("beta", "\u03B2")]
    [InlineData("pi", "\u03C0")]
    [InlineData("infinity", "\u221E")]
    [InlineData("universal", "\u2200")]
    [InlineData("integral", "\u222B")]
    public void Maps_symbol_glyphs(string name, string expected)
        => Assert.Equal(expected, GlyphList.ToUnicode(name));

    [Theory]
    [InlineData("a1", "\u2701")]
    [InlineData("a13", "\u270C")]
    [InlineData("a120", "\u2460")]
    public void Maps_dingbats_glyphs(string name, string expected)
        => Assert.Equal(expected, GlyphList.ToUnicode(name));

    [Fact]
    public void Latin_meaning_wins_over_symbol_name()
        => Assert.Equal("A", GlyphList.ToUnicode("A")); // not Greek Alpha

    [Theory]
    [InlineData(".notdef")]
    [InlineData("")]
    [InlineData("g42")]
    [InlineData("cid123")]
    public void Returns_empty_for_unmappable(string name)
        => Assert.Equal(string.Empty, GlyphList.ToUnicode(name));
}
