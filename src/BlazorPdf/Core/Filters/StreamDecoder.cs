// The stream filter pipeline: decode-stream factories.

using System.IO.Compression;

namespace BlazorPdf.Core.Filters;

/// <summary>
/// Applies the <c>/Filter</c> chain declared on a PDF stream dictionary to
/// produce the decoded bytes. Supports the filters required for structural
/// decoding (FlateDecode with predictors) plus a few simple ASCII filters.
/// Image-only filters (DCT/JPX/CCITT/JBIG2) are returned undecoded for now.
/// </summary>
public static class StreamDecoder
{
    /// <summary>Returns the fully decoded bytes for <paramref name="stream"/>.</summary>
    public static byte[] Decode(PdfStream stream)
    {
        Dict dict = stream.Dict ?? throw new PdfFormatException("Stream has no dictionary.");

        stream.Reset();
        byte[] data = stream.GetBytes();

        var filters = ResolveNames(dict.Get("Filter", "F"));
        if (filters.Count == 0)
        {
            return data;
        }

        var parmsList = ResolveParms(dict.Get("DecodeParms", "DP"), filters.Count);
        for (int i = 0; i < filters.Count; i++)
        {
            data = ApplyFilter(filters[i], data, parmsList[i]);
        }
        return data;
    }

    private static byte[] ApplyFilter(string name, byte[] data, Dict? parms)
    {
        switch (name)
        {
            case "FlateDecode":
            case "Fl":
                return ApplyPredictorIfAny(Inflate(data), parms);
            case "LZWDecode":
            case "LZW":
                return ApplyPredictorIfAny(LzwDecode.Decode(data, EarlyChange(parms)), parms);
            case "ASCIIHexDecode":
            case "AHx":
                return AsciiHexDecode(data);
            case "ASCII85Decode":
            case "A85":
                return Ascii85Decode(data);
            case "RunLengthDecode":
            case "RL":
                return RunLengthDecode(data);
            // Image compression filters are decoded later by the image pipeline.
            case "DCTDecode":
            case "DCT":
            case "JPXDecode":
            case "CCITTFaxDecode":
            case "CCF":
            case "JBIG2Decode":
                return data;
            default:
                return data;
        }
    }

    private static byte[] ApplyPredictorIfAny(byte[] data, Dict? parms)
    {
        if (parms is null)
        {
            return data;
        }
        int predictor = ToInt(parms.Get("Predictor"), 1);
        if (predictor <= 1)
        {
            return data;
        }
        int colors = ToInt(parms.Get("Colors"), 1);
        int bpc = ToInt(parms.Get("BitsPerComponent"), 8);
        int columns = ToInt(parms.Get("Columns"), 1);
        return Predictor.Apply(data, predictor, colors, bpc, columns);
    }

    private static int EarlyChange(Dict? parms) => ToInt(parms?.Get("EarlyChange"), 1);

    private static byte[] Inflate(byte[] data)
    {
        // PDF FlateDecode is zlib-wrapped deflate (RFC 1950). Try ZLib first,
        // then fall back to raw deflate for malformed producers.
        try
        {
            return InflateWith(data, raw: false);
        }
        catch
        {
            try
            {
                return InflateWith(data, raw: true);
            }
            catch
            {
                return [];
            }
        }
    }

    private static byte[] InflateWith(byte[] data, bool raw)
    {
        using var input = new MemoryStream(data);
        using Stream decompressor = raw
            ? new DeflateStream(input, CompressionMode.Decompress)
            : new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        decompressor.CopyTo(output);
        return output.ToArray();
    }

    private static byte[] AsciiHexDecode(byte[] data)
    {
        var output = new List<byte>(data.Length / 2);
        int hi = -1;
        foreach (byte b in data)
        {
            if (b == (byte)'>')
            {
                break;
            }
            int v = b switch
            {
                >= (byte)'0' and <= (byte)'9' => b - '0',
                >= (byte)'A' and <= (byte)'F' => b - 'A' + 10,
                >= (byte)'a' and <= (byte)'f' => b - 'a' + 10,
                _ => -1,
            };
            if (v < 0)
            {
                continue; // skip whitespace and other characters
            }
            if (hi < 0)
            {
                hi = v;
            }
            else
            {
                output.Add((byte)((hi << 4) | v));
                hi = -1;
            }
        }
        if (hi >= 0)
        {
            output.Add((byte)(hi << 4));
        }
        return output.ToArray();
    }

    private static byte[] Ascii85Decode(byte[] data)
    {
        var output = new List<byte>(data.Length);
        var group = new int[5];
        int count = 0;

        for (int idx = 0; idx < data.Length; idx++)
        {
            int c = data[idx];
            if (c == '~')
            {
                break;
            }
            if (c is 0x20 or 0x09 or 0x0A or 0x0C or 0x0D or 0x00)
            {
                continue;
            }
            if (c == 'z' && count == 0)
            {
                output.Add(0);
                output.Add(0);
                output.Add(0);
                output.Add(0);
                continue;
            }
            if (c < '!' || c > 'u')
            {
                continue;
            }
            group[count++] = c - '!';
            if (count == 5)
            {
                long value = 0;
                for (int i = 0; i < 5; i++)
                {
                    value = value * 85 + group[i];
                }
                output.Add((byte)(value >> 24));
                output.Add((byte)(value >> 16));
                output.Add((byte)(value >> 8));
                output.Add((byte)value);
                count = 0;
            }
        }

        if (count > 0)
        {
            for (int i = count; i < 5; i++)
            {
                group[i] = 84; // pad with 'u'
            }
            long value = 0;
            for (int i = 0; i < 5; i++)
            {
                value = value * 85 + group[i];
            }
            for (int i = 0; i < count - 1; i++)
            {
                output.Add((byte)(value >> (24 - i * 8)));
            }
        }

        return output.ToArray();
    }

    private static byte[] RunLengthDecode(byte[] data)
    {
        var output = new List<byte>(data.Length * 2);
        int i = 0;
        while (i < data.Length)
        {
            int length = data[i++];
            if (length == 128)
            {
                break; // EOD
            }
            if (length < 128)
            {
                int count = length + 1;
                for (int j = 0; j < count && i < data.Length; j++)
                {
                    output.Add(data[i++]);
                }
            }
            else
            {
                int count = 257 - length;
                if (i < data.Length)
                {
                    byte value = data[i++];
                    for (int j = 0; j < count; j++)
                    {
                        output.Add(value);
                    }
                }
            }
        }
        return output.ToArray();
    }

    private static List<string> ResolveNames(object? filter)
    {
        var result = new List<string>();
        switch (filter)
        {
            case Name n:
                result.Add(n.Value);
                break;
            case List<object?> arr:
                foreach (var item in arr)
                {
                    if (item is Name name)
                    {
                        result.Add(name.Value);
                    }
                }
                break;
        }
        return result;
    }

    private static List<Dict?> ResolveParms(object? parms, int count)
    {
        var result = new List<Dict?>(count);
        if (parms is List<object?> arr)
        {
            for (int i = 0; i < count; i++)
            {
                result.Add(i < arr.Count ? arr[i] as Dict : null);
            }
        }
        else
        {
            result.Add(parms as Dict);
            for (int i = 1; i < count; i++)
            {
                result.Add(null);
            }
        }
        return result;
    }

    private static int ToInt(object? value, int fallback)
        => value is double d ? (int)d : fallback;
}
