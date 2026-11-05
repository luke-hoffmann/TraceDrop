namespace Tracedrop.Core.Vector;

/// <summary>
/// Sub-pixel iso-contours of a float field (marching squares). Every loop is closed and oriented so the inside
/// (values above the level) is always on the same side, which tells outer outlines from holes by signed area.
/// </summary>
public static class MarchingSquares
{
    /// <summary>
    /// Traces all loops where <paramref name="field"/> crosses <paramref name="level"/>.
    /// Coordinates are in pixels with pixel centres at +0.5 (so the picture spans 0..width).
    /// Outer outlines come out with positive <see cref="Geometry2D.SignedArea"/>, holes negative.
    /// </summary>
    public static List<List<Vec2>> Trace(float[] field, int width, int height, float level = 0.5f)
    {
        // Pad with a ring of "outside" so every loop closes inside the grid.
        var w = width + 2;
        var h = height + 2;
        var f = new float[w * h];
        for (var y = 0; y < height; y++)
            Array.Copy(field, y * width, f, (y + 1) * w + 1, width);

        // Edge ids: horizontal edge (x,y)-(x+1,y) = 2*(y*w+x); vertical edge (x,y)-(x,y+1) = 2*(y*w+x)+1.
        var next = new Dictionary<int, int>();
        var points = new Dictionary<int, Vec2>();

        Vec2 Crossing(int edge)
        {
            if (points.TryGetValue(edge, out var p)) return p;
            var cell = edge >> 1;
            var x = cell % w;
            var y = cell / w;
            float v0 = f[cell], v1;
            double px, py;
            if ((edge & 1) == 0)
            {
                v1 = f[cell + 1];
                var t = Math.Clamp((level - v0) / (double)(v1 - v0), 0, 1);
                px = x + t; py = y;
            }
            else
            {
                v1 = f[cell + w];
                var t = Math.Clamp((level - v0) / (double)(v1 - v0), 0, 1);
                px = x; py = y + t;
            }
            // Undo the padding (-1) and move to pixel-centre coordinates (+0.5).
            p = new Vec2(px - 0.5, py - 0.5);
            points[edge] = p;
            return p;
        }

        for (var y = 0; y < h - 1; y++)
        {
            for (var x = 0; x < w - 1; x++)
            {
                var i = y * w + x;
                var tl = f[i] > level;
                var tr = f[i + 1] > level;
                var br = f[i + w + 1] > level;
                var bl = f[i + w] > level;
                var index = (tl ? 1 : 0) | (tr ? 2 : 0) | (br ? 4 : 0) | (bl ? 8 : 0);
                if (index == 0 || index == 15) continue;

                var top = 2 * i;
                var bottom = 2 * (i + w);
                var left = 2 * i + 1;
                var right = 2 * (i + 1) + 1;
                var cTl = new Vec2(x - 0.5, y - 0.5);
                var cTr = new Vec2(x + 0.5, y - 0.5);
                var cBr = new Vec2(x + 0.5, y + 0.5);
                var cBl = new Vec2(x - 0.5, y + 0.5);

                void Add(int a, int b, Vec2 refCorner, bool refInside)
                {
                    var pa = Crossing(a);
                    var pb = Crossing(b);
                    var s = Vec2.Cross(pb - pa, refCorner - pa);
                    // Inside must lie on the positive-cross side of every directed segment.
                    if ((s > 0) != refInside) (a, b) = (b, a);
                    next[a] = b;
                }

                switch (index)
                {
                    case 1: Add(left, top, cTl, true); break;
                    case 2: Add(top, right, cTr, true); break;
                    case 3: Add(left, right, cTl, true); break;
                    case 4: Add(right, bottom, cBr, true); break;
                    case 6: Add(top, bottom, cTl, false); break;
                    case 7: Add(left, bottom, cBl, false); break;
                    case 8: Add(bottom, left, cBl, true); break;
                    case 9: Add(top, bottom, cTl, true); break;
                    case 11: Add(right, bottom, cBr, false); break;
                    case 12: Add(left, right, cTl, false); break;
                    case 13: Add(top, right, cTr, false); break;
                    case 14: Add(left, top, cTl, false); break;
                    case 5:
                    case 10:
                    {
                        var center = (f[i] + f[i + 1] + f[i + w] + f[i + w + 1]) / 4 > level;
                        if (index == 5)
                        {
                            if (center) { Add(top, right, cTr, false); Add(bottom, left, cBl, false); }
                            else { Add(left, top, cTl, true); Add(right, bottom, cBr, true); }
                        }
                        else
                        {
                            if (center) { Add(left, top, cTl, false); Add(right, bottom, cBr, false); }
                            else { Add(top, right, cTr, true); Add(bottom, left, cBl, true); }
                        }
                        break;
                    }
                }
            }
        }

        var loops = new List<List<Vec2>>();
        var visited = new HashSet<int>();
        foreach (var start in next.Keys)
        {
            if (visited.Contains(start)) continue;
            var loop = new List<Vec2>();
            var edge = start;
            while (visited.Add(edge))
            {
                loop.Add(Crossing(edge));
                if (!next.TryGetValue(edge, out edge)) break;
            }
            if (loop.Count >= 3) loops.Add(Dedupe(loop));
        }

        // With the inside on the positive-cross side of every segment, outer outlines have positive shoelace
        // area and holes negative (checked by the tests).
        return loops;
    }

    static List<Vec2> Dedupe(List<Vec2> loop)
    {
        var result = new List<Vec2>(loop.Count);
        foreach (var p in loop)
        {
            if (result.Count > 0 && (p - result[^1]).LengthSquared < 1e-10) continue;
            result.Add(p);
        }
        while (result.Count > 1 && (result[0] - result[^1]).LengthSquared < 1e-10) result.RemoveAt(result.Count - 1);
        return result;
    }
}
