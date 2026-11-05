using System.Text;
using System.Xml.Linq;
using netDxf;
using netDxf.Entities;
using OpenCvSharp;
using Tracedrop.Core.Ai;
using Tracedrop.Core.Editing;
using Tracedrop.Core.Export;
using Tracedrop.Core.Imaging;
using Tracedrop.Core.Vector;
using Xunit;

namespace Tracedrop.Core.Tests;

public class PipelineTests
{
    static readonly ModelStore NoModels = new(Path.Combine(Path.GetTempPath(), "tracedrop-no-models-" + Guid.NewGuid().ToString("N"))) { PreferGpu = false };

    static TraceSettings Preset(string name) => Presets.Find(name)!.Settings.Clone();

    [Fact]
    public void Part_preset_finds_plate_holes_and_slot_as_cad_geometry()
    {
        using var job = new TraceJob(Synthetic.Part(), NoModels);
        var result = job.Run(Preset("Part to copy"));
        var d = result.Drawing;

        var circles = d.Paths.Where(p => p.Circle is not null).ToList();
        var outlines = d.Paths.Where(p => p.Role == PathRole.Outer).ToList();
        var holes = d.Paths.Where(p => p.Role == PathRole.Hole).ToList();
        Assert.Single(outlines);
        Assert.Equal(4, holes.Count);
        Assert.Equal(3, circles.Count);
        Assert.All(circles, c => Assert.Equal("HOLES", c.Layer));
        // Radii in picture pixels, through the working scale (1200 → 1600 px).
        var scale = result.Width / 1200.0;
        var radii = circles.Select(c => c.Circle!.Value.Radius / scale).OrderBy(r => r).ToList();
        Assert.InRange(radii[0], 29, 31);
        Assert.InRange(radii[1], 44, 46);
        Assert.InRange(radii[2], 59, 61);
        // The plate is mostly straight lines and four corner arcs: a small number of entities.
        Assert.InRange(outlines[0].Segments.Count, 8, 16);
        Assert.Equal(4, outlines[0].Segments.OfType<ArcSegment>().Count(a => Math.Abs(a.Circle().Radius / scale - 40) < 3));
    }

    [Fact]
    public void Size_is_the_traced_shape_and_offset_grows_it()
    {
        using var job = new TraceJob(Synthetic.Part(), NoModels);
        var s = Preset("Part to copy");
        s.OutputSize = 136; // plate is 680 px wide → 0.2 mm per source pixel
        var plain = job.Run(s).InUnits();
        Assert.InRange(plain.Width, 135.5, 136.5);
        Assert.InRange(plain.Height, 91.5, 92.5);

        s.Offset = 2;
        var grown = job.Run(s).InUnits();
        Assert.InRange(grown.Width, 139.4, 140.6);
        Assert.InRange(grown.Height, 95.4, 96.6);
        var hole = grown.Paths.Where(p => p.Circle is not null).Max(p => p.Circle!.Value.Radius);
        Assert.InRange(hole, 12 - 2 - 0.4, 12 - 2 + 0.4); // 60 px hole = 12 mm radius, shrunk by the 2 mm offset
    }

    [Fact]
    public void Dxf_reloads_with_closed_outlines_arcs_circles_layers_and_units()
    {
        using var job = new TraceJob(Synthetic.Part(), NoModels);
        var result = job.Run(Preset("Part to copy"));
        using var stream = new MemoryStream();
        Exporters.Write(result.InUnits(), OutputUnits.Millimeters, ExportFormat.Dxf, stream);
        stream.Position = 0;
        var doc = DxfDocument.Load(stream);
        Assert.NotNull(doc);
        Assert.Equal(netDxf.Units.DrawingUnits.Millimeters, doc.DrawingVariables.InsUnits);
        var polylines = doc.Entities.Polylines2D.ToList();
        Assert.Equal(2, polylines.Count);
        Assert.All(polylines, p => Assert.True(p.IsClosed));
        Assert.Contains(polylines, p => p.Vertexes.Any(v => Math.Abs(v.Bulge) > 0.01));
        Assert.Equal(3, doc.Entities.Circles.Count());
        Assert.Contains(doc.Layers, l => l.Name == "OUTLINE");
        Assert.Contains(doc.Layers, l => l.Name == "HOLES");
    }

