namespace Tracedrop.Core.Vector;

/// <summary>A 2D point/vector in picture pixels (x right, y down).</summary>
public readonly record struct Vec2(double X, double Y)
{
    public static Vec2 operator +(Vec2 a, Vec2 b) => new(a.X + b.X, a.Y + b.Y);
    public static Vec2 operator -(Vec2 a, Vec2 b) => new(a.X - b.X, a.Y - b.Y);
    public static Vec2 operator *(Vec2 a, double k) => new(a.X * k, a.Y * k);
    public static Vec2 operator *(double k, Vec2 a) => new(a.X * k, a.Y * k);
    public static Vec2 operator /(Vec2 a, double k) => new(a.X / k, a.Y / k);
    public static Vec2 operator -(Vec2 a) => new(-a.X, -a.Y);

    public double Length => Math.Sqrt(X * X + Y * Y);
    public double LengthSquared => X * X + Y * Y;
    public Vec2 Normalized() { var l = Length; return l < 1e-12 ? new Vec2(0, 0) : new Vec2(X / l, Y / l); }
    /// <summary>Rotated 90°: (-y, x).</summary>
    public Vec2 Perp() => new(-Y, X);

    public static double Dot(Vec2 a, Vec2 b) => a.X * b.X + a.Y * b.Y;
    public static double Cross(Vec2 a, Vec2 b) => a.X * b.Y - a.Y * b.X;
    public static double Distance(Vec2 a, Vec2 b) => (a - b).Length;
    public static Vec2 Lerp(Vec2 a, Vec2 b, double t) => new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);

    public override string ToString() => $"({X:0.###}, {Y:0.###})";
}

/// <summary>One piece of a path. Every segment starts where the previous one ends.</summary>
public abstract record Segment(Vec2 Start, Vec2 End)
{
    /// <summary>Points along the segment (excluding Start) for drawing and measuring.</summary>
    public abstract IEnumerable<Vec2> Flatten(double tolerance);
    public abstract Segment Transform(Func<Vec2, Vec2> map);
}

public sealed record LineSegment(Vec2 Start, Vec2 End) : Segment(Start, End)
{
    public override IEnumerable<Vec2> Flatten(double tolerance) { yield return End; }
    public override Segment Transform(Func<Vec2, Vec2> map) => new LineSegment(map(Start), map(End));
}

/// <summary>
/// A circular arc stored as start, the arc's own midpoint, and end. This survives any uniform scale, move or
/// mirror (the midpoint stays the midpoint), so exporters can flip the y axis without tracking direction.
/// </summary>
public sealed record ArcSegment(Vec2 Start, Vec2 Mid, Vec2 End) : Segment(Start, End)
{
    /// <summary>DXF bulge for this arc in its current coordinates: tan(sweep/4), positive when the arc turns left (counter-clockwise in a y-up system).</summary>
    public double Bulge
    {
        get
        {
            var chord = End - Start;
            var c = chord.Length;
            if (c < 1e-12) return 0;
            var mid = (Start + End) / 2;
            var sagitta = (Mid - mid).Length;
            var sign = Math.Sign(Vec2.Cross(Mid - Start, End - Mid)); // left turn = counter-clockwise
            return sign * 2 * sagitta / c;
        }
    }

    public (Vec2 Center, double Radius) Circle()
    {
        var center = Geometry2D.Circumcenter(Start, Mid, End);
        return (center, (Start - center).Length);
    }

    /// <summary>Signed sweep in radians (positive = counter-clockwise in a y-up frame, i.e. left turn).</summary>
    public double Sweep => 4 * Math.Atan(Bulge);

