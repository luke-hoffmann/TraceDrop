using Clipper2Lib;
using Tracedrop.Core.Editing;
using Tracedrop.Core.Vector;

namespace Tracedrop.Core.Gridfinity;

/// <summary>
/// Gridfinity dimensions (Zack Freedman's open standard): 42 mm grid, 7 mm height units, bins 0.5 mm smaller than
/// their grid, the stepped 45° foot that drops into a baseplate, and the stacking lip that the next bin's feet sit in.
/// </summary>
public static class Spec
{
    public const double Pitch = 42;
    public const double HeightUnit = 7;
    /// <summary>A bin is this much smaller than its grid footprint (0.25 mm all round).</summary>
    public const double Gap = 0.5;
    public const double OuterRadius = 3.75;

    // Foot, from the bottom: 0.8 mm at 45°, 1.8 mm straight up, 2.15 mm at 45° = 4.75 mm.
    public const double FootChamfer1 = 0.8;
    public const double FootStraight = 1.8;
    public const double FootChamfer2 = 2.15;
    public const double FootHeight = FootChamfer1 + FootStraight + FootChamfer2;
    /// <summary>The foot's footprint at the very bottom: 41.5 − 2 × (0.8 + 2.15) = 35.6 mm.</summary>
    public const double FootBottom = Pitch - Gap - 2 * (FootChamfer1 + FootChamfer2);

    public const double MagnetDiameter = 6.5;
    public const double MagnetDepth = 2.4;
    /// <summary>Magnet holes sit at ±13 mm from each cell's centre.</summary>
    public const double MagnetOffset = 13;

    // Stacking lip (above the bin's height), as an inset from the outer wall going up: 2.6 → 0.7 at 45°,
    // straight for 1.8, then 0.7 → 0.25 at 45° to a small flat top.
    public const double LipInsetBottom = 2.6;
    public const double LipInsetMiddle = 0.7;
    public const double LipInsetTop = 0.25;
    public const double LipStraight = 1.8;
    public const double LipHeight = (LipInsetBottom - LipInsetMiddle) + LipStraight + (LipInsetMiddle - LipInsetTop);

    /// <summary>Solid material kept between the deepest pocket and the feet.</summary>
    public const double MinFloor = 1.2;

    /// <summary>Distance kept between pockets and the bin's outside (more with a lip: the lip must not overhang a pocket).</summary>
    public static double Wall(bool lip) => lip ? 3.0 : 1.8;
}

/// <summary>How bins are made from traced tools. Shared through presets like the other settings.</summary>
public sealed class BinSettings
{
    /// <summary>Make a bin from the traced tools (off = just trace outlines).</summary>
    public bool Enabled { get; set; }

    /// <summary>Grid units across (X) and deep (Y); 0 = the smallest bin that fits the tools.</summary>
    public int GridX { get; set; }
    public int GridY { get; set; }

    /// <summary>Height in 7 mm units; 0 = just tall enough for the deepest pocket.</summary>
    public int HeightUnits { get; set; }

    /// <summary>Gap around each tool in its pocket, mm. 0.5 = snug, 1 = easy, 2 = loose.</summary>
    public double Clearance { get; set; } = 1.0;

    /// <summary>Pocket depth, mm (each tool can have its own).</summary>
    public double Depth { get; set; } = 15;

    /// <summary>Least material between two pockets when arranging, mm.</summary>
    public double Spacing { get; set; } = 3;

    public bool StackingLip { get; set; } = true;

    /// <summary>6 × 2 mm magnet holes under each grid cell.</summary>
    public bool Magnets { get; set; }

    /// <summary>Finger slots: rounded bars across the tools, to get a finger under them. Placed automatically
    /// until you change them.</summary>
    public bool FingerSlots { get; set; } = true;

    /// <summary>Finger slot width, mm.</summary>
    public double FingerSlotWidth { get; set; } = 20;

    /// <summary>Largest bin the automatic size picks, in grid units per side (printer bed: 5 = 210 mm).</summary>
    public int MaxGrid { get; set; } = 6;

    /// <summary>Depth of engraved labels, mm.</summary>
    public double LabelDepth { get; set; } = 0.8;

    public BinSettings Clone() => (BinSettings)MemberwiseClone();

    public BinSettings Normalized()
    {
        GridX = Math.Clamp(GridX, 0, 20);
        GridY = Math.Clamp(GridY, 0, 20);
        HeightUnits = Math.Clamp(HeightUnits, 0, 30);
        Clearance = Math.Clamp(Clearance, 0, 10);
        Depth = Math.Clamp(Depth, 1, 150);
        Spacing = Math.Clamp(Spacing, 0.8, 30);
        FingerSlotWidth = Math.Clamp(FingerSlotWidth, 6, 60);
        MaxGrid = Math.Clamp(MaxGrid, 1, 20);
        LabelDepth = Math.Clamp(LabelDepth, 0.2, 3);
        return this;
    }
}

/// <summary>A finger hole beside a tool, in the tool's own coordinates (mm from its centre, y up).</summary>
public sealed record FingerHole(double X, double Y, double Diameter);

/// <summary>
/// A finger slot: a bar with rounded ends from (X1, Y1) to (X2, Y2), in bin mm, cut as deep as the deepest pocket it
/// crosses. Laid across several tools it lets a finger get under each of them.
/// </summary>
public sealed record FingerSlot(double X1, double Y1, double X2, double Y2, double Width)
{
    public Vec2 A => new(X1, Y1);
    public Vec2 B => new(X2, Y2);
    public FingerSlot Moved(double dx, double dy) => new(X1 + dx, Y1 + dy, X2 + dx, Y2 + dy, Width);
}

/// <summary>Where one traced tool sits in the bin.</summary>
public sealed record ToolPlacement
{
    /// <summary>The tool's centre in the traced picture (0..1): how a re-trace finds the same tool again.</summary>
    public NormPoint Anchor { get; init; } = new(0.5, 0.5);
    /// <summary>Centre of the tool in the bin, mm from the bin's bottom-left corner (y up).</summary>
    public double X { get; init; }
    public double Y { get; init; }
    /// <summary>Degrees, counter-clockwise.</summary>
    public double Rotation { get; init; }
    /// <summary>Pocket depth for this tool, mm; 0 = the bin's depth.</summary>
    public double Depth { get; init; }
    /// <summary>Left out of the bin.</summary>
    public bool Removed { get; init; }
    /// <summary>Round finger holes beside this tool (none unless added); null = none.</summary>
    public List<FingerHole>? FingerHoles { get; init; }

