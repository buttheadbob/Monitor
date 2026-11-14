using System.Globalization;

namespace SenX.Monitor.Services;

/// <summary>
/// Letters drawn as lines, written for Monitor, so a picture has text without a font file to ship or a font
/// library to depend on. Each letter is on a grid where capitals stand 12 tall on a baseline at 12, and lowercase
/// letters reach up to 4. A stroke starting with ~ is rounded off, and | starts a new stroke. Anything outside
/// plain ASCII is drawn as a question mark, so a name in another alphabet shows as ??? and not as a gap.
/// </summary>
internal static class StrokeFont
{
    private const double GridHeight = 12d;
    private const double LetterGap = 2.2d;
    private const int RoundingPasses = 2;

    private static readonly Dictionary<char, Glyph> s_glyphs = Parse(new Dictionary<char, (int Width, string Strokes)>
    {
        [' '] = (5, string.Empty),
        ['!'] = (1, "1,0 1,8|1,11 1,12"),
        ['"'] = (4, "0,0 0,3|4,0 4,3"),
        ['#'] = (8, "3,1 1,11|7,1 5,11|0,4 8,4|0,8 8,8"),
        ['%'] = (9, "9,0 0,12|~2,0 4,1 4,4 2,5 0,4 0,1 2,0|~7,7 9,8 9,11 7,12 5,11 5,8 7,7"),
        ['&'] = (9, "~9,12 2,4 2,1 4,0 6,1 6,3 0,8 0,11 3,12 6,12 9,7"),
        ['\''] = (1, "1,0 1,3"),
        ['('] = (3, "~3,0 0,4 0,8 3,12"),
        [')'] = (3, "~0,0 3,4 3,8 0,12"),
        ['*'] = (6, "3,1 3,7|0,2 6,6|6,2 0,6"),
        ['+'] = (8, "4,3 4,11|0,7 8,7"),
        [','] = (2, "2,11 0,14"),
        ['-'] = (6, "0,7 6,7"),
        ['.'] = (1, "1,11 1,12"),
        ['/'] = (6, "6,0 0,12"),
        [':'] = (1, "1,4 1,5|1,11 1,12"),
        [';'] = (2, "2,4 2,5|2,11 0,14"),
        ['<'] = (7, "7,2 0,7 7,12"),
        ['='] = (8, "0,5 8,5|0,9 8,9"),
        ['>'] = (7, "0,2 7,7 0,12"),
        ['?'] = (7, "~0,3 1,0 6,0 7,3 3,7 3,9|3,11 3,12"),
        ['@'] = (11, "~8,8 8,4 4,4 3,8 6,9 8,8 11,8 11,3 7,0 2,1 0,6 2,11 7,12 10,11"),
        ['['] = (3, "3,0 0,0 0,12 3,12"),
        ['\\'] = (6, "0,0 6,12"),
        [']'] = (3, "0,0 3,0 3,12 0,12"),
        ['^'] = (6, "0,4 3,0 6,4"),
        ['_'] = (8, "0,13 8,13"),
        ['`'] = (2, "0,0 2,2"),
        ['{'] = (4, "~4,0 2,1 2,5 0,6 2,7 2,11 4,12"),
        ['|'] = (1, "1,0 1,14"),
        ['}'] = (4, "~0,0 2,1 2,5 4,6 2,7 2,11 0,12"),
        ['~'] = (8, "~0,8 2,6 6,8 8,6"),

        ['0'] = (8, "~4,0 8,0 8,12 0,12 0,0 4,0"),
        ['1'] = (5, "0,3 4,0 4,12"),
        ['2'] = (8, "~0,3 1,0 7,0 8,4 4,8 0,12|0,12 8,12"),
        ['3'] = (8, "~0,1 7,0 8,5 3,6|~3,6 8,6 8,12 0,12 0,10"),
        ['4'] = (8, "6,12 6,0 0,8 8,8"),
        ['5'] = (8, "8,0 1,0 0,5|~0,5 4,4 8,5 8,11 4,12 0,11"),
        ['6'] = (8, "~7,0 1,1 0,8 1,12 7,12 8,6 2,4 0,8"),
        ['7'] = (8, "0,0 8,0 3,12"),
        ['8'] = (8, "~4,6 8,5 8,0 0,0 0,5 4,6 8,7 8,12 0,12 0,7 4,6"),
        ['9'] = (8, "~1,12 7,11 8,4 7,0 1,0 0,6 6,8 8,4"),

        ['A'] = (9, "0,12 4,0 5,0 9,12|2,8 7,8"),
        ['B'] = (8, "0,0 0,12|~0,0 8,0 8,6 0,6|~0,6 8,6 8,12 0,12"),
        ['C'] = (9, "~9,2 7,0 0,0 0,12 7,12 9,10"),
        ['D'] = (9, "0,0 0,12|~0,0 9,0 9,12 0,12"),
        ['E'] = (7, "7,0 0,0 0,12 7,12|0,6 6,6"),
        ['F'] = (7, "7,0 0,0 0,12|0,6 6,6"),
        ['G'] = (9, "~9,2 7,0 0,0 0,12 7,12 9,11|9,11 9,7 5,7"),
        ['H'] = (8, "0,0 0,12|8,0 8,12|0,6 8,6"),
        ['I'] = (1, "1,0 1,12"),
        ['J'] = (6, "6,0 6,8|~6,8 6,12 0,12 0,9"),
        ['K'] = (8, "0,0 0,12|8,0 0,7|3,5 8,12"),
        ['L'] = (7, "0,0 0,12 7,12"),
        ['M'] = (11, "0,12 0,0 5,9 6,9 11,0 11,12"),
        ['N'] = (9, "0,12 0,0 9,12 9,0"),
        ['O'] = (10, "~5,0 10,0 10,12 0,12 0,0 5,0"),
        ['P'] = (8, "0,12 0,0|~0,0 8,0 8,7 0,7"),
        ['Q'] = (10, "~5,0 10,0 10,12 0,12 0,0 5,0|6,8 10,13"),
        ['R'] = (8, "0,12 0,0|~0,0 8,0 8,6 0,6|4,6 8,12"),
        ['S'] = (8, "~8,2 6,0 0,0 0,6 8,6 8,12 2,12 0,10"),
        ['T'] = (8, "0,0 8,0|4,0 4,12"),
        ['U'] = (8, "0,0 0,8|~0,8 0,12 8,12 8,8|8,8 8,0"),
        ['V'] = (9, "0,0 4,12 5,12 9,0"),
        ['W'] = (13, "0,0 3,12 4,12 6,2 7,2 9,12 10,12 13,0"),
        ['X'] = (8, "0,0 8,12|8,0 0,12"),
        ['Y'] = (8, "0,0 4,6 8,0|4,6 4,12"),
        ['Z'] = (8, "0,0 8,0 0,12 8,12"),

        ['a'] = (7, "~7,5 5,4 0,4 0,12 5,12 7,10|7,4 7,12"),
        ['b'] = (7, "0,0 0,12|~0,6 2,4 7,4 7,12 2,12 0,10"),
        ['c'] = (6, "~6,5 4,4 0,4 0,12 4,12 6,11"),
        ['d'] = (7, "7,0 7,12|~7,6 5,4 0,4 0,12 5,12 7,10"),
        ['e'] = (7, "0,8 7,8|~7,8 7,4 0,4 0,12 5,12 7,11"),
        ['f'] = (4, "~4,0 1,0 1,3|1,3 1,12|0,5 4,5"),
        ['g'] = (7, "~7,6 5,4 0,4 0,12 5,12 7,10|7,4 7,12|~7,12 7,16 1,16 0,14"),
        ['h'] = (7, "0,0 0,12|~0,6 2,4 7,4 7,8|7,8 7,12"),
        ['i'] = (1, "1,1 1,2|1,4 1,12"),
        ['j'] = (3, "3,1 3,2|3,4 3,13|~3,13 3,16 0,16"),
        ['k'] = (7, "0,0 0,12|6,4 0,9|2,7 7,12"),
        ['l'] = (1, "1,0 1,12"),
        ['m'] = (11, "0,4 0,12|~0,6 1,4 5,4 6,6|6,6 6,12|~6,6 7,4 11,4 11,8|11,8 11,12"),
        ['n'] = (7, "0,4 0,12|~0,6 2,4 7,4 7,8|7,8 7,12"),
        ['o'] = (8, "~4,4 8,4 8,12 0,12 0,4 4,4"),
        ['p'] = (7, "0,4 0,16|~0,6 2,4 7,4 7,12 2,12 0,10"),
        ['q'] = (7, "7,4 7,16|~7,6 5,4 0,4 0,12 5,12 7,10"),
        ['r'] = (4, "0,4 0,12|~0,6 1,4 4,4"),
        ['s'] = (6, "~6,5 4,4 0,4 0,8 6,8 6,12 2,12 0,11"),
        ['t'] = (4, "1,1 1,9|~1,9 1,12 4,12|0,5 4,5"),
        ['u'] = (7, "0,4 0,8|~0,8 0,12 5,12 7,10|7,4 7,12"),
        ['v'] = (7, "0,4 3,12 4,12 7,4"),
        ['w'] = (11, "0,4 2,12 3,12 5,5 6,5 8,12 9,12 11,4"),
        ['x'] = (6, "0,4 6,12|6,4 0,12"),
        ['y'] = (7, "0,4 3,12|7,4 3,12 1,16"),
        ['z'] = (6, "0,4 6,4 0,12 6,12"),
    });

