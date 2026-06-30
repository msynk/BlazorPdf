using BlazorPdf.Engine.Fonts;

namespace BlazorPdf.Engine.Rendering;

/// <summary>
/// A compact built-in vector font used to draw text when no embedded font program is
/// available (e.g. the standard-14 fonts such as Helvetica). Glyphs are monoline
/// polylines emitted in glyph space (1/1000 em, y up, baseline at 0) so the renderer
/// can place and scale them exactly like real outline glyphs. Advance widths follow
/// the Helvetica metrics, giving proportional, mixed-case text instead of a fixed-pitch
/// placeholder.
/// </summary>
/// <remarks>
/// This is a faithful-enough monoline approximation of the standard typeface; it is not
/// a substitute for an embedded TrueType/CFF/Type1 program when one is present.
/// </remarks>
internal static class StrokeFont
{
    // Design grid used by the raw glyph definitions below: x spans 0..4, y spans the
    // descender..ascender range where y=6 maps to the cap/ascender height.
    private const double DesignWidth = 4.0;
    private const double CapHeight = 700.0;   // em units for design y = 6
    private const double YScale = CapHeight / 6.0;

    private static readonly Dictionary<char, double[][]> Glyphs = Build();

    /// <summary>Polylines for a character in glyph space (1/1000 em, y up), or empty.</summary>
    public static double[][] Get(char c)
    {
        if (c is ' ' or '\t' or '\u00A0') return [];
        if (Glyphs.TryGetValue(c, out var g)) return g;
        return Glyphs.GetValueOrDefault('?', []);
    }

    /// <summary>Advance width of a character in 1/1000 em.</summary>
    public static double Width(char c) => Standard14Metrics.HelveticaWidth(c);

    private static double SideBearing(double advance) => Math.Clamp(advance * 0.10, 30, 80);