    public ToolPlacement Copy() => this with { FingerHoles = FingerHoles?.ToList() };
}

/// <summary>Engraved text on the bin's top, mm from the bin's bottom-left corner.</summary>
public sealed record BinLabel(string Text, double X, double Y, double Height = 6, double Rotation = 0);

/// <summary>Per picture: the arrangement in the bin. Empty = arrange automatically.</summary>
public sealed class BinLayout
{
    public List<ToolPlacement> Tools { get; set; } = new();
    public List<BinLabel> Labels { get; set; } = new();
    /// <summary>Finger slots; null = placed automatically across the tools.</summary>
    public List<FingerSlot>? Slots { get; set; }

    public bool IsEmpty => Tools.Count == 0 && Labels.Count == 0 && Slots is null;

    public BinLayout Clone() => new() { Tools = Tools.Select(t => t.Copy()).ToList(), Labels = Labels.ToList(), Slots = Slots?.ToList() };

    public BinLayout Normalized()
    {
        Tools ??= new();
        Tools.RemoveAll(t => t is null || t.Anchor is null || double.IsNaN(t.X) || double.IsNaN(t.Y));
        Labels ??= new();
        Labels.RemoveAll(l => l is null || string.IsNullOrWhiteSpace(l.Text));
        Slots?.RemoveAll(sl => sl is null || double.IsNaN(sl.X1) || double.IsNaN(sl.Y1) || double.IsNaN(sl.X2) || double.IsNaN(sl.Y2) || sl.Width <= 0);
        return this;
    }

    public string Key() =>
        string.Join(";", Tools.Select(t => $"{t.Anchor.X:0.####},{t.Anchor.Y:0.####},{t.X:0.###},{t.Y:0.###},{t.Rotation:0.##},{t.Depth:0.##},{t.Removed}," +
                                           (t.FingerHoles is null ? "auto" : string.Join("/", t.FingerHoles.Select(f => $"{f.X:0.##}:{f.Y:0.##}:{f.Diameter:0.#}"))))) + "|" +
        string.Join(";", Labels.Select(l => $"{l.Text}@{l.X:0.##},{l.Y:0.##},{l.Height:0.##},{l.Rotation:0.#}")) + "|" +
        (Slots is null ? "auto" : string.Join(";", Slots.Select(sl => $"{sl.X1:0.##},{sl.Y1:0.##},{sl.X2:0.##},{sl.Y2:0.##},{sl.Width:0.#}")));
}

/// <summary>A traced tool's outline in millimetres (y up), centred on its bounding box.</summary>
public sealed class ToolShape
{
    public required int Index { get; init; }
    public required string Name { get; init; }
    public required List<Vec2> Outline { get; init; }
    /// <summary>Centre in the traced picture (0..1).</summary>
    public required NormPoint Anchor { get; init; }
    public double Area => Math.Abs(Geometry2D.SignedArea(Outline));
    public double Width => Outline.Max(p => p.X) - Outline.Min(p => p.X);
    public double Height => Outline.Max(p => p.Y) - Outline.Min(p => p.Y);
}

/// <summary>A tool resolved in the bin: its shape, placement and the finger holes that apply.</summary>
public sealed record PlacedTool(ToolShape Tool, ToolPlacement Placement, List<FingerHole> FingerHoles, double Depth)
{
    /// <summary>Tool-local point (mm) to bin coordinates.</summary>
    public Vec2 ToBin(Vec2 local)
    {
        var a = Placement.Rotation * Math.PI / 180;
        var (c, s) = (Math.Cos(a), Math.Sin(a));
        return new Vec2(Placement.X + local.X * c - local.Y * s, Placement.Y + local.X * s + local.Y * c);
    }

    /// <summary>Bin point to tool-local coordinates.</summary>
    public Vec2 ToLocal(Vec2 bin)
    {
        var a = -Placement.Rotation * Math.PI / 180;
        var (c, s) = (Math.Cos(a), Math.Sin(a));
        var x = bin.X - Placement.X;
        var y = bin.Y - Placement.Y;
        return new Vec2(x * c - y * s, x * s + y * c);
    }

    public List<Vec2> OutlineInBin() => Tool.Outline.Select(ToBin).ToList();
}

/// <summary>Everything needed to build one bin: size, the tools in it and the labels.</summary>
public sealed class BinDesign
{
    public required BinSettings Settings { get; init; }
    public required int GridX { get; init; }
    public required int GridY { get; init; }
    public required int HeightUnits { get; init; }
    public required List<PlacedTool> Tools { get; init; }
    /// <summary>Tools traced but left out of the bin.</summary>
    public required List<ToolShape> LeftOut { get; init; }
    public required List<BinLabel> Labels { get; init; }
    /// <summary>Finger slots in use (automatic or the user's).</summary>
    public required List<FingerSlot> Slots { get; init; }
    /// <summary>True while the slots are placed automatically.</summary>
    public bool SlotsAutomatic { get; init; }
    public required List<string> Notes { get; init; }

    public double Width => GridX * Spec.Pitch - Spec.Gap;
    public double Depth => GridY * Spec.Pitch - Spec.Gap;
    /// <summary>Bin height without the stacking lip (7 mm × units).</summary>
    public double Height => HeightUnits * Spec.HeightUnit;
    public double Wall => Spec.Wall(Settings.StackingLip);

    /// <summary>The layout as stored per picture (every tool frozen where it is now).</summary>
    public BinLayout ToLayout() => new()
    {
        Tools = Tools.Select(t => t.Placement.Copy()).Concat(LeftOut.Select(t => new ToolPlacement { Anchor = t.Anchor, Removed = true })).ToList(),
        Labels = Labels.ToList(),
        Slots = SlotsAutomatic ? null : Slots.ToList()
    };
}

/// <summary>Turns a traced drawing into a bin design: tools in mm, placed (kept, matched or arranged).</summary>
public static class Designer
{
    /// <summary>Smallest outline kept as a tool, mm².</summary>
    public const double MinToolArea = 60;

    /// <summary>Pieces of one tool closer than twice this (mm) are joined: a clear handle traced in parts.</summary>
    const double JoinGap = 1.5;

