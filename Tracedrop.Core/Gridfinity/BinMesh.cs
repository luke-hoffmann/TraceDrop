using Clipper2Lib;
using LibTessDotNet;
using Tracedrop.Core.Vector;

namespace Tracedrop.Core.Gridfinity;

/// <summary>A triangle mesh in millimetres. Triangles wind counter-clockwise seen from outside.</summary>
public sealed class Mesh
{
    public List<(double X, double Y, double Z)> Vertices { get; } = new();
    /// <summary>Three vertex indices per triangle.</summary>
    public List<int> Indices { get; } = new();
    /// <summary>Vertices the triangulator had to invent (should be 0: each one risks a crack).</summary>
    public int InventedVertices { get; set; }

    public int TriangleCount => Indices.Count / 3;

    public ((double X, double Y, double Z) Min, (double X, double Y, double Z) Max) Bounds()
    {
        if (Vertices.Count == 0) return ((0, 0, 0), (0, 0, 0));
        return ((Vertices.Min(v => v.X), Vertices.Min(v => v.Y), Vertices.Min(v => v.Z)),
            (Vertices.Max(v => v.X), Vertices.Max(v => v.Y), Vertices.Max(v => v.Z)));
    }

    /// <summary>Enclosed volume in mm³ (positive when the triangles face outwards).</summary>
    public double Volume()
    {
        double sum = 0;
        for (var i = 0; i < Indices.Count; i += 3)
        {
            var a = Vertices[Indices[i]];
            var b = Vertices[Indices[i + 1]];
            var c = Vertices[Indices[i + 2]];
            sum += a.X * (b.Y * c.Z - b.Z * c.Y) - a.Y * (b.X * c.Z - b.Z * c.X) + a.Z * (b.X * c.Y - b.Y * c.X);
        }
        return sum / 6;
    }

    /// <summary>
    /// Edges not shared by exactly one triangle in each direction (open or non-manifold edges). 0 = watertight with
    /// consistent winding.
    /// </summary>
    public int BadEdges()
    {
        var directed = new Dictionary<(int, int), int>();
        for (var i = 0; i < Indices.Count; i += 3)
        {
            for (var k = 0; k < 3; k++)
            {
                var e = (Indices[i + k], Indices[i + (k + 1) % 3]);
                directed[e] = directed.GetValueOrDefault(e) + 1;
            }
        }
        var bad = 0;
        foreach (var ((a, b), count) in directed)
            if (count != 1 || directed.GetValueOrDefault((b, a)) != 1) bad++;
        return bad;
    }
}

/// <summary>
/// Builds the bin as a stack of layers, each a set of loops that are either straight (prisms) or lofted between
/// two similar outlines (the 45° steps of the foot and lip). Between layers the exposed flat areas (pocket floors,
/// the bin top, the underside between feet) are triangulated, and every loop edge is split wherever another
/// outline's vertex lies on it, so all faces meet vertex-to-vertex: the mesh is watertight by construction.
/// All 2D work is in integer micrometres.
/// </summary>
public static class BinMesh
{
    const long Um = 1000; // micrometres per mm
    const int CornerSegments = 8;

    sealed class Loop
    {
        public required Path64 Bottom;
        public required Path64 Top;
    }

    sealed class Layer
    {
        public required long Z0;
        public required long Z1;
        public List<Loop> Loops { get; } = new();
        public Paths64 BottomRegion => new(Loops.Select(l => l.Bottom));
        public Paths64 TopRegion => new(Loops.Select(l => l.Top));
    }

    static long U(double mm) => (long)Math.Round(mm * Um);

    /// <summary>A rounded rectangle, counter-clockwise, always 4 × (segments + 1) points.</summary>
    static Path64 RoundedRect(double x0, double y0, double x1, double y1, double r, int segments = CornerSegments)
    {
        r = Math.Clamp(r, 0.05, Math.Min(x1 - x0, y1 - y0) / 2);
        var path = new Path64();
        var corners = new[] { (x1 - r, y0 + r, -90.0), (x1 - r, y1 - r, 0.0), (x0 + r, y1 - r, 90.0), (x0 + r, y0 + r, 180.0) };
        foreach (var (cx, cy, start) in corners)
        {
            for (var i = 0; i <= segments; i++)
            {
                var a = (start + 90.0 * i / segments) * Math.PI / 180;
                path.Add(new Point64(U(cx + r * Math.Cos(a)), U(cy + r * Math.Sin(a))));
            }
        }
        return path;
    }

