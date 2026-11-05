namespace Tracedrop.Core.Vector;

/// <summary>How a traced point chain is turned into clean geometry.</summary>
public sealed record FitOptions
{
    /// <summary>Gaussian smoothing along the outline, in pixels (0 = none).</summary>
    public double SmoothSigma { get; init; } = 1.5;
    /// <summary>Largest allowed distance between the fitted geometry and the smoothed outline, in pixels.</summary>
    public double Tolerance { get; init; } = 1.0;
    /// <summary>A turn sharper than this (degrees, 0 = straight) is kept as a corner.</summary>
    public double CornerAngle { get; init; } = 55;
    public CurveStyle Style { get; init; } = CurveStyle.LinesAndArcs;
    public bool SnapCircles { get; init; } = true;
}

/// <summary>
/// Turns noisy traced outlines into a few clean pieces: finds corners, smooths between them, then fits
/// lines and arcs, cubic splines or a simplified polyline within a tolerance. Closed outlines that are round
/// become true circles.
/// </summary>
public static class Fitter
{
    public sealed record Fitted(List<Segment> Segments, (Vec2 Center, double Radius)? Circle);

    public static Fitted Fit(IReadOnlyList<Vec2> raw, bool closed, FitOptions o)
    {
        var points = Resample(raw, closed, 1.0);
        if (points.Count < (closed ? 3 : 2))
        {
            if (raw.Count >= 2) return new Fitted(new List<Segment> { new LineSegment(raw[0], raw[^1]) }, null);
            return new Fitted(new List<Segment>(), null);
        }

        var window = Math.Max(2, (int)Math.Round(1.5 + o.SmoothSigma * 1.2));
        var corners = FindCorners(points, closed, window, o.CornerAngle);

        if (closed && corners.Count == 0)
        {
            var smooth = SmoothClosed(points, o.SmoothSigma);
            if (o.SnapCircles && TryCircle(smooth, o.Tolerance, out var circle)) return new Fitted(new List<Segment>(), circle);
            // Fit a corner-less loop as one open run that starts and ends at the same point.
            var run = new List<Vec2>(smooth) { smooth[0] };
            var tangent = (smooth[1] - smooth[^1]).Normalized();
            return new Fitted(FitRun(run, o, tangent, tangent), null);
        }

        // Rotate a closed loop to start at a corner, so every run goes corner to corner.
        if (closed)
        {
            var shift = corners[0];
            points = points.Skip(shift).Concat(points.Take(shift)).ToList();
            corners = corners.Select(c => (c - shift + points.Count) % points.Count).OrderBy(c => c).ToList();
        }

        var breaks = new List<int>(corners);
        if (!closed)
        {
            if (breaks.Count == 0 || breaks[0] != 0) breaks.Insert(0, 0);
            if (breaks[^1] != points.Count - 1) breaks.Add(points.Count - 1);
        }
        else
        {
            breaks.Add(points.Count); // wraps to index 0
        }

        var segments = new List<Segment>();
        for (var b = 0; b + 1 < breaks.Count; b++)
        {
            var from = breaks[b];
            var to = breaks[b + 1];
            var run = new List<Vec2>();
            for (var k = from; k <= to; k++) run.Add(points[k % points.Count]);
            if (run.Count < 2) continue;
            var smoothRun = SmoothOpen(run, o.SmoothSigma);
            segments.AddRange(FitRun(smoothRun, o, null, null));
        }
        return new Fitted(segments, null);
    }

    // ------------------------------------------------------------------ resampling & smoothing

