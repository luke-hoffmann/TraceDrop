namespace Tracedrop.Core.Vector;

/// <summary>Centerlines of thin strokes: thinning to a one-pixel skeleton and tracing it into chains.</summary>
public static class Skeleton
{
    // Neighbour offsets in clockwise order starting north: P2..P9 in Zhang-Suen notation.
    static readonly int[] Dx = { 0, 1, 1, 1, 0, -1, -1, -1 };
    static readonly int[] Dy = { -1, -1, 0, 1, 1, 1, 0, -1 };

    /// <summary>Zhang-Suen thinning, in place. <paramref name="pixels"/> is 0 / non-zero, row-major.</summary>
    public static void Thin(byte[] pixels, int width, int height)
    {
        if (ThinWithOpenCv(pixels, width, height)) return;
        ThinManaged(pixels, width, height);
    }

    /// <summary>OpenCV's (contrib) Zhang-Suen is ~10x faster than the managed loop; the redundant-pixel pass still runs here.</summary>
    static bool ThinWithOpenCv(byte[] pixels, int width, int height)
    {
        try
        {
            using var mat = new OpenCvSharp.Mat(height, width, OpenCvSharp.MatType.CV_8U);
            for (var i = 0; i < pixels.Length; i++) pixels[i] = pixels[i] != 0 ? (byte)255 : (byte)0;
            // Clear the frame so neighbour lookups never leave the picture.
            for (var x = 0; x < width; x++) { pixels[x] = 0; pixels[(height - 1) * width + x] = 0; }
            for (var y = 0; y < height; y++) { pixels[y * width] = 0; pixels[y * width + width - 1] = 0; }
            mat.SetArray(pixels);
            using var thinned = new OpenCvSharp.Mat();
            OpenCvSharp.XImgProc.CvXImgProc.Thinning(mat, thinned, OpenCvSharp.XImgProc.ThinningTypes.ZHANGSUEN);
            thinned.GetArray(out byte[] result);
            var active = new List<int>();
            for (var i = 0; i < result.Length; i++)
            {
                pixels[i] = result[i] != 0 ? (byte)1 : (byte)0;
                if (pixels[i] != 0) active.Add(i);
            }
            RemoveRedundant(pixels, width, active);
            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or TypeInitializationException or OpenCvSharp.OpenCVException)
        {
            return false;
        }
    }

    static void ThinManaged(byte[] pixels, int width, int height)
    {
        for (var i = 0; i < pixels.Length; i++) pixels[i] = pixels[i] != 0 ? (byte)1 : (byte)0;
        // Clear the frame so neighbour lookups never leave the picture.
        for (var x = 0; x < width; x++) { pixels[x] = 0; pixels[(height - 1) * width + x] = 0; }
        for (var y = 0; y < height; y++) { pixels[y * width] = 0; pixels[y * width + width - 1] = 0; }

        var active = new List<int>();
        for (var i = 0; i < pixels.Length; i++) if (pixels[i] != 0) active.Add(i);
        var remove = new List<int>();
        var changed = true;
        while (changed)
        {
            changed = false;
            for (var pass = 0; pass < 2; pass++)
            {
                remove.Clear();
                foreach (var i in active)
                {
                    if (pixels[i] == 0) continue;
                    int p2 = pixels[i - width], p3 = pixels[i - width + 1], p4 = pixels[i + 1], p5 = pixels[i + width + 1];
                    int p6 = pixels[i + width], p7 = pixels[i + width - 1], p8 = pixels[i - 1], p9 = pixels[i - width - 1];
                    var b = p2 + p3 + p4 + p5 + p6 + p7 + p8 + p9;
                    if (b < 2 || b > 6) continue;
                    var a = (p2 == 0 && p3 == 1 ? 1 : 0) + (p3 == 0 && p4 == 1 ? 1 : 0) + (p4 == 0 && p5 == 1 ? 1 : 0) +
                            (p5 == 0 && p6 == 1 ? 1 : 0) + (p6 == 0 && p7 == 1 ? 1 : 0) + (p7 == 0 && p8 == 1 ? 1 : 0) +
                            (p8 == 0 && p9 == 1 ? 1 : 0) + (p9 == 0 && p2 == 1 ? 1 : 0);
                    if (a != 1) continue;
                    if (pass == 0 ? p2 * p4 * p6 != 0 || p4 * p6 * p8 != 0 : p2 * p4 * p8 != 0 || p2 * p6 * p8 != 0) continue;
                    remove.Add(i);
                }
                foreach (var i in remove) pixels[i] = 0;
                if (remove.Count > 0) changed = true;
            }
            active.RemoveAll(i => pixels[i] == 0);
        }

        RemoveRedundant(pixels, width, active);
    }

