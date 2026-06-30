using System.IO.Compression;

namespace BlazorPdf.Engine;

/// <summary>
/// Decodes PDF stream data by applying its filter chain. Implemented entirely with
/// the .NET base class library (no external dependencies).
/// </summary>
internal static class PdfFilters
{
    /// <summary>Decodes a stream's raw bytes using its <c>/Filter</c> and <c>/DecodeParms</c>.</summary>
    public static byte[] Decode(PdfStream stream, PdfDocument document)
    {
        var data = stream.RawData;
        var dict = stream.Dictionary;

        var filters = NamesOf(document.Resolve(dict.Get("Filter") ?? dict.Get("F")));
        if (filters.Count == 0)
        {
            return data;
        }

        var parmsObj = document.Resolve(dict.Get("DecodeParms") ?? dict.Get("DP"));
        var parmsList = ParmsList(parmsObj, filters.Count, document);

        for (var i = 0; i < filters.Count; i++)
        {
            var parms = parmsList[i];
            data = filters[i] switch
            {
                "FlateDecode" or "Fl" => ApplyPredictor(Inflate(data), parms, document),
                "ASCIIHexDecode" or "AHx" => AsciiHexDecode(data),
                "ASCII85Decode" or "A85" => Ascii85Decode(data),
                "RunLengthDecode" or "RL" => RunLengthDecode(data),
                "LZWDecode" or "LZW" => ApplyPredictor(LzwDecode(data, EarlyChange(parms, document)), parms, document),
                "DCTDecode" or "JPXDecode" or "CCITTFaxDecode" or "JBIG2Decode" => data, // image codecs: left raw
                _ => data,
            };
        }

        return data;
    }

    private static List<string> NamesOf(PdfObject? filterObj)
    {
        var names = new List<string>();
        switch (filterObj)
        {
            case PdfName name:
                names.Add(name.Value);
                break;
            case PdfArray array:
                foreach (var item in array.Items)
                {
                    if (item is PdfName n) names.Add(n.Value);
                }
                break;
        }
        return names;
    }

    private static List<PdfDictionary?> ParmsList(PdfObject? parmsObj, int count, PdfDocument document)
    {
        var list = new List<PdfDictionary?>();
        if (parmsObj is PdfArray array)
        {
            foreach (var item in array.Items)
            {
                list.Add(document.Resolve(item) as PdfDictionary);
            }
        }
        else if (parmsObj is PdfDictionary dict)
        {
            list.Add(dict);
        }

        while (list.Count < count) list.Add(null);
        return list;
    }

    private static byte[] Inflate(byte[] data)
    {
        // PDF Flate streams are zlib-wrapped. Try ZLibStream, then fall back to raw
        // DEFLATE (skipping a 2-byte zlib header) for lenient producers.
        try
        {
            using var input = new MemoryStream(data);
            using var zlib = new ZLibStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            zlib.CopyTo(output);
            return output.ToArray();
        }
        catch
        {
            try
            {
                var offset = data.Length >= 2 ? 2 : 0;
                using var input = new MemoryStream(data, offset, data.Length - offset);
                using var deflate = new DeflateStream(input, CompressionMode.Decompress);
                using var output = new MemoryStream();
                deflate.CopyTo(output);
                return output.ToArray();
            }
            catch
            {
                return [];
            }
        }
    }

    private static int EarlyChange(PdfDictionary? parms, PdfDocument document)
        => parms?.Get("EarlyChange") is PdfNumber n ? n.AsInt : 1;

    private static byte[] ApplyPredictor(byte[] data, PdfDictionary? parms, PdfDocument document)
    {
        if (parms is null) return data;

        var predictor = (document.Resolve(parms.Get("Predictor")) as PdfNumber)?.AsInt ?? 1;
        if (predictor <= 1) return data;

        var colors = (document.Resolve(parms.Get("Colors")) as PdfNumber)?.AsInt ?? 1;
        var bpc = (document.Resolve(parms.Get("BitsPerComponent")) as PdfNumber)?.AsInt ?? 8;
        var columns = (document.Resolve(parms.Get("Columns")) as PdfNumber)?.AsInt ?? 1;

        var bytesPerPixel = Math.Max(1, colors * bpc / 8);
        var rowLength = (colors * bpc * columns + 7) / 8;
        if (rowLength <= 0) return data;

        if (predictor == 2)
        {
            return TiffPredictor(data, rowLength, bytesPerPixel);
        }

        return PngPredictor(data, rowLength, bytesPerPixel);
    }

    private static byte[] TiffPredictor(byte[] data, int rowLength, int bpp)
    {
        for (var row = 0; row + rowLength <= data.Length; row += rowLength)
        {
            for (var i = bpp; i < rowLength; i++)
            {
                data[row + i] = (byte)(data[row + i] + data[row + i - bpp]);
            }
        }
        return data;
    }