    public override IEnumerable<Vec2> Flatten(double tolerance)
    {
        var (center, radius) = Circle();
        var sweep = Sweep;
        var a0 = Math.Atan2(Start.Y - center.Y, Start.X - center.X);
        // Atan2 measures in the mat's own frame. A left turn (positive bulge) increases the angle when y is up
        // and decreases it when y is down; the cross-product sign used for Bulge already handles both.
        var steps = Math.Max(2, (int)Math.Ceiling(Math.Abs(sweep) / Math.Max(0.02, 2 * Math.Acos(Math.Max(-1, 1 - tolerance / Math.Max(radius, 1e-9))))));
        for (var i = 1; i <= steps; i++)
        {
            var t = i / (double)steps;
            if (i == steps) { yield return End; break; }
            var a = a0 + sweep * t;
            yield return new Vec2(center.X + radius * Math.Cos(a), center.Y + radius * Math.Sin(a));
        }
    }

    public override Segment Transform(Func<Vec2, Vec2> map) => new ArcSegment(map(Start), map(Mid), map(End));
}

public sealed record CubicSegment(Vec2 Start, Vec2 Control1, Vec2 Control2, Vec2 End) : Segment(Start, End)
{
    public Vec2 At(double t)
    {
        var u = 1 - t;
        return u * u * u * Start + 3 * u * u * t * Control1 + 3 * u * t * t * Control2 + t * t * t * End;
    }

    public override IEnumerable<Vec2> Flatten(double tolerance)
    {
        var length = Vec2.Distance(Start, Control1) + Vec2.Distance(Control1, Control2) + Vec2.Distance(Control2, End);
        var steps = Math.Clamp((int)Math.Ceiling(length / Math.Max(0.5, tolerance * 4)), 2, 200);
        for (var i = 1; i <= steps; i++) yield return i == steps ? End : At(i / (double)steps);
    }

    public override Segment Transform(Func<Vec2, Vec2> map) => new CubicSegment(map(Start), map(Control1), map(Control2), map(End));
}

public enum PathRole
{
    /// <summary>Outside boundary of a filled shape.</summary>
    Outer,
    /// <summary>Boundary of a hole inside a shape.</summary>
    Hole,
    /// <summary>An open or closed line (centerline drawing).</summary>
    Stroke
}

/// <summary>One continuous outline or line. A closed path ends where it starts. A circle has no segments.</summary>
public sealed class VectorPath
{
    public List<Segment> Segments { get; init; } = new();
    public bool Closed { get; init; }
    public PathRole Role { get; init; }
    /// <summary>Layer name in the DXF (OUTLINE, HOLES, LINES, COLOR_n).</summary>
    public string Layer { get; init; } = "OUTLINE";
    /// <summary>Colour as (R, G, B) 0..255.</summary>
    public (byte R, byte G, byte B) Color { get; init; } = (0, 0, 0);
    /// <summary>Set when the path is an exact circle.</summary>
    public (Vec2 Center, double Radius)? Circle { get; init; }

    public int EntityCount => Circle is not null ? 1 : Segments.Count;

    public VectorPath Transform(Func<Vec2, Vec2> map, double scale) => new()
    {
        Segments = Segments.Select(s => s.Transform(map)).ToList(),
        Closed = Closed,
        Role = Role,
        Layer = Layer,
        Color = Color,
        Circle = Circle is { } c ? (map(c.Center), c.Radius * scale) : null
    };

    /// <summary>All points along the path (for drawing), starting with the first point.</summary>
    public List<Vec2> Flatten(double tolerance)
    {
        var points = new List<Vec2>();
        if (Circle is { } c)
        {
            var steps = Math.Clamp((int)Math.Ceiling(2 * Math.PI * c.Radius / Math.Max(0.5, tolerance * 4)), 12, 360);
            for (var i = 0; i <= steps; i++)
            {
                var a = 2 * Math.PI * i / steps;
                points.Add(new Vec2(c.Center.X + c.Radius * Math.Cos(a), c.Center.Y + c.Radius * Math.Sin(a)));
            }
            return points;
        }
        if (Segments.Count == 0) return points;
        points.Add(Segments[0].Start);
        foreach (var s in Segments) points.AddRange(s.Flatten(tolerance));
        return points;
    }
}

