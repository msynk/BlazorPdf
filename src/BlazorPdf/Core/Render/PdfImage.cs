// Image decoding: the raster cases needed to produce displayable pixels.

using BlazorPdf.Core.Filters;

namespace BlazorPdf.Core.Render;

/// <summary>
/// Decodes an image XObject (or inline image) into a browser-displayable data
/// URI. JPEG (DCTDecode) streams are passed through directly; other images are
/// decoded to RGBA and PNG-encoded.
/// </summary>
internal static class PdfImage
{
    public static string? BuildDataUri(PdfStream stream, IXRef xref, Dict? resources, (byte R, byte G, byte B) fillColor)
    {
        Dict dict = stream.Dict!;
        int width = GetInt(dict, "Width", "W");
        int height = GetInt(dict, "Height", "H");
        if (width <= 0 || height <= 0)
        {
            return null;
        }

        var filterNames = GetFilterNames(dict);
        if (filterNames.Contains("JPXDecode"))
        {
            return null; // JPEG2000 not supported by browsers
        }

        bool isJpeg = filterNames.Contains("DCTDecode") || filterNames.Contains("DCT");

        byte[] data;
        try
        {
            data = StreamDecoder.Decode(stream);
        }
        catch
        {
            return null;
        }

        if (isJpeg)
        {
            // StreamDecoder passes DCT data through, so `data` is the JPEG stream.
            return "data:image/jpeg;base64," + Convert.ToBase64String(data);
        }

        if (filterNames.Contains("CCITTFaxDecode") || filterNames.Contains("CCF"))
        {
            try
            {
                data = CcittFaxDecoder.Decode(data, ReadCcittParams(dict, xref, width, height));
            }
            catch
            {
                return null;
            }
        }

        bool imageMask = IsTrue(dict.Get("ImageMask", "IM"));
        int bpc = imageMask ? 1 : GetInt(dict, "BitsPerComponent", "BPC");
        if (bpc <= 0)
        {
            bpc = 8;
        }

        var rgba = new byte[width * height * 4];

        if (imageMask)
        {
            DecodeImageMask(data, width, height, dict, fillColor, rgba);
        }
        else
        {
            ColorSpace cs = ColorSpace.Create(dict.Get("ColorSpace", "CS"), xref, resources);
            DecodeColorImage(data, width, height, bpc, cs, rgba);
            ApplySoftMask(dict, xref, resources, width, height, rgba);
        }

        byte[] png = PngEncoder.EncodeRgba(width, height, rgba);
        return "data:image/png;base64," + Convert.ToBase64String(png);
    }

    private static void DecodeImageMask(byte[] data, int width, int height, Dict dict,
        (byte R, byte G, byte B) fill, byte[] rgba)
    {
        // Default Decode [0 1]: sample 0 paints, 1 is transparent.
        bool invert = false;
        if (dict.Get("Decode", "D") is List<object?> dec && dec.Count >= 2 && dec[0] is double d0 && d0 == 1)
        {
            invert = true;
        }

        int rowBytes = (width + 7) / 8;
        for (int y = 0; y < height; y++)
        {
            int rowStart = y * rowBytes;
            for (int x = 0; x < width; x++)
            {
                int bytePos = rowStart + (x >> 3);
                int bit = bytePos < data.Length ? (data[bytePos] >> (7 - (x & 7))) & 1 : 1;
                bool paint = invert ? bit == 1 : bit == 0;
                int p = (y * width + x) * 4;
                rgba[p] = fill.R;
                rgba[p + 1] = fill.G;
                rgba[p + 2] = fill.B;
                rgba[p + 3] = (byte)(paint ? 255 : 0);
            }
        }
    }

    private static void DecodeColorImage(byte[] data, int width, int height, int bpc,
        ColorSpace cs, byte[] rgba)
    {
        int nComps = cs.Components;
        bool indexed = cs is IndexedColorSpace;
        double maxVal = (1 << bpc) - 1;
        int rowBits = width * nComps * bpc;
        int rowBytes = (rowBits + 7) / 8;
        var comps = new double[nComps];

        for (int y = 0; y < height; y++)
        {
            int rowStart = y * rowBytes;
            int bitPos = 0;
            for (int x = 0; x < width; x++)
            {
                for (int c = 0; c < nComps; c++)
                {
                    int sample = ReadBits(data, rowStart, bitPos, bpc);
                    bitPos += bpc;
                    comps[c] = indexed ? sample : sample / maxVal;
                }
                var (r, g, b) = cs.GetRgb(comps);
                int p = (y * width + x) * 4;
                rgba[p] = r;
                rgba[p + 1] = g;
                rgba[p + 2] = b;
                rgba[p + 3] = 255;
            }
        }
    }