    /// <summary>
    /// The traced outer outlines as tools, in mm (y up), numbered left to right, top to bottom. Pieces less than
    /// 3 mm apart become one tool (a transparent handle often traces in parts); specks are dropped.
    /// </summary>
    public static List<ToolShape> ExtractTools(VectorDrawing drawing, double mmPerPixel)
    {
        var parts = new Paths64();
        foreach (var path in drawing.Paths)
        {
            var closed = path.Closed || path.Circle is not null;
            if (!closed || path.Role == PathRole.Hole) continue;
            var pixels = path.Flatten(0.25);
            if (pixels.Count < 3) continue;
            var mm = pixels.Select(p => new Vec2(p.X * mmPerPixel, -p.Y * mmPerPixel)).ToList();
            if (Math.Abs(Geometry2D.SignedArea(mm)) < 2) continue;
            var outline = ToPath(mm);
            if (Clipper.Area(outline) < 0) outline.Reverse();
            parts.Add(outline);
        }
        // Grow, merge, shrink back: pieces within 2 × JoinGap join up; everything else keeps its exact outline.
        var joined = Clipper.InflatePaths(
            Clipper.InflatePaths(parts, JoinGap * Scale, JoinType.Round, EndType.Polygon, 2.0, 0.02 * Scale),
            -JoinGap * Scale, JoinType.Round, EndType.Polygon, 2.0, 0.02 * Scale);
        var shapes = new List<(List<Vec2> Outline, NormPoint Anchor)>();
        foreach (var path in joined)
        {
            if (Clipper.Area(path) <= 0) continue; // holes: the pocket is the outer outline
            var mm = path.Select(p => new Vec2(p.X / Scale, p.Y / Scale)).ToList();
            if (Geometry2D.SignedArea(mm) < MinToolArea) continue;
            var cx = (mm.Min(p => p.X) + mm.Max(p => p.X)) / 2;
            var cy = (mm.Min(p => p.Y) + mm.Max(p => p.Y)) / 2;
            var anchor = new NormPoint(cx / mmPerPixel / Math.Max(1, drawing.Width), -cy / mmPerPixel / Math.Max(1, drawing.Height));
            shapes.Add((mm.Select(p => new Vec2(p.X - cx, p.Y - cy)).ToList(), anchor));
        }
        // Reading order: rows of tools top to bottom, left to right within a row.
        var ordered = shapes.OrderBy(t => Math.Round(t.Anchor.Y * 4)).ThenBy(t => t.Anchor.X).ToList();
        return ordered.Select((t, i) => new ToolShape { Index = i, Name = $"Tool {i + 1}", Outline = t.Outline, Anchor = t.Anchor }).ToList();
    }

    /// <summary>
    /// Builds the design: tools already placed by the user keep their place (matched by where they are in the
    /// picture), new tools are arranged into free space, and the grid is the smallest that fits (or the fixed one).
    /// </summary>
    public static BinDesign Design(List<ToolShape> tools, BinSettings settings, BinLayout layout)
    {
        var notes = new List<string>();
        var wall = Spec.Wall(settings.StackingLip);
        var placed = new List<PlacedTool>();
        var leftOut = new List<ToolShape>();
        var unplaced = new List<ToolShape>();
        var used = new HashSet<ToolPlacement>(ReferenceEqualityComparer.Instance);

        foreach (var tool in tools)
        {
            var match = layout.Tools.Where(p => !used.Contains(p))
                .Select(p => (Placement: p, Distance: Math.Sqrt(Math.Pow(p.Anchor.X - tool.Anchor.X, 2) + Math.Pow(p.Anchor.Y - tool.Anchor.Y, 2))))
                .Where(m => m.Distance < 0.04)
                .OrderBy(m => m.Distance)
                .Select(m => m.Placement)
                .FirstOrDefault();
            if (match is null) { unplaced.Add(tool); continue; }
            used.Add(match);
            if (match.Removed) { leftOut.Add(tool); continue; }
            placed.Add(Resolve(tool, match with { Anchor = tool.Anchor }, settings));
        }

        int gridX, gridY;
        if (placed.Count == 0)
        {
            // Nothing placed yet: arrange everything and pick the bin size.
            (gridX, gridY, placed) = Arrange(unplaced, settings, notes);
        }
        else
        {
            // An automatic size follows the tools: if one was pushed past the left or bottom wall, everything moves
            // over so the bin grows on that side instead of cutting the pocket off.
            var minX = placed.Min(t => Bounds(t, settings).Min.X);
            var minY = placed.Min(t => Bounds(t, settings).Min.Y);
            var dx = settings.GridX == 0 && minX < wall ? wall - minX : 0;
            var dy = settings.GridY == 0 && minY < wall ? wall - minY : 0;
            if (dx > 0 || dy > 0)
            {
                placed = placed.Select(t => t with { Placement = t.Placement with { X = Math.Round(t.Placement.X + dx, 3), Y = Math.Round(t.Placement.Y + dy, 3) } }).ToList();
                layout = new BinLayout
                {
                    Tools = layout.Tools,
                    Labels = layout.Labels.Select(l => l with { X = l.X + dx, Y = l.Y + dy }).ToList(),
                    Slots = layout.Slots?.Select(sl => sl.Moved(dx, dy)).ToList()
                };
            }
            (gridX, gridY) = FitGrid(placed, settings, wall);
            if (unplaced.Count > 0)
            {
                // New tools go into the free space around the ones already placed (growing an automatic bin if needed).
                for (var attempt = 0; attempt < 12; attempt++)
                {
                    var extra = PlaceInto(unplaced, placed, gridX, gridY, settings);
                    if (extra is not null) { placed.AddRange(extra); unplaced.Clear(); break; }
                    if (settings.GridX > 0 && settings.GridY > 0) break;
                    if (settings.GridX == 0 && (gridX <= gridY || settings.GridY > 0)) gridX++;
                    else gridY++;
                }
                if (unplaced.Count > 0)
                {
                    notes.Add($"{unplaced.Count} tool{(unplaced.Count == 1 ? " doesn't" : "s don't")} fit in a {gridX}×{gridY} bin.");
                    var x = gridX * Spec.Pitch + 10;
                    foreach (var t in unplaced)
                    {
                        var r = Resolve(t, new ToolPlacement { Anchor = t.Anchor }, settings);
                        var b = Bounds(r, settings);
                        placed.Add(r with { Placement = r.Placement with { X = x + (b.Max.X - b.Min.X) / 2, Y = (b.Max.Y - b.Min.Y) / 2 } });
                        x += b.Max.X - b.Min.X + settings.Spacing;
                    }
                }
            }
        }

        // Height: tall enough for the deepest pocket over the feet and floor.
        var deepest = placed.Count == 0 ? settings.Depth : placed.Max(t => t.Depth);
        var label = layout.Labels.Count > 0 ? settings.LabelDepth : 0;
        var needed = Spec.FootHeight + Spec.MinFloor + Math.Max(deepest, label);
        var heightUnits = settings.HeightUnits > 0 ? settings.HeightUnits : Math.Max(2, (int)Math.Ceiling(needed / Spec.HeightUnit - 1e-9));
        var height = heightUnits * Spec.HeightUnit;
        var maxDepth = height - Spec.FootHeight - Spec.MinFloor;
        if (placed.Any(t => t.Depth > maxDepth + 1e-9))
        {
            notes.Add($"Pockets limited to {maxDepth:0.#} mm deep by the {heightUnits}-unit height.");
            placed = placed.Select(t => t.Depth > maxDepth ? t with { Depth = maxDepth } : t).ToList();
        }

        var design = new BinDesign
        {
            Settings = settings,
            GridX = gridX,
            GridY = gridY,
            HeightUnits = heightUnits,
            Tools = placed,
            LeftOut = leftOut,
            Labels = layout.Labels.ToList(),
            Slots = layout.Slots?.ToList() ?? (settings.FingerSlots ? AutoSlots(placed, settings) : new List<FingerSlot>()),
            SlotsAutomatic = layout.Slots is null,
            Notes = notes
        };
        foreach (var t in placed)
        {
            var b = Bounds(t, settings);
            if (b.Min.X < wall - 0.01 || b.Min.Y < wall - 0.01 || b.Max.X > design.Width - wall + 0.01 || b.Max.Y > design.Depth - wall + 0.01)
            {
                notes.Add($"{t.Tool.Name} sticks out of the bin: its pocket is cut off at the wall.");
                break;
            }
        }
        return design;
    }