    static Path64 Reversed(Path64 p)
    {
        var copy = new Path64(p);
        copy.Reverse();
        return copy;
    }

    static Path64 CircleHole(double cx, double cy, double r, int n = 24)
    {
        var path = new Path64(n);
        for (var i = 0; i < n; i++)
        {
            var a = -2 * Math.PI * i / n; // clockwise: a hole
            path.Add(new Point64(U(cx + r * Math.Cos(a)), U(cy + r * Math.Sin(a))));
        }
        return path;
    }

    /// <summary>
    /// Builds the bin. Pocket outlines are simplified to 0.1 mm (a quarter of a nozzle width: invisible in print,
    /// and far fewer near-collinear points for the triangulator); should the mesh not close, it is rebuilt with
    /// coarser outlines (0.25 mm, then 0.5 mm).
    /// </summary>
    public static Mesh Build(BinDesign design, CancellationToken cancel = default)
    {
        Mesh? mesh = null;
        foreach (var tolerance in new[] { 100, 250, 500 })
        {
            mesh = Build(design, tolerance, cancel);
            if (mesh.BadEdges() == 0) return mesh;
        }
        return mesh!;
    }

    static Mesh Build(BinDesign design, double simplify, CancellationToken cancel)
    {
        var s = design.Settings;
        var width = design.Width;
        var depth = design.Depth;
        var height = design.Height;
        var layers = new List<Layer>();

        // ---- Feet: one per grid cell.
        var cells = new List<(double Cx, double Cy)>();
        for (var i = 0; i < design.GridX; i++)
            for (var j = 0; j < design.GridY; j++)
                cells.Add((Spec.Pitch * i + (Spec.Pitch - Spec.Gap) / 2, Spec.Pitch * j + (Spec.Pitch - Spec.Gap) / 2));

        Path64 Foot(double cx, double cy, double half) =>
            RoundedRect(cx - half, cy - half, cx + half, cy + half, Spec.OuterRadius - ((Spec.Pitch - Spec.Gap) / 2 - half));

        var h0 = Spec.FootBottom / 2;
        var h1 = h0 + Spec.FootChamfer1;
        var h2 = (Spec.Pitch - Spec.Gap) / 2;
        var z1 = Spec.FootChamfer1;
        var z2 = Spec.FootChamfer1 + Spec.FootStraight;
        var z3 = Spec.FootHeight;
        var magnetTop = s.Magnets ? Spec.MagnetDepth : -1;

        void AddFootLayer(double za, double zb, double halfA, double halfB, bool magnets)
        {
            var layer = new Layer { Z0 = U(za), Z1 = U(zb) };
            foreach (var (cx, cy) in cells)
            {
                layer.Loops.Add(new Loop { Bottom = Foot(cx, cy, halfA), Top = Foot(cx, cy, halfB) });
                if (!magnets) continue;
                foreach (var (mx, my) in new[] { (-1, -1), (1, -1), (1, 1), (-1, 1) })
                {
                    var hole = CircleHole(cx + mx * Spec.MagnetOffset, cy + my * Spec.MagnetOffset, Spec.MagnetDiameter / 2);
                    layer.Loops.Add(new Loop { Bottom = hole, Top = hole });
                }
            }
            layers.Add(layer);
        }

        AddFootLayer(0, z1, h0, h1, s.Magnets);
        if (s.Magnets && magnetTop < z2)
        {
            AddFootLayer(z1, magnetTop, h1, h1, true);
            AddFootLayer(magnetTop, z2, h1, h1, false);
        }
        else AddFootLayer(z1, z2, h1, h1, false);
        AddFootLayer(z2, z3, h1, h2, false);

        // ---- Body with pockets: straight slabs between pocket floors.
        var body = RoundedRect(0, 0, width, depth, Spec.OuterRadius);
        var wall = design.Wall;
        var interior = new Paths64 { RoundedRect(wall, wall, width - wall, depth - wall, Math.Max(0.5, Spec.OuterRadius - wall)) };
        var pockets = new List<(Paths64 Region, double Floor)>();
        // Outlines simplified (0.1 mm normally): far below print resolution, and no crowds of near-duplicate points for the
        // triangulator to trip over.
        Paths64 Clean(Paths64 region) => Clipper.SimplifyPaths(Clipper.Intersect(region, interior, FillRule.NonZero), simplify, true);
        foreach (var t in design.Tools)
        {
            var region = Clean(Designer.Pocket(t, s));
            if (region.Count > 0) pockets.Add((region, height - t.Depth));
        }
        foreach (var slot in design.Slots)
        {
            var region = Clean(Designer.SlotRegion(slot));
            if (region.Count > 0) pockets.Add((region, height - Designer.SlotDepth(slot, design)));
        }
        foreach (var label in design.Labels)
        {
            var region = Clean(Designer.LabelRegion(label));
            if (region.Count > 0) pockets.Add((region, height - s.LabelDepth));
        }
        var floors = pockets.Select(p => Math.Round(Math.Max(p.Floor, z3 + 0.2), 3)).Distinct().Where(f => f < height).OrderBy(f => f).ToList();
        var levels = new List<double> { z3 };
        levels.AddRange(floors.Where(f => f > z3 + 1e-6));
        levels.Add(height);
        for (var k = 0; k + 1 < levels.Count; k++)
        {
            cancel.ThrowIfCancellationRequested();
            var za = levels[k];
            var zb = levels[k + 1];
            var open = new Paths64();
            foreach (var (region, floor) in pockets)
                if (Math.Max(floor, z3 + 0.2) <= za + 1e-6) open.AddRange(region);
            var section = open.Count == 0
                ? new Paths64 { body }
                : Clipper.Difference(new Paths64 { body }, Clipper.Union(open, FillRule.NonZero), FillRule.NonZero);
            var layer = new Layer { Z0 = U(za), Z1 = U(zb) };
            foreach (var path in section) layer.Loops.Add(new Loop { Bottom = path, Top = path });
            layers.Add(layer);
        }

        // ---- Stacking lip: the outer wall carries on up, the inside steps out at 45°, straight, then in again.
        if (s.StackingLip)
        {
            Path64 Inner(double inset) => Reversed(RoundedRect(inset, inset, width - inset, depth - inset, Spec.OuterRadius - inset));
            var la = height;
            var lb = la + (Spec.LipInsetBottom - Spec.LipInsetMiddle);
            var lc = lb + Spec.LipStraight;
            var ld = lc + (Spec.LipInsetMiddle - Spec.LipInsetTop);
            foreach (var (za, zb, ia, ib) in new[]
                     {
                         (la, lb, Spec.LipInsetBottom, Spec.LipInsetMiddle), (lb, lc, Spec.LipInsetMiddle, Spec.LipInsetMiddle),
                         (lc, ld, Spec.LipInsetMiddle, Spec.LipInsetTop)
                     })
            {
                var layer = new Layer { Z0 = U(za), Z1 = U(zb) };
                layer.Loops.Add(new Loop { Bottom = body, Top = body });
                layer.Loops.Add(new Loop { Bottom = Inner(ia), Top = Inner(ib) });
                layers.Add(layer);
            }
        }

        return Assemble(layers, cancel);
    }