    [Fact]
    public void Splines_export_as_dxf_spline_entities()
    {
        using var job = new TraceJob(Synthetic.Shapes(), NoModels);
        var s = Preset("Logo / artwork");
        s.Curves = CurveStyle.Splines;
        s.SnapCircles = false;
        var result = job.Run(s);
        using var stream = new MemoryStream();
        Exporters.Write(result.InUnits(), OutputUnits.Millimeters, ExportFormat.Dxf, stream);
        stream.Position = 0;
        var doc = DxfDocument.Load(stream);
        Assert.True(doc.Entities.Splines.Any());
    }

    [Fact]
    public void Svg_and_pdf_are_well_formed_and_true_size()
    {
        using var job = new TraceJob(Synthetic.Part(), NoModels);
        var units = job.Run(Preset("Part to copy")).InUnits();

        using var svg = new MemoryStream();
        Exporters.Write(units, OutputUnits.Millimeters, ExportFormat.Svg, svg);
        var xml = XDocument.Parse(Encoding.UTF8.GetString(svg.ToArray()));
        var root = xml.Root!;
        Assert.EndsWith("mm", root.Attribute("width")!.Value);
        Assert.True(root.Descendants().Count(e => e.Name.LocalName == "path") >= 5);

        using var pdf = new MemoryStream();
        Exporters.Write(units, OutputUnits.Millimeters, ExportFormat.Pdf, pdf);
        var text = Encoding.ASCII.GetString(pdf.ToArray());
        Assert.StartsWith("%PDF-1.4", text);
        Assert.Contains("/MediaBox [0 0 283.", text); // 100 mm = 283.46 pt
        Assert.EndsWith("%%EOF\n", text);
    }

    [Fact]
    public void Shapes_mode_keeps_the_ring_hole_and_snaps_both_circles()
    {
        using var job = new TraceJob(Synthetic.Shapes(), NoModels);
        var d = job.Run(Preset("Logo / artwork")).Drawing;
        Assert.Equal(3, d.Paths.Count(p => p.Role == PathRole.Outer));
        Assert.Single(d.Paths, p => p.Role == PathRole.Hole);
        Assert.Equal(2, d.Paths.Count(p => p.Circle is not null));
    }

    [Fact]
    public void Colour_layers_split_by_colour_and_skip_the_background()
    {
        var image = new Mat(400, 600, MatType.CV_8UC3, Scalar.All(255));
        Cv2.Rectangle(image, new Rect(40, 40, 150, 150), new Scalar(200, 60, 20), -1);   // blue
        Cv2.Rectangle(image, new Rect(240, 40, 150, 150), new Scalar(30, 30, 210), -1);  // red
        Cv2.Rectangle(image, new Rect(440, 40, 120, 300), new Scalar(20, 20, 20), -1);   // black
        using var job = new TraceJob(new LoadedImage(image, null), NoModels);
        var s = Preset("Logo / artwork");
        s.Colors = 4;
        var d = job.Run(s).Drawing;
        Assert.Equal(3, d.Paths.Select(p => p.Layer).Distinct().Count());
        Assert.All(d.Paths, p => Assert.StartsWith("COLOR_", p.Layer));
    }

    [Fact]
    public void Line_art_turns_pen_strokes_into_unbroken_centerlines()
    {
        using var job = new TraceJob(Synthetic.Sketch(), NoModels);
        var d = job.Run(Preset("Sketch / drawing")).Drawing;
        Assert.Equal(1, d.ClosedCount);
        Assert.Equal(1, d.OpenCount);
        Assert.All(d.Paths, p => Assert.Equal(PathRole.Stroke, p.Role));
    }