    /// <summary>
    /// Zhang-Suen leaves staircase steps two pixels thick, which would read as junctions and split lines.
    /// Removes any pixel that has at least two neighbours which all still touch each other without it
    /// (one 8-connected group around it): the line stays connected, just one pixel thinner.
    /// </summary>
    static void RemoveRedundant(byte[] pixels, int width, List<int> active)
    {
        var changedAny = true;
        while (changedAny)
        {
            changedAny = false;
            foreach (var i in active)
            {
                if (pixels[i] == 0) continue;
                if (IsRedundant(pixels, i, width))
                {
                    pixels[i] = 0;
                    changedAny = true;
                }
            }
            active.RemoveAll(i => pixels[i] == 0);
        }
    }

    /// <summary>True when the pixel has ≥2 neighbours forming a single 8-connected group (so removing it disconnects nothing).</summary>
    static bool IsRedundant(byte[] pixels, int i, int width)
    {
        Span<int> nx = stackalloc int[8];
        Span<int> ny = stackalloc int[8];
        var count = 0;
        for (var k = 0; k < 8; k++)
        {
            if (pixels[i + Dy[k] * width + Dx[k]] == 0) continue;
            nx[count] = Dx[k];
            ny[count] = Dy[k];
            count++;
        }
        if (count < 2 || count > 6) return false;

        // Union-find over the set neighbours; two neighbours are joined when they touch (8-adjacency).
        Span<int> parent = stackalloc int[8];
        for (var a = 0; a < count; a++) parent[a] = a;
        int Find(Span<int> p, int a) { while (p[a] != a) a = p[a] = p[p[a]]; return a; }
        var groups = count;
        for (var a = 0; a < count; a++)
        {
            for (var b = a + 1; b < count; b++)
            {
                if (Math.Abs(nx[a] - nx[b]) > 1 || Math.Abs(ny[a] - ny[b]) > 1) continue;
                var ra = Find(parent, a);
                var rb = Find(parent, b);
                if (ra == rb) continue;
                parent[ra] = rb;
                groups--;
            }
        }
        return groups == 1;
    }

    /// <summary>A traced centerline: pixel positions in order, and whether it closes on itself.</summary>
    public sealed record Chain(List<Vec2> Points, bool Closed, bool StartIsEnd, bool EndIsEnd);

