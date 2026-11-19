using OpenCvSharp;
using Tracedrop.Core.Ai;
using Tracedrop.Core.Editing;
using Tracedrop.Core.Export;
using Tracedrop.Core.Paper;
using Tracedrop.Core.Gridfinity;
using Tracedrop.Core.Vector;
using Xunit;

namespace Tracedrop.Core.Tests;

public class GridfinityTests
{
    static ToolShape Rect(int index, double w, double h, double ax = 0.5, double ay = 0.5) => new()
    {
        Index = index,
        Name = $"Tool {index + 1}",
        Outline = new() { new(-w / 2, -h / 2), new(w / 2, -h / 2), new(w / 2, h / 2), new(-w / 2, h / 2) },
        Anchor = new NormPoint(ax, ay)
    };

    static ToolShape Round(int index, double r, double ax, double ay) => new()
    {
        Index = index,
        Name = $"Tool {index + 1}",
        Outline = Enumerable.Range(0, 64).Select(i => new Vec2(r * Math.Cos(i * Math.PI / 32), r * Math.Sin(i * Math.PI / 32))).ToList(),
        Anchor = new NormPoint(ax, ay)
    };

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Empty_bin_is_watertight_and_true_to_the_standard(bool lip, bool magnets)
    {
        var settings = new BinSettings { GridX = 2, GridY = 1, HeightUnits = 3, StackingLip = lip, Magnets = magnets };
        var design = Designer.Design(new List<ToolShape>(), settings, new BinLayout());
        var mesh = BinMesh.Build(design);
        Assert.Equal(0, mesh.InventedVertices);
        Assert.Equal(0, mesh.BadEdges());
        var (min, max) = mesh.Bounds();
        Assert.Equal(0, min.X, 6);
        Assert.Equal(83.5, max.X, 6);
        Assert.Equal(41.5, max.Y, 6);
        Assert.Equal(lip ? 21 + Spec.LipHeight : 21, max.Z, 6);
        Assert.True(mesh.Volume() > 0);
    }

    [Fact]
    public void Tools_are_arranged_into_the_smallest_bin_and_pocketed()
    {
        var tools = new List<ToolShape> { Rect(0, 150, 20, 0.3, 0.3), Round(1, 12, 0.7, 0.3), Rect(2, 60, 15, 0.5, 0.7) };
        var settings = new BinSettings { Magnets = true };
        var design = Designer.Design(tools, settings, new BinLayout());
        Assert.Equal(3, design.Tools.Count);
        Assert.True(design.GridX * design.GridY <= 12, $"{design.GridX}x{design.GridY}");
        Assert.True(Math.Max(design.Width, design.Depth) >= 150);
        // Pockets stay inside the walls and apart from each other (by their real outlines, not their boxes).
        AssertPocketsApart(design);

        var plain = BinMesh.Build(Designer.Design(new List<ToolShape>(), new BinSettings { GridX = design.GridX, GridY = design.GridY, HeightUnits = design.HeightUnits, Magnets = true }, new BinLayout()));
        var mesh = BinMesh.Build(design);
        Assert.Equal(0, mesh.InventedVertices);
        Assert.Equal(0, mesh.BadEdges());
        // The pockets take out at least the tools' own volume.
        var removed = plain.Volume() - mesh.Volume();
        var toolVolume = tools.Sum(t => t.Area) * settings.Depth;
        Assert.InRange(removed, toolVolume, toolVolume * 3.5); // plus clearance and finger slots
    }