    static PlacedTool Resolve(ToolShape tool, ToolPlacement placement, BinSettings settings)
    {
        var holes = placement.FingerHoles ?? new List<FingerHole>();
        var depth = placement.Depth > 0 ? placement.Depth : settings.Depth;
        return new PlacedTool(tool, placement, holes, depth);
    }

    /// <summary>Rotation (radians) whose axis-aligned box around the outline is smallest (convex hull edges).</summary>
    public static double MinAreaAngle(IReadOnlyList<Vec2> outline)
    {
        var hull = Hull(outline);
        var bestAngle = 0.0;
        var bestArea = double.MaxValue;
        for (var i = 0; i < hull.Count; i++)
        {
            var e = hull[(i + 1) % hull.Count] - hull[i];
            if (e.Length < 1e-9) continue;
            var angle = Math.Atan2(e.Y, e.X);
            var (c, s) = (Math.Cos(angle), Math.Sin(angle));
            double x0 = double.MaxValue, x1 = double.MinValue, y0 = double.MaxValue, y1 = double.MinValue;
            foreach (var p in hull)
            {
                var x = p.X * c + p.Y * s;
                var y = -p.X * s + p.Y * c;
                x0 = Math.Min(x0, x); x1 = Math.Max(x1, x); y0 = Math.Min(y0, y); y1 = Math.Max(y1, y);
            }
            var area = (x1 - x0) * (y1 - y0);
            if (area < bestArea - 1e-9) { bestArea = area; bestAngle = angle; }
        }
        return bestAngle;
    }

    static List<Vec2> Hull(IReadOnlyList<Vec2> points)
    {
        var p = points.OrderBy(v => v.X).ThenBy(v => v.Y).ToList();
        if (p.Count < 3) return p;
        var lower = new List<Vec2>();
        foreach (var v in p)
        {
            while (lower.Count >= 2 && Vec2.Cross(lower[^1] - lower[^2], v - lower[^2]) <= 0) lower.RemoveAt(lower.Count - 1);
            lower.Add(v);
        }
        var upper = new List<Vec2>();
        for (var i = p.Count - 1; i >= 0; i--)
        {
            var v = p[i];
            while (upper.Count >= 2 && Vec2.Cross(upper[^1] - upper[^2], v - upper[^2]) <= 0) upper.RemoveAt(upper.Count - 1);
            upper.Add(v);
        }
        lower.RemoveAt(lower.Count - 1);
        upper.RemoveAt(upper.Count - 1);
        return lower.Concat(upper).ToList();
    }

    /// <summary>The pocket's bounding box in bin mm: the outline grown by the clearance, plus its finger holes.</summary>
    public static (Vec2 Min, Vec2 Max) Bounds(PlacedTool t, BinSettings settings)
    {
        var pts = t.OutlineInBin();
        double x0 = pts.Min(p => p.X) - settings.Clearance, x1 = pts.Max(p => p.X) + settings.Clearance;
        double y0 = pts.Min(p => p.Y) - settings.Clearance, y1 = pts.Max(p => p.Y) + settings.Clearance;
        foreach (var h in t.FingerHoles)
        {
            var c = t.ToBin(new Vec2(h.X, h.Y));
            x0 = Math.Min(x0, c.X - h.Diameter / 2); x1 = Math.Max(x1, c.X + h.Diameter / 2);
            y0 = Math.Min(y0, c.Y - h.Diameter / 2); y1 = Math.Max(y1, c.Y + h.Diameter / 2);
        }
        return (new Vec2(x0, y0), new Vec2(x1, y1));
    }

    /// <summary>Smallest grid that holds the placed tools where they are (or the fixed size).</summary>
    static (int X, int Y) FitGrid(List<PlacedTool> placed, BinSettings settings, double wall)
    {
        var maxX = placed.Max(t => Bounds(t, settings).Max.X);
        var maxY = placed.Max(t => Bounds(t, settings).Max.Y);
        var gx = settings.GridX > 0 ? settings.GridX : Math.Max(1, (int)Math.Ceiling((maxX + wall + Spec.Gap) / Spec.Pitch - 1e-9));
        var gy = settings.GridY > 0 ? settings.GridY : Math.Max(1, (int)Math.Ceiling((maxY + wall + Spec.Gap) / Spec.Pitch - 1e-9));
        return (gx, gy);
    }