    // ------------------------------------------------------------------ assembly

    sealed class Builder
    {
        public readonly Mesh Mesh = new();
        public readonly List<(long X, long Y, long Z)> Exact = new();
        readonly Dictionary<(long, long, long), int> _index = new();

        public int Vertex(long x, long y, long z)
        {
            if (_index.TryGetValue((x, y, z), out var i)) return i;
            i = Mesh.Vertices.Count;
            Mesh.Vertices.Add((x / (double)Um, y / (double)Um, z / (double)Um));
            Exact.Add((x, y, z));
            _index[(x, y, z)] = i;
            return i;
        }

        public void Triangle(int a, int b, int c)
        {
            if (a == b || b == c || a == c) return;
            Mesh.Indices.Add(a);
            Mesh.Indices.Add(b);
            Mesh.Indices.Add(c);
        }
    }

    static Mesh Assemble(List<Layer> layers, CancellationToken cancel)
    {
        var builder = new Builder();
        // Split points per loop edge, at each loop's bottom and top.
        var bottomSplits = layers.Select(l => l.Loops.Select(loop => new List<Point64>[loop.Bottom.Count]).ToList()).ToList();
        var topSplits = layers.Select(l => l.Loops.Select(loop => new List<Point64>[loop.Top.Count]).ToList()).ToList();

        for (var level = 0; level <= layers.Count; level++)
        {
            cancel.ThrowIfCancellationRequested();
            var below = level > 0 ? layers[level - 1] : null;
            var above = level < layers.Count ? layers[level] : null;
            var z = below?.Z1 ?? above!.Z0;
            var a = below?.TopRegion ?? new Paths64();
            var b = above?.BottomRegion ?? new Paths64();
            var up = Clipper.Difference(a, b, FillRule.NonZero);
            var down = Clipper.Difference(b, a, FillRule.NonZero);

            // Every vertex on this level, for splitting edges that pass through it.
            var hash = new PointHash();
            foreach (var p in a) hash.AddRange(p);
            foreach (var p in b) hash.AddRange(p);
            foreach (var p in up) hash.AddRange(p);
            foreach (var p in down) hash.AddRange(p);

            if (below is not null)
                for (var i = 0; i < below.Loops.Count; i++)
                    topSplits[level - 1][i] = SplitEdges(below.Loops[i].Top, hash);
            if (above is not null)
                for (var i = 0; i < above.Loops.Count; i++)
                    bottomSplits[level][i] = SplitEdges(above.Loops[i].Bottom, hash);

            Cap(builder, up, hash, z, faceUp: true);
            Cap(builder, down, hash, z, faceUp: false);
        }

        // Side walls.
        for (var l = 0; l < layers.Count; l++)
        {
            var layer = layers[l];
            for (var i = 0; i < layer.Loops.Count; i++)
            {
                var loop = layer.Loops[i];
                var n = loop.Bottom.Count;
                for (var e = 0; e < n; e++)
                {
                    var bottom = Chain(loop.Bottom, e, bottomSplits[l][i][e]);
                    var top = Chain(loop.Top, e, topSplits[l][i][e]);
                    Wall(builder, bottom, top, layer.Z0, layer.Z1);
                }
            }
        }
        return builder.Mesh;
    }

