namespace BlazorPdf.Engine.Rendering;

/// <summary>
/// A baseline (sequential DCT) JPEG decoder built from scratch on the .NET BCL.
/// Handles grayscale, YCbCr (→RGB) and 4-component (YCCK/CMYK) images with chroma
/// subsampling and restart intervals. Progressive and arithmetic-coded JPEGs are
/// not supported (returns null).
/// </summary>
internal sealed class JpegDecoder
{
    private const long MaxPixels = 40_000_000;

    /// <summary>Decoded result: interleaved samples (1=Gray, 3=RGB, 4=CMYK).</summary>
    public sealed class Result
    {
        public int Width { get; init; }
        public int Height { get; init; }
        public int Components { get; init; }
        public byte[] Pixels { get; init; } = [];
    }

    private static readonly int[] ZigZag =
    [
        0, 1, 8, 16, 9, 2, 3, 10, 17, 24, 32, 25, 18, 11, 4, 5,
        12, 19, 26, 33, 40, 48, 41, 34, 27, 20, 13, 6, 7, 14, 21, 28,
        35, 42, 49, 56, 57, 50, 43, 36, 29, 22, 15, 23, 30, 37, 44, 51,
        58, 59, 52, 45, 38, 31, 39, 46, 53, 60, 61, 54, 47, 55, 62, 63,
    ];

    private sealed class Component
    {
        public int Id, H, V, QuantId, DcTable, AcTable, Pred;
        public int BlocksPerLine, BlocksPerColumn;
        public float[] Plane = [];
        public int PlaneWidth, PlaneHeight;
    }

    private sealed class Huffman
    {
        public int[] MinCode = new int[17];
        public int[] MaxCode = new int[18];
        public int[] ValPtr = new int[17];
        public byte[] Values = [];
    }

    private readonly byte[] _data;
    private int _pos;

    private readonly int[][] _quant = new int[4][];
    private readonly Huffman?[] _dc = new Huffman?[4];
    private readonly Huffman?[] _ac = new Huffman?[4];
    private Component[] _components = [];
    private int _width, _height, _maxH = 1, _maxV = 1;
    private int _restartInterval;
    private int _adobeTransform = -1;

    private JpegDecoder(byte[] data) => _data = data;

    public static Result? Decode(byte[] data)
    {
        try
        {
            return new JpegDecoder(data).Run();
        }
        catch
        {
            return null;
        }
    }

    private Result? Run()
    {
        if (_data.Length < 2 || _data[0] != 0xFF || _data[1] != 0xD8) return null; // SOI
        _pos = 2;

        while (_pos + 1 < _data.Length)
        {
            if (_data[_pos] != 0xFF) { _pos++; continue; }
            var marker = _data[_pos + 1];
            _pos += 2;

            switch (marker)
            {
                case 0xD9: // EOI
                    return null;
                case 0xC0: // SOF0 baseline
                    ReadFrame();
                    break;
                case 0xC1: // SOF1 extended sequential (treat like baseline)
                    ReadFrame();
                    break;
                case 0xC2: case 0xC3: case 0xC5: case 0xC6: case 0xC7:
                case 0xC9: case 0xCA: case 0xCB: case 0xCD: case 0xCE: case 0xCF:
                    return null; // progressive / arithmetic / lossless unsupported
                case 0xC4: // DHT
                    ReadHuffmanTables();
                    break;
                case 0xDB: // DQT
                    ReadQuantTables();
                    break;
                case 0xDD: // DRI
                    ReadRestartInterval();
                    break;
                case 0xEE: // APP14 (Adobe)
                    ReadAdobe();
                    break;
                case 0xDA: // SOS
                    return ReadScanAndDecode();
                default:
                    SkipSegment();
                    break;
            }
        }

        return null;
    }

    private int ReadU16()
    {
        var v = (_data[_pos] << 8) | _data[_pos + 1];
        _pos += 2;
        return v;
    }

    private void SkipSegment()
    {
        var len = ReadU16();
        _pos += len - 2;
    }

    private void ReadRestartInterval()
    {
        ReadU16();
        _restartInterval = ReadU16();
    }

