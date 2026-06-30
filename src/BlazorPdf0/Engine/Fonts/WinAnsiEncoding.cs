namespace BlazorPdf.Engine.Fonts;

/// <summary>Maps single-byte WinAnsi (CP1252) codes to Unicode code points.</summary>
internal static class WinAnsiEncoding
{
    private static readonly int[] Map = Build();

    public static int ToUnicode(int code) => code is >= 0 and < 256 ? Map[code] : code;

    private static int[] Build()
    {
        var map = new int[256];
        for (var i = 0; i < 256; i++) map[i] = i; // Latin-1 default

        // CP1252 differences in 0x80-0x9F.
        var hi = new Dictionary<int, int>
        {
            [0x80] = 0x20AC, [0x82] = 0x201A, [0x83] = 0x0192, [0x84] = 0x201E,
            [0x85] = 0x2026, [0x86] = 0x2020, [0x87] = 0x2021, [0x88] = 0x02C6,
            [0x89] = 0x2030, [0x8A] = 0x0160, [0x8B] = 0x2039, [0x8C] = 0x0152,
            [0x8E] = 0x017D, [0x91] = 0x2018, [0x92] = 0x2019, [0x93] = 0x201C,
            [0x94] = 0x201D, [0x95] = 0x2022, [0x96] = 0x2013, [0x97] = 0x2014,
            [0x98] = 0x02DC, [0x99] = 0x2122, [0x9A] = 0x0161, [0x9B] = 0x203A,
            [0x9C] = 0x0153, [0x9E] = 0x017E, [0x9F] = 0x0178,
        };
        foreach (var (k, v) in hi) map[k] = v;
        return map;
    }
}
