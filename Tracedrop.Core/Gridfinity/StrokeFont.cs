using System.Globalization;
using Tracedrop.Core.Vector;

namespace Tracedrop.Core.Gridfinity;

/// <summary>
/// A plain single-stroke engraving font (capitals, digits and common signs), drawn on a 4 × 6 grid. Strokes are
/// thickened into regions for engraving, so labels print cleanly and import into CAD as simple geometry.
/// </summary>
public static class StrokeFont
{
    const double CapHeight = 6;
    const double Advance = 5.6;
    const double SpaceAdvance = 4;

    static readonly Dictionary<char, string> Glyphs = new()
    {
        ['A'] = "0,0 2,6 4,0|0.7,2 3.3,2",
        ['B'] = "0,3 3,3 4,4 4,5 3,6 0,6 0,0 3,0 4,1 4,2 3,3",
        ['C'] = "4,5 3,6 1,6 0,5 0,1 1,0 3,0 4,1",
        ['D'] = "0,0 0,6 2.5,6 4,4.5 4,1.5 2.5,0 0,0",
        ['E'] = "4,6 0,6 0,0 4,0|0,3 3,3",
        ['F'] = "4,6 0,6 0,0|0,3 3,3",
        ['G'] = "4,5 3,6 1,6 0,5 0,1 1,0 3,0 4,1 4,3 2,3",
        ['H'] = "0,0 0,6|4,0 4,6|0,3 4,3",
        ['I'] = "1,6 3,6|2,6 2,0|1,0 3,0",
        ['J'] = "4,6 4,1 3,0 1,0 0,1",
        ['K'] = "0,0 0,6|4,6 0,2|1.4,3.4 4,0",
        ['L'] = "0,6 0,0 4,0",
        ['M'] = "0,0 0,6 2,3 4,6 4,0",
        ['N'] = "0,0 0,6 4,0 4,6",
        ['O'] = "1,0 0,1 0,5 1,6 3,6 4,5 4,1 3,0 1,0",
        ['P'] = "0,0 0,6 3,6 4,5 4,4 3,3 0,3",
        ['Q'] = "1,0 0,1 0,5 1,6 3,6 4,5 4,1 3,0 1,0|2.5,1.5 4,0",
        ['R'] = "0,0 0,6 3,6 4,5 4,4 3,3 0,3|2,3 4,0",
        ['S'] = "4,5 3,6 1,6 0,5 0,4 1,3 3,3 4,2 4,1 3,0 1,0 0,1",
        ['T'] = "0,6 4,6|2,6 2,0",
        ['U'] = "0,6 0,1 1,0 3,0 4,1 4,6",
        ['V'] = "0,6 2,0 4,6",
        ['W'] = "0,6 1,0 2,4 3,0 4,6",
        ['X'] = "0,0 4,6|0,6 4,0",
        ['Y'] = "0,6 2,3 4,6|2,3 2,0",
        ['Z'] = "0,6 4,6 0,0 4,0",
        ['0'] = "1,0 0,1 0,5 1,6 3,6 4,5 4,1 3,0 1,0|0.6,1.2 3.4,4.8",
        ['1'] = "1,5 2,6 2,0|1,0 3,0",
        ['2'] = "0,5 1,6 3,6 4,5 4,4 0,0 4,0",
        ['3'] = "0,5 1,6 3,6 4,5 4,4 3,3 4,2 4,1 3,0 1,0 0,1|1.5,3 3,3",
        ['4'] = "3,0 3,6 0,2 4,2",
        ['5'] = "4,6 0,6 0,3.5 3,3.5 4,2.5 4,1 3,0 1,0 0,1",
        ['6'] = "4,5 3,6 1,6 0,5 0,1 1,0 3,0 4,1 4,2.5 3,3.5 1,3.5 0,2.5",
        ['7'] = "0,6 4,6 1.5,0",
        ['8'] = "1,3 0,4 0,5 1,6 3,6 4,5 4,4 3,3 1,3 0,2 0,1 1,0 3,0 4,1 4,2 3,3",
        ['9'] = "0,1 1,0 3,0 4,1 4,5 3,6 1,6 0,5 0,3.5 1,2.5 3,2.5 4,3.5",
        ['-'] = "1,3 3,3",
        ['+'] = "0.5,3 3.5,3|2,1.5 2,4.5",
        ['='] = "0.5,2 3.5,2|0.5,4 3.5,4",
        ['.'] = "2,0 2,0.3",
        [','] = "2,0.4 1.4,-1",
        [':'] = "2,1 2,1.3|2,4 2,4.3",
        ['/'] = "0.5,0 3.5,6",
        ['\\'] = "0.5,6 3.5,0",
        ['('] = "2.8,6 1.6,4.5 1.6,1.5 2.8,0",
        [')'] = "1.2,6 2.4,4.5 2.4,1.5 1.2,0",
        ['#'] = "1,0 1.5,6|2.5,0 3,6|0,2 4,2|0,4 4,4",
        ['\''] = "2,6 2,4.8",
        ['"'] = "1.4,6 1.4,4.8|2.6,6 2.6,4.8",
        ['_'] = "0,-0.6 4,-0.6",
        ['°'] = "1.4,6 2.6,6 2.6,4.8 1.4,4.8 1.4,6",
        ['%'] = "0,0 4,6|0.6,5.4 0.6,4.8|3.4,1.2 3.4,0.6",
        ['?'] = "0,5 1,6 3,6 4,5 4,4 2,3 2,1.8|2,0 2,0.3",
        ['!'] = "2,6 2,1.8|2,0 2,0.3",
        ['*'] = "2,1.5 2,4.5|0.7,2.2 3.3,3.8|0.7,3.8 3.3,2.2",
        ['&'] = "4,0 1,4.5 1,5.5 1.5,6 2.5,6 3,5.5 3,4.5 0,2 0,1 1,0 2,0 4,2.5"
    };