    /// <summary>How wide this text is at this capital height, in pixels.</summary>
    public static double Measure(string text, double size)
    {
        double width = 0d;

        foreach (char letter in text)
            width += GlyphOf(letter).Width + LetterGap;

        return (width - (text.Length > 0 ? LetterGap : 0d)) * size / GridHeight;
    }

    /// <summary>Draws text with its left end at x and its baseline at y.</summary>
    public static void Draw(ImageCanvas canvas, string text, double x, double y, double size, int color, double thickness = 1.05d)
    {
        double scale = size / GridHeight;
        var strokes = new List<IReadOnlyList<(double X, double Y)>>();
        double at = 0d;

        foreach (char letter in text)
        {
            Glyph glyph = GlyphOf(letter);

            foreach ((double X, double Y)[] stroke in glyph.Strokes)
            {
                var placed = new (double X, double Y)[stroke.Length];

                for (int point = 0; point < stroke.Length; point++)
                    placed[point] = (x + ((at + stroke[point].X) * scale), y + ((stroke[point].Y - GridHeight) * scale));

                strokes.Add(placed);
            }

            at += glyph.Width + LetterGap;
        }

        canvas.StrokeAll(strokes, thickness, color);
    }

    /// <summary>Cuts text down to fit, ending it with dots.</summary>
    public static string Fit(string text, double size, double width)
    {
        if (Measure(text, size) <= width)
            return text;

        const string Ending = "..";

        for (int length = text.Length - 1; length > 0; length--)
        {
            string shorter = text.Substring(0, length) + Ending;

            if (Measure(shorter, size) <= width)
                return shorter;
        }

        return string.Empty;
    }