/// <summary>The traced result: paths in picture pixels plus the picture size they live in.</summary>
public sealed class VectorDrawing
{
    public VectorDrawing(double width, double height, List<VectorPath> paths)
    {
        Width = width;
        Height = height;
        Paths = paths;
    }

    public double Width { get; }
    public double Height { get; }
    public List<VectorPath> Paths { get; }

    public int EntityCount => Paths.Sum(p => p.EntityCount);
    public int ClosedCount => Paths.Count(p => p.Closed || p.Circle is not null);
    public int OpenCount => Paths.Count(p => !p.Closed && p.Circle is null);

    /// <summary>Bounding box of all paths (min, max), or the picture when there are none.</summary>
    public (Vec2 Min, Vec2 Max) Extents()
    {
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var p in Paths)
        {
            foreach (var v in p.Flatten(0.5))
            {
                minX = Math.Min(minX, v.X); minY = Math.Min(minY, v.Y);
                maxX = Math.Max(maxX, v.X); maxY = Math.Max(maxY, v.Y);
            }
        }
        return minX > maxX ? (new Vec2(0, 0), new Vec2(Width, Height)) : (new Vec2(minX, minY), new Vec2(maxX, maxY));
    }

    /// <summary>
    /// The drawing in output units with the y axis pointing up (CAD convention), scaled by
    /// <paramref name="unitsPerPixel"/> and moved so its bounding box starts at (0, 0).
    /// </summary>
    public VectorDrawing ToOutputUnits(double unitsPerPixel)
    {
        var (min, max) = Extents();
        var k = unitsPerPixel;
        Vec2 Map(Vec2 p) => new((p.X - min.X) * k, (max.Y - p.Y) * k);
        return new VectorDrawing((max.X - min.X) * k, (max.Y - min.Y) * k, Paths.Select(p => p.Transform(Map, k)).ToList());
    }
}

public static class Geometry2D
{
    public static Vec2 Circumcenter(Vec2 a, Vec2 b, Vec2 c)
    {
        var d = 2 * (a.X * (b.Y - c.Y) + b.X * (c.Y - a.Y) + c.X * (a.Y - b.Y));
        if (Math.Abs(d) < 1e-12) return (a + c) / 2;
        var a2 = a.LengthSquared;
        var b2 = b.LengthSquared;
        var c2 = c.LengthSquared;
        return new Vec2(
            (a2 * (b.Y - c.Y) + b2 * (c.Y - a.Y) + c2 * (a.Y - b.Y)) / d,
            (a2 * (c.X - b.X) + b2 * (a.X - c.X) + c2 * (b.X - a.X)) / d);
    }

    /// <summary>Distance from p to the segment a-b.</summary>
    public static double DistanceToSegment(Vec2 p, Vec2 a, Vec2 b)
    {
        var ab = b - a;
        var len2 = ab.LengthSquared;
        if (len2 < 1e-18) return (p - a).Length;
        var t = Math.Clamp(Vec2.Dot(p - a, ab) / len2, 0, 1);
        return (p - (a + ab * t)).Length;
    }

    /// <summary>Signed area (shoelace). Positive for counter-clockwise in a y-up frame (clockwise on screen).</summary>
    public static double SignedArea(IReadOnlyList<Vec2> loop)
    {
        double sum = 0;
        for (int i = 0, n = loop.Count; i < n; i++)
        {
            var a = loop[i];
            var b = loop[(i + 1) % n];
            sum += a.X * b.Y - b.X * a.Y;
        }
        return sum / 2;
    }

    public static double PolylineLength(IReadOnlyList<Vec2> points, bool closed)
    {
        double length = 0;
        for (var i = 1; i < points.Count; i++) length += Vec2.Distance(points[i - 1], points[i]);
        if (closed && points.Count > 1) length += Vec2.Distance(points[^1], points[0]);
        return length;
    }
}
