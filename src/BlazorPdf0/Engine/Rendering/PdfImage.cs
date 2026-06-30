namespace BlazorPdf.Engine.Rendering;

/// <summary>
/// Decodes a PDF image XObject into a top-down RGBA buffer (plus optional alpha for
/// soft masks). Supports DeviceGray/RGB/CMYK, ICCBased (by component count), Indexed
/// palettes, image masks, and 1/2/4/8/16 bits per component. Image codecs
/// (DCT/JPX/CCITT/JBIG2) are not decoded and yield a null result.
/// </summary>
internal sealed class PdfImage
{
    private const long MaxPixels = 40_000_000;

    public int Width { get; private init; }
    public int Height { get; private init; }

    /// <summary>RGBA buffer (top-down) for color images; null for stencil masks.</summary>
    public byte[]? Rgba { get; private init; }

    /// <summary>True for a 1-bit stencil mask painted with the current fill color.</summary>
    public bool IsStencil { get; private init; }

    /// <summary>Per-pixel paint flag for stencil masks (true = paint).</summary>
    public bool[]? StencilPaint { get; private init; }

    public static PdfImage? Decode(PdfDocument doc, PdfStream stream)
    {
        try
        {
            return DecodeInternal(doc, stream);
        }
        catch
        {
            return null;
        }
    }

    private static PdfImage? DecodeInternal(PdfDocument doc, PdfStream stream)
    {
        var dict = stream.Dictionary;
        var width = (doc.Resolve(dict.Get("Width") ?? dict.Get("W")) as PdfNumber)?.AsInt ?? 0;
        var height = (doc.Resolve(dict.Get("Height") ?? dict.Get("H")) as PdfNumber)?.AsInt ?? 0;
        if (width <= 0 || height <= 0 || (long)width * height > MaxPixels) return null;

        // Image codecs other than baseline JPEG are not decoded.
        var hasDct = false;
        foreach (var f in FilterNames(doc, dict))
        {
            if (f is "DCTDecode" or "DCT") hasDct = true;
            else if (f is "JPXDecode" or "CCITTFaxDecode" or "CCF" or "JBIG2Decode") return null;
        }

        var data = PdfFilters.Decode(stream, doc);

        if (hasDct)
        {
            return DecodeJpeg(doc, dict, data, width, height);
        }

        var isMask = (doc.Resolve(dict.Get("ImageMask") ?? dict.Get("IM")) as PdfBoolean)?.Value == true;
        var bpc = isMask ? 1 : (doc.Resolve(dict.Get("BitsPerComponent") ?? dict.Get("BPC")) as PdfNumber)?.AsInt ?? 8;

        if (isMask)
        {
            return DecodeStencil(doc, dict, data, width, height);
        }

        var cs = ColorSpaceInfo.Parse(doc, doc.Resolve(dict.Get("ColorSpace") ?? dict.Get("CS")));
        var rgba = DecodeColor(data, width, height, bpc, cs);
        ApplySoftMask(doc, dict, rgba, width, height);

        return new PdfImage { Width = width, Height = height, Rgba = rgba };
    }

    private static PdfImage? DecodeJpeg(PdfDocument doc, PdfDictionary dict, byte[] jpegBytes, int width, int height)
    {
        var jpeg = JpegDecoder.Decode(jpegBytes);
        if (jpeg is null) return null;

        var w = jpeg.Width;
        var h = jpeg.Height;
        var n = jpeg.Components;
        var src = jpeg.Pixels;
        var rgba = new byte[w * h * 4];

        // A reversed /Decode (e.g., [1 0 1 0 1 0 1 0]) flags inverted CMYK, common in
        // Adobe-produced CMYK JPEGs.
        var invertCmyk = n == 4 && IsInverted(doc, dict);

        for (var i = 0; i < w * h; i++)
        {
            PdfColor color;
            var s = i * n;
            switch (n)
            {
                case 1:
                    color = PdfColor.FromGray(src[s] / 255.0);
                    break;
                case 3:
                    color = new PdfColor(src[s], src[s + 1], src[s + 2]); // already RGB
                    break;
                case 4:
                    double c = src[s] / 255.0, m = src[s + 1] / 255.0, y = src[s + 2] / 255.0, k = src[s + 3] / 255.0;
                    if (invertCmyk) { c = 1 - c; m = 1 - m; y = 1 - y; k = 1 - k; }
                    color = PdfColor.FromCmyk(c, m, y, k);
                    break;
                default:
                    color = PdfColor.Black;
                    break;
            }

            var o = i * 4;
            rgba[o] = color.R;
            rgba[o + 1] = color.G;
            rgba[o + 2] = color.B;
            rgba[o + 3] = 255;
        }

        var img = new PdfImage { Width = w, Height = h, Rgba = rgba };
        ApplySoftMask(doc, dict, rgba, w, h);
        return img;
    }

