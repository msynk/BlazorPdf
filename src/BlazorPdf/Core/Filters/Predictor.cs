// C# implementation of the PNG/TIFF predictors used by FlateDecode /
// LZWDecode, following the PDF specification.

namespace BlazorPdf.Core.Filters;

/// <summary>
/// Reverses the predictor transformation applied before Flate/LZW compression.
/// Predictor 2 is the TIFF horizontal predictor; predictors &gt;= 10 are the
/// PNG row filters (None/Sub/Up/Average/Paeth).
/// </summary>
internal static class Predictor
{
    public static byte[] Apply(byte[] data, int predictor, int colors, int bitsPerComponent, int columns)
    {
        if (predictor <= 1)
        {
            return data;
        }

        int colorsSafe = Math.Max(1, colors);
        int bpc = bitsPerComponent <= 0 ? 8 : bitsPerComponent;
        int cols = Math.Max(1, columns);

        int bytesPerPixel = Math.Max(1, (colorsSafe * bpc + 7) / 8);
        int rowBytes = (cols * colorsSafe * bpc + 7) / 8;

        return predictor == 2
            ? ApplyTiff(data, colorsSafe, bpc, cols, rowBytes)
            : ApplyPng(data, bytesPerPixel, rowBytes);
    }

    private static byte[] ApplyTiff(byte[] data, int colors, int bpc, int columns, int rowBytes)
    {
        // Only the common byte-aligned case (bpc >= 8) is handled here.
        if (bpc < 8 || rowBytes == 0)
        {
            return data;
        }

        int bytesPerSample = bpc / 8;
        int rows = data.Length / rowBytes;
        var output = new byte[data.Length];
        Array.Copy(data, output, data.Length);

        for (int row = 0; row < rows; row++)
        {
            int rowStart = row * rowBytes;
            int samplesPerRow = columns * colors;
            for (int sample = colors; sample < samplesPerRow; sample++)
            {
                int cur = rowStart + sample * bytesPerSample;
                int prev = cur - colors * bytesPerSample;
                if (bytesPerSample == 1)
                {
                    output[cur] = (byte)(output[cur] + output[prev]);
                }
                else // 16-bit samples
                {
                    int value = ((output[cur] << 8) | output[cur + 1])
                              + ((output[prev] << 8) | output[prev + 1]);
                    output[cur] = (byte)(value >> 8);
                    output[cur + 1] = (byte)value;
                }
            }
        }
        return output;
    }

    private static byte[] ApplyPng(byte[] data, int bytesPerPixel, int rowBytes)
    {
        if (rowBytes == 0)
        {
            return data;
        }

        // Each PNG-predicted row is prefixed with a one-byte filter type.
        int stride = rowBytes + 1;
        int rows = data.Length / stride;
        var output = new byte[rows * rowBytes];
        var previous = new byte[rowBytes];

        for (int row = 0; row < rows; row++)
        {
            int inStart = row * stride;
            int filterType = data[inStart];
            int outStart = row * rowBytes;
            var current = new byte[rowBytes];

            for (int i = 0; i < rowBytes; i++)
            {
                int raw = data[inStart + 1 + i];
                int left = i >= bytesPerPixel ? current[i - bytesPerPixel] : 0;
                int up = previous[i];
                int upLeft = i >= bytesPerPixel ? previous[i - bytesPerPixel] : 0;

                int value = filterType switch
                {
                    0 => raw,                                  // None
                    1 => raw + left,                           // Sub
                    2 => raw + up,                             // Up
                    3 => raw + ((left + up) >> 1),             // Average
                    4 => raw + Paeth(left, up, upLeft),        // Paeth
                    _ => raw,
                };
                current[i] = (byte)value;
            }

            Array.Copy(current, 0, output, outStart, rowBytes);
            previous = current;
        }

        return output;
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a);
        int pb = Math.Abs(p - b);
        int pc = Math.Abs(p - c);
        if (pa <= pb && pa <= pc)
        {
            return a;
        }
        return pb <= pc ? b : c;
    }
}