    /// <summary>
    /// Traces a one-pixel skeleton (1 = on) into chains between endpoints and junctions, plus closed loops.
    /// Pixel centres map to +0.5 coordinates.
    /// </summary>
    public static List<Chain> Trace(byte[] skeleton, int width, int height)
    {
        // 4-neighbours first, so a walk prefers straight steps over diagonal shortcuts.
        var order = WalkOrder;
        var offsets = new int[8];
        for (var k = 0; k < 8; k++) offsets[k] = Dy[order[k]] * width + Dx[order[k]];

        bool Inside(int i, int k)
        {
            int x = i % width + Dx[order[k]], y = i / width + Dy[order[k]];
            return x >= 0 && y >= 0 && x < width && y < height;
        }

        var on = new List<int>();
        for (var i = 0; i < skeleton.Length; i++) if (skeleton[i] != 0) on.Add(i);
        var degree = new byte[skeleton.Length];
        foreach (var i in on)
        {
            byte d = 0;
            for (var k = 0; k < 8; k++) if (Inside(i, k) && skeleton[i + offsets[k]] != 0) d++;
            degree[i] = d;
        }

        Vec2 P(int i) => new(i % width + 0.5, i / width + 0.5);
        var usedEdges = new HashSet<long>();
        long EdgeKey(int a, int b) => a < b ? (long)a * skeleton.Length + b : (long)b * skeleton.Length + a;
        var visited = new bool[skeleton.Length];
        var chains = new List<Chain>();
        var pixels = new List<int>();

        // Open chains: walk from every node (endpoint or junction) along every unused edge to the next node.
        foreach (var start in on)
        {
            if (degree[start] == 2) continue;
            for (var k0 = 0; k0 < 8; k0++)
            {
                if (!Inside(start, k0)) continue;
                var first = start + offsets[k0];
                if (skeleton[first] == 0 || usedEdges.Contains(EdgeKey(start, first))) continue;
                pixels.Clear();
                pixels.Add(start);
                var prev = start;
                var current = first;
                usedEdges.Add(EdgeKey(prev, current));
                while (true)
                {
                    pixels.Add(current);
                    visited[current] = true;
                    if (degree[current] != 2) break;
                    var step = -1;
                    for (var k = 0; k < 8; k++)
                    {
                        if (!Inside(current, k)) continue;
                        var n = current + offsets[k];
                        if (skeleton[n] == 0 || n == prev || usedEdges.Contains(EdgeKey(current, n))) continue;
                        step = n;
                        break;
                    }
                    if (step < 0) break;
                    usedEdges.Add(EdgeKey(current, step));
                    prev = current;
                    current = step;
                }
                visited[start] = true;
                var end = pixels[^1];
                // Two junction pixels side by side make a 2-pixel "chain" that is really part of the junction.
                if (pixels.Count == 2 && degree[start] > 2 && degree[end] > 2) continue;
                chains.Add(new Chain(pixels.Select(P).ToList(), start == end && pixels.Count > 2, degree[start] == 1, degree[end] == 1));
            }
        }

        // Closed loops: what's left has degree 2 everywhere.
        foreach (var start in on)
        {
            if (visited[start] || degree[start] != 2) continue;
            pixels.Clear();
            pixels.Add(start);
            visited[start] = true;
            var prev = -1;
            var current = start;
            while (true)
            {
                var step = -1;
                for (var k = 0; k < 8; k++)
                {
                    if (!Inside(current, k)) continue;
                    var n = current + offsets[k];
                    if (skeleton[n] == 0 || n == prev || visited[n]) continue;
                    step = n;
                    break;
                }
                if (step < 0) break;
                visited[step] = true;
                pixels.Add(step);
                prev = current;
                current = step;
            }
            if (pixels.Count >= 4) chains.Add(new Chain(pixels.Select(P).ToList(), true, false, false));
        }
        return chains;
    }

    static readonly int[] WalkOrder = { 0, 2, 4, 6, 1, 3, 5, 7 };

    /// <summary>
    /// Thins, then removes spurs (dead-end branches shorter than <paramref name="spurLength"/>) and chains
    /// shorter than <paramref name="minLength"/>, re-tracing after each pass so junctions that lose a branch merge.
    /// </summary>
    public static List<Chain> Centerlines(byte[] strokeMask, int width, int height, double spurLength, double minLength)
    {
        var pixels = (byte[])strokeMask.Clone();
        Thin(pixels, width, height);
        List<Chain> chains = Trace(pixels, width, height);
        for (var pass = 0; pass < 3; pass++)
        {
            var removed = false;
            foreach (var chain in chains)
            {
                var length = Geometry2D.PolylineLength(chain.Points, chain.Closed);
                var spur = !chain.Closed && (chain.StartIsEnd ^ chain.EndIsEnd) && length < spurLength;
                var isolated = !chain.Closed && chain.StartIsEnd && chain.EndIsEnd && length < minLength;
                var tinyLoop = chain.Closed && length < minLength;
                if (!spur && !isolated && !tinyLoop) continue;
                // Erase the chain but keep its junction end, which other chains still use.
                var points = chain.Points;
                var from = chain.StartIsEnd || chain.Closed ? 0 : 1;
                var to = chain.EndIsEnd || chain.Closed ? points.Count : points.Count - 1;
                for (var k = from; k < to; k++)
                {
                    var x = (int)(points[k].X - 0.5);
                    var y = (int)(points[k].Y - 0.5);
                    pixels[y * width + x] = 0;
                }
                removed = true;
            }
            if (!removed) break;
            chains = Trace(pixels, width, height);
        }
        return chains.Where(c => Geometry2D.PolylineLength(c.Points, c.Closed) >= Math.Max(2, minLength * 0.5)).ToList();
    }
}