    [Fact]
    public void Picks_keep_one_shape_and_right_click_in_a_hole_fills_it()
    {
        using var job = new TraceJob(Synthetic.Shapes(), NoModels);
        var s = Preset("Logo / artwork");
        // Keep only the ring (click on its body at x=520+75).
        s.Picks = new() { new PickPoint(595 / 900.0, 180 / 600.0, PickKind.Keep) };
        var kept = job.Run(s).Drawing;
        Assert.Equal(2, kept.Paths.Count); // ring outside + inside
        // Now drop the ring's hole instead.
        s.Picks = new() { new PickPoint(520 / 900.0, 180 / 600.0, PickKind.Remove) };
        var filled = job.Run(s).Drawing;
        Assert.DoesNotContain(filled.Paths, p => p.Role == PathRole.Hole);
        Assert.Equal(3, filled.Paths.Count);
    }

    [Fact]
    public void Fit_only_changes_reuse_the_cached_detection()
    {
        using var job = new TraceJob(Synthetic.Part(), NoModels);
        var s = Preset("Object / tool");
        job.Run(s);
        s.Smoothness = 80;
        var again = job.Run(s);
        Assert.False(again.StageMs.ContainsKey("detect"));
        Assert.False(again.StageMs.ContainsKey("trace"));
    }

    [Fact]
    public void Transparent_png_traces_its_alpha_as_the_silhouette()
    {
        var bgra = new Mat(300, 300, MatType.CV_8UC4, new Scalar(255, 255, 255, 0));
        Cv2.Circle(bgra, new OpenCvSharp.Point(150, 150), 100, new Scalar(250, 250, 250, 255), -1, LineTypes.AntiAlias); // nearly white on transparent
        var png = bgra.ImEncode(".png");
        using var job = new TraceJob(ImageLoader.Load(png, ".png"), NoModels);
        var d = job.Run(Preset("Object / tool")).Drawing;
        var circle = Assert.Single(d.Paths);
        Assert.NotNull(circle.Circle);
    }

    [Fact]
    public void Exif_orientation_is_applied()
    {
        using var landscape = new Mat(100, 200, MatType.CV_8UC3, Scalar.All(255));
        var jpeg = landscape.ImEncode(".jpg");
        // Insert an APP1 Exif segment with Orientation = 6 (rotate 90° clockwise to view) after SOI.
        byte[] tiff =
        {
            (byte)'I', (byte)'I', 42, 0, 8, 0, 0, 0,     // little-endian TIFF header, IFD at 8
            1, 0,                                          // one entry
            0x12, 0x01, 3, 0, 1, 0, 0, 0, 6, 0, 0, 0,      // 0x0112 SHORT x1 = 6
            0, 0, 0, 0                                     // no next IFD
        };
        var payload = Encoding.ASCII.GetBytes("Exif\0\0").Concat(tiff).ToArray();
        var length = payload.Length + 2;
        var app1 = new byte[] { 0xFF, 0xE1, (byte)(length >> 8), (byte)length }.Concat(payload);
        var bytes = jpeg.Take(2).Concat(app1).Concat(jpeg.Skip(2)).ToArray();

        Assert.Equal(6, ImageLoader.ReadExifOrientation(bytes));
        using var loaded = ImageLoader.Load(bytes, ".jpg");
        Assert.Equal(100, loaded.Width);
        Assert.Equal(200, loaded.Height);
    }

    [Fact]
    public void Garbage_is_rejected_with_a_clear_message()
    {
        var ex = Assert.Throws<InvalidDataException>(() => ImageLoader.Load(Encoding.UTF8.GetBytes("not a picture at all"), ".png"));
        Assert.Contains("isn't a picture", ex.Message);
    }