    private static Glyph GlyphOf(char letter) => s_glyphs.TryGetValue(letter, out Glyph? glyph) ? glyph : s_glyphs['?'];

    private sealed record Glyph(double Width, (double X, double Y)[][] Strokes);

    private static Dictionary<char, Glyph> Parse(Dictionary<char, (int Width, string Strokes)> table)
    {
        var glyphs = new Dictionary<char, Glyph>(table.Count);

        foreach (KeyValuePair<char, (int Width, string Strokes)> entry in table)
        {
            (double X, double Y)[][] strokes = entry.Value.Strokes.Length == 0
                ? []
                : entry.Value.Strokes.Split('|').Select(ParseStroke).ToArray();

            glyphs[entry.Key] = new Glyph(entry.Value.Width, strokes);
        }

        return glyphs;
    }

    private static (double X, double Y)[] ParseStroke(string text)
    {
        bool rounded = text[0] == '~';

        (double X, double Y)[] points = text.TrimStart('~')
            .Split(' ')
            .Select(pair =>
            {
                string[] parts = pair.Split(',');
                return (double.Parse(parts[0], CultureInfo.InvariantCulture), double.Parse(parts[1], CultureInfo.InvariantCulture));
            })
            .ToArray();

        return rounded ? Rounded(points) : points;
    }

    // Cutting corners twice turns a few points into a smooth curve, and the ends stay where they were.
    private static (double X, double Y)[] Rounded((double X, double Y)[] points)
    {
        for (int pass = 0; pass < RoundingPasses; pass++)
        {
            var next = new List<(double X, double Y)>((points.Length * 2) + 2) { points[0] };

            for (int i = 0; i < points.Length - 1; i++)
            {
                (double X, double Y) from = points[i];
                (double X, double Y) to = points[i + 1];

                next.Add((from.X + ((to.X - from.X) * 0.25d), from.Y + ((to.Y - from.Y) * 0.25d)));
                next.Add((from.X + ((to.X - from.X) * 0.75d), from.Y + ((to.Y - from.Y) * 0.75d)));
            }

            next.Add(points[points.Length - 1]);
            points = [.. next];
        }

        return points;
    }
}