    private static void ApplySoftMask(Dict dict, IXRef xref, Dict? resources, int width, int height, byte[] rgba)
    {
        if (dict.Get("SMask") is not PdfStream smask || smask.Dict is null)
        {
            return;
        }
        int mw = GetInt(smask.Dict, "Width", "W");
        int mh = GetInt(smask.Dict, "Height", "H");
        int mbpc = GetInt(smask.Dict, "BitsPerComponent", "BPC");
        if (mw <= 0 || mh <= 0 || mbpc <= 0)
        {
            return;
        }

        byte[] mdata;
        try
        {
            mdata = StreamDecoder.Decode(smask);
        }
        catch
        {
            return;
        }

        double maxVal = (1 << mbpc) - 1;
        int rowBytes = (mw * mbpc + 7) / 8;
        for (int y = 0; y < height; y++)
        {
            int my = mh == height ? y : y * mh / height;
            int rowStart = my * rowBytes;
            for (int x = 0; x < width; x++)
            {
                int mx = mw == width ? x : x * mw / width;
                int sample = ReadBits(mdata, rowStart, mx * mbpc, mbpc);
                rgba[(y * width + x) * 4 + 3] = (byte)Math.Clamp((int)Math.Round(sample / maxVal * 255), 0, 255);
            }
        }
    }

    private static int ReadBits(byte[] data, int rowStart, int bitPos, int bpc)
    {
        if (bpc == 8)
        {
            int idx = rowStart + (bitPos >> 3);
            return idx < data.Length ? data[idx] : 0;
        }
        int value = 0;
        for (int i = 0; i < bpc; i++)
        {
            int absBit = bitPos + i;
            int bytePos = rowStart + (absBit >> 3);
            int bit = bytePos < data.Length ? (data[bytePos] >> (7 - (absBit & 7))) & 1 : 0;
            value = (value << 1) | bit;
        }
        return value;
    }

    private static List<string> GetFilterNames(Dict dict)
    {
        var names = new List<string>();
        object? filter = dict.Get("Filter", "F");
        if (filter is Name n)
        {
            names.Add(n.Value);
        }
        else if (filter is List<object?> arr)
        {
            foreach (var item in arr)
            {
                if (item is Name name)
                {
                    names.Add(name.Value);
                }
            }
        }
        return names;
    }

    private static CcittParams ReadCcittParams(Dict dict, IXRef xref, int width, int height)
    {
        // /DecodeParms may be a single dictionary or an array (one per filter).
        Dict? parms = null;
        object? dp = dict.Get("DecodeParms", "DP");
        if (dp is Dict d)
        {
            parms = d;
        }
        else if (dp is List<object?> arr)
        {
            foreach (var item in arr)
            {
                if (xref.FetchIfRef(item) is Dict candidate && candidate.Has("K"))
                {
                    parms = candidate;
                    break;
                }
                parms ??= xref.FetchIfRef(item) as Dict;
            }
        }

        int GetI(string key, int fallback) => parms?.Get(key) is double v ? (int)v : fallback;
        bool GetB(string key) => parms?.Get(key) is bool b && b;

        return new CcittParams
        {
            K = GetI("K", 0),
            Columns = GetI("Columns", width > 0 ? width : 1728),
            Rows = GetI("Rows", height),
            BlackIs1 = GetB("BlackIs1"),
            EncodedByteAlign = GetB("EncodedByteAlign"),
            EndOfBlock = parms?.Get("EndOfBlock") is not bool eob || eob,
        };
    }

    private static int GetInt(Dict dict, string key1, string key2)
        => dict.Get(key1, key2) is double d ? (int)d : 0;

    private static bool IsTrue(object? value) => value is bool b && b;
}