    private static Dictionary<char, double[][]> Build()
    {
        // Each entry: polylines separated by ';', points by space, coords "x,y" on the
        // design grid (x: 0..4, y: descender..6). Lowercase letters have their own
        // shapes with an x-height around y=4 and proper ascenders/descenders.
        var raw = new Dictionary<char, string>
        {
            // Uppercase.
            ['A'] = "0,0 2,6 4,0;0.8,2.4 3.2,2.4",
            ['B'] = "0,0 0,6 3,6 4,5 4,4 3,3 0,3 3,3 4,2 4,1 3,0 0,0",
            ['C'] = "4,5 3,6 1,6 0,5 0,1 1,0 3,0 4,1",
            ['D'] = "0,0 0,6 2,6 4,4 4,2 2,0 0,0",
            ['E'] = "4,6 0,6 0,0 4,0;0,3 3,3",
            ['F'] = "4,6 0,6 0,0;0,3 3,3",
            ['G'] = "4,5 3,6 1,6 0,5 0,1 1,0 3,0 4,1 4,3 2,3",
            ['H'] = "0,6 0,0;4,6 4,0;0,3 4,3",
            ['I'] = "2,6 2,0",
            ['J'] = "4,6 4,1 3,0 1,0 0,1",
            ['K'] = "0,6 0,0;4,6 0,3 4,0",
            ['L'] = "0,6 0,0 4,0",
            ['M'] = "0,0 0,6 2,3 4,6 4,0",
            ['N'] = "0,0 0,6 4,0 4,6",
            ['O'] = "1,0 0,1 0,5 1,6 3,6 4,5 4,1 3,0 1,0",
            ['P'] = "0,0 0,6 3,6 4,5 4,4 3,3 0,3",
            ['Q'] = "1,0 0,1 0,5 1,6 3,6 4,5 4,1 3,0 1,0;2,2 4,0",
            ['R'] = "0,0 0,6 3,6 4,5 4,4 3,3 0,3;2,3 4,0",
            ['S'] = "4,5 3,6 1,6 0,5 0,4 1,3 3,3 4,2 4,1 3,0 1,0 0,1",
            ['T'] = "0,6 4,6;2,6 2,0",
            ['U'] = "0,6 0,1 1,0 3,0 4,1 4,6",
            ['V'] = "0,6 2,0 4,6",
            ['W'] = "0,6 1,0 2,3 3,0 4,6",
            ['X'] = "0,6 4,0;0,0 4,6",
            ['Y'] = "0,6 2,3 4,6;2,3 2,0",
            ['Z'] = "0,6 4,6 0,0 4,0",

            // Lowercase (x-height ~4, ascenders to 6, descenders to -1.8).
            ['a'] = "4,4 4,0;4,1 3,0 1,0 0,1 1,2 3,2 4,1.5",
            ['b'] = "0,6 0,0;0,1 1,0 3,0 4,1 4,3 3,4 1,4 0,3",
            ['c'] = "4,3 3,4 1,4 0,3 0,1 1,0 3,0 4,1",
            ['d'] = "4,6 4,0;4,3 3,4 1,4 0,3 0,1 1,0 3,0 4,1",
            ['e'] = "0,2 4,2 4,3 3,4 1,4 0,3 0,1 1,0 3,0 4,1",
            ['f'] = "1,0 1,5 2,6 3,6;0,4 2.4,4",
            ['g'] = "4,4 4,-1 3,-1.8 1,-1.8 0,-1;4,3 3,4 1,4 0,3 0,1 1,0 3,0 4,1",
            ['h'] = "0,6 0,0;0,3 1,4 3,4 4,3 4,0",
            ['i'] = "2,4 2,0;2,5 2,5.4",
            ['j'] = "2.4,4 2.4,-1 1.4,-1.8 0.4,-1.4;2.4,5 2.4,5.4",
            ['k'] = "0,6 0,0;0,1.5 2.6,4;0.8,2.4 4,0",
            ['l'] = "2,6 2,0",
            ['m'] = "0,0 0,4;0,3 1,4 2,3.2 2,0;2,3.2 3,4 4,3 4,0",
            ['n'] = "0,0 0,4;0,3 1,4 3,4 4,3 4,0",
            ['o'] = "1,0 0,1 0,3 1,4 3,4 4,3 4,1 3,0 1,0",
            ['p'] = "0,-1.8 0,4;0,1 1,0 3,0 4,1 4,3 3,4 1,4 0,3",
            ['q'] = "4,-1.8 4,4;4,1 3,0 1,0 0,1 0,3 1,4 3,4 4,3",
            ['r'] = "0,0 0,4;0,3 1,4 3,4",
            ['s'] = "4,3 3,4 1,4 0,3 1,2 3,2 4,1 3,0 1,0 0,1",
            ['t'] = "1,6 1,1 2,0 3,0;0,4 2.4,4",
            ['u'] = "0,4 0,1 1,0 3,0 4,1;4,4 4,0",
            ['v'] = "0,4 2,0 4,4",
            ['w'] = "0,4 1,0 2,3 3,0 4,4",
            ['x'] = "0,4 4,0;0,0 4,4",
            ['y'] = "0,4 2,0;4,4 2,0 1,-1.8 0,-1.4",
            ['z'] = "0,4 4,4 0,0 4,0",

            // Digits.
            ['0'] = "1,0 0,1 0,5 1,6 3,6 4,5 4,1 3,0 1,0;0,1 4,5",
            ['1'] = "1,5 2,6 2,0;0,0 4,0",
            ['2'] = "0,5 1,6 3,6 4,5 4,4 0,0 4,0",
            ['3'] = "0,6 4,6 2,3 4,2 4,1 3,0 1,0 0,1",
            ['4'] = "3,0 3,6 0,2 4,2",
            ['5'] = "4,6 0,6 0,3 3,3 4,2 4,1 3,0 1,0 0,1",
            ['6'] = "4,5 3,6 1,6 0,5 0,1 1,0 3,0 4,1 4,2 3,3 0,3",
            ['7'] = "0,6 4,6 2,0",
            ['8'] = "1,3 0,4 0,5 1,6 3,6 4,5 4,4 3,3 1,3 0,2 0,1 1,0 3,0 4,1 4,2 3,3",
            ['9'] = "0,1 1,0 3,0 4,1 4,5 3,6 1,6 0,5 0,4 1,3 4,3",

            // Punctuation and symbols.
            ['.'] = "1.6,0 2.4,0 2.4,0.8 1.6,0.8 1.6,0",
            [','] = "2.2,0.8 2.2,0 1.4,-1",
            ['-'] = "0.5,3 3.5,3",
            ['_'] = "0,-0.4 4,-0.4",
            [':'] = "2,4.2 2,3.4;2,1.6 2,0.8",
            [';'] = "2,4.2 2,3.4;2.2,1 1.4,-0.6",
            ['/'] = "0,0 4,6",
            ['\\'] = "0,6 4,0",
            ['('] = "3,6 1,3 3,0",
            [')'] = "1,6 3,3 1,0",
            ['['] = "3,6 1,6 1,0 3,0",
            [']'] = "1,6 3,6 3,0 1,0",
            ['{'] = "3,6 2,5 2,3.5 1,3 2,2.5 2,1 3,0",
            ['}'] = "1,6 2,5 2,3.5 3,3 2,2.5 2,1 1,0",
            ['|'] = "2,6 2,-1",
            ['!'] = "2,6 2,2;2,0.8 2,0",
            ['?'] = "0,5 1,6 3,6 4,5 4,4 2,2.6 2,2;2,0.8 2,0",
            ['+'] = "0,3 4,3;2,5 2,1",
            ['='] = "0,4 4,4;0,2 4,2",
            ['<'] = "4,5 0,3 4,1",
            ['>'] = "0,5 4,3 0,1",
            ['~'] = "0,3 1,3.6 3,2.4 4,3",
            ['^'] = "0.5,4 2,6 3.5,4",
            ['`'] = "1.6,6 2.4,5",
            ['*'] = "0,3 4,3;1,5 3,1;3,5 1,1",
            ['#'] = "1,6 1,0;3,6 3,0;0,4 4,4;0,2 4,2",
            ['%'] = "0,0 4,6;0,5 1,5 1,4 0,4 0,5;3,2 4,2 4,1 3,1 3,2",
            ['&'] = "4,0 1,4 1,5 2,6 3,5 0,1 1,0 3,2",
            ['@'] = "3,2 2,2 2,4 3,4 3,2 4,3 4,5 3,6 1,6 0,5 0,1 1,0 3,0",
            ['$'] = "4,5 3,6 1,6 0,5 0,4 1,3 3,3 4,2 4,1 3,0 1,0 0,1;2,6.4 2,-0.4",
            ['"'] = "1.4,6 1.4,4.6;2.6,6 2.6,4.6",
            ['\''] = "2,6 2,4.6",
        };

        var glyphs = new Dictionary<char, double[][]>();
        foreach (var (ch, def) in raw)
        {
            var advance = Standard14Metrics.HelveticaWidth(ch);
            var sb = SideBearing(advance);
            var scaleX = (advance - 2 * sb) / DesignWidth;

            var polylines = new List<double[]>();
            foreach (var part in def.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var pts = part.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var coords = new double[pts.Length * 2];
                for (var i = 0; i < pts.Length; i++)
                {
                    var xy = pts[i].Split(',');
                    var dx = double.Parse(xy[0], System.Globalization.CultureInfo.InvariantCulture);
                    var dy = double.Parse(xy[1], System.Globalization.CultureInfo.InvariantCulture);
                    coords[i * 2] = sb + dx * scaleX;   // glyph-space x (1/1000 em)
                    coords[i * 2 + 1] = dy * YScale;    // glyph-space y (1/1000 em)
                }
                polylines.Add(coords);
            }
            glyphs[ch] = [.. polylines];
        }

        return glyphs;
    }
}