    [Fact]
    public void Placed_tools_stay_put_and_labels_and_depths_stay_watertight()
    {
        var tools = new List<ToolShape> { Rect(0, 40, 20, 0.2, 0.5), Round(1, 10, 0.8, 0.5) };
        var settings = new BinSettings { GridX = 2, GridY = 2 };
        var layout = new BinLayout
        {
            Tools =
            {
                new ToolPlacement { Anchor = new NormPoint(0.21, 0.5), X = 30, Y = 60, Rotation = 30, Depth = 22 },
                new ToolPlacement { Anchor = new NormPoint(0.79, 0.5), X = 60, Y = 25, FingerHoles = new() { new FingerHole(10, 0, 18) } }
            },
            Labels = { new BinLabel("M3 HEX 2.5", 41.75, 10, 5) }
        };
        var design = Designer.Design(tools, settings, layout);
        Assert.Equal(30, design.Tools[0].Placement.X);
        Assert.Equal(22, design.Tools[0].Depth);
        Assert.Single(design.Tools[1].FingerHoles);
        var mesh = BinMesh.Build(design);
        Assert.Equal(0, mesh.InventedVertices);
        Assert.Equal(0, mesh.BadEdges());
        Assert.True(design.HeightUnits * 7 >= 22 + Spec.FootHeight + Spec.MinFloor);
    }

    static readonly ModelStore NoModels = new(Path.Combine(Path.GetTempPath(), "tracedrop-no-models-" + Guid.NewGuid().ToString("N"))) { PreferGpu = false };

    [Fact]
    public void The_sheet_of_paper_is_found_to_a_fraction_of_a_millimetre()
    {
        using var photo = Synthetic.ToolsOnPaper(out var truth, out _);
        var found = PaperDetector.Detect(photo.Bgr);
        Assert.NotNull(found);
        for (var i = 0; i < 4; i++)
        {
            var d = Math.Sqrt(Math.Pow(found![i].X - truth[i].X, 2) + Math.Pow(found[i].Y - truth[i].Y, 2));
            Assert.True(d < 4, $"corner {i} off by {d:0.0} px"); // ~6 px per mm on this photo
        }
        Assert.Equal(PaperSize.Letter, Papers.Resolve(PaperSize.Auto, found!));
    }