    /// <summary>
    /// Arranges tools from scratch by their real outlines: each lies along its long side and is dropped into the
    /// bin as low as it goes (head to tail if that nests better), like packing a drawer by hand. Every bin width
    /// up to the biggest automatic size is tried; the smallest bin wins.
    /// </summary>
    public static (int GridX, int GridY, List<PlacedTool> Placed) Arrange(List<ToolShape> tools, BinSettings settings, List<string>? notes = null)
    {
        var wall = Spec.Wall(settings.StackingLip);
        if (tools.Count == 0)
            return (settings.GridX > 0 ? settings.GridX : 1, settings.GridY > 0 ? settings.GridY : 1, new List<PlacedTool>());

        var shapes = tools.Select(t => new NestShape(t, settings)).ToList();
        double Usable(int units) => units * Spec.Pitch - Spec.Gap - 2 * wall + settings.Spacing;
        int UnitsFor(double used) => Math.Max(1, (int)Math.Ceiling((used - settings.Spacing + 2 * wall + Spec.Gap) / Spec.Pitch - 1e-9));

        // Widths to try: from the narrowest that holds the longest tool lying down, to the biggest automatic size.
        var widest = shapes.Max(sh => sh.MinWidth);
        var widths = settings.GridX > 0
            ? new List<int> { settings.GridX }
            : Enumerable.Range(Math.Max(1, UnitsFor(widest)), Math.Max(1, Math.Max(settings.MaxGrid, UnitsFor(widest)) - Math.Max(1, UnitsFor(widest)) + 1)).ToList();

        (int X, int Y, List<PlacedTool> Placed, double Score)? best = null;
        foreach (var gx in widths)
        {
            var nested = Nest(shapes, Usable(gx), settings);
            if (nested is null) continue;
            var gy = settings.GridY > 0 ? settings.GridY : UnitsFor(nested.Value.Height);
            var fits = nested.Value.Height <= Usable(gy) + 1e-6;
            var oversize = gx > settings.MaxGrid || gy > settings.MaxGrid;
            // Smallest area first, then squarer; a bin over the biggest size or one that overflows only as a last resort.
            var score = gx * gy + Math.Abs(gx - gy) * 0.01 + (oversize ? 1000 : 0) + (fits ? 0 : 10000);
            if (best is null || score < best.Value.Score) best = (gx, gy, nested.Value.Placed, score);
        }
        if (best is null)
        {
            // Not even one tool fits the fixed width: lay them out anyway so they can be seen and moved.
            var x0 = wall;
            var lined = new List<PlacedTool>();
            foreach (var t in tools)
            {
                var r = Resolve(t, new ToolPlacement { Anchor = t.Anchor }, settings);
                var b = Bounds(r, settings);
                lined.Add(r with { Placement = r.Placement with { X = x0 - b.Min.X, Y = wall - b.Min.Y } });
                x0 += b.Max.X - b.Min.X + settings.Spacing;
            }
            notes?.Add("The tools are longer than the bin is wide: pick a wider bin, or Fit the tools.");
            return (settings.GridX, Math.Max(1, settings.GridY), lined);
        }
        var (bx, by, placed, finalScore) = best.Value;
        if (finalScore >= 10000) notes?.Add($"The tools don't all fit in a {bx}×{by} bin: make it deeper or leave some out.");
        else if (finalScore >= 1000) notes?.Add($"These tools need a {bx}×{by} bin, more than the biggest size set ({settings.MaxGrid}×{settings.MaxGrid}).");
        return (bx, by, Centre(placed, bx, by, settings));
    }

    /// <summary>A tool prepared for nesting: its outline in the four lying-down orientations, as column profiles.</summary>
    sealed class NestShape
    {
        public const double Step = 0.5; // mm per column

        public ToolShape Tool { get; }
        public List<(double Rotation, double[] Bottom, double[] Top, double MinX, double MinY)> Orientations { get; } = new();
        /// <summary>Narrowest width of the grown outline over the orientations, mm.</summary>
        public double MinWidth { get; }
        public double Area { get; }

        public NestShape(ToolShape tool, BinSettings settings)
        {
            Tool = tool;
            Area = tool.Area;
            // Lying down: the long side along X, then also turned end for end, and standing up.
            var angle = LyingAngle(tool.Outline);
            var grow = settings.Clearance + settings.Spacing / 2;
            var widths = new List<double>();
            foreach (var turn in new[] { 0.0, 180.0, 90.0, 270.0 })
            {
                var rotation = Normalize(angle + turn);
                var a = rotation * Math.PI / 180;
                var (c, si) = (Math.Cos(a), Math.Sin(a));
                var rotated = new Paths64 { ToPath(tool.Outline.Select(p => new Vec2(p.X * c - p.Y * si, p.X * si + p.Y * c))) };
                var grown = Clipper.InflatePaths(rotated, grow * Scale, JoinType.Round, EndType.Polygon, 2.0, 0.05 * Scale);
                var pts = grown.SelectMany(path => Dense(path)).ToList();
                if (pts.Count == 0) continue;
                var minX = pts.Min(p => p.X);
                var maxX = pts.Max(p => p.X);
                var minY = pts.Min(p => p.Y);
                var columns = Math.Max(1, (int)Math.Ceiling((maxX - minX) / Step) + 1);
                var bottom = Enumerable.Repeat(double.MaxValue, columns).ToArray();
                var top = Enumerable.Repeat(double.MinValue, columns).ToArray();
                foreach (var p in pts)
                {
                    var col = Math.Clamp((int)((p.X - minX) / Step), 0, columns - 1);
                    bottom[col] = Math.Min(bottom[col], p.Y - minY);
                    top[col] = Math.Max(top[col], p.Y - minY);
                }
                // Columns the outline only crosses (no sample landed): take the neighbours' span.
                for (var k = 0; k < columns; k++)
                {
                    if (bottom[k] != double.MaxValue) continue;
                    var l = k > 0 ? k - 1 : k + 1;
                    bottom[k] = bottom[Math.Clamp(l, 0, columns - 1)];
                    top[k] = top[Math.Clamp(l, 0, columns - 1)];
                }
                Orientations.Add((rotation, bottom, top, minX, minY));
                widths.Add(maxX - minX);
            }
            MinWidth = widths.Count > 0 ? widths.Min() : 0;
        }