    private static bool IsInverted(PdfDocument doc, PdfDictionary dict)
    {
        return doc.Resolve(dict.Get("Decode") ?? dict.Get("D")) is PdfArray { Count: >= 2 } dec &&
               doc.Resolve(dec[0]) is PdfNumber d0 && d0.Value == 1;
    }

    private static PdfImage DecodeStencil(PdfDocument doc, PdfDictionary dict, byte[] data, int width, int height)
    {
        // Default Decode for a mask is [0 1]: sample 0 paints. [1 0] inverts.
        var invert = false;
        if (doc.Resolve(dict.Get("Decode") ?? dict.Get("D")) is PdfArray { Count: >= 2 } dec &&
            doc.Resolve(dec[0]) is PdfNumber d0 && d0.Value == 1)
        {
            invert = true;
        }

        var paint = new bool[width * height];
        var rowBytes = (width + 7) / 8;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var idx = y * rowBytes + (x >> 3);
                var bit = idx < data.Length ? (data[idx] >> (7 - (x & 7))) & 1 : 0;
                var sampleZero = bit == 0;
                paint[y * width + x] = invert ? !sampleZero : sampleZero;
            }
        }

        return new PdfImage { Width = width, Height = height, IsStencil = true, StencilPaint = paint };
    }

    private static byte[] DecodeColor(byte[] data, int width, int height, int bpc, ColorSpaceInfo cs)
    {
        var rgba = new byte[width * height * 4];
        var components = cs.Indexed ? 1 : cs.Components;
        var rowBits = width * components * bpc;
        var rowBytes = (rowBits + 7) / 8;
        var maxVal = (1 << bpc) - 1;
        Span<double> comps = stackalloc double[Math.Max(1, components)];

        for (var y = 0; y < height; y++)
        {
            var reader = new BitReader(data, y * rowBytes);
            for (var x = 0; x < width; x++)
            {
                PdfColor color;
                if (cs.Indexed)
                {
                    var index = reader.Read(bpc);
                    color = cs.PaletteColor(index);
                }
                else
                {
                    for (var c = 0; c < components; c++)
                    {
                        comps[c] = reader.Read(bpc) / (double)maxVal;
                    }
                    color = cs.ToColor(comps[..components]);
                }

                var o = (y * width + x) * 4;
                rgba[o] = color.R;
                rgba[o + 1] = color.G;
                rgba[o + 2] = color.B;
                rgba[o + 3] = 255;
            }
        }

        return rgba;
    }

    private static void ApplySoftMask(PdfDocument doc, PdfDictionary dict, byte[] rgba, int width, int height)
    {
        if (doc.Resolve(dict.Get("SMask")) is not PdfStream smaskStream) return;

        var sm = Decode(doc, smaskStream);
        if (sm?.Rgba is null) return;

        // Nearest-neighbor resample of the mask's luminance into the image's alpha.
        for (var y = 0; y < height; y++)
        {
            var sy = sm.Height == height ? y : y * sm.Height / height;
            for (var x = 0; x < width; x++)
            {
                var sx = sm.Width == width ? x : x * sm.Width / width;
                var so = (sy * sm.Width + sx) * 4;
                rgba[(y * width + x) * 4 + 3] = sm.Rgba[so]; // gray => R channel is the alpha
            }
        }
    }

    private static List<string> FilterNames(PdfDocument doc, PdfDictionary dict)
    {
        var names = new List<string>();
        var filter = doc.Resolve(dict.Get("Filter") ?? dict.Get("F"));
        if (filter is PdfName n) names.Add(n.Value);
        else if (filter is PdfArray arr)
        {
            foreach (var item in arr.Items)
            {
                if (doc.Resolve(item) is PdfName fn) names.Add(fn.Value);
            }
        }
        return names;
    }
}