    /// <summary>Points spaced <paramref name="spacing"/> apart along the polyline (endpoints kept).</summary>
    internal static List<Vec2> Resample(IReadOnlyList<Vec2> pts, bool closed, double spacing)
    {
        var result = new List<Vec2>();
        if (pts.Count == 0) return result;
        var source = closed ? pts.Concat(new[] { pts[0] }).ToList() : pts.ToList();
        result.Add(source[0]);
        var carry = 0.0;
        for (var i = 1; i < source.Count; i++)
        {
            var a = source[i - 1];
            var b = source[i];
            var len = Vec2.Distance(a, b);
            if (len < 1e-12) continue;
            var t = spacing - carry;
            while (t <= len + 1e-9)
            {
                result.Add(Vec2.Lerp(a, b, t / len));
                t += spacing;
            }
            carry = len - (t - spacing);
        }
        if (closed)
        {
            // Drop the duplicate of the start if resampling landed on it.
            if (result.Count > 1 && Vec2.Distance(result[^1], result[0]) < spacing * 0.5) result.RemoveAt(result.Count - 1);
        }
        else if (Vec2.Distance(result[^1], source[^1]) > 1e-9)
        {
            if (Vec2.Distance(result[^1], source[^1]) < spacing * 0.5 && result.Count > 1) result[^1] = source[^1];
            else result.Add(source[^1]);
        }
        return result;
    }

    static double[] Kernel(double sigma)
    {
        var radius = Math.Max(1, (int)Math.Ceiling(sigma * 2.5));
        var k = new double[2 * radius + 1];
        double sum = 0;
        for (var i = -radius; i <= radius; i++) sum += k[i + radius] = Math.Exp(-i * i / (2 * sigma * sigma));
        for (var i = 0; i < k.Length; i++) k[i] /= sum;
        return k;
    }

    internal static List<Vec2> SmoothClosed(List<Vec2> p, double sigma)
    {
        if (sigma < 0.2 || p.Count < 5) return p;
        sigma = Math.Min(sigma, p.Count / 6.0);
        var k = Kernel(sigma);
        var r = k.Length / 2;
        var n = p.Count;
        var result = new List<Vec2>(n);
        for (var i = 0; i < n; i++)
        {
            double x = 0, y = 0;
            for (var j = -r; j <= r; j++)
            {
                var q = p[((i + j) % n + n) % n];
                x += q.X * k[j + r];
                y += q.Y * k[j + r];
            }
            result.Add(new Vec2(x, y));
        }
        return result;
    }

    /// <summary>Smooths an open run with both ends pinned (point reflection past the ends keeps straight lines straight).</summary>
    internal static List<Vec2> SmoothOpen(List<Vec2> p, double sigma)
    {
        if (sigma < 0.2 || p.Count < 4) return p;
        sigma = Math.Min(sigma, p.Count / 4.0);
        var k = Kernel(sigma);
        var r = k.Length / 2;
        var n = p.Count;
        Vec2 At(int i)
        {
            if (i < 0) return 2 * p[0] - p[Math.Min(n - 1, -i)];
            if (i >= n) return 2 * p[n - 1] - p[Math.Max(0, 2 * (n - 1) - i)];
            return p[i];
        }
        var result = new List<Vec2>(n) { p[0] };
        for (var i = 1; i < n - 1; i++)
        {
            double x = 0, y = 0;
            for (var j = -r; j <= r; j++)
            {
                var q = At(i + j);
                x += q.X * k[j + r];
                y += q.Y * k[j + r];
            }
            result.Add(new Vec2(x, y));
        }
        result.Add(p[n - 1]);
        return result;
    }

    // ------------------------------------------------------------------ corners