        static IEnumerable<Vec2> Dense(Path64 path)
        {
            for (var i = 0; i < path.Count; i++)
            {
                var a = new Vec2(path[i].X / Scale, path[i].Y / Scale);
                var b = new Vec2(path[(i + 1) % path.Count].X / Scale, path[(i + 1) % path.Count].Y / Scale);
                var n = Math.Max(1, (int)Math.Ceiling(Vec2.Distance(a, b) / (Step / 2)));
                for (var k = 0; k < n; k++) yield return Vec2.Lerp(a, b, k / (double)n);
            }
        }

        static (double W, double H) Extent(List<Vec2> outline, double degrees)
        {
            var a = degrees * Math.PI / 180;
            var (c, s) = (Math.Cos(a), Math.Sin(a));
            var xs = outline.Select(p => p.X * c - p.Y * s).ToList();
            var ys = outline.Select(p => p.X * s + p.Y * c).ToList();
            return (xs.Max() - xs.Min(), ys.Max() - ys.Min());
        }
    }

    /// <summary>
    /// Drops the shapes, biggest first, into a strip of the given usable width: each goes where (and which way round)
    /// it keeps the pile lowest and wastes the least space underneath. Null if a shape is wider than the strip.
    /// </summary>
    static (List<PlacedTool> Placed, double Height)? Nest(List<NestShape> shapes, double width, BinSettings settings)
    {
        var step = NestShape.Step;
        var columns = (int)Math.Floor(width / step);
        if (columns <= 0) return null;
        var sky = new double[columns];
        var wall = Spec.Wall(settings.StackingLip);
        var origin = wall - settings.Spacing / 2; // where the grown outlines may start
        var placed = new List<PlacedTool>();
        foreach (var shape in shapes.OrderByDescending(sh => sh.Area))
        {
            (int Orientation, int Column, double Y, double Peak, double Waste)? best = null;
            for (var o = 0; o < shape.Orientations.Count; o++)
            {
                var (_, bottom, top, _, _) = shape.Orientations[o];
                var span = bottom.Length;
                if (span > columns) continue;
                var stride = Math.Max(1, span > 200 ? 2 : 1);
                for (var k = 0; k + span <= columns; k += stride)
                {
                    var y = 0.0;
                    for (var c = 0; c < span; c++) y = Math.Max(y, sky[k + c] - bottom[c]);
                    double peak = 0, waste = 0;
                    for (var c = 0; c < span; c++)
                    {
                        peak = Math.Max(peak, y + top[c]);
                        waste += y + bottom[c] - sky[k + c];
                    }
                    for (var c = 0; c < k; c++) peak = Math.Max(peak, sky[c]);
                    for (var c = k + span; c < columns; c++) peak = Math.Max(peak, sky[c]);
                    var better = best is null
                        || peak < best.Value.Peak - 1e-6
                        || (Math.Abs(peak - best.Value.Peak) <= 1e-6 && waste < best.Value.Waste - 1e-6);
                    if (better) best = (o, k, y, peak, waste);
                }
                // Also flush against the right wall.
                var right = columns - span;
                if (right >= 0 && right % stride != 0)
                {
                    var y = 0.0;
                    for (var c = 0; c < span; c++) y = Math.Max(y, sky[right + c] - bottom[c]);
                    double peak = 0, waste = 0;
                    for (var c = 0; c < span; c++) { peak = Math.Max(peak, y + top[c]); waste += y + bottom[c] - sky[right + c]; }
                    for (var c = 0; c < right; c++) peak = Math.Max(peak, sky[c]);
                    if (best is null || peak < best.Value.Peak - 1e-6 || (Math.Abs(peak - best.Value.Peak) <= 1e-6 && waste < best.Value.Waste - 1e-6))
                        best = (o, right, y, peak, waste);
                }
            }
            if (best is null) return null;
            var (oi, col, yy, _, _) = best.Value;
            var orientation = shape.Orientations[oi];
            for (var c = 0; c < orientation.Top.Length; c++) sky[col + c] = Math.Max(sky[col + c], yy + orientation.Top[c]);
            // The grown outline's min corner lands at (col·step, yy); the tool's centre is offset from it.
            var x = origin + col * step - orientation.MinX;
            var y0 = origin + yy - orientation.MinY;
            placed.Add(Resolve(shape.Tool, new ToolPlacement { Anchor = shape.Tool.Anchor, Rotation = orientation.Rotation, X = Math.Round(x, 3), Y = Math.Round(y0, 3) }, settings));
        }
        return (placed, sky.Max());
    }

    /// <summary>
    /// Finger slots across the tools: tools lying the same way share bars square to them, through the stretch they
    /// all cover (two bars for long tools); a tool on its own gets a short bar across its middle.
    /// </summary>
    public static List<FingerSlot> AutoSlots(List<PlacedTool> placed, BinSettings settings)
    {
        var slots = new List<FingerSlot>();
        var width = settings.FingerSlotWidth;
        // Group by direction (an end-for-end turn is the same direction).
        var groups = new List<List<PlacedTool>>();
        foreach (var t in placed)
        {
            var axis = LongAxis(t);
            var group = groups.FirstOrDefault(g => Math.Abs(Math.Sin((LongAxis(g[0]) - axis) * Math.PI / 180)) < Math.Sin(12 * Math.PI / 180));
            if (group is null) groups.Add(new List<PlacedTool> { t });
            else group.Add(t);
        }
        foreach (var group in groups)
        {
            var axis = LongAxis(group[0]) * Math.PI / 180;
            var along = new Vec2(Math.Cos(axis), Math.Sin(axis));
            var across = new Vec2(-along.Y, along.X);
            // Each tool's extent along and across the shared direction.
            var spans = group.Select(t =>
            {
                var pts = t.OutlineInBin();
                return (A0: pts.Min(p => Vec2.Dot(p, along)), A1: pts.Max(p => Vec2.Dot(p, along)),
                    C0: pts.Min(p => Vec2.Dot(p, across)), C1: pts.Max(p => Vec2.Dot(p, across)));
            }).ToList();
            var common0 = spans.Max(sp => sp.A0);
            var common1 = spans.Min(sp => sp.A1);
            var c0 = spans.Min(sp => sp.C0) - settings.Clearance;
            var c1 = spans.Max(sp => sp.C1) + settings.Clearance;
            var shortest = spans.Min(sp => sp.A1 - sp.A0);
            List<double> positions;
            if (common1 - common0 >= width)
            {
                // Bars where every tool is: one in the middle, or two (at a third and two thirds) for long tools.
                var length = common1 - common0;
                positions = shortest >= 140 && length >= 3 * width
                    ? new List<double> { common0 + length / 3, common0 + 2 * length / 3 }
                    : new List<double> { (common0 + common1) / 2 };
            }
            else
            {
                // The tools don't overlap enough: a bar through each one's middle (merging ones close together).
                positions = spans.Select(sp => (sp.A0 + sp.A1) / 2).OrderBy(v => v).ToList();
                var merged = new List<double>();
                foreach (var v in positions)
                    if (merged.Count == 0 || v - merged[^1] > width * 1.5) merged.Add(v);
                positions = merged;
            }
            foreach (var at in positions)
            {
                // From the first tool across to the last, ends just past them.
                var inGroup = spans.Where(sp => sp.A0 <= at && at <= sp.A1).ToList();
                var lo = inGroup.Count > 0 ? inGroup.Min(sp => sp.C0) - settings.Clearance : c0;
                var hi = inGroup.Count > 0 ? inGroup.Max(sp => sp.C1) + settings.Clearance : c1;
                var a = along * at + across * (lo + width * 0.15);
                var b = along * at + across * (hi - width * 0.15);
                slots.Add(new FingerSlot(Math.Round(a.X, 3), Math.Round(a.Y, 3), Math.Round(b.X, 3), Math.Round(b.Y, 3), width));
            }
        }
        return slots;
    }