    private static byte[] PngPredictor(byte[] data, int rowLength, int bpp)
    {
        var stride = rowLength + 1; // each row is prefixed by a filter-type byte
        var rows = data.Length / stride;
        var output = new byte[rows * rowLength];
        var prev = new byte[rowLength];

        for (var r = 0; r < rows; r++)
        {
            var srcRow = r * stride;
            var filterType = data[srcRow];
            var cur = new byte[rowLength];
            Array.Copy(data, srcRow + 1, cur, 0, rowLength);

            for (var i = 0; i < rowLength; i++)
            {
                int a = i >= bpp ? cur[i - bpp] : 0;          // left
                int b = prev[i];                              // up
                int c = i >= bpp ? prev[i - bpp] : 0;         // up-left
                int x = cur[i];

                cur[i] = filterType switch
                {
                    0 => (byte)x,
                    1 => (byte)(x + a),
                    2 => (byte)(x + b),
                    3 => (byte)(x + (a + b) / 2),
                    4 => (byte)(x + Paeth(a, b, c)),
                    _ => (byte)x,
                };
            }

            Array.Copy(cur, 0, output, r * rowLength, rowLength);
            prev = cur;
        }

        return output;
    }

    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        var pa = Math.Abs(p - a);
        var pb = Math.Abs(p - b);
        var pc = Math.Abs(p - c);
        if (pa <= pb && pa <= pc) return a;
        return pb <= pc ? b : c;
    }

    private static byte[] AsciiHexDecode(byte[] data)
    {
        var output = new List<byte>(data.Length / 2);
        var hi = -1;
        foreach (var b in data)
        {
            if (b == (byte)'>') break;
            int v;
            if (b >= (byte)'0' && b <= (byte)'9') v = b - (byte)'0';
            else if (b >= (byte)'a' && b <= (byte)'f') v = b - (byte)'a' + 10;
            else if (b >= (byte)'A' && b <= (byte)'F') v = b - (byte)'A' + 10;
            else continue;

            if (hi < 0) hi = v;
            else { output.Add((byte)((hi << 4) | v)); hi = -1; }
        }
        if (hi >= 0) output.Add((byte)(hi << 4));
        return [.. output];
    }

    private static byte[] Ascii85Decode(byte[] data)
    {
        var output = new List<byte>();
        var tuple = new int[5];
        var count = 0;

        for (var i = 0; i < data.Length; i++)
        {
            var b = data[i];
            if (b == (byte)'~') break;
            if (b is 0 or 9 or 10 or 12 or 13 or 32) continue;
            if (b == (byte)'z' && count == 0)
            {
                output.AddRange([0, 0, 0, 0]);
                continue;
            }
            if (b < (byte)'!' || b > (byte)'u') continue;

            tuple[count++] = b - (byte)'!';
            if (count == 5)
            {
                long value = 0;
                for (var k = 0; k < 5; k++) value = value * 85 + tuple[k];
                output.Add((byte)(value >> 24));
                output.Add((byte)(value >> 16));
                output.Add((byte)(value >> 8));
                output.Add((byte)value);
                count = 0;
            }
        }

        if (count > 0)
        {
            for (var k = count; k < 5; k++) tuple[k] = 84;
            long value = 0;
            for (var k = 0; k < 5; k++) value = value * 85 + tuple[k];
            for (var k = 0; k < count - 1; k++)
            {
                output.Add((byte)(value >> (24 - k * 8)));
            }
        }

        return [.. output];
    }

    private static byte[] RunLengthDecode(byte[] data)
    {
        var output = new List<byte>();
        var i = 0;
        while (i < data.Length)
        {
            var length = data[i++];
            if (length == 128) break;
            if (length < 128)
            {
                for (var j = 0; j <= length && i < data.Length; j++) output.Add(data[i++]);
            }
            else
            {
                if (i >= data.Length) break;
                var value = data[i++];
                for (var j = 0; j < 257 - length; j++) output.Add(value);
            }
        }
        return [.. output];
    }

    private static byte[] LzwDecode(byte[] data, int earlyChange)
    {
        var output = new List<byte>();
        var table = new List<byte[]>();

        void ResetTable()
        {
            table.Clear();
            for (var i = 0; i < 256; i++) table.Add([(byte)i]);
            table.Add([]); // 256 = clear
            table.Add([]); // 257 = EOD
        }

        ResetTable();
        var codeWidth = 9;
        var bitBuffer = 0;
        var bitCount = 0;
        byte[]? previous = null;

        foreach (var b in data)
        {
            bitBuffer = (bitBuffer << 8) | b;
            bitCount += 8;
            while (bitCount >= codeWidth)
            {
                var code = (bitBuffer >> (bitCount - codeWidth)) & ((1 << codeWidth) - 1);
                bitCount -= codeWidth;

                if (code == 256)
                {
                    ResetTable();
                    codeWidth = 9;
                    previous = null;
                    continue;
                }
                if (code == 257)
                {
                    return [.. output];
                }

                byte[] entry;
                if (code < table.Count)
                {
                    entry = table[code];
                }
                else if (previous is not null)
                {
                    entry = [.. previous, previous[0]];
                }
                else
                {
                    return [.. output];
                }

                output.AddRange(entry);

                if (previous is not null)
                {
                    table.Add([.. previous, entry[0]]);
                }

                previous = entry;

                if (table.Count + earlyChange - 1 >= (1 << codeWidth) && codeWidth < 12)
                {
                    codeWidth++;
                }
            }
        }

        return [.. output];
    }
}