    private void ReadAdobe()
    {
        var len = ReadU16();
        var end = _pos + len - 2;
        if (len >= 14) _adobeTransform = _data[_pos + 11];
        _pos = end;
    }

    private void ReadQuantTables()
    {
        var end = _pos + ReadU16() - 2;
        while (_pos < end)
        {
            var pq_tq = _data[_pos++];
            var precision = pq_tq >> 4;
            var id = pq_tq & 0x0F;
            if (id > 3) return;
            var table = new int[64];
            for (var i = 0; i < 64; i++)
            {
                table[i] = precision == 0 ? _data[_pos++] : ReadU16();
            }
            _quant[id] = table;
        }
    }

    private void ReadHuffmanTables()
    {
        var end = _pos + ReadU16() - 2;
        while (_pos < end)
        {
            var tc_th = _data[_pos++];
            var clazz = tc_th >> 4;
            var id = tc_th & 0x0F;
            if (id > 3) return;

            var counts = new int[17];
            var total = 0;
            for (var i = 1; i <= 16; i++) { counts[i] = _data[_pos++]; total += counts[i]; }

            var values = new byte[total];
            for (var i = 0; i < total; i++) values[i] = _data[_pos++];

            var h = BuildHuffman(counts, values);
            if (clazz == 0) _dc[id] = h; else _ac[id] = h;
        }
    }

    private static Huffman BuildHuffman(int[] counts, byte[] values)
    {
        // Canonical code generation per the JPEG spec (Annex C/F).
        var huffSize = new int[values.Length + 1];
        var k = 0;
        for (var l = 1; l <= 16; l++)
        {
            for (var i = 0; i < counts[l]; i++) huffSize[k++] = l;
        }

        var huffCode = new int[values.Length];
        var code = 0;
        var si = huffSize.Length > 0 ? huffSize[0] : 0;
        for (var p = 0; p < values.Length;)
        {
            while (p < values.Length && huffSize[p] == si) { huffCode[p++] = code++; }
            code <<= 1;
            si++;
        }

        var h = new Huffman { Values = values };
        var idx = 0;
        for (var l = 1; l <= 16; l++)
        {
            if (counts[l] > 0)
            {
                h.ValPtr[l] = idx;
                h.MinCode[l] = huffCode[idx];
                idx += counts[l];
                h.MaxCode[l] = huffCode[idx - 1];
            }
            else
            {
                h.MaxCode[l] = -1;
            }
        }
        h.MaxCode[17] = int.MaxValue;
        return h;
    }

    private void ReadFrame()
    {
        ReadU16(); // length
        _pos++; // precision
        _height = ReadU16();
        _width = ReadU16();
        var n = _data[_pos++];
        if (_width <= 0 || _height <= 0 || (long)_width * _height > MaxPixels || n is < 1 or > 4)
        {
            throw new InvalidOperationException("Unsupported frame.");
        }

        _components = new Component[n];
        for (var i = 0; i < n; i++)
        {
            var id = _data[_pos++];
            var hv = _data[_pos++];
            var q = _data[_pos++];
            var comp = new Component { Id = id, H = hv >> 4, V = hv & 0x0F, QuantId = q };
            _maxH = Math.Max(_maxH, comp.H);
            _maxV = Math.Max(_maxV, comp.V);
            _components[i] = comp;
        }
    }

    private Result ReadScanAndDecode()
    {
        ReadU16(); // header length
        var ns = _data[_pos++];
        for (var i = 0; i < ns; i++)
        {
            var cs = _data[_pos++];
            var td_ta = _data[_pos++];
            var comp = Array.Find(_components, c => c.Id == cs) ?? _components[i];
            comp.DcTable = td_ta >> 4;
            comp.AcTable = td_ta & 0x0F;
        }
        _pos += 3; // Ss, Se, Ah/Al (ignored for baseline)

        var mcusPerLine = (_width + 8 * _maxH - 1) / (8 * _maxH);
        var mcusPerColumn = (_height + 8 * _maxV - 1) / (8 * _maxV);

        foreach (var comp in _components)
        {
            comp.BlocksPerLine = mcusPerLine * comp.H;
            comp.BlocksPerColumn = mcusPerColumn * comp.V;
            comp.PlaneWidth = comp.BlocksPerLine * 8;
            comp.PlaneHeight = comp.BlocksPerColumn * 8;
            comp.Plane = new float[comp.PlaneWidth * comp.PlaneHeight];
        }

        DecodeScan(mcusPerLine, mcusPerColumn);
        return BuildResult();
    }

