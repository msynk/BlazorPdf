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
    [InlineData(".notdef")]
    [InlineData("")]
    [InlineData("g42")]
    [InlineData("cid123")]
    public void Returns_empty_for_unmappable(string name)
        => Assert.Equal(string.Empty, GlyphList.ToUnicode(name));
}
