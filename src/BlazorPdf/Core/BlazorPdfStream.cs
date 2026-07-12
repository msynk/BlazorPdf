// An in-memory PDF stream.

namespace BlazorPdf;

/// <summary>
/// A <see cref="BlazorPdfBaseStream"/> backed by an in-memory byte buffer. A PDF stream
/// object is represented by one of
/// these with its <see cref="BlazorPdfBaseStream.Dict"/> set.
/// </summary>
public sealed class BlazorPdfStream : BlazorPdfBaseStream
{
    private readonly byte[] _bytes;

    /// <summary>The underlying buffer. Reads are bounded by Start/End, not the buffer length.</summary>
    public byte[] Buffer => _bytes;

    public BlazorPdfStream(byte[] bytes, int start = 0, int? length = null, BlazorPdfDict? dict = null)
    {
        _bytes = bytes ?? throw new ArgumentNullException(nameof(bytes));
        Start = start;
        Pos = start;
        End = length.HasValue ? start + length.Value : bytes.Length;
        Dict = dict;
    }

    public override int GetByte() => Pos >= End ? -1 : _bytes[Pos++];

    public override byte[] GetBytes(int length = 0)
    {
        int pos = Pos;
        int strEnd = End;

        int end;
        if (length <= 0)
        {
            end = strEnd;
        }
        else
        {
            end = pos + length;
            if (end > strEnd)
            {
                end = strEnd;
            }
        }

        if (end <= pos)
        {
            Pos = pos;
            return [];
        }

        Pos = end;
        return _bytes[pos..end];
    }

    public override byte[] GetByteRange(int begin, int end)
    {
        if (begin < 0)
        {
            begin = 0;
        }
        if (end > _bytes.Length)
        {
            end = _bytes.Length;
        }
        return end <= begin ? [] : _bytes[begin..end];
    }

    public override void Reset() => Pos = Start;

    public override void MoveStart() => Start = Pos;

    public override BlazorPdfBaseStream MakeSubStream(int start, int length, BlazorPdfDict? dict = null)
        => new BlazorPdfStream(_bytes, start, length, dict);
}