    /// <summary>Indices where the outline turns more than <paramref name="angleDeg"/> over ±window samples (local maxima only).</summary>
    internal static List<int> FindCorners(List<Vec2> p, bool closed, int window, double angleDeg)
    {
        var n = p.Count;
        var corners = new List<int>();
        if (n < 2 * window + 3) return corners;
        var turn = new double[n];
        for (var i = 0; i < n; i++)
        {
            if (!closed && (i < window || i >= n - window)) continue;
            var a = p[((i - window) % n + n) % n];
            var b = p[i];
            var c = p[(i + window) % n];
            var v1 = b - a;
            var v2 = c - b;
            var denom = v1.Length * v2.Length;
            if (denom < 1e-12) continue;
            var cos = Math.Clamp(Vec2.Dot(v1, v2) / denom, -1, 1);
            turn[i] = Math.Acos(cos) * 180 / Math.PI;
        }
        for (var i = 0; i < n; i++)
        {
            if (turn[i] < angleDeg) continue;
            var isMax = true;
            for (var j = -window; j <= window && isMax; j++)
            {
                if (j == 0) continue;
                var k = closed ? ((i + j) % n + n) % n : i + j;
                if (k < 0 || k >= n) continue;
                if (turn[k] > turn[i] || (turn[k] == turn[i] && j < 0)) isMax = false;
            }
            if (isMax) corners.Add(i);
        }
        // Refine each corner to the sample farthest from the chord between its neighbours (sharper placement).
        for (var c = 0; c < corners.Count; c++)
        {
            var i = corners[c];
            var a = p[((i - window) % n + n) % n];
            var b = p[(i + window) % n];
            var best = i;
            var bestD = -1.0;
            for (var j = -window + 1; j < window; j++)
            {
                var k = closed ? ((i + j) % n + n) % n : i + j;
                if (!closed && (k <= 0 || k >= n - 1)) continue;
                var d = Geometry2D.DistanceToSegment(p[k], a, b);
                if (d > bestD) { bestD = d; best = k; }
            }
            corners[c] = best;
        }
        return corners.Distinct().OrderBy(i => i).ToList();
    }

    // ------------------------------------------------------------------ circles

    static bool TryCircle(List<Vec2> p, double tolerance, out (Vec2 Center, double Radius) circle)
    {
        circle = default;
        if (p.Count < 12) return false;
        // Kasa algebraic fit: x² + y² + D x + E y + F = 0.
        double sx = 0, sy = 0;
        foreach (var q in p) { sx += q.X; sy += q.Y; }
        var mx = sx / p.Count;
        var my = sy / p.Count;
        double suu = 0, svv = 0, suv = 0, suuu = 0, svvv = 0, suvv = 0, svuu = 0;
        foreach (var q in p)
        {
            var u = q.X - mx;
            var v = q.Y - my;
            suu += u * u; svv += v * v; suv += u * v;
            suuu += u * u * u; svvv += v * v * v; suvv += u * v * v; svuu += v * u * u;
        }
        var det = suu * svv - suv * suv;
        if (Math.Abs(det) < 1e-12) return false;
        var rhs1 = 0.5 * (suuu + suvv);
        var rhs2 = 0.5 * (svvv + svuu);
        var uc = (rhs1 * svv - rhs2 * suv) / det;
        var vc = (rhs2 * suu - rhs1 * suv) / det;
        var center = new Vec2(uc + mx, vc + my);
        var radius = Math.Sqrt(uc * uc + vc * vc + (suu + svv) / p.Count);
        if (radius < 2) return false;
        var allowed = Math.Max(tolerance, radius * 0.012);
        foreach (var q in p)
        {
            if (Math.Abs(Vec2.Distance(q, center) - radius) > allowed) return false;
        }
        circle = (center, radius);
        return true;
    }

    // ------------------------------------------------------------------ fitting one corner-to-corner run

    static List<Segment> FitRun(List<Vec2> run, FitOptions o, Vec2? startTangent, Vec2? endTangent) => o.Style switch
    {
        CurveStyle.Polylines => Polyline(run, o.Tolerance),
        CurveStyle.Splines => Splines(run, o.Tolerance, startTangent, endTangent),
        _ => LinesAndArcs(run, o.Tolerance)
    };

    static List<Segment> Polyline(List<Vec2> run, double tol)
    {
        var keep = new bool[run.Count];
        keep[0] = keep[^1] = true;
        DouglasPeucker(run, 0, run.Count - 1, tol, keep);
        var segments = new List<Segment>();
        var last = 0;
        for (var i = 1; i < run.Count; i++)
        {
            if (!keep[i]) continue;
            if (Vec2.Distance(run[last], run[i]) > 1e-9) segments.Add(new LineSegment(run[last], run[i]));
            last = i;
        }
        return segments;
    }

