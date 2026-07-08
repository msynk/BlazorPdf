using System.IO.Compression;
using System.Text;
using BlazorPdf.Core;
using BlazorPdf.Core.Filters;

namespace BlazorPdf.Tests;

/// <summary>
/// Regression tests for the Phase 1 decoder correctness fixes.
/// </summary>
public class Phase1DecoderTests
{
    // 1.4 — TIFF predictor 2 now handles sub-byte (bpc 4) samples. Encoded row of
    // per-sample deltas [1,1,1,1] must decode to running values [1,2,3,4].
    [Fact]
    public void Tiff_predictor_handles_4bit_samples()
    {
        // Two bytes = four 4-bit samples: 0x11, 0x11 (deltas all 1).
        byte[] encoded = { 0x11, 0x11 };
        byte[] decoded = Predictor.Apply(encoded, predictor: 2, colors: 1, bitsPerComponent: 4, columns: 4);

        // Expect samples 1,2,3,4 -> nibbles 0x12, 0x34.
        Assert.Equal(new byte[] { 0x12, 0x34 }, decoded);
    }

    // 1.5 — a Flate stream with a corrupted trailing Adler-32 must still yield the
    // bytes that inflated before the checksum failed, not an empty result.
    [Fact]
    public void Flate_recovers_partial_output_on_bad_adler()
    {
        byte[] raw = Encoding.ASCII.GetBytes("Hello, world! This survives a bad checksum.");
        byte[] compressed;
        using (var ms = new MemoryStream())
        {
            using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            {
                z.Write(raw, 0, raw.Length);
            }
            compressed = ms.ToArray();
        }

        // Corrupt the 4-byte Adler-32 trailer so strict validation would throw.
        compressed[^1] ^= 0xFF;

        var dict = new Dict();
        dict.Set("Filter", Name.Get("FlateDecode"));
        dict.Set("Length", (double)compressed.Length);
        var stream = new PdfStream(compressed, 0, compressed.Length, dict);

        byte[] result = StreamDecoder.Decode(stream);
        Assert.Equal(raw, result);
    }

    // 1.1 — a G4 row coded with a vertical-left (VL2) mode must decode correctly.
    // Before the sentinel fix, VL2 (-2) aliased the Horizontal marker and the
    // whole row decoded as garbage. Bitstream: 000010 (VL2, a1 = b1-2 = 2) then
    // 1 (V0, a1 = b1 = 4) over a 4-pixel all-white reference line.
    [Fact]
    public void Ccitt_g4_decodes_vertical_left_mode()
    {
        var p = new CcittParams { K = -1, Columns = 4, Rows = 1, BlackIs1 = false, EndOfBlock = false };
        byte[] data = { 0b0000_1010 }; // 000010 (VL2) + 1 (V0) + pad

        byte[] result = CcittFaxDecoder.Decode(data, p);

        // Row is white [0,2) then black [2,4). With BlackIs1=false the decoder
        // emits white as 1 bits -> 0b11000000 = 0xC0.
        Assert.Equal(new byte[] { 0xC0 }, result);
    }
}
