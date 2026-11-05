using System.Text;
using System.Xml.Linq;
using netDxf;
using netDxf.Entities;
using OpenCvSharp;
using Tracedrop.Core.Ai;
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
        s.Picks = new() { new PickPoint(595 / 900.0, 180 / 600.0, true) };
        var kept = job.Run(s).Drawing;
        Assert.Equal(2, kept.Paths.Count); // ring outside + inside
        // Now drop the ring's hole instead.
        s.Picks = new() { new PickPoint(520 / 900.0, 180 / 600.0, false) };
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
    public void Teed_edges_trace_when_the_model_is_available()
    {
        var folder = Environment.GetEnvironmentVariable("TRACEDROP_MODELS") ?? Path.Combine(RepoRoot(), "models");
        if (!File.Exists(Path.Combine(folder, ModelNames.Edges))) return; // model not fetched: nothing to check
        using var models = new ModelStore(folder) { PreferGpu = false };
        using var job = new TraceJob(Synthetic.Shapes(), models);
        var result = job.Run(Preset("All edges"));
        Assert.Empty(result.Notes);
        Assert.True(result.Drawing.Paths.Count >= 4);
    }

    static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Tracedrop.sln"))) dir = dir.Parent;
        return dir?.FullName ?? AppContext.BaseDirectory;
    }
}
