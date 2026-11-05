using OpenCvSharp;
using Tracedrop.Core.Vector;

namespace Tracedrop.Core.Editing;

/// <summary>A point in picture-relative coordinates: X and Y in 0..1 of the picture's width and height.</summary>
public sealed record NormPoint(double X, double Y)
{
    public Vec2 ToPixels(double width, double height) => new(X * width, Y * height);
    public static NormPoint FromPixels(Vec2 p, double width, double height) => new(p.X / width, p.Y / height);
}

public enum RegionKind
{
    /// <summary>"Lines are missing here": re-trace this area zoomed in and use that inside it.</summary>
    Find,
    /// <summary>"No lines here": everything inside is removed (shapes are cut in Outline and Shapes modes).</summary>
    Erase,
    /// <summary>A line the user deleted: a thin strip along it. Lines lying in it are dropped (never cut).</summary>
    Suppress
}

public enum RegionShape
{
    Circle,
    Rectangle,
    Brush
}

/// <summary>
/// An area the user marked on the picture. Circle: Points = [centre], Size = radius. Rectangle: Points = two
/// corners. Brush: Points = the painted path, Size = brush radius. Sizes are fractions of the picture's longest side.
/// </summary>
public sealed record GuideRegion
{
    public RegionKind Kind { get; init; }
    public RegionShape Shape { get; init; }
    public List<NormPoint> Points { get; init; } = new();
    public double Size { get; init; }
    /// <summary>Find only: -4 (fewer lines here) .. +6 (more, fainter lines here).</summary>
    public int Amount { get; init; }

    public GuideRegion Copy() => this with { Points = Points.ToList() };

    /// <summary>Key text that changes whenever the region would trace differently.</summary>
    public string Key() =>
        $"{Kind}:{Shape}:{Size:0.#####}:{Amount}:" + string.Join(";", Points.Select(p => $"{p.X:0.#####},{p.Y:0.#####}"));

    /// <summary>The area as an 8-bit mask (255 inside) for a picture of this size.</summary>
    public Mat Mask(Size size)
    {
        var mask = new Mat(size, MatType.CV_8U, Scalar.All(0));
        Draw(mask, Scalar.All(255));
        return mask;
    }

    /// <summary>Paints the area into <paramref name="target"/> (picture-sized) with <paramref name="color"/>.</summary>
    public void Draw(Mat target, Scalar color)
    {
        var w = target.Cols;
        var h = target.Rows;
        var longest = Math.Max(w, h);
        Point Px(NormPoint p) => new((int)Math.Round(p.X * w), (int)Math.Round(p.Y * h));
        var radius = Math.Max(1, (int)Math.Round(Size * longest));
        switch (Shape)
        {
            case RegionShape.Circle when Points.Count > 0:
                Cv2.Circle(target, Px(Points[0]), radius, color, -1, LineTypes.Link8);
                break;
            case RegionShape.Rectangle when Points.Count > 1:
                var a = Px(Points[0]);
                var b = Px(Points[1]);
                Cv2.Rectangle(target, new Point(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y)), new Point(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y)), color, -1);
                break;
            case RegionShape.Brush when Points.Count > 0:
                var pts = Points.Select(Px).ToArray();
                if (pts.Length == 1) Cv2.Circle(target, pts[0], radius, color, -1, LineTypes.Link8);
                else Cv2.Polylines(target, new[] { pts }, false, color, Math.Max(1, 2 * radius), LineTypes.Link8);
                foreach (var p in new[] { pts[0], pts[^1] }) Cv2.Circle(target, p, radius, color, -1, LineTypes.Link8);
                break;
        }
    }

    /// <summary>Bounding box in pixels of a picture this size (clipped to it), or an empty rectangle.</summary>
    public Rect Bounds(Size size)
    {
        if (Points.Count == 0) return new Rect();
        var w = size.Width;
        var h = size.Height;
        var r = Shape == RegionShape.Rectangle ? 0 : Size * Math.Max(w, h);
        var xs = Points.Select(p => p.X * w).ToList();
        var ys = Points.Select(p => p.Y * h).ToList();
        var x0 = (int)Math.Floor(Math.Max(0, xs.Min() - r));
        var y0 = (int)Math.Floor(Math.Max(0, ys.Min() - r));
        var x1 = (int)Math.Ceiling(Math.Min(w, xs.Max() + r));
        var y1 = (int)Math.Ceiling(Math.Min(h, ys.Max() + r));
        return x1 > x0 && y1 > y0 ? new Rect(x0, y0, x1 - x0, y1 - y0) : new Rect();
    }
}