    /// <summary>Points of edge e (from vertex e to e+1) including the split points, in order.</summary>
    static List<Point64> Chain(Path64 path, int e, List<Point64> splits)
    {
        var chain = new List<Point64>(splits.Count + 2) { path[e] };
        chain.AddRange(splits);
        chain.Add(path[(e + 1) % path.Count]);
        return chain;
    }

    /// <summary>One wall face between a bottom edge and the top edge above it, zipped into triangles.</summary>
    static void Wall(Builder builder, List<Point64> bottom, List<Point64> top, long z0, long z1)
    {
        static double Fraction(List<Point64> chain, int k)
        {
            var first = chain[0];
            var last = chain[^1];
            double dx = last.X - first.X, dy = last.Y - first.Y;
            var len = dx * dx + dy * dy;
            return len <= 0 ? k / (double)Math.Max(1, chain.Count - 1) : ((chain[k].X - first.X) * dx + (chain[k].Y - first.Y) * dy) / len;
        }
        var bi = 0;
        var ti = 0;
        while (bi < bottom.Count - 1 || ti < top.Count - 1)
        {
            var b0 = builder.Vertex(bottom[bi].X, bottom[bi].Y, z0);
            var t0 = builder.Vertex(top[ti].X, top[ti].Y, z1);
            var advanceBottom = ti == top.Count - 1 || (bi < bottom.Count - 1 && Fraction(bottom, bi + 1) <= Fraction(top, ti + 1));
            if (advanceBottom)
            {
                var b1 = builder.Vertex(bottom[bi + 1].X, bottom[bi + 1].Y, z0);
                builder.Triangle(b0, b1, t0);
                bi++;
            }
            else
            {
                var t1 = builder.Vertex(top[ti + 1].X, top[ti + 1].Y, z1);
                builder.Triangle(b0, t1, t0);
                ti++;
            }
        }
    }