    private int _bitBuffer;
    private int _bitCount;
    private bool _markerHit;

    private void ResetBits()
    {
        _bitBuffer = 0;
        _bitCount = 0;
        _markerHit = false;
    }

    private int NextBit()
    {
        if (_bitCount == 0)
        {
            if (_markerHit || _pos >= _data.Length) return 0;
            var b = _data[_pos++];
            if (b == 0xFF)
            {
                var next = _pos < _data.Length ? _data[_pos] : 0xD9;
                if (next == 0x00) { _pos++; }
                else { _markerHit = true; return 0; }
            }
            _bitBuffer = b;
            _bitCount = 8;
        }
        _bitCount--;
        return (_bitBuffer >> _bitCount) & 1;
    }

    private int Receive(int n)
    {
        var v = 0;
        for (var i = 0; i < n; i++) v = (v << 1) | NextBit();
        return v;
    }

    private static int Extend(int v, int n) => v < (1 << (n - 1)) ? v - (1 << n) + 1 : v;

    private int DecodeHuffman(Huffman h)
    {
        var code = 0;
        for (var l = 1; l <= 16; l++)
        {
            code = (code << 1) | NextBit();
            if (h.MaxCode[l] >= 0 && code <= h.MaxCode[l])
            {
                return h.Values[h.ValPtr[l] + code - h.MinCode[l]];
            }
        }
        return 0;
    }

    private void DecodeScan(int mcusPerLine, int mcusPerColumn)
    {
        ResetBits();
        foreach (var c in _components) c.Pred = 0;

        var mcuCount = 0;
        var totalMcus = mcusPerLine * mcusPerColumn;
        var block = new float[64];

        for (var my = 0; my < mcusPerColumn; my++)
        {
            for (var mx = 0; mx < mcusPerLine; mx++)
            {
                if (_restartInterval > 0 && mcuCount > 0 && mcuCount % _restartInterval == 0)
                {
                    HandleRestart();
                    foreach (var c in _components) c.Pred = 0;
                }

                foreach (var comp in _components)
                {
                    for (var by = 0; by < comp.V; by++)
                    {
                        for (var bx = 0; bx < comp.H; bx++)
                        {
                            DecodeBlock(comp, block);
                            StoreBlock(comp, block, (mx * comp.H + bx), (my * comp.V + by));
                        }
                    }
                }

                mcuCount++;
                if (mcuCount >= totalMcus) return;
            }
        }
    }

    private void HandleRestart()
    {
        // Align to the byte boundary and skip the RSTn marker.
        _bitCount = 0;
        _markerHit = false;
        while (_pos + 1 < _data.Length)
        {
            if (_data[_pos] == 0xFF && _data[_pos + 1] >= 0xD0 && _data[_pos + 1] <= 0xD7)
            {
                _pos += 2;
                return;
            }
            _pos++;
        }
    }

    private void DecodeBlock(Component comp, float[] block)
    {
        Array.Clear(block, 0, 64);
        var quant = _quant[comp.QuantId] ?? throw new InvalidOperationException("Missing quant table.");
        var dc = _dc[comp.DcTable] ?? throw new InvalidOperationException("Missing DC table.");
        var ac = _ac[comp.AcTable] ?? throw new InvalidOperationException("Missing AC table.");

        var t = DecodeHuffman(dc);
        var diff = t == 0 ? 0 : Extend(Receive(t), t);
        comp.Pred += diff;
        block[0] = comp.Pred * quant[0];

        var k = 1;
        while (k < 64)
        {
            var rs = DecodeHuffman(ac);
            var r = rs >> 4;
            var s = rs & 0x0F;
            if (s == 0)
            {
                if (r == 15) { k += 16; continue; }
                break;
            }
            k += r;
            if (k >= 64) break;
            var val = Extend(Receive(s), s);
            block[ZigZag[k]] = val * quant[k];
            k++;
        }

        Idct(block);
    }

    private static readonly float[,] IdctCos = BuildIdctCos();