    [Fact]
    public void Showing_a_traced_picture_again_redoes_no_work()
    {
        using var job = new TraceJob(Synthetic.Part(), NoModels);
        var s = Preset("Object / tool");
        job.Run(s);
        var again = job.Run(s);
        Assert.DoesNotContain(again.StageMs, kv => kv.Value >= 5); // everything came from the cache
    }

    [Fact]
    public void Full_size_picture_is_let_go_and_reloaded_when_resolution_changes()
    {
        var reloads = 0;
        using var job = new TraceJob(Synthetic.Part(), NoModels, () => { reloads++; return Synthetic.Part(); });
        var s = Preset("Object / tool");
        job.Run(s);
        var cachedOnly = job.EstimatedBytes;
        Assert.InRange(cachedOnly, 1, 200L << 20);
        Assert.Equal(0, reloads);

        s.WorkingSize = 1024;
        var smaller = job.Run(s);
        Assert.Equal(1, reloads);
        Assert.Equal(1024, Math.Max(smaller.Width, smaller.Height));
        Assert.Equal(1200, job.SourceWidth);
    }

    static GuideRegion Circle(RegionKind kind, double x, double y, double r, int amount = 0) => new()
    {
        Kind = kind, Shape = RegionShape.Circle, Points = { new NormPoint(x, y) }, Size = r, Amount = amount
    };

    static int PathsNear(VectorDrawing d, double scale, double x, double y, double dx, double dy) =>
        d.Paths.Count(p => !p.IsManual && Dense(p).Any(v => Math.Abs(v.X / scale - x) < dx && Math.Abs(v.Y / scale - y) < dy));

    /// <summary>Points every ~2 px along the path (Flatten gives only the ends of straight lines).</summary>
    static IEnumerable<Vec2> Dense(VectorPath p)
    {
        var pts = p.Flatten(1);
        for (var i = 0; i + 1 < pts.Count; i++)
        {
            var steps = Math.Max(1, (int)(Vec2.Distance(pts[i], pts[i + 1]) / 2));
            for (var k = 0; k < steps; k++) yield return pts[i] + (pts[i + 1] - pts[i]) * (k / (double)steps);
        }
        if (pts.Count > 0) yield return pts[^1];
    }

    /// <summary>A strong frame plus faint "glasses" (5 grey levels of contrast) inside it.</summary>
    static Mat FaintGlasses()
    {
        var image = new Mat(600, 800, MatType.CV_8UC3, Scalar.All(235));
        Cv2.Rectangle(image, new Rect(100, 100, 600, 400), Scalar.All(30), 5);
        foreach (var cx in new[] { 330, 470 })
            Cv2.Circle(image, new OpenCvSharp.Point(cx, 300), 45, Scalar.All(230), 3, LineTypes.AntiAlias);
        return image;
    }

    [Fact]
    public void A_drawn_rectangle_and_circles_trace_as_single_closed_paths()
    {
        var image = FaintGlasses();
        foreach (var cx in new[] { 330, 470 })
            Cv2.Circle(image, new OpenCvSharp.Point(cx, 300), 45, Scalar.All(40), 3, LineTypes.AntiAlias);
        using var job = new TraceJob(new LoadedImage(image, null), NoModels);
        var d = job.Run(Preset("Sketch / drawing")).Drawing;
        Assert.Equal(3, d.Paths.Count);
        Assert.All(d.Paths, p => Assert.True(p.Closed || p.Circle is not null));
    }