    /// <summary>For each edge of the path, the level's vertices lying on it (excluding its ends), in order.</summary>
    static List<Point64>[] SplitEdges(Path64 path, PointHash hash)
    {
        var result = new List<Point64>[path.Count];
        for (var e = 0; e < path.Count; e++)
            result[e] = hash.OnSegment(path[e], path[(e + 1) % path.Count]);
        return result;
    }

    /// <summary>Triangulates a flat region at height z (pocket floor, bin top, underside), facing up or down.</summary>
    static void Cap(Builder builder, Paths64 region, PointHash hash, long z, bool faceUp)
    {
        if (region.Count == 0) return;
        long ox = long.MaxValue, oy = long.MaxValue;
        foreach (var path in region)
            foreach (var p in path) { ox = Math.Min(ox, p.X); oy = Math.Min(oy, p.Y); }

        var tess = new Tess { NoEmptyPolygons = true };
        foreach (var path in region)
        {
            var contour = new List<ContourVertex>();
            ContourVertex V(Point64 p) => new()
            {
                Position = new Vec3((p.X - ox) / (float)Um, (p.Y - oy) / (float)Um, 0),
                Data = builder.Vertex(p.X, p.Y, z)
            };
            for (var e = 0; e < path.Count; e++)
            {
                contour.Add(V(path[e]));
                foreach (var p in hash.OnSegment(path[e], path[(e + 1) % path.Count])) contour.Add(V(p));
            }
            if (contour.Count >= 3) tess.AddContour(contour.ToArray(), ContourOrientation.Original);
        }
        tess.Tessellate(WindingRule.NonZero, ElementType.Polygons, 3, (position, data, weights) =>
        {
            // A vertex the triangulator had to make (edges crossing): should not happen with clean regions.
            builder.Mesh.InventedVertices++;
            return builder.Vertex(ox + (long)Math.Round(position.X * Um), oy + (long)Math.Round(position.Y * Um), z);
        });
        var vertices = tess.Vertices;
        var elements = tess.Elements;
        var triangles = new List<(int A, int B, int C)>();
        for (var t = 0; t < tess.ElementCount; t++)
        {
            var e0 = elements[3 * t];
            var e1 = elements[3 * t + 1];
            var e2 = elements[3 * t + 2];
            if (e0 < 0 || e1 < 0 || e2 < 0) continue;
            var ia = (int)vertices[e0].Data;
            var ib = (int)vertices[e1].Data;
            var ic = (int)vertices[e2].Data;
            if (ia == ib || ib == ic || ia == ic) continue;
            var cross = Cross(builder, ia, ib, ic);
            // Zero-area triangles (three points on a straight edge) are kept for now and resolved below.
            triangles.Add(cross > 0 == faceUp || cross == 0 ? (ia, ib, ic) : (ia, ic, ib));
        }
        SplitAtPoints(builder, triangles, hash);
        foreach (var (ta, tb, tc) in triangles) builder.Triangle(ta, tb, tc);
    }

    static long Cross(Builder builder, int a, int b, int c)
    {
        var pa = builder.Exact[a];
        var pb = builder.Exact[b];
        var pc = builder.Exact[c];
        return (pb.X - pa.X) * (pc.Y - pa.Y) - (pb.Y - pa.Y) * (pc.X - pa.X);
    }

