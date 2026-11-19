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

    /// <summary>New tools get finger holes on both sides, to lift them out.</summary>
    public bool FingerHoles { get; set; } = true;

    /// <summary>Finger hole diameter, mm.</summary>
    public double FingerHoleSize { get; set; } = 22;

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
        FingerHoleSize = Math.Clamp(FingerHoleSize, 6, 60);
        MaxGrid = Math.Clamp(MaxGrid, 1, 20);
        LabelDepth = Math.Clamp(LabelDepth, 0.2, 3);
        return this;
    }
}

/// <summary>A finger hole beside a tool, in the tool's own coordinates (mm from its centre, y up).</summary>
public sealed record FingerHole(double X, double Y, double Diameter);

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
    /// <summary>Finger holes; null = automatic (from the bin settings).</summary>
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

    public bool IsEmpty => Tools.Count == 0 && Labels.Count == 0;

    public BinLayout Clone() => new() { Tools = Tools.Select(t => t.Copy()).ToList(), Labels = Labels.ToList() };

    public BinLayout Normalized()
    {
        Tools ??= new();
        Tools.RemoveAll(t => t is null || t.Anchor is null || double.IsNaN(t.X) || double.IsNaN(t.Y));
        Labels ??= new();
        Labels.RemoveAll(l => l is null || string.IsNullOrWhiteSpace(l.Text));
        return this;
    }

    public string Key() =>
        string.Join(";", Tools.Select(t => $"{t.Anchor.X:0.####},{t.Anchor.Y:0.####},{t.X:0.###},{t.Y:0.###},{t.Rotation:0.##},{t.Depth:0.##},{t.Removed}," +
                                           (t.FingerHoles is null ? "auto" : string.Join("/", t.FingerHoles.Select(f => $"{f.X:0.##}:{f.Y:0.##}:{f.Diameter:0.#}"))))) + "|" +
        string.Join(";", Labels.Select(l => $"{l.Text}@{l.X:0.##},{l.Y:0.##},{l.Height:0.##},{l.Rotation:0.#}"));
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
        Labels = Labels.ToList()
    };
}

/// <summary>Turns a traced drawing into a bin design: tools in mm, placed (kept, matched or arranged).</summary>
public static class Designer
{
    /// <summary>Smallest outline kept as a tool, mm².</summary>
    public const double MinToolArea = 40;