    static readonly Dictionary<char, List<List<Vec2>>> Parsed = Glyphs.ToDictionary(kv => kv.Key, kv => Parse(kv.Value));

    static List<List<Vec2>> Parse(string glyph) =>
        glyph.Split('|').Select(stroke => stroke.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(pt =>
        {
            var xy = pt.Split(',');
            return new Vec2(double.Parse(xy[0], CultureInfo.InvariantCulture), double.Parse(xy[1], CultureInfo.InvariantCulture));
        }).ToList()).ToList();

    /// <summary>Characters the font can draw (lowercase letters are drawn as capitals).</summary>
    public static bool CanDraw(char c) => c == ' ' || Parsed.ContainsKey(char.ToUpperInvariant(c));

    /// <summary>Width of the text in mm at this cap height.</summary>
    public static double Width(string text, double height)
    {
        var unit = height / CapHeight;
        var advance = text.Sum(c => c == ' ' ? SpaceAdvance : Advance);
        return Math.Max(0, advance - (Advance - 4)) * unit;
    }

    /// <summary>
    /// The text's strokes as polylines in mm, centred on <paramref name="centre"/> and turned by
    /// <paramref name="rotation"/> degrees (counter-clockwise, y up). Unknown characters are skipped.
    /// </summary>
    public static List<List<Vec2>> Layout(string text, double height, Vec2 centre, double rotation = 0)
    {
        var unit = height / CapHeight;
        var total = Width(text, height);
        var a = rotation * Math.PI / 180;
        var (c, s) = (Math.Cos(a), Math.Sin(a));
        Vec2 Place(Vec2 p)
        {
            var x = p.X - total / 2;
            var y = p.Y - height / 2;
            return new Vec2(centre.X + x * c - y * s, centre.Y + x * s + y * c);
        }

        var strokes = new List<List<Vec2>>();
        var cursor = 0.0;
        foreach (var ch in text)
        {
            if (ch == ' ') { cursor += SpaceAdvance * unit; continue; }
            if (!Parsed.TryGetValue(char.ToUpperInvariant(ch), out var glyph)) { cursor += SpaceAdvance * unit; continue; }
            foreach (var stroke in glyph)
                strokes.Add(stroke.Select(p => Place(new Vec2(cursor + p.X * unit, p.Y * unit))).ToList());
            cursor += Advance * unit;
        }
        return strokes;
    }
}