    [Fact]
    public void Crossing_lines_meet_at_one_point()
    {
        // A thick X and a plus: each should trace as two straight lines crossing (or four arms meeting), never with a
        // short bridge piece between two junctions.
        var mask = new Mat(400, 400, MatType.CV_8U, Scalar.All(0));
        Cv2.Line(mask, new OpenCvSharp.Point(40, 40), new OpenCvSharp.Point(360, 360), Scalar.All(255), 11);
        Cv2.Line(mask, new OpenCvSharp.Point(360, 40), new OpenCvSharp.Point(40, 360), Scalar.All(255), 11);
        mask.GetArray(out byte[] px);
        var chains = Skeleton.Centerlines(px, 400, 400, 5, 8);
        Assert.InRange(chains.Count, 2, 4);
        Assert.All(chains, c => Assert.True(Geometry2D.PolylineLength(c.Points, false) > 100));
        var ends = chains.SelectMany(c => new[] { c.Points[0], c.Points[^1] }).Where(p => Vec2.Distance(p, new Vec2(200.5, 200.5)) < 20).ToList();
        Assert.All(ends, e => Assert.True(Vec2.Distance(e, ends[0]) < 1e-6)); // all arms end at the same point
    }

    [Fact]
    public void Find_lines_here_traces_faint_detail_inside_the_area_and_leaves_the_rest_alone()
    {
        var s = Preset("Sketch / drawing");
        s.Threshold = 40;
        using var job = new TraceJob(new LoadedImage(FaintGlasses(), null), NoModels);
        var before = job.Run(s);
        var scale = before.Width / 800.0;
        Assert.Equal(0, PathsNear(before.Drawing, scale, 400, 300, 130, 60));
        var frame = PathsNear(before.Drawing, scale, 400, 100, 200, 10);

        s.Regions = new() { Circle(RegionKind.Find, 0.5, 0.5, 0.2, amount: 4) };
        var after = job.Run(s);
        Assert.True(PathsNear(after.Drawing, scale, 400, 300, 130, 60) >= 2, $"found {PathsNear(after.Drawing, scale, 400, 300, 130, 60)} paths at the glasses");
        Assert.Equal(frame, PathsNear(after.Drawing, scale, 400, 100, 200, 10)); // outside the area nothing changed
    }

    [Fact]
    public void Erase_lines_here_removes_lines_and_cuts_shapes()
    {
        // Line mode: the left half of the frame's top edge goes, the right half stays.
        var s = Preset("Sketch / drawing");
        using var job = new TraceJob(new LoadedImage(FaintGlasses(), null), NoModels);
        var before = job.Run(s);
        var scale = before.Width / 800.0;
        Assert.True(PathsNear(before.Drawing, scale, 200, 100, 60, 10) > 0);
        s.Regions = new() { Circle(RegionKind.Erase, 200 / 800.0, 100 / 600.0, 0.08) };
        var after = job.Run(s);
        Assert.Equal(0, PathsNear(after.Drawing, scale, 200, 100, 50, 10));
        Assert.True(PathsNear(after.Drawing, scale, 600, 100, 60, 10) > 0);

        // Shape mode: erasing inside the plate cuts a new round hole in it.
        using var shapes = new TraceJob(Synthetic.Part(), NoModels);
        var part = Preset("Part to copy");
        var whole = shapes.Run(part);
        part.Regions = new() { Circle(RegionKind.Erase, 420 / 1200.0, 470 / 900.0, 0.04) };
        var cut = shapes.Run(part);
        Assert.Equal(whole.Drawing.Paths.Count(p => p.Role == PathRole.Hole) + 1, cut.Drawing.Paths.Count(p => p.Role == PathRole.Hole));
    }

    [Fact]
    public void A_deleted_line_stays_deleted_when_the_settings_change()
    {
        var s = Preset("Sketch / drawing");
        using var job = new TraceJob(Synthetic.Sketch(), NoModels);
        var before = job.Run(s);
        var victim = before.Drawing.Paths.OrderByDescending(p => p.Flatten(1).Count).First();
        var points = victim.Flatten(1);
        s.Regions = new() { EditGeometry.SuppressAlong(points, before.Width, before.Height, 4) };

        var after = job.Run(s);
        Assert.Equal(before.Drawing.Paths.Count - 1, after.Drawing.Paths.Count);
        s.Smoothness += 10;
        s.Detail += 5;
        var again = job.Run(s);
        Assert.DoesNotContain(again.Drawing.Paths, p => EditGeometry.FractionInside(p.Flatten(1), v => points.Any(q => Vec2.Distance(q, v) < 3)) > 0.8);
    }