    /// <summary>
    /// The triangulator may run a triangle edge straight past points lying on it (three or more points on a line),
    /// or make zero-area triangles from them. Splitting every triangle at the level's points on its edges removes
    /// both: slivers fall apart into nothing and their neighbours pick up the middle point.
    /// </summary>
    static void SplitAtPoints(Builder builder, List<(int A, int B, int C)> triangles, PointHash hash)
    {
        var work = new Queue<(int A, int B, int C)>(triangles);
        triangles.Clear();
        var guard = 0;
        while (work.Count > 0 && guard++ < 200000)
        {
            var (a, b, c) = work.Dequeue();
            if (a == b || b == c || a == c) continue;
            var split = false;
            foreach (var (p, q, r) in new[] { (a, b, c), (b, c, a), (c, a, b) })
            {
                var ep = builder.Exact[p];
                var eq = builder.Exact[q];
                var on = hash.OnSegment(new Point64(ep.X, ep.Y), new Point64(eq.X, eq.Y));
                if (on.Count == 0) continue;
                // Fan from the opposite corner over the split edge.
                var chain = new List<int> { p };
                chain.AddRange(on.Select(v => builder.Vertex(v.X, v.Y, ep.Z)));
                chain.Add(q);
                for (var k = 0; k + 1 < chain.Count; k++) work.Enqueue((chain[k], chain[k + 1], r));
                split = true;
                break;
            }
            if (!split && Cross(builder, a, b, c) != 0) triangles.Add((a, b, c));
        }
    }

    /// <summary>True when b lies strictly between a and c (all three on one line).</summary>
    static bool Between(Builder builder, int a, int b, int c)
    {
        var pa = builder.Exact[a];
        var pb = builder.Exact[b];
        var pc = builder.Exact[c];
        var dot = (pb.X - pa.X) * (pc.X - pa.X) + (pb.Y - pa.Y) * (pc.Y - pa.Y);
        var len = (pc.X - pa.X) * (pc.X - pa.X) + (pc.Y - pa.Y) * (pc.Y - pa.Y);
        return dot > 0 && dot < len;
    }

    /// <summary>A grid hash of a level's vertices, to find the ones lying on an edge quickly.</summary>
    sealed class PointHash
    {
        const long Cell = 500; // 0.5 mm
        const double Tolerance = 2.5; // µm off the line still counts as on it
        readonly Dictionary<(long, long), List<Point64>> _cells = new();
        readonly HashSet<Point64> _all = new();

        public void AddRange(Path64 path)
        {
            foreach (var p in path)
            {
                if (!_all.Add(p)) continue;
                var key = (Floor(p.X), Floor(p.Y));
                if (!_cells.TryGetValue(key, out var list)) _cells[key] = list = new List<Point64>();
                list.Add(p);
            }
        }

        static long Floor(long v) => v >= 0 ? v / Cell : (v - Cell + 1) / Cell;

        public List<Point64> OnSegment(Point64 a, Point64 b)
        {
            var found = new List<(double T, Point64 P)>();
            double dx = b.X - a.X, dy = b.Y - a.Y;
            var len2 = dx * dx + dy * dy;
            if (len2 <= 0) return new List<Point64>();
            var len = Math.Sqrt(len2);
            var steps = Math.Max(1, (int)Math.Ceiling(len / Cell));
            var seen = new HashSet<(long, long)>();
            for (var k = 0; k <= steps; k++)
            {
                var x = a.X + dx * k / steps;
                var y = a.Y + dy * k / steps;
                var cx = Floor((long)Math.Round(x));
                var cy = Floor((long)Math.Round(y));
                for (var ix = cx - 1; ix <= cx + 1; ix++)
                    for (var iy = cy - 1; iy <= cy + 1; iy++)
                    {
                        if (!seen.Add((ix, iy)) || !_cells.TryGetValue((ix, iy), out var list)) continue;
                        foreach (var p in list)
                        {
                            if (p == a || p == b) continue;
                            var t = ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len2;
                            if (t <= 1e-12 || t >= 1 - 1e-12) continue;
                            var off = Math.Abs((p.X - a.X) * dy - (p.Y - a.Y) * dx) / len;
                            if (off <= Tolerance) found.Add((t, p));
                        }
                    }
            }
            return found.OrderBy(f => f.T).Select(f => f.P).Distinct().ToList();
        }
    }
}
