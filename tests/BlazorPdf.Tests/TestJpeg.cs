namespace BlazorPdf.Tests;

/// <summary>
/// Builds a minimal baseline JPEG of a solid color (single 8x8 MCU, 3 components,
/// quantization = 1, tiny custom Huffman tables) so the engine's JPEG decoder can be
/// tested deterministically without shipping a sample JPEG.
/// </summary>
public static class TestJpeg
{
    public static byte[] BuildSolid(byte r, byte g, byte b) => BuildSolid(8, 8, r, g, b);

    public static byte[] BuildSolid(int width, int height, byte r, byte g, byte b)
    {
        // RGB -> YCbCr (JFIF).
        var y = (int)Math.Round(0.299 * r + 0.587 * g + 0.114 * b);
        var cb = (int)Math.Round(128 - 0.168736 * r - 0.331264 * g + 0.5 * b);
        var cr = (int)Math.Round(128 + 0.5 * r - 0.418688 * g - 0.081312 * b);
        y = Math.Clamp(y, 0, 255);
        cb = Math.Clamp(cb, 0, 255);
        cr = Math.Clamp(cr, 0, 255);

        var bytes = new List<byte>();

        // SOI
        Marker(bytes, 0xD8);

        // DQT: one table, id 0, all 1s.
        Marker(bytes, 0xDB);
        U16(bytes, 2 + 1 + 64);
        bytes.Add(0x00); // precision 0, id 0
        for (var i = 0; i < 64; i++) bytes.Add(1);

        // SOF0: 8-bit, WxH, 3 components 1x1 using quant table 0.
        Marker(bytes, 0xC0);
        U16(bytes, 2 + 1 + 2 + 2 + 1 + 3 * 3);
        bytes.Add(8);
        U16(bytes, height);
        U16(bytes, width);
        bytes.Add(3);
        for (var id = 1; id <= 3; id++) { bytes.Add((byte)id); bytes.Add(0x11); bytes.Add(0); }

        // DHT: DC table (12 symbols, length 4) and AC table (1 symbol, length 1).
        WriteDht(bytes);

        // SOS: 3 components, DC/AC table 0.
        Marker(bytes, 0xDA);
        U16(bytes, 2 + 1 + 3 * 2 + 3);
        bytes.Add(3);
        for (var id = 1; id <= 3; id++) { bytes.Add((byte)id); bytes.Add(0x00); }
        bytes.Add(0); bytes.Add(63); bytes.Add(0);

        // Entropy-coded data: one block per component per MCU. DC is differential, so
        // only the first block of each component carries the value; the rest are 0.
        var bw = new BitWriter(bytes);
        var mcus = (width / 8) * (height / 8);
        var samples = new[] { y, cb, cr };
        for (var mcu = 0; mcu < mcus; mcu++)
        {
            for (var ci = 0; ci < 3; ci++)
            {
                EncodeSolidBlock(bw, mcu == 0 ? samples[ci] : -1);
            }
        }
        bw.Flush();

        // EOI
        Marker(bytes, 0xD9);
        return [.. bytes];
    }

    private static void EncodeSolidBlock(BitWriter bw, int sample)
    {
        // sample < 0 signals a repeated block (DC diff = 0, category 0).
        var dc = sample < 0 ? 0 : (int)Math.Round(8.0 * (sample - 128));
        var s = BitLength(Math.Abs(dc));

        // DC Huffman code for category s: canonical 4-bit code == s.
        bw.Write(s, 4);
        if (s > 0)
        {
            var v = dc >= 0 ? dc : dc + (1 << s) - 1;
            bw.Write(v, s);
        }

        // AC EOB symbol (0x00): canonical 1-bit code == 0.
        bw.Write(0, 1);
    }

    private static void WriteDht(List<byte> bytes)
    {
        Marker(bytes, 0xC4);

        // DC class 0, table 0: counts (12 codes of length 4), values 0..11.
        var dcCounts = new byte[16];
        dcCounts[3] = 12; // length index 4 (0-based 3)
        var dcValues = new byte[12];
        for (var i = 0; i < 12; i++) dcValues[i] = (byte)i;

        // AC class 1, table 0: 1 code of length 1, value 0x00.
        var acCounts = new byte[16];
        acCounts[0] = 1; // length 1
        var acValues = new byte[] { 0x00 };

        var len = 2 + (1 + 16 + dcValues.Length) + (1 + 16 + acValues.Length);
        U16(bytes, len);

        bytes.Add(0x00); // class 0, id 0
        bytes.AddRange(dcCounts);
        bytes.AddRange(dcValues);

        bytes.Add(0x10); // class 1, id 0
        bytes.AddRange(acCounts);
        bytes.AddRange(acValues);
    }

    private static int BitLength(int v)
    {
        var n = 0;
        while (v > 0) { n++; v >>= 1; }
        return n;
    }

    private static void Marker(List<byte> bytes, byte m) { bytes.Add(0xFF); bytes.Add(m); }

    private static void U16(List<byte> bytes, int v) { bytes.Add((byte)(v >> 8)); bytes.Add((byte)(v & 0xFF)); }

    private sealed class BitWriter(List<byte> output)
    {
        private int _acc;
        private int _count;

        public void Write(int value, int bits)
        {
            for (var i = bits - 1; i >= 0; i--)
            {
                _acc = (_acc << 1) | ((value >> i) & 1);
                if (++_count == 8) Emit();
            }
        }

        public void Flush()
        {
            if (_count > 0)
            {
                _acc = (_acc << (8 - _count)) | ((1 << (8 - _count)) - 1); // pad with 1s
                _count = 8;
                Emit();
            }
        }

        private void Emit()
        {
            var b = (byte)(_acc & 0xFF);
            output.Add(b);
            if (b == 0xFF) output.Add(0x00); // byte stuffing
            _acc = 0;
            _count = 0;
        }
    }
}