    /// <summary>The traced outer outlines as tools, in mm (y up), numbered left to right, top to bottom.</summary>
    public static List<ToolShape> ExtractTools(VectorDrawing drawing, double mmPerPixel)
    {
        var shapes = new List<(List<Vec2> Outline, NormPoint Anchor)>();
        foreach (var path in drawing.Paths)
        {
            var closed = path.Closed || path.Circle is not null;
            if (!closed || path.Role == PathRole.Hole) continue;
            var pixels = path.Flatten(0.25);
            if (pixels.Count < 3) continue;
            var mm = pixels.Select(p => new Vec2(p.X * mmPerPixel, -p.Y * mmPerPixel)).ToList();
            var area = Geometry2D.SignedArea(mm);
            if (Math.Abs(area) < MinToolArea) continue;
            if (area < 0) mm.Reverse(); // counter-clockwise with y up
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
                layout = new BinLayout { Tools = layout.Tools, Labels = layout.Labels.Select(l => l with { X = l.X + dx, Y = l.Y + dy }).ToList() };
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
        var holes = placement.FingerHoles ?? (settings.FingerHoles ? AutoFingerHoles(tool, settings.FingerHoleSize) : new List<FingerHole>());
        var depth = placement.Depth > 0 ? placement.Depth : settings.Depth;
        return new PlacedTool(tool, placement, holes, depth);
    }

    /// <summary>
    /// Two finger holes across the tool's narrow middle: on the line through its centre square to its long axis,
    /// centred where that line leaves the outline (half the hole in the pocket, half beside it).
    /// </summary>
    public static List<FingerHole> AutoFingerHoles(ToolShape tool, double diameter)
    {
        var angle = MinAreaAngle(tool.Outline);
        // Long axis direction after rotating by -angle: pick the axis with the larger extent.
        var (c, s) = (Math.Cos(angle), Math.Sin(angle));
        var xs = tool.Outline.Select(p => p.X * c + p.Y * s).ToList();
        var ys = tool.Outline.Select(p => -p.X * s + p.Y * c).ToList();
        var longAlongX = xs.Max() - xs.Min() >= ys.Max() - ys.Min();
        var across = longAlongX ? new Vec2(-s, c) : new Vec2(c, s); // direction across the tool
        var centre = new Vec2(0, 0);
        var holes = new List<FingerHole>();
        foreach (var sign in new[] { 1.0, -1.0 })
        {
            var dir = across * sign;
            // Furthest crossing of the outline along this ray (the tool's edge on that side).
            var best = double.NaN;
            for (var i = 0; i < tool.Outline.Count; i++)
            {
                var a = tool.Outline[i];
                var b = tool.Outline[(i + 1) % tool.Outline.Count];
                if (RayHit(centre, dir, a, b) is { } t && (double.IsNaN(best) || t > best)) best = t;
            }
            if (double.IsNaN(best)) continue;
            var at = centre + dir * best;
            holes.Add(new FingerHole(Math.Round(at.X, 3), Math.Round(at.Y, 3), diameter));
        }
        return holes;
    }

    static double? RayHit(Vec2 origin, Vec2 dir, Vec2 a, Vec2 b)
    {
        var e = b - a;
        var denom = Vec2.Cross(dir, e);
        if (Math.Abs(denom) < 1e-12) return null;
        var w = a - origin;
        var t = Vec2.Cross(w, e) / denom;
        var u = Vec2.Cross(w, dir) / denom;
        return t >= 0 && u >= 0 && u <= 1 ? t : null;
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
    /// Arranges tools from scratch: each turned to its narrowest box, packed (MaxRects) into the smallest bin
    /// that holds them all, then centred.
    /// </summary>
    public static (int GridX, int GridY, List<PlacedTool> Placed) Arrange(List<ToolShape> tools, BinSettings settings, List<string>? notes = null)
    {
        var wall = Spec.Wall(settings.StackingLip);
        var items = tools.Select(t =>
        {
            var angle = -MinAreaAngle(t.Outline) * 180 / Math.PI;
            var r = Resolve(t, new ToolPlacement { Anchor = t.Anchor, Rotation = Normalize(angle) }, settings);
            // Lie long side across (X): turn a further 90° if it came out tall.
            var b = Bounds(r, settings);
            if (b.Max.Y - b.Min.Y > b.Max.X - b.Min.X) r = r with { Placement = r.Placement with { Rotation = Normalize(r.Placement.Rotation + 90) } };
            return r;
        }).ToList();
        if (items.Count == 0)
        {
            var gx0 = settings.GridX > 0 ? settings.GridX : 1;
            var gy0 = settings.GridY > 0 ? settings.GridY : 1;
            return (gx0, gy0, items);
        }

        var candidates = new List<(int X, int Y)>();
        if (settings.GridX > 0 && settings.GridY > 0) candidates.Add((settings.GridX, settings.GridY));
        else
        {
            var max = Math.Max(settings.MaxGrid, Math.Max(settings.GridX, settings.GridY));
            for (var x = 1; x <= max; x++)
                for (var y = 1; y <= max; y++)
                    if ((settings.GridX == 0 || x == settings.GridX) && (settings.GridY == 0 || y == settings.GridY)) candidates.Add((x, y));
            candidates = candidates.OrderBy(c => c.X * c.Y).ThenBy(c => Math.Abs(c.X - c.Y)).ThenByDescending(c => c.X).ToList();
        }

        foreach (var (gx, gy) in candidates)
        {
            var packed = Pack(items, gx, gy, settings, wall, Array.Empty<PlacedTool>());
            if (packed is not null) return (gx, gy, Centre(packed, gx, gy, settings));
        }
        // Too big for the largest automatic bin: use it anyway and line the rest up beside it.
        var (fx, fy) = candidates[^1];
        notes?.Add($"The tools don't all fit in a {fx}×{fy} bin: set a bigger size, or leave some out.");
        var x0 = wall;
        var result = new List<PlacedTool>();
        foreach (var t in items)
        {
            var b = Bounds(t, settings);
            result.Add(t with { Placement = t.Placement with { X = x0 + (t.Placement.X - b.Min.X), Y = wall + (t.Placement.Y - b.Min.Y) } });
            x0 += b.Max.X - b.Min.X + settings.Spacing;
        }
        return (fx, fy, result);
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
        var outline = new Paths64 { ToPath(t.OutlineInBin()) };
        var grown = settings.Clearance > 1e-6
            ? Clipper.InflatePaths(outline, settings.Clearance * Scale, JoinType.Round, EndType.Polygon, 2.0, 0.02 * Scale)
            : Clipper.Union(outline, FillRule.NonZero);
        if (t.FingerHoles.Count == 0) return grown;
        var holes = new Paths64(t.FingerHoles.Select(h => Circle(t.ToBin(new Vec2(h.X, h.Y)), h.Diameter / 2)));
        return Clipper.Union(grown, holes, FillRule.NonZero);
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