/// <summary>One anchor of a user path, with optional Bezier handles (absolute positions; null = no handle).</summary>
public sealed record ManualNode(NormPoint Point, NormPoint? In = null, NormPoint? Out = null);

/// <summary>A path the user drew or took over from the AI. Never changed by tracing; exported as it is.</summary>
public sealed record ManualPath
{
    public List<ManualNode> Nodes { get; init; } = new();
    public bool Closed { get; init; }

    public ManualPath Copy() => this with { Nodes = Nodes.ToList() };

    /// <summary>Segments in picture pixels: a line where neither end has a handle on that side, else a cubic.</summary>
    public List<Segment> Segments(double width, double height)
    {
        var segments = new List<Segment>();
        var count = Closed ? Nodes.Count : Nodes.Count - 1;
        for (var i = 0; i < count; i++)
        {
            var a = Nodes[i];
            var b = Nodes[(i + 1) % Nodes.Count];
            var p0 = a.Point.ToPixels(width, height);
            var p3 = b.Point.ToPixels(width, height);
            if (a.Out is null && b.In is null)
            {
                if (Vec2.Distance(p0, p3) > 1e-9) segments.Add(new LineSegment(p0, p3));
            }
            else
            {
                var c1 = (a.Out ?? a.Point).ToPixels(width, height);
                var c2 = (b.In ?? b.Point).ToPixels(width, height);
                segments.Add(new CubicSegment(p0, c1, c2, p3));
            }
        }
        return segments;
    }

    public VectorPath ToVectorPath(double width, double height, int index) => new()
    {
        Segments = Segments(width, height),
        Closed = Closed,
        Role = Closed ? PathRole.Outer : PathRole.Stroke,
        Layer = Closed ? "OUTLINE" : "LINES",
        Color = (20, 20, 20),
        ManualIndex = index
    };

    /// <summary>A traced path (pixels) as an editable user path: lines stay lines, arcs and curves become Beziers.</summary>
    public static ManualPath FromVectorPath(VectorPath path, double width, double height)
    {
        NormPoint N(Vec2 v) => NormPoint.FromPixels(v, width, height);
        if (path.Circle is { } c)
        {
            // Four Bezier quarters.
            const double k = 0.5522847498;
            var r = c.Radius;
            var o = c.Center;
            var nodes = new List<ManualNode>
            {
                new(N(o + new Vec2(r, 0)), N(o + new Vec2(r, -k * r)), N(o + new Vec2(r, k * r))),
                new(N(o + new Vec2(0, r)), N(o + new Vec2(k * r, r)), N(o + new Vec2(-k * r, r))),
                new(N(o + new Vec2(-r, 0)), N(o + new Vec2(-r, k * r)), N(o + new Vec2(-r, -k * r))),
                new(N(o + new Vec2(0, -r)), N(o + new Vec2(-k * r, -r)), N(o + new Vec2(k * r, -r)))
            };
            return new ManualPath { Nodes = nodes, Closed = true };
        }

        var pieces = new List<Segment>();
        foreach (var s in path.Segments)
        {
            if (s is ArcSegment arc) pieces.AddRange(Export.Pdf.ArcToCubics(arc));
            else pieces.Add(s);
        }
        var list = new List<ManualNode>();
        if (pieces.Count == 0) return new ManualPath();
        list.Add(new ManualNode(N(pieces[0].Start)));
        foreach (var s in pieces)
        {
            if (s is CubicSegment cubic)
            {
                list[^1] = list[^1] with { Out = N(cubic.Control1) };
                list.Add(new ManualNode(N(cubic.End), N(cubic.Control2)));
            }
            else list.Add(new ManualNode(N(s.End)));
        }
        if (path.Closed && list.Count > 1 && Vec2.Distance(pieces[^1].End, pieces[0].Start) < 1e-6)
        {
            // The last node repeats the first: fold its incoming handle into the first and drop it.
            var last = list[^1];
            list.RemoveAt(list.Count - 1);
            list[0] = list[0] with { In = last.In };
        }
        return new ManualPath { Nodes = list, Closed = path.Closed };
    }
}