/// <summary>A small big-endian bit reader for image sample decoding.</summary>
internal struct BitReader(byte[] data, int byteOffset)
{
    private readonly byte[] _data = data;
    private int _bytePos = byteOffset;
    private int _bitPos;

    public int Read(int bits)
    {
        if (bits == 8)
        {
            return _bytePos < _data.Length ? _data[_bytePos++] : 0;
        }

        var value = 0;
        for (var i = 0; i < bits; i++)
        {
            var bit = _bytePos < _data.Length ? (_data[_bytePos] >> (7 - _bitPos)) & 1 : 0;
            value = (value << 1) | bit;
            if (++_bitPos == 8) { _bitPos = 0; _bytePos++; }
        }
        return value;
    }
}

/// <summary>Resolved image color space with conversion to RGB.</summary>
internal sealed class ColorSpaceInfo
{
    private enum Kind { Gray, Rgb, Cmyk }

    private Kind _kind = Kind.Rgb;
    public int Components { get; private set; } = 3;
    public bool Indexed { get; private set; }
    private byte[] _palette = [];
    private ColorSpaceInfo? _base;

    public static ColorSpaceInfo Parse(PdfDocument doc, PdfObject? obj)
    {
        switch (obj)
        {
            case PdfName name:
                return FromName(name.Value);

            case PdfArray { Count: > 0 } array when doc.Resolve(array[0]) is PdfName family:
                switch (family.Value)
                {
                    case "ICCBased" when doc.Resolve(array[1]) is PdfStream icc:
                        var n = (doc.Resolve(icc.Dictionary.Get("N")) as PdfNumber)?.AsInt ?? 3;
                        return FromComponents(n);

                    case "Indexed" or "I":
                        return ParseIndexed(doc, array);

                    case "CalRGB" or "Lab": return FromComponents(3);
                    case "CalGray": return FromComponents(1);
                    case "DeviceN" when doc.Resolve(array[1]) is PdfArray names:
                        return FromComponents(names.Count);
                    case "Separation": return FromComponents(1);
                }
                break;
        }

        return FromComponents(3);
    }

    private static ColorSpaceInfo ParseIndexed(PdfDocument doc, PdfArray array)
    {
        var baseCs = Parse(doc, doc.Resolve(array[1]));
        var lookup = doc.Resolve(array.Count > 3 ? array[3] : null);
        byte[] palette = lookup switch
        {
            PdfString s => s.Bytes,
            PdfStream st => PdfFilters.Decode(st, doc),
            _ => [],
        };

        return new ColorSpaceInfo
        {
            Indexed = true,
            Components = 1,
            _base = baseCs,
            _palette = palette,
        };
    }

    private static ColorSpaceInfo FromName(string name) => name switch
    {
        "DeviceGray" or "G" or "CalGray" => FromComponents(1),
        "DeviceCMYK" or "CMYK" => FromComponents(4),
        _ => FromComponents(3),
    };

    private static ColorSpaceInfo FromComponents(int n) => new()
    {
        Components = n,
        _kind = n switch { 1 => Kind.Gray, 4 => Kind.Cmyk, _ => Kind.Rgb },
    };

    public PdfColor ToColor(ReadOnlySpan<double> comps) => _kind switch
    {
        Kind.Gray => PdfColor.FromGray(comps.Length > 0 ? comps[0] : 0),
        Kind.Cmyk => PdfColor.FromCmyk(comps[0], comps[1], comps[2], comps[3]),
        _ => comps.Length >= 3 ? PdfColor.FromRgb(comps[0], comps[1], comps[2]) : PdfColor.Black,
    };

    public PdfColor PaletteColor(int index)
    {
        if (_base is null) return PdfColor.Black;
        var bc = _base.Components;
        var start = index * bc;
        if (start + bc > _palette.Length) return PdfColor.Black;

        Span<double> comps = stackalloc double[bc];
        for (var i = 0; i < bc; i++) comps[i] = _palette[start + i] / 255.0;
        return _base.ToColor(comps);
    }
}