    static void DouglasPeucker(List<Vec2> p, int a, int b, double tol, bool[] keep)
    {
        while (true)
        {
            if (b <= a + 1) return;
            var maxD = -1.0;
            var index = -1;
            for (var i = a + 1; i < b; i++)
            {
                var d = Geometry2D.DistanceToSegment(p[i], p[a], p[b]);
                if (d > maxD) { maxD = d; index = i; }
            }
            if (maxD <= tol) return;
            keep[index] = true;
            DouglasPeucker(p, a, index, tol, keep);
            a = index;
        }
    }

    /// <summary>Greedy lines-and-arcs: from each point, take whichever of a line or an arc reaches furthest within tolerance.</summary>
    static List<Segment> LinesAndArcs(List<Vec2> run, double tol)
    {
        var segments = new List<Segment>();
        var n = run.Count;
        var i = 0;
        while (i < n - 1)
        {
            var lineEnd = Extend(i, n, j => LineFits(run, i, j, tol));
            var arcEnd = Extend(i, n, j => ArcFit(run, i, j, tol) is not null, minLength: 4);
            // Prefer the line unless the arc covers clearly more of the outline.
            if (arcEnd > lineEnd + Math.Max(2, (lineEnd - i) / 5) && ArcFit(run, i, arcEnd, tol) is { } arc)
            {
                segments.Add(arc);
                i = arcEnd;
            }
            else
            {
                segments.Add(new LineSegment(run[i], run[lineEnd]));
                i = lineEnd;
            }
        }
        return MergeLines(segments, tol);
    }

    /// <summary>Largest j (i &lt; j &lt; n) for which fits(j) holds, by doubling then bisecting.</summary>
    static int Extend(int i, int n, Func<int, bool> fits, int minLength = 1)
    {
        var good = i + 1;
        var step = Math.Max(1, minLength);
        var probe = i + step;
        if (probe >= n) probe = n - 1;
        if (probe > good && !fits(probe)) return good;
        if (probe > good) good = probe;
        var bad = n;
        while (good < n - 1)
        {
            step *= 2;
            probe = Math.Min(n - 1, i + step);
            if (fits(probe)) { good = probe; if (probe == n - 1) break; }
            else { bad = probe; break; }
        }
        while (bad - good > 1)
        {
            var mid = (good + bad) / 2;
            if (fits(mid)) good = mid; else bad = mid;
        }
        return good;
    }

    static bool LineFits(List<Vec2> p, int a, int b, double tol)
    {
        for (var k = a + 1; k < b; k++)
            if (Geometry2D.DistanceToSegment(p[k], p[a], p[b]) > tol) return false;
        return true;
    }

    /// <summary>The best arc through p[a] and p[b] for the points between, or null if it misses any by more than tol.</summary>
    static ArcSegment? ArcFit(List<Vec2> p, int a, int b, double tol)
    {
        if (b - a < 4) return null;
        var s = p[a];
        var e = p[b];
        var chord = e - s;
        var c = chord.Length;
        if (c < 1e-9) return null;
        var m = (s + e) / 2;
        var normal = chord.Perp() / c;

        // Centre on the chord's perpendicular bisector: c(t) = m + t·n. Minimise Σ(|p-c|² - |s-c|²)², linear in t.
        double num = 0, den = 0;
        for (var k = a + 1; k < b; k++)
        {
            var q = p[k];
            var alpha = (q.LengthSquared - s.LengthSquared) - 2 * Vec2.Dot(q - s, m);
            var beta = 2 * Vec2.Dot(q - s, normal);
            num += alpha * beta;
            den += beta * beta;
        }
        if (den < 1e-12) return null;
        var t = num / den;
        var center = m + normal * t;
        var radius = Vec2.Distance(s, center);
        if (radius < Math.Max(1.0, tol) || radius > 1e5) return null;

        // Which side the points bulge to decides the arc's midpoint (minor or major arc).
        var middle = p[(a + b) / 2];
        var side = Vec2.Dot(middle - m, normal) >= 0 ? normal : -normal;
        var mid = center + side * radius;

        // Every point must sit on the circle within tolerance and inside the arc's angular span.
        var arc = new ArcSegment(s, mid, e);
        if (Math.Abs(arc.Sweep) > Math.PI * 1.6) return null;
        for (var k = a + 1; k < b; k++)
        {
            var q = p[k];
            if (Math.Abs(Vec2.Distance(q, center) - radius) > tol) return null;
            if (Vec2.Dot(q - m, side) < -tol) return null; // on the wrong side of the chord
        }
        return arc;
    }