    [Fact]
    public void A_photo_of_tools_on_paper_becomes_a_true_size_watertight_bin()
    {
        using var job = new TraceJob(Synthetic.ToolsOnPaper(out _, out var areas), NoModels);
        var settings = Presets.Find("Gridfinity bin")!.Settings.Clone();
        var result = job.Run(settings);
        Assert.True(result.MillimetresPerPixel > 0);
        Assert.True(result.PaperFoundAutomatically);
        Assert.Equal(PaperSize.Letter, result.PaperSize);

        var tools = Designer.ExtractTools(result.Drawing, result.MillimetresPerPixel);
        Assert.Equal(3, tools.Count);
        var found = tools.Select(t => t.Area).OrderBy(a => a).ToList();
        var expected = areas.OrderBy(a => a).ToList();
        Assert.True(found.Zip(expected).All(f => Math.Abs(f.First - f.Second) / f.Second < 0.04),
            "areas " + string.Join(", ", found.Zip(expected).Select(f => $"{f.First:0} vs {f.Second:0} mm²")));
        var bar = tools.OrderByDescending(t => Math.Max(t.Width, t.Height)).First();
        Assert.InRange(Math.Max(bar.Width, bar.Height), 118.5, 121.5);

        var design = Designer.Design(tools, settings.Bin, settings.Layout);
        Assert.Equal(3, design.Tools.Count);
        Assert.True(Math.Max(design.Width, design.Depth) > 120);
        var mesh = BinMesh.Build(design);
        Assert.Equal(0, mesh.BadEdges());
        Assert.Equal(0, mesh.InventedVertices);

        // Writes: STL, 3MF and the flat layout.
        var dir = Path.Combine(Path.GetTempPath(), "tracedrop-bin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            BinExport.WriteMeshFile(mesh, ExportFormat.Stl, Path.Combine(dir, "bin.stl"));
            Assert.Equal(84 + 50L * mesh.TriangleCount, new FileInfo(Path.Combine(dir, "bin.stl")).Length);
            BinExport.WriteMeshFile(mesh, ExportFormat.ThreeMf, Path.Combine(dir, "bin.3mf"));
            using (var zip = System.IO.Compression.ZipFile.OpenRead(Path.Combine(dir, "bin.3mf")))
            {
                var model = zip.GetEntry("3D/3dmodel.model");
                Assert.NotNull(model);
                using var reader = new StreamReader(model!.Open());
                var xml = System.Xml.Linq.XDocument.Parse(reader.ReadToEnd());
                Assert.Equal(mesh.TriangleCount, xml.Descendants().Count(e => e.Name.LocalName == "triangle"));
            }
            Exporters.WriteFile(BinExport.Layout(design), OutputUnits.Millimeters, ExportFormat.Dxf, Path.Combine(dir, "layout.dxf"));
            Assert.NotNull(netDxf.DxfDocument.Load(Path.Combine(dir, "layout.dxf")));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Corners_set_by_hand_override_the_found_sheet()
    {
        using var job = new TraceJob(Synthetic.ToolsOnPaper(out var truth, out _), NoModels);
        var settings = Presets.Find("Gridfinity bin")!.Settings.Clone();
        var auto = job.Run(settings);
        // Hand-set corners 2% inside the true sheet: the scale changes accordingly.
        var cx = truth.Average(p => p.X);
        var cy = truth.Average(p => p.Y);
        settings.PaperCorners = truth.Select(p => new NormPoint((cx + (p.X - cx) * 0.98) / 3000.0, (cy + (p.Y - cy) * 0.98) / 2250.0)).ToList();
        var manual = job.Run(settings);
        Assert.False(manual.PaperFoundAutomatically);
        double Bar(TraceResult r) => Designer.ExtractTools(r.Drawing, r.MillimetresPerPixel).Max(t => Math.Max(t.Width, t.Height));
        // A smaller sheet in the photo means everything on it measures bigger, by the same 2%.
        var ratio = Bar(manual) / Bar(auto);
        Assert.InRange(ratio, 1.01, 1.03);
    }

    [Fact]
    public void An_automatic_bin_grows_when_a_tool_is_pushed_past_its_wall()
    {
        var tools = new List<ToolShape> { Rect(0, 60, 20, 0.3, 0.5) };
        var settings = new BinSettings { FingerSlots = false };
        var layout = new BinLayout { Tools = { new ToolPlacement { Anchor = new NormPoint(0.3, 0.5), X = 10, Y = 20 } } };
        var design = Designer.Design(tools, settings, layout);
        var b = Designer.Bounds(design.Tools[0], settings);
        Assert.True(b.Min.X >= design.Wall - 1e-6, $"min x {b.Min.X}");
        Assert.True(b.Max.X <= design.Width - design.Wall + 1e-6);
        Assert.Empty(design.Notes);
    }

    [Fact]
    public void Tools_lie_along_their_own_axis()
    {
        // A "screwdriver": wide handle and a thin shaft off centre to one side of the handle's outline, turned 23°.
        var outline = new List<Vec2> { new(0, -18), new(90, -14), new(95, -4), new(200, -3), new(200, 3), new(95, 4), new(90, 14), new(0, 18) };
        var a = 23 * Math.PI / 180;
        var turned = outline.Select(p => new Vec2(p.X * Math.Cos(a) - p.Y * Math.Sin(a), p.X * Math.Sin(a) + p.Y * Math.Cos(a))).ToList();
        var lying = Designer.LyingAngle(turned);
        var off = ((lying + 23) % 180 + 180) % 180; // turning by `lying` undoes the 23°
        Assert.True(off < 0.6 || off > 179.4, $"lying {lying:0.0}°");
    }

    static void AssertPocketsApart(BinDesign design)
    {
        var settings = design.Settings;
        var interior = new Clipper2Lib.Paths64 { Designer.ToPath(new[] {
            new Vec2(design.Wall - 0.01, design.Wall - 0.01), new Vec2(design.Width - design.Wall + 0.01, design.Wall - 0.01),
            new Vec2(design.Width - design.Wall + 0.01, design.Depth - design.Wall + 0.01), new Vec2(design.Wall - 0.01, design.Depth - design.Wall + 0.01) }) };
        var pockets = design.Tools.Select(t => Designer.Pocket(t, settings)).ToList();
        foreach (var p in pockets)
        {
            var outside = Clipper2Lib.Clipper.Difference(p, interior, Clipper2Lib.FillRule.NonZero);
            Assert.True(Clipper2Lib.Clipper.Area(outside) / 1e6 < 1, $"pocket outside the wall by {Clipper2Lib.Clipper.Area(outside) / 1e6:0.0} mm²");
        }
        for (var i = 0; i < pockets.Count; i++)
            for (var j = i + 1; j < pockets.Count; j++)
            {
                // At least most of the spacing between any two pockets.
                var grown = Clipper2Lib.Clipper.InflatePaths(pockets[i], (settings.Spacing * 0.45) * Designer.Scale, Clipper2Lib.JoinType.Round, Clipper2Lib.EndType.Polygon);
                var overlap = Clipper2Lib.Clipper.Intersect(grown, pockets[j], Clipper2Lib.FillRule.NonZero);
                Assert.True(Clipper2Lib.Clipper.Area(overlap) / 1e6 < 0.5, $"pockets {i} and {j} closer than the spacing");
            }
    }

    /// <summary>A screwdriver-like outline lying along X: handle (fat end) and a thin shaft.</summary>
    static ToolShape Screwdriver(int index, double handle, double length, double width, double ax)
    {
        var shaft = width * 0.2;
        var pts = new List<Vec2>
        {
            new(0, -width / 2), new(handle * 0.8, -width / 2), new(handle, -width * 0.3), new(handle, -shaft / 2),
            new(length, -shaft / 2), new(length, shaft / 2), new(handle, shaft / 2), new(handle, width * 0.3),
            new(handle * 0.8, width / 2), new(0, width / 2)
        };
        // Stood up as in a photo (shaft pointing up), centred on its box.
        var stood = pts.Select(p => new Vec2(p.Y, p.X - length / 2)).ToList();
        return new ToolShape { Index = index, Name = $"Tool {index + 1}", Outline = stood, Anchor = new NormPoint(ax, 0.5) };
    }

    [Fact]
    public void Screwdrivers_nest_head_to_tail_with_finger_slots_across_them()
    {
        var tools = new List<ToolShape>
        {
            Screwdriver(0, 110, 216, 41, 0.1), Screwdriver(1, 95, 182, 35, 0.2), Screwdriver(2, 110, 215, 39, 0.3),
            Screwdriver(3, 95, 181, 34, 0.4), Screwdriver(4, 95, 203, 34, 0.5), Screwdriver(5, 95, 228, 34, 0.6)
        };
        var settings = new BinSettings();
        var design = Designer.Design(tools, settings, new BinLayout());
        // Boxes alone (41 + 35 + 39 + 34 + 34 + 34 mm wide, each plus clearance and spacing) need 6 × 6;
        // lying head to tail they share space.
        Assert.True(design.GridX * design.GridY <= 30, $"{design.GridX}×{design.GridY}");
        Assert.All(design.Tools, t => Assert.True(Math.Abs(Math.Sin((t.Placement.Rotation - Designer.LyingAngle(t.Tool.Outline)) * Math.PI / 180)) < 0.02, "lying straight"));
        AssertPocketsApart(design);

        // Bars square to the tools, each crossing every one of them.
        Assert.InRange(design.Slots.Count, 1, 2);
        foreach (var slot in design.Slots)
        {
            Assert.True(Math.Abs(slot.X1 - slot.X2) < 0.5, "a bar across horizontal tools runs up-down");
            var region = Designer.SlotRegion(slot);
            Assert.All(design.Tools, t => Assert.NotEmpty(Clipper2Lib.Clipper.Intersect(region, Designer.Pocket(t, settings), Clipper2Lib.FillRule.NonZero)));
        }
        var mesh = BinMesh.Build(design);
        Assert.Equal(0, mesh.BadEdges());

        // A user's own slot sticks; moving a tool doesn't move it.
        var layout = design.ToLayout();
        layout.Slots = new List<FingerSlot> { new(50, 20, 50, 120, 18) };
        layout.Tools[0] = layout.Tools[0] with { X = layout.Tools[0].X + 2 };
        var again = Designer.Design(tools, settings, layout);
        Assert.Equal(new FingerSlot(50, 20, 50, 120, 18), Assert.Single(again.Slots));
        Assert.False(again.SlotsAutomatic);
    }
}
