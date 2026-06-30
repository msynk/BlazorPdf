namespace BlazorPdf.Engine.Fonts;

/// <summary>Big-endian reader for sfnt/TrueType binary structures.</summary>
internal sealed class ByteReader(byte[] data, int position = 0)
{
    private readonly byte[] _data = data;

    public int Position { get; set; } = position;
    public int Length => _data.Length;

    public byte U8() => _data[Position++];

    public int I8() => (sbyte)_data[Position++];

    public int U16()
    {
        var v = (_data[Position] << 8) | _data[Position + 1];
        Position += 2;
        return v;
    }

    public int I16() => (short)U16();

    public long U32()
    {
        long v = ((long)_data[Position] << 24) | ((long)_data[Position + 1] << 16) |
                 ((long)_data[Position + 2] << 8) | _data[Position + 3];
        Position += 4;
        return v;
    }

    public int I32()
    {
        var v = (_data[Position] << 24) | (_data[Position + 1] << 16) |
                (_data[Position + 2] << 8) | _data[Position + 3];
        Position += 4;
        return v;
    }

    public string Tag()
    {
        var s = System.Text.Encoding.ASCII.GetString(_data, Position, 4);
        Position += 4;
        return s;
    }
}
