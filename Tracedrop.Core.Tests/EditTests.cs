using Tracedrop.Core.Editing;
using Tracedrop.Core.Vector;
using Xunit;

namespace Tracedrop.Core.Tests;

public class EditTests
{
    static readonly ManualPath Curve = new()
    {
        Nodes =
        {
            new(new NormPoint(0.1, 0.5), Out: new NormPoint(0.2, 0.1)),
            new(new NormPoint(0.9, 0.5), In: new NormPoint(0.8, 0.1))
        }
    };

    static List<Vec2> Sample(ManualPath p) => p.ToVectorPath(1000, 1000, 0).Flatten(0.2);

    static double MaxDeviation(ManualPath a, ManualPath b)
    {
        var pa = Sample(a);
        var pb = Sample(b);
        static double ToSegment(Vec2 p, Vec2 a, Vec2 b)
        {
            var ab = b - a;
            var t = ab.LengthSquared < 1e-12 ? 0 : Math.Clamp(Vec2.Dot(p - a, ab) / ab.LengthSquared, 0, 1);
            return Vec2.Distance(p, a + ab * t);
        }
        return pb.Max(q => Enumerable.Range(0, pa.Count - 1).Min(i => ToSegment(q, pa[i], pa[i + 1])));
    }

    [Fact]
    public void Adding_a_point_keeps_the_curve_exactly()
    {
        var split = PathEdits.InsertNode(Curve, 0, 0.3);
        Assert.Equal(3, split.Nodes.Count);
        Assert.True(MaxDeviation(Curve, split) < 0.6, $"moved {MaxDeviation(Curve, split):0.00} px");
        var line = new ManualPath { Nodes = { new(new NormPoint(0, 0)), new(new NormPoint(1, 1)) } };
        var mid = PathEdits.InsertNode(line, 0, 0.5);
        Assert.Equal(new NormPoint(0.5, 0.5), mid.Nodes[1].Point);
        Assert.Null(mid.Nodes[1].In);
    }

    [Fact]
    public void Moving_a_point_carries_its_handles_and_removing_keeps_two()
    {
        var moved = PathEdits.MoveNode(Curve, 0, 0.05, 0);
        Assert.Equal(0.15, moved.Nodes[0].Point.X, 9);
        Assert.Equal(0.25, moved.Nodes[0].Out!.X, 9);
        Assert.Null(PathEdits.RemoveNode(Curve, 0));
        var three = PathEdits.InsertNode(Curve, 0, 0.5);
        Assert.Equal(2, PathEdits.RemoveNode(three, 1)!.Nodes.Count);
    }

    [Fact]
    public void Smooth_points_keep_handles_opposite_unless_alt()
    {
        var smooth = PathEdits.ToggleSmooth(new ManualPath
        {
            Nodes = { new(new NormPoint(0, 0)), new(new NormPoint(0.5, 0.5)), new(new NormPoint(1, 0)) }
        }, 1);
        Assert.NotNull(smooth.Nodes[1].In);
        var turned = PathEdits.SetHandle(smooth, 1, outHandle: true, new NormPoint(0.5, 0.7), independent: false);
        var n = turned.Nodes[1];
        Assert.Equal(0.5, n.In!.X, 9);      // straight up, opposite of the out handle pointing down
        Assert.True(n.In.Y < 0.5);
        var broken = PathEdits.SetHandle(smooth, 1, outHandle: true, new NormPoint(0.5, 0.7), independent: true);
        Assert.Equal(smooth.Nodes[1].In, broken.Nodes[1].In);
        Assert.Null(PathEdits.ToggleSmooth(smooth, 1).Nodes[1].In);
    }

    [Fact]
    public void Closest_point_finds_the_segment_under_the_mouse()
    {
        var (segment, t, distance) = PathEdits.Closest(Curve, 1000, 1000, new Vec2(500, 205));
        Assert.Equal(0, segment);
        Assert.InRange(t, 0.4, 0.6);
        Assert.True(distance < 10);
    }
}
