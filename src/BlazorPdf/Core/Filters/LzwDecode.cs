// Clean-room C# implementation of the LZWDecode filter, following the variable
// width LZW scheme described in the PDF specification and pdf.js
// `src/core/lzw_stream.js`. See NOTICE.

namespace BlazorPdf.Core.Filters;

/// <summary>
/// Decodes PDF <c>LZWDecode</c> data: variable-width LZW (9–12 bits) with the
/// <c>EarlyChange</c> convention.
/// </summary>
internal static class LzwDecode
{
    private const int ClearCode = 256;
    private const int EodCode = 257;

    public static byte[] Decode(byte[] data, int earlyChange)
    {
        var output = new List<byte>(data.Length * 3);

        // Dictionary of byte sequences; first 256 entries are the literals.
        var table = new List<byte[]>(4096);
        void ResetTable()
        {
            table.Clear();
            for (int i = 0; i < 256; i++)
            {
                table.Add([(byte)i]);
            }
            table.Add([]); // 256 clear
            table.Add([]); // 257 eod
        }
        ResetTable();

        int codeWidth = 9;
        int bitBuffer = 0;
        int bitCount = 0;
        int pos = 0;
        byte[]? previous = null;

        int NextCode()
        {
            while (bitCount < codeWidth)
            {
                if (pos >= data.Length)
                {
                    return -1;
                }
                bitBuffer = (bitBuffer << 8) | data[pos++];
                bitCount += 8;
            }
            bitCount -= codeWidth;
            return (bitBuffer >> bitCount) & ((1 << codeWidth) - 1);
        }

        while (true)
        {
            int code = NextCode();
            if (code < 0 || code == EodCode)
            {
                break;
            }
            if (code == ClearCode)
            {
                ResetTable();
                codeWidth = 9;
                previous = null;
                continue;
            }

            byte[] entry;
            if (code < table.Count)
            {
                entry = table[code];
            }
            else if (previous is not null)
            {
                // Special case: code not yet in table (KwKwK).
                entry = new byte[previous.Length + 1];
                Array.Copy(previous, entry, previous.Length);
                entry[^1] = previous[0];
            }
            else
            {
                break; // malformed
            }

            output.AddRange(entry);

            if (previous is not null)
            {
                var newEntry = new byte[previous.Length + 1];
                Array.Copy(previous, newEntry, previous.Length);
                newEntry[^1] = entry[0];
                table.Add(newEntry);
            }

            previous = entry;

            // Grow the code width as the table fills (accounting for EarlyChange).
            int limit = (1 << codeWidth) - (earlyChange != 0 ? 1 : 0);
            if (table.Count >= limit && codeWidth < 12)
            {
                codeWidth++;
            }
        }

        return output.ToArray();
    }
}
