namespace BlazorPdf.Engine.Rendering;

/// <summary>An sRGB color with straight alpha, one byte per channel.</summary>
public readonly struct PdfColor(byte r, byte g, byte b, byte a = 255)
{
    /// <summary>Red channel.</summary>
    public byte R { get; } = r;
    /// <summary>Green channel.</summary>
    public byte G { get; } = g;
    /// <summary>Blue channel.</summary>
    public byte B { get; } = b;
    /// <summary>Alpha channel.</summary>
    public byte A { get; } = a;

    /// <summary>Opaque black.</summary>
    public static readonly PdfColor Black = new(0, 0, 0);
    /// <summary>Opaque white.</summary>
    public static readonly PdfColor White = new(255, 255, 255);

    /// <summary>Creates a color from a grayscale value in [0,1].</summary>
    public static PdfColor FromGray(double g)
    {
        var v = Clamp(g);
        return new PdfColor(v, v, v);
    }

    /// <summary>Creates a color from RGB components in [0,1].</summary>
    public static PdfColor FromRgb(double r, double g, double b) =>
        new(Clamp(r), Clamp(g), Clamp(b));

    /// <summary>Creates a color from CMYK components in [0,1].</summary>
    public static PdfColor FromCmyk(double c, double m, double y, double k) => new(
        Clamp((1 - c) * (1 - k)),
        Clamp((1 - m) * (1 - k)),
        Clamp((1 - y) * (1 - k)));

    private static byte Clamp(double v) => (byte)Math.Clamp((int)Math.Round(v * 255.0), 0, 255);
}

/// <summary>A rendered page bitmap in top-down RGBA8888 layout.</summary>
public sealed class RenderedImage(int width, int height, byte[] pixels)
{
    /// <summary>Width in pixels.</summary>
    public int Width { get; } = width;

    /// <summary>Height in pixels.</summary>
    public int Height { get; } = height;

    /// <summary>RGBA pixel data, 4 bytes per pixel, row-major, top-down.</summary>
    public byte[] Pixels { get; } = pixels;

    /// <summary>Base64 of the raw RGBA buffer, for transfer to a canvas via <c>putImageData</c>.</summary>
    public string ToBase64() => Convert.ToBase64String(Pixels);

    /// <summary>Encodes this image as a PNG byte array.</summary>
    public byte[] ToPng() => PngEncoder.Encode(this);
}