    static List<Segment> MergeLines(List<Segment> segments, double tol)
    {
        if (segments.Count < 2) return segments;
        var merged = new List<Segment> { segments[0] };
        for (var i = 1; i < segments.Count; i++)
        {
            if (merged[^1] is LineSegment prev && segments[i] is LineSegment next)
            {
                var d = Geometry2D.DistanceToSegment(prev.End, prev.Start, next.End);
                if (d <= tol * 0.5)
                {
                    merged[^1] = new LineSegment(prev.Start, next.End);
                    continue;
                }
            }
            merged.Add(segments[i]);
        }
        return merged;
    }

    // ------------------------------------------------------------------ splines (Schneider, "An Algorithm for Automatically Fitting Digitized Curves", Graphics Gems 1990)

    static List<Segment> Splines(List<Vec2> run, double tol, Vec2? startTangent, Vec2? endTangent)
    {
        var result = new List<Segment>();
        if (run.Count == 2)
        {
            result.Add(new LineSegment(run[0], run[1]));
            return result;
        }
        var t1 = startTangent ?? LeftTangent(run, 0, run.Count - 1);
        var t2 = endTangent is { } et ? -et : RightTangent(run, 0, run.Count - 1);
        FitCubic(run, 0, run.Count - 1, t1, t2, tol, result, 0);
        return result;
    }

    static Vec2 LeftTangent(List<Vec2> p, int first, int last)
    {
        var k = Math.Min(last, first + Math.Max(1, Math.Min(4, (last - first) / 3)));
        return (p[k] - p[first]).Normalized();
    }

    static Vec2 RightTangent(List<Vec2> p, int first, int last)
    {
        var k = Math.Max(first, last - Math.Max(1, Math.Min(4, (last - first) / 3)));
        return (p[k] - p[last]).Normalized();
    }

    static Vec2 CenterTangent(List<Vec2> p, int center)
    {
        var a = p[Math.Max(0, center - 2)];
        var b = p[Math.Min(p.Count - 1, center + 2)];
        return (a - b).Normalized();
    }

    static void FitCubic(List<Vec2> p, int first, int last, Vec2 t1, Vec2 t2, double tol, List<Segment> output, int depth)
    {
        var count = last - first + 1;
        if (count == 2 || depth > 24)
        {
            var dist = Vec2.Distance(p[first], p[last]) / 3;
            output.Add(new CubicSegment(p[first], p[first] + t1 * dist, p[last] + t2 * dist, p[last]));
            return;
        }

        var u = ChordLengthParameterize(p, first, last);
        var bezier = Generate(p, first, last, u, t1, t2);
        var (maxError, split) = MaxError(p, first, last, bezier, u);
        if (maxError <= tol)
        {
            output.Add(bezier);
            return;
        }
        if (maxError <= tol * 4)
        {
            for (var iteration = 0; iteration < 6; iteration++)
            {
                u = Reparameterize(p, first, last, u, bezier);
                bezier = Generate(p, first, last, u, t1, t2);
                (maxError, split) = MaxError(p, first, last, bezier, u);
                if (maxError <= tol)
                {
                    output.Add(bezier);
                    return;
                }
            }
        }
        split = Math.Clamp(split, first + 1, last - 1);
        var center = CenterTangent(p, split);
        FitCubic(p, first, split, t1, center, tol, output, depth + 1);
        FitCubic(p, split, last, -center, t2, tol, output, depth + 1);
    }

