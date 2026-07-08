using BlazorPdf.Core.Filters;

namespace BlazorPdf.Tests;

public class JpegDecoderTests
{
    // A minimal baseline 1x1 JPEG (JFIF).
    private const string OnePixelJpegBase64 =
        "/9j/4AAQSkZJRgABAQEAYABgAAD/2wBDAAgGBgcGBQgHBwcJCQgKDBQNDAsLDBkSEw8UHRof" +
        "Hh0aHBwgJC4nICIsIxwcKDcpLDAxNDQ0Hyc5PTgyPC4zNDL/wAALCAABAAEBAREA/8QAFAAB" +
        "AAAAAAAAAAAAAAAAAAAACP/EABQQAQAAAAAAAAAAAAAAAAAAAAD/2gAIAQEAAD8AN//Z";

    [Fact]
    public void Decodes_dimensions_of_a_baseline_jpeg()
    {
        byte[] bytes = Convert.FromBase64String(OnePixelJpegBase64);
        JpegImage? img = JpegDecoder.Decode(bytes);

        Assert.NotNull(img);
        Assert.Equal(1, img!.Width);
        Assert.Equal(1, img.Height);
        Assert.True(img.Components is 1 or 3, $"unexpected component count {img.Components}");
        Assert.Equal(img.Width * img.Height * img.Components, img.Data.Length);
    }

    [Fact]
    public void Returns_null_for_non_jpeg_data()
    {
        Assert.Null(JpegDecoder.Decode(new byte[] { 1, 2, 3, 4, 5 }));
        Assert.Null(JpegDecoder.Decode(Array.Empty<byte>()));
    }
}