    /// <summary>Direction of a placed tool's long side, degrees.</summary>
    static double LongAxis(PlacedTool t) => t.Placement.Rotation - LyingAngle(t.Tool.Outline);

    /// <summary>
    /// The turn (degrees) that lays the outline flat along X: its principal axis (from the area's second moments,
    /// so a screwdriver lines up with its shaft, not with one side of its handle) turned horizontal.
    /// </summary>
    public static double LyingAngle(IReadOnlyList<Vec2> outline)
    {
        double area = 0, cx = 0, cy = 0, sxx = 0, syy = 0, sxy = 0;
        for (var i = 0; i < outline.Count; i++)
        {
            var p = outline[i];
            var q = outline[(i + 1) % outline.Count];
            var cross = p.X * q.Y - q.X * p.Y;
            area += cross;
            cx += (p.X + q.X) * cross;
            cy += (p.Y + q.Y) * cross;
            sxx += (p.X * p.X + p.X * q.X + q.X * q.X) * cross;
            syy += (p.Y * p.Y + p.Y * q.Y + q.Y * q.Y) * cross;
            sxy += (p.X * q.Y + 2 * p.X * p.Y + 2 * q.X * q.Y + q.X * p.Y) * cross;
        }
        if (area < 0)
        {
            // Clockwise outline: the same integrals with the sign flipped.
            area = -area; cx = -cx; cy = -cy; sxx = -sxx; syy = -syy; sxy = -sxy;
        }
        area /= 2;
        if (area < 1e-9) return 0;
        cx /= 6 * area;
        cy /= 6 * area;
        // Central second moments ∫x², ∫y², ∫xy over the area.
        var xx = sxx / 12 - area * cx * cx;
        var yy = syy / 12 - area * cy * cy;
        var xy = sxy / 24 - area * cx * cy;
        var axis = 0.5 * Math.Atan2(2 * xy, xx - yy) * 180 / Math.PI;
        return Normalize(-axis);
    }

    static double Normalize(double degrees)
    {
        degrees %= 360;
        if (degrees < 0) degrees += 360;
        return Math.Round(degrees, 3);
    }

    /// <summary>Places new tools into a bin that already has tools in it; null if they don't all fit.</summary>
    static List<PlacedTool>? PlaceInto(List<ToolShape> tools, List<PlacedTool> fixedTools, int gx, int gy, BinSettings settings)
    {
        var wall = Spec.Wall(settings.StackingLip);
        var items = tools.Select(t => Resolve(t, new ToolPlacement { Anchor = t.Anchor, Rotation = Normalize(-MinAreaAngle(t.Outline) * 180 / Math.PI) }, settings)).ToList();
        return Pack(items, gx, gy, settings, wall, fixedTools);
    }

    /// <summary>MaxRects (best short side fit) over the pockets' boxes, each may turn 90°. Null when they don't fit.</summary>
    static List<PlacedTool>? Pack(List<PlacedTool> items, int gx, int gy, BinSettings settings, double wall, IReadOnlyList<PlacedTool> occupied)
    {
        var gap = settings.Spacing;
        var width = gx * Spec.Pitch - Spec.Gap - 2 * wall + gap;
        var height = gy * Spec.Pitch - Spec.Gap - 2 * wall + gap;
        if (width <= 0 || height <= 0) return null;
        var free = new List<(double X, double Y, double W, double H)> { (0, 0, width, height) };
        foreach (var o in occupied)
        {
            var b = Bounds(o, settings);
            SplitFree(free, (b.Min.X - wall, b.Min.Y - wall, b.Max.X - b.Min.X + gap, b.Max.Y - b.Min.Y + gap));
        }

        var result = new List<PlacedTool>();
        foreach (var item in items.OrderByDescending(i => { var b = Bounds(i, settings); return Math.Max(b.Max.X - b.Min.X, b.Max.Y - b.Min.Y); }))
        {
            (double X, double Y, bool Turn, double Score)? best = null;
            foreach (var turn in new[] { false, true })
            {
                var candidate = turn ? item with { Placement = item.Placement with { Rotation = Normalize(item.Placement.Rotation + 90) } } : item;
                var b = Bounds(candidate, settings);
                var w = b.Max.X - b.Min.X + gap;
                var h = b.Max.Y - b.Min.Y + gap;
                foreach (var f in free)
                {
                    if (w > f.W + 1e-9 || h > f.H + 1e-9) continue;
                    var score = Math.Min(f.W - w, f.H - h) + 1e-3 * (f.Y + f.X * 0.01);
                    if (best is null || score < best.Value.Score) best = (f.X, f.Y, turn, score);
                }
            }
            if (best is null) return null;
            var chosen = best.Value.Turn ? item with { Placement = item.Placement with { Rotation = Normalize(item.Placement.Rotation + 90) } } : item;
            var cb = Bounds(chosen, settings);
            var dx = wall + best.Value.X - cb.Min.X;
            var dy = wall + best.Value.Y - cb.Min.Y;
            chosen = chosen with { Placement = chosen.Placement with { X = chosen.Placement.X + dx, Y = chosen.Placement.Y + dy } };
            result.Add(chosen);
            SplitFree(free, (best.Value.X, best.Value.Y, cb.Max.X - cb.Min.X + gap, cb.Max.Y - cb.Min.Y + gap));
        }
        return result;
    }

