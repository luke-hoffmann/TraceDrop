using Tracedrop.Core.Vector;
using Xunit;

namespace Tracedrop.Core.Tests;

public class VectorTests
{
    static float[] Disk(int w, int h, double cx, double cy, double r, double hole = 0)
    {
        var f = new float[w * h];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var d = Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
            f[y * w + x] = d <= r && d >= hole ? 1f : 0f;
        }
        return f;
    }

    [Fact]
    public void Marching_squares_gives_positive_outer_and_negative_hole()
    {
        var loops = MarchingSquares.Trace(Disk(200, 200, 100, 100, 80, 30), 200, 200);
        Assert.Equal(2, loops.Count);
        var areas = loops.Select(Geometry2D.SignedArea).OrderByDescending(a => a).ToList();
        Assert.True(areas[0] > 0, "outer outline should have positive area");
        Assert.True(areas[1] < 0, "hole should have negative area");
        Assert.InRange(areas[0], Math.PI * 80 * 80 * 0.97, Math.PI * 80 * 80 * 1.03);
        Assert.InRange(-areas[1], Math.PI * 30 * 30 * 0.93, Math.PI * 30 * 30 * 1.07);
    }

    [Fact]
    public void Marching_squares_closes_shapes_touching_the_edge()
    {
        var f = new float[50 * 40];
        for (var y = 0; y < 40; y++) for (var x = 0; x < 20; x++) f[y * 50 + x] = 1;
        var loops = MarchingSquares.Trace(f, 50, 40);
        Assert.Single(loops);
        Assert.True(Geometry2D.SignedArea(loops[0]) > 0);
    }

    [Fact]
    public void Round_loop_snaps_to_an_exact_circle()
    {
        var loop = MarchingSquares.Trace(Disk(300, 300, 150.5, 140.25, 97), 300, 300).Single();
        var fitted = Fitter.Fit(loop, true, new FitOptions { SnapCircles = true, Tolerance = 0.8, SmoothSigma = 1 });
        Assert.NotNull(fitted.Circle);
        var (center, radius) = fitted.Circle!.Value;
        Assert.InRange(radius, 96.3, 97.7);
        Assert.InRange(center.X, 150.5 - 0.5 + 0.5 - 0.6, 150.5 + 0.5 + 0.6); // pixel centres sit at +0.5
        Assert.InRange(center.Y, 140.25 + 0.5 - 0.6, 140.25 + 0.5 + 0.6);
    }

    [Fact]
    public void Square_becomes_four_lines_with_sharp_corners()
    {
        var square = new List<Vec2>();
        for (var i = 0; i < 100; i++) square.Add(new Vec2(10 + i, 10));
        for (var i = 0; i < 100; i++) square.Add(new Vec2(110, 10 + i));
        for (var i = 0; i < 100; i++) square.Add(new Vec2(110 - i, 110));
        for (var i = 0; i < 100; i++) square.Add(new Vec2(10, 110 - i));
        var fitted = Fitter.Fit(square, true, new FitOptions { Style = CurveStyle.LinesAndArcs, CornerAngle = 55, Tolerance = 0.5, SmoothSigma = 1.5 });
        Assert.Null(fitted.Circle);
        Assert.Equal(4, fitted.Segments.Count);
        Assert.All(fitted.Segments, s => Assert.IsType<LineSegment>(s));
        foreach (var corner in new[] { new Vec2(10, 10), new Vec2(110, 10), new Vec2(110, 110), new Vec2(10, 110) })
            Assert.Contains(fitted.Segments, s => Vec2.Distance(s.Start, corner) < 1.5);
    }

    [Fact]
    public void Quarter_circle_run_becomes_one_arc()
    {
        var points = Enumerable.Range(0, 91).Select(d => new Vec2(200 + 80 * Math.Cos(d * Math.PI / 180), 200 + 80 * Math.Sin(d * Math.PI / 180))).ToList();
        var fitted = Fitter.Fit(points, false, new FitOptions { Style = CurveStyle.LinesAndArcs, Tolerance = 0.3, SmoothSigma = 0.5 });
        var arc = Assert.IsType<ArcSegment>(Assert.Single(fitted.Segments));
        var (center, radius) = arc.Circle();
        Assert.InRange(radius, 79.5, 80.5);
        Assert.InRange(Math.Abs(arc.Sweep), Math.PI / 2 - 0.05, Math.PI / 2 + 0.05);
        Assert.True(Vec2.Distance(center, new Vec2(200, 200)) < 0.5);
    }

    [Fact]
    public void Arc_flattening_follows_the_arc_in_both_directions()
    {
        foreach (var sign in new[] { 1, -1 })
        {
            var arc = new ArcSegment(new Vec2(100, 0), new Vec2(100 * Math.Cos(Math.PI / 4), sign * 100 * Math.Sin(Math.PI / 4)), new Vec2(0, sign * 100));
            foreach (var p in arc.Flatten(0.1))
            {
                Assert.InRange(p.Length, 99.9, 100.1);
                Assert.True(sign * p.Y >= -1e-6);
            }
        }
    }

    [Fact]
    public void Spline_fit_stays_within_tolerance()
    {
        var points = Enumerable.Range(0, 400).Select(i => new Vec2(i, 50 * Math.Sin(i / 40.0))).ToList();
        var fitted = Fitter.Fit(points, false, new FitOptions { Style = CurveStyle.Splines, Tolerance = 0.5, SmoothSigma = 0.5, CornerAngle = 80 });
        Assert.All(fitted.Segments, s => Assert.IsType<CubicSegment>(s));
        Assert.True(fitted.Segments.Count < 20, $"too many pieces: {fitted.Segments.Count}");
        foreach (var s in fitted.Segments.Cast<CubicSegment>())
            for (var t = 0.0; t <= 1; t += 0.1)
            {
                var p = s.At(t);
                Assert.InRange(p.Y - 50 * Math.Sin(p.X / 40.0), -1.2, 1.2);
            }
    }

    [Fact]
    public void Thick_diagonal_stroke_thins_to_one_unbroken_chain()
    {
        const int w = 200, h = 200;
        var pixels = new byte[w * h];
        // A 5 px wide line at ~30°, which gives Zhang-Suen plenty of staircase steps.
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var d = Math.Abs(Math.Sin(Math.PI / 6) * (x - 20) - Math.Cos(Math.PI / 6) * (y - 30));
            if (d <= 2.5 && x >= 20 && x <= 180) pixels[y * w + x] = 255;
        }
        var chains = Skeleton.Centerlines(pixels, w, h, spurLength: 4, minLength: 5);
        var chain = Assert.Single(chains);
        Assert.False(chain.Closed);
        // End to end it spans the stroke (an 8-connected pixel path is longer than the straight distance).
        Assert.InRange(Vec2.Distance(chain.Points[0], chain.Points[^1]), 160 / Math.Cos(Math.PI / 6) - 8, 160 / Math.Cos(Math.PI / 6) + 2);
    }

    [Fact]
    public void Ring_stroke_thins_to_one_closed_chain()
    {
        const int w = 200, h = 200;
        var pixels = new byte[w * h];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var d = Math.Sqrt((x - 100.0) * (x - 100.0) + (y - 100.0) * (y - 100.0));
            if (Math.Abs(d - 60) <= 2.5) pixels[y * w + x] = 255;
        }
        var chain = Assert.Single(Skeleton.Centerlines(pixels, w, h, 4, 5));
        Assert.True(chain.Closed);
        Assert.InRange(Geometry2D.PolylineLength(chain.Points, true), 2 * Math.PI * 60 * 0.95, 2 * Math.PI * 60 * 1.12);
    }

    [Fact]
    public void Output_units_put_the_drawing_at_the_origin_with_y_up()
    {
        var path = new VectorPath { Segments = { new LineSegment(new Vec2(100, 50), new Vec2(300, 50)), new LineSegment(new Vec2(300, 50), new Vec2(300, 150)) }, Closed = false };
        var drawing = new VectorDrawing(1000, 1000, new List<VectorPath> { path }).ToOutputUnits(0.5);
        Assert.Equal(100, drawing.Width, 6);
        Assert.Equal(50, drawing.Height, 6);
        var first = drawing.Paths[0].Segments[0];
        Assert.Equal(new Vec2(0, 50), first.Start); // top of the picture is the top of the drawing
        Assert.Equal(new Vec2(100, 50), first.End);
        Assert.Equal(new Vec2(100, 0), drawing.Paths[0].Segments[1].End);
    }
}