    private static float[,] BuildIdctCos()
    {
        var a = new float[8, 8];
        for (var u = 0; u < 8; u++)
        {
            var cu = u == 0 ? (float)(1.0 / Math.Sqrt(2)) : 1f;
            for (var x = 0; x < 8; x++)
            {
                a[u, x] = (float)(cu * Math.Cos((2 * x + 1) * u * Math.PI / 16.0));
            }
        }
        return a;
    }

    private static void Idct(float[] block)
    {
        var tmp = new float[64];
        // Rows: for each row y, transform the 8 frequency coefficients to samples.
        for (var y = 0; y < 8; y++)
        {
            for (var x = 0; x < 8; x++)
            {
                float sum = 0;
                for (var u = 0; u < 8; u++) sum += IdctCos[u, x] * block[y * 8 + u];
                tmp[y * 8 + x] = sum * 0.5f;
            }
        }
        // Columns.
        for (var x = 0; x < 8; x++)
        {
            for (var y = 0; y < 8; y++)
            {
                float sum = 0;
                for (var v = 0; v < 8; v++) sum += IdctCos[v, y] * tmp[v * 8 + x];
                block[y * 8 + x] = sum * 0.5f + 128f;
            }
        }
    }

    private static void StoreBlock(Component comp, float[] block, int blockX, int blockY)
    {
        var px0 = blockX * 8;
        var py0 = blockY * 8;
        for (var y = 0; y < 8; y++)
        {
            var py = py0 + y;
            if (py >= comp.PlaneHeight) break;
            for (var x = 0; x < 8; x++)
            {
                var px = px0 + x;
                if (px >= comp.PlaneWidth) break;
                comp.Plane[py * comp.PlaneWidth + px] = block[y * 8 + x];
            }
        }
    }

    private Result BuildResult()
    {
        var n = _components.Length;
        var pixels = new byte[_width * _height * n];

        for (var y = 0; y < _height; y++)
        {
            for (var x = 0; x < _width; x++)
            {
                var o = (y * _width + x) * n;
                for (var ci = 0; ci < n; ci++)
                {
                    var comp = _components[ci];
                    var sx = x * comp.H / _maxH;
                    var sy = y * comp.V / _maxV;
                    sx = Math.Min(sx, comp.PlaneWidth - 1);
                    sy = Math.Min(sy, comp.PlaneHeight - 1);
                    pixels[o + ci] = Clamp(comp.Plane[sy * comp.PlaneWidth + sx]);
                }
            }
        }

        ColorConvert(pixels, n);
        return new Result { Width = _width, Height = _height, Components = n, Pixels = pixels };
    }

    private void ColorConvert(byte[] pixels, int n)
    {
        var transform = _adobeTransform;
        if (n == 3 && transform < 0) transform = 1; // default JFIF: YCbCr
        if (n == 4 && transform < 0) transform = 0; // default: CMYK as-is

        if (n == 3 && transform == 1)
        {
            for (var i = 0; i < pixels.Length; i += 3)
            {
                YccToRgb(pixels[i], pixels[i + 1], pixels[i + 2], out var r, out var g, out var b);
                pixels[i] = r; pixels[i + 1] = g; pixels[i + 2] = b;
            }
        }
        else if (n == 4 && transform == 2)
        {
            // YCCK -> CMYK (K channel left intact).
            for (var i = 0; i < pixels.Length; i += 4)
            {
                YccToRgb(pixels[i], pixels[i + 1], pixels[i + 2], out var r, out var g, out var b);
                pixels[i] = (byte)(255 - r);
                pixels[i + 1] = (byte)(255 - g);
                pixels[i + 2] = (byte)(255 - b);
            }
        }
    }

    private static void YccToRgb(byte yv, byte cb, byte cr, out byte r, out byte g, out byte b)
    {
        var y = (double)yv;
        var cbv = cb - 128.0;
        var crv = cr - 128.0;
        r = Clamp(y + 1.402 * crv);
        g = Clamp(y - 0.344136 * cbv - 0.714136 * crv);
        b = Clamp(y + 1.772 * cbv);
    }

    private static byte Clamp(double v) => (byte)Math.Clamp((int)Math.Round(v), 0, 255);
}