    static void SplitFree(List<(double X, double Y, double W, double H)> free, (double X, double Y, double W, double H) used)
    {
        var next = new List<(double X, double Y, double W, double H)>();
        foreach (var f in free)
        {
            if (used.X >= f.X + f.W || used.X + used.W <= f.X || used.Y >= f.Y + f.H || used.Y + used.H <= f.Y)
            {
                next.Add(f);
                continue;
            }
            if (used.X > f.X) next.Add((f.X, f.Y, used.X - f.X, f.H));
            if (used.X + used.W < f.X + f.W) next.Add((used.X + used.W, f.Y, f.X + f.W - used.X - used.W, f.H));
            if (used.Y > f.Y) next.Add((f.X, f.Y, f.W, used.Y - f.Y));
            if (used.Y + used.H < f.Y + f.H) next.Add((f.X, used.Y + used.H, f.W, f.Y + f.H - used.Y - used.H));
        }
        // Drop rectangles inside others.
        free.Clear();
        for (var i = 0; i < next.Count; i++)
        {
            var a = next[i];
            if (a.W <= 1e-6 || a.H <= 1e-6) continue;
            var contained = false;
            for (var j = 0; j < next.Count && !contained; j++)
            {
                if (i == j) continue;
                var b = next[j];
                if (a.X >= b.X - 1e-9 && a.Y >= b.Y - 1e-9 && a.X + a.W <= b.X + b.W + 1e-9 && a.Y + a.H <= b.Y + b.H + 1e-9 && (j < i || a != b)) contained = true;
            }
            if (!contained) free.Add(a);
        }
    }

    /// <summary>Moves the arranged group to the middle of the bin.</summary>
    static List<PlacedTool> Centre(List<PlacedTool> placed, int gx, int gy, BinSettings settings)
    {
        if (placed.Count == 0) return placed;
        var x0 = placed.Min(t => Bounds(t, settings).Min.X);
        var x1 = placed.Max(t => Bounds(t, settings).Max.X);
        var y0 = placed.Min(t => Bounds(t, settings).Min.Y);
        var y1 = placed.Max(t => Bounds(t, settings).Max.Y);
        var dx = (gx * Spec.Pitch - Spec.Gap) / 2 - (x0 + x1) / 2;
        var dy = (gy * Spec.Pitch - Spec.Gap) / 2 - (y0 + y1) / 2;
        return placed.Select(t => t with { Placement = t.Placement with { X = Math.Round(t.Placement.X + dx, 3), Y = Math.Round(t.Placement.Y + dy, 3) } }).ToList();
    }

    // ------------------------------------------------------------------ 2D regions (Clipper, 1 unit = 1 µm)

    public const double Scale = 1000;

    /// <summary>Pockets fill in gaps up to twice this wide, mm.</summary>
    const double FillGap = 2;

    public static Path64 ToPath(IEnumerable<Vec2> mm) => new(mm.Select(p => new Point64((long)Math.Round(p.X * Scale), (long)Math.Round(p.Y * Scale))));

    public static Path64 Circle(Vec2 centre, double radius, int segments = 0)
    {
        var n = segments > 0 ? segments : Math.Clamp((int)Math.Ceiling(2 * Math.PI * radius / 0.6), 24, 128);
        var path = new Path64(n);
        for (var i = 0; i < n; i++)
        {
            var a = 2 * Math.PI * i / n;
            path.Add(new Point64((long)Math.Round((centre.X + radius * Math.Cos(a)) * Scale), (long)Math.Round((centre.Y + radius * Math.Sin(a)) * Scale)));
        }
        return path;
    }

    /// <summary>A tool's pocket (outline grown by the clearance, plus its finger holes), in bin coordinates.</summary>
    public static Paths64 Pocket(PlacedTool t, BinSettings settings)
    {
        // Grown by the clearance, with slits and notches narrower than 4 mm filled in (they'd only print as
        // fragile slivers): grow 2 mm further, then shrink back by 2 mm.
        var outline = new Paths64 { ToPath(t.OutlineInBin()) };
        var grown = Clipper.InflatePaths(
            Clipper.InflatePaths(outline, (settings.Clearance + FillGap) * Scale, JoinType.Round, EndType.Polygon, 2.0, 0.02 * Scale),
            -FillGap * Scale, JoinType.Round, EndType.Polygon, 2.0, 0.02 * Scale);
        if (t.FingerHoles.Count == 0) return grown;
        var holes = new Paths64(t.FingerHoles.Select(h => Circle(t.ToBin(new Vec2(h.X, h.Y)), h.Diameter / 2)));
        return Clipper.Union(grown, holes, FillRule.NonZero);
    }

    /// <summary>A finger slot as a region: the bar with rounded ends, in bin coordinates.</summary>
    public static Paths64 SlotRegion(FingerSlot slot)
    {
        if (Vec2.Distance(slot.A, slot.B) < 0.01) return new Paths64 { Circle(slot.A, slot.Width / 2) };
        return Clipper.InflatePaths(new Paths64 { ToPath(new[] { slot.A, slot.B }) }, slot.Width / 2 * Scale, JoinType.Round, EndType.Round, 2.0, 0.02 * Scale);
    }

    /// <summary>How deep a slot is cut: as deep as the deepest pocket it crosses (or the bin's depth).</summary>
    public static double SlotDepth(FingerSlot slot, BinDesign design)
    {
        var region = SlotRegion(slot);
        var depth = 0.0;
        foreach (var t in design.Tools)
            if (Clipper.Intersect(region, Pocket(t, design.Settings), FillRule.NonZero).Count > 0) depth = Math.Max(depth, t.Depth);
        return depth > 0 ? depth : design.Settings.Depth;
    }

    /// <summary>A label's engraved strokes as regions, in bin coordinates.</summary>
    public static Paths64 LabelRegion(BinLabel label)
    {
        var strokes = StrokeFont.Layout(label.Text, label.Height, new Vec2(label.X, label.Y), label.Rotation);
        var paths = new Paths64(strokes.Select(ToPath));
        var width = Math.Max(0.8, label.Height * 0.14);
        return Clipper.InflatePaths(paths, width / 2 * Scale, JoinType.Round, EndType.Round, 2.0, 0.02 * Scale);
    }
}