/// <summary>Node editing on user paths. Every method returns a new path; the input is not changed.</summary>
public static class PathEdits
{
    static NormPoint Lerp(NormPoint a, NormPoint b, double t) => new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);
    static NormPoint Add(NormPoint a, double dx, double dy) => new(a.X + dx, a.Y + dy);

    /// <summary>Inserts a node on segment <paramref name="segment"/> (from node i to i+1) at parameter t, keeping the shape.</summary>
    public static ManualPath InsertNode(ManualPath path, int segment, double t)
    {
        var nodes = path.Nodes.ToList();
        var count = nodes.Count;
        if (count < 2 || segment < 0 || segment >= (path.Closed ? count : count - 1)) return path;
        t = Math.Clamp(t, 0.02, 0.98);
        var a = nodes[segment];
        var bi = (segment + 1) % count;
        var b = nodes[bi];
        ManualNode inserted;
        if (a.Out is null && b.In is null)
        {
            inserted = new ManualNode(Lerp(a.Point, b.Point, t));
        }
        else
        {
            // de Casteljau split of the cubic.
            var p0 = a.Point;
            var c1 = a.Out ?? a.Point;
            var c2 = b.In ?? b.Point;
            var p3 = b.Point;
            var q0 = Lerp(p0, c1, t);
            var q1 = Lerp(c1, c2, t);
            var q2 = Lerp(c2, p3, t);
            var r0 = Lerp(q0, q1, t);
            var r1 = Lerp(q1, q2, t);
            inserted = new ManualNode(Lerp(r0, r1, t), r0, r1);
            nodes[segment] = a with { Out = q0 };
            nodes[bi] = b with { In = q2 };
        }
        nodes.Insert(segment + 1, inserted);
        return path with { Nodes = nodes };
    }

    /// <summary>Removes node i. Returns null when fewer than two nodes would be left.</summary>
    public static ManualPath? RemoveNode(ManualPath path, int i)
    {
        if (i < 0 || i >= path.Nodes.Count) return path;
        if (path.Nodes.Count <= 2) return null;
        var nodes = path.Nodes.ToList();
        nodes.RemoveAt(i);
        return path with { Nodes = nodes, Closed = path.Closed && nodes.Count > 2 };
    }

    /// <summary>Moves node i (and its handles) by (dx, dy).</summary>
    public static ManualPath MoveNode(ManualPath path, int i, double dx, double dy)
    {
        var nodes = path.Nodes.ToList();
        var n = nodes[i];
        nodes[i] = new ManualNode(Add(n.Point, dx, dy), n.In is null ? null : Add(n.In, dx, dy), n.Out is null ? null : Add(n.Out, dx, dy));
        return path with { Nodes = nodes };
    }

    /// <summary>Moves the whole path by (dx, dy).</summary>
    public static ManualPath Move(ManualPath path, double dx, double dy)
    {
        var moved = path;
        for (var i = 0; i < path.Nodes.Count; i++) moved = MoveNode(moved, i, dx, dy);
        return moved;
    }

    /// <summary>
    /// Sets a handle of node i. On a smooth node (both handles) the other handle turns to stay opposite, keeping its
    /// length, unless <paramref name="independent"/> (Alt) is set.
    /// </summary>
    public static ManualPath SetHandle(ManualPath path, int i, bool outHandle, NormPoint to, bool independent, double aspect = 1)
    {
        var nodes = path.Nodes.ToList();
        var n = nodes[i];
        var other = outHandle ? n.In : n.Out;
        NormPoint? mirrored = other;
        if (!independent && other is not null)
        {
            // Directions are measured in picture pixels (aspect = width / height), so "opposite" looks opposite.
            var dx = (to.X - n.Point.X) * aspect;
            var dy = to.Y - n.Point.Y;
            var len = Math.Sqrt(dx * dx + dy * dy);
            var ox = (other.X - n.Point.X) * aspect;
            var oy = other.Y - n.Point.Y;
            var otherLen = Math.Sqrt(ox * ox + oy * oy);
            if (len > 1e-9) mirrored = new NormPoint(n.Point.X - dx / len * otherLen / aspect, n.Point.Y - dy / len * otherLen);
        }
        nodes[i] = outHandle ? new ManualNode(n.Point, mirrored, to) : new ManualNode(n.Point, to, mirrored);
        return path with { Nodes = nodes };
    }

    /// <summary>Corner ↔ smooth: drops both handles, or adds handles along the line through the neighbours.</summary>
    public static ManualPath ToggleSmooth(ManualPath path, int i)
    {
        var nodes = path.Nodes.ToList();
        var n = nodes[i];
        if (n.In is not null || n.Out is not null)
        {
            nodes[i] = new ManualNode(n.Point);
            return path with { Nodes = nodes };
        }
        var count = nodes.Count;
        var prev = i > 0 ? nodes[i - 1].Point : path.Closed ? nodes[count - 1].Point : n.Point;
        var next = i < count - 1 ? nodes[i + 1].Point : path.Closed ? nodes[0].Point : n.Point;
        var dx = (next.X - prev.X) / 6;
        var dy = (next.Y - prev.Y) / 6;
        if (Math.Abs(dx) + Math.Abs(dy) < 1e-9) return path;
        nodes[i] = new ManualNode(n.Point, Add(n.Point, -dx, -dy), Add(n.Point, dx, dy));
        return path with { Nodes = nodes };
    }

    /// <summary>
    /// Closest point on the path to <paramref name="target"/> (all in picture pixels): segment index, parameter and
    /// distance.
    /// </summary>
    public static (int Segment, double T, double Distance) Closest(ManualPath path, double width, double height, Vec2 target)
    {
        var best = (-1, 0.0, double.MaxValue);
        var count = path.Closed ? path.Nodes.Count : path.Nodes.Count - 1;
        for (var i = 0; i < count; i++)
        {
            var a = path.Nodes[i];
            var b = path.Nodes[(i + 1) % path.Nodes.Count];
            var p0 = a.Point.ToPixels(width, height);
            var p3 = b.Point.ToPixels(width, height);
            var cubic = a.Out is null && b.In is null
                ? null
                : new CubicSegment(p0, (a.Out ?? a.Point).ToPixels(width, height), (b.In ?? b.Point).ToPixels(width, height), p3);
            const int steps = 48;
            for (var j = 0; j <= steps; j++)
            {
                var t = j / (double)steps;
                var d = Vec2.Distance(cubic?.At(t) ?? Vec2.Lerp(p0, p3, t), target);
                if (d < best.Item3) best = (i, t, d);
            }
        }
        return best;
    }
}