    static double[] ChordLengthParameterize(List<Vec2> p, int first, int last)
    {
        var u = new double[last - first + 1];
        for (var i = first + 1; i <= last; i++) u[i - first] = u[i - first - 1] + Vec2.Distance(p[i], p[i - 1]);
        var total = u[^1];
        if (total < 1e-12) return u;
        for (var i = 1; i < u.Length; i++) u[i] /= total;
        return u;
    }

    static CubicSegment Generate(List<Vec2> p, int first, int last, double[] u, Vec2 t1, Vec2 t2)
    {
        double c00 = 0, c01 = 0, c11 = 0, x0 = 0, x1 = 0;
        var start = p[first];
        var end = p[last];
        for (var i = 0; i < u.Length; i++)
        {
            var t = u[i];
            var b0 = (1 - t) * (1 - t) * (1 - t);
            var b1 = 3 * t * (1 - t) * (1 - t);
            var b2 = 3 * t * t * (1 - t);
            var b3 = t * t * t;
            var a1 = t1 * b1;
            var a2 = t2 * b2;
            c00 += Vec2.Dot(a1, a1);
            c01 += Vec2.Dot(a1, a2);
            c11 += Vec2.Dot(a2, a2);
            var tmp = p[first + i] - (start * (b0 + b1) + end * (b2 + b3));
            x0 += Vec2.Dot(a1, tmp);
            x1 += Vec2.Dot(a2, tmp);
        }
        var det = c00 * c11 - c01 * c01;
        double alphaL = 0, alphaR = 0;
        if (Math.Abs(det) > 1e-12)
        {
            alphaL = (x0 * c11 - x1 * c01) / det;
            alphaR = (c00 * x1 - c01 * x0) / det;
        }
        var segLength = Vec2.Distance(start, end);
        var epsilon = 1e-6 * segLength;
        if (alphaL < epsilon || alphaR < epsilon)
        {
            // Degenerate least-squares result: fall back to Wu/Barsky's heuristic.
            alphaL = alphaR = segLength / 3;
        }
        return new CubicSegment(start, start + t1 * alphaL, end + t2 * alphaR, end);
    }

    static (double Error, int Split) MaxError(List<Vec2> p, int first, int last, CubicSegment b, double[] u)
    {
        var maxDist = 0.0;
        var split = (first + last) / 2;
        for (var i = first + 1; i < last; i++)
        {
            var d = Vec2.Distance(b.At(u[i - first]), p[i]);
            if (d > maxDist) { maxDist = d; split = i; }
        }
        return (maxDist, split);
    }

    static double[] Reparameterize(List<Vec2> p, int first, int last, double[] u, CubicSegment b)
    {
        var result = new double[u.Length];
        for (var i = 0; i < u.Length; i++) result[i] = NewtonRoot(b, p[first + i], u[i]);
        return result;
    }

    static double NewtonRoot(CubicSegment b, Vec2 point, double u)
    {
        var q = b.At(u);
        // First and second derivatives of the cubic.
        var d1 = new[] { (b.Control1 - b.Start) * 3, (b.Control2 - b.Control1) * 3, (b.End - b.Control2) * 3 };
        var d2 = new[] { (d1[1] - d1[0]) * 2, (d1[2] - d1[1]) * 2 };
        var mu = 1 - u;
        var q1 = d1[0] * (mu * mu) + d1[1] * (2 * mu * u) + d1[2] * (u * u);
        var q2 = d2[0] * mu + d2[1] * u;
        var numerator = Vec2.Dot(q - point, q1);
        var denominator = Vec2.Dot(q1, q1) + Vec2.Dot(q - point, q2);
        if (Math.Abs(denominator) < 1e-12) return u;
        return Math.Clamp(u - numerator / denominator, 0, 1);
    }
}