    [Fact]
    public void Own_paths_are_added_exactly_as_drawn_and_exported()
    {
        var s = Preset("Part to copy");
        using var job = new TraceJob(Synthetic.Part(), NoModels);
        var plain = job.Run(s);
        s.Manual = new()
        {
            new ManualPath { Nodes = { new(new NormPoint(0.1, 0.1)), new(new NormPoint(0.9, 0.1)) } },
            new ManualPath
            {
                Closed = true,
                Nodes =
                {
                    new(new NormPoint(0.2, 0.2), Out: new NormPoint(0.3, 0.15)),
                    new(new NormPoint(0.4, 0.2), In: new NormPoint(0.35, 0.15)),
                    new(new NormPoint(0.3, 0.4))
                }
            }
        };
        var result = job.Run(s);
        Assert.Equal(plain.Drawing.Paths.Count + 2, result.Drawing.Paths.Count);
        var mine = result.Drawing.Paths.Where(p => p.IsManual).OrderBy(p => p.ManualIndex).ToList();
        Assert.Equal(new[] { 0, 1 }, mine.Select(p => p.ManualIndex));
        var line = Assert.IsType<LineSegment>(Assert.Single(mine[0].Segments));
        Assert.Equal(0.1 * result.Width, line.Start.X, 6);
        Assert.Equal(0.9 * result.Width, line.End.X, 6);
        Assert.IsType<CubicSegment>(mine[1].Segments[0]);
        Assert.Equal(3, mine[1].Segments.Count);
        Assert.True(mine[1].Closed);

        // Adding a path is instant: no stage before "fit" runs again.
        s.Manual.Add(new ManualPath { Nodes = { new(new NormPoint(0.5, 0.5)), new(new NormPoint(0.6, 0.6)) } });
        var more = job.Run(s);
        Assert.Equal(plain.Drawing.Paths.Count + 3, more.Drawing.Paths.Count);
        Assert.True(more.StageMs.Where(kv => kv.Key != "fit").All(kv => kv.Value < 5), string.Join(", ", more.StageMs));

        var dxf = Path.Combine(Path.GetTempPath(), "tracedrop-manual-" + Guid.NewGuid().ToString("N") + ".dxf");
        try
        {
            Exporters.WriteFile(result.InUnits(), s.Units, ExportFormat.Dxf, dxf);
            var doc = DxfDocument.Load(dxf);
            Assert.NotNull(doc);
        }
        finally
        {
            File.Delete(dxf);
        }
    }

    [Fact]
    public void Taking_over_a_traced_path_keeps_its_shape()
    {
        var circle = new VectorPath { Circle = (new Vec2(100, 80), 30), Closed = true, Role = PathRole.Outer, Segments = new() };
        var m = ManualPath.FromVectorPath(circle, 200, 160);
        Assert.Equal(4, m.Nodes.Count);
        Assert.True(m.Closed);
        var back = m.ToVectorPath(200, 160, 0);
        Assert.All(back.Flatten(0.5), v => Assert.InRange(Vec2.Distance(v, new Vec2(100, 80)), 29.8, 30.2));

        var poly = new VectorPath
        {
            Closed = true, Role = PathRole.Outer,
            Segments = new() { new LineSegment(new Vec2(10, 10), new Vec2(50, 10)), new LineSegment(new Vec2(50, 10), new Vec2(50, 40)), new LineSegment(new Vec2(50, 40), new Vec2(10, 10)) }
        };
        var p = ManualPath.FromVectorPath(poly, 200, 160);
        Assert.Equal(3, p.Nodes.Count);
        Assert.All(p.Nodes, n => Assert.True(n.In is null && n.Out is null));
        Assert.All(p.ToVectorPath(200, 160, 0).Segments, seg => Assert.IsType<LineSegment>(seg));
    }
}