/// <summary>Geometry helpers for applying edits to traced paths.</summary>
public static class EditGeometry
{
    /// <summary>
    /// The pieces of <paramref name="points"/> that lie inside (keepInside) or outside the mask, as separate open
    /// runs. A closed path that is entirely kept stays closed.
    /// </summary>
    public static List<(List<Vec2> Points, bool Closed)> Split(IReadOnlyList<Vec2> points, bool closed, Func<Vec2, bool> inside, bool keepInside)
    {
        var result = new List<(List<Vec2>, bool)>();
        if (points.Count == 0) return result;
        var keep = points.Select(p => inside(p) == keepInside).ToArray();
        if (keep.All(k => k))
        {
            result.Add((points.ToList(), closed));
            return result;
        }
        if (!keep.Any(k => k)) return result;

        // For a closed path start right after a dropped point so runs don't wrap around.
        var n = points.Count;
        var start = 0;
        if (closed)
        {
            for (var i = 0; i < n; i++)
            {
                if (!keep[i] && keep[(i + 1) % n]) { start = (i + 1) % n; break; }
            }
        }
        var current = new List<Vec2>();
        for (var k = 0; k < n; k++)
        {
            var i = closed ? (start + k) % n : k;
            if (keep[i]) current.Add(points[i]);
            else if (current.Count > 0)
            {
                if (current.Count >= 2) result.Add((current, false));
                current = new List<Vec2>();
            }
        }
        if (current.Count >= 2) result.Add((current, false));
        return result;
    }

    /// <summary>Fraction of the points that are inside.</summary>
    public static double FractionInside(IReadOnlyList<Vec2> points, Func<Vec2, bool> inside) =>
        points.Count == 0 ? 0 : points.Count(inside) / (double)points.Count;

    /// <summary>A brush region (Suppress) following a traced path, for deleting it so re-traces don't bring it back.</summary>
    public static GuideRegion SuppressAlong(IReadOnlyList<Vec2> pixels, double width, double height, double brushPixels)
    {
        var longest = Math.Max(width, height);
        var thinned = new List<NormPoint>();
        Vec2? last = null;
        foreach (var p in pixels)
        {
            if (last is { } l && Vec2.Distance(l, p) < Math.Max(2, brushPixels)) continue;
            thinned.Add(NormPoint.FromPixels(p, width, height));
            last = p;
        }
        if (pixels.Count > 0) thinned.Add(NormPoint.FromPixels(pixels[^1], width, height));
        return new GuideRegion { Kind = RegionKind.Suppress, Shape = RegionShape.Brush, Points = thinned, Size = brushPixels / longest };
    }
}
