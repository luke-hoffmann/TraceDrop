using Tracedrop.Core;
using Tracedrop.Core.Editing;
using Tracedrop.Core.Vector;

namespace TracedropApp;

/// <summary>The tools of the editor. The letter is the keyboard shortcut.</summary>
enum EditTool
{
    /// <summary>V: select lines (yours or the AI's), move yours, delete, keep only.</summary>
    Select,
    /// <summary>A: drag points and handles; clicking an AI line takes it over as yours.</summary>
    Points,
    /// <summary>P: draw your own line: click for corners, drag for curves.</summary>
    Pen,
    /// <summary>F: mark where lines are missing; the AI looks again there, zoomed in.</summary>
    Find,
    /// <summary>E: mark where there should be no lines.</summary>
    Erase,
    /// <summary>H: pan (or hold Space with any tool).</summary>
    Hand
}

/// <summary>
/// The hand-made part of one picture: Find / Erase / deleted-line areas, your own paths, and shape picks.
/// Treated as immutable once committed: a change makes a <see cref="Copy"/> and edits that.
/// </summary>
sealed class EditState
{
    public List<GuideRegion> Regions { get; init; } = new();
    public List<ManualPath> Manual { get; init; } = new();
    public List<PickPoint> Picks { get; init; } = new();

    public static readonly EditState Empty = new();

    public bool IsEmpty => Regions.Count == 0 && Manual.Count == 0 && Picks.Count == 0;

    public EditState Copy() => new()
    {
        Regions = Regions.Select(r => r.Copy()).ToList(),
        Manual = Manual.Select(m => m.Copy()).ToList(),
        Picks = Picks.ToList()
    };

    public static EditState From(TraceSettings s) => new()
    {
        Regions = s.Regions.Select(r => r.Copy()).ToList(),
        Manual = s.Manual.Select(m => m.Copy()).ToList(),
        Picks = s.Picks.ToList()
    };

    public void ApplyTo(TraceSettings s)
    {
        s.Regions = Regions.Select(r => r.Copy()).ToList();
        s.Manual = Manual.Select(m => m.Copy()).ToList();
        s.Picks = Picks.ToList();
    }

    public string Key() =>
        string.Join("/", Regions.Select(r => r.Key())) + "|" + Manual.Count + ":" +
        string.Join(";", Manual.SelectMany(m => m.Nodes).Select(n => $"{n.Point.X:0.#####},{n.Point.Y:0.#####},{n.In?.X:0.#####},{n.Out?.X:0.#####}")) + "|" +
        string.Join(";", Picks.Select(p => $"{p.X:0.####},{p.Y:0.####},{p.Kind}"));
}

/// <summary>Undo and redo for one picture's edits.</summary>
sealed class EditHistory
{
    readonly Stack<EditState> _undo = new();
    readonly Stack<EditState> _redo = new();
    DateTime _lastCoalesce;
    object? _coalesceTag;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    /// <summary>
    /// Records that the edits are about to change from <paramref name="before"/>. Steps with the same
    /// <paramref name="coalesceTag"/> within a second (scrolling an area's amount) become one undo step.
    /// </summary>
    public void Record(EditState before, object? coalesceTag = null)
    {
        var now = DateTime.UtcNow;
        var merge = coalesceTag is not null && Equals(coalesceTag, _coalesceTag) && (now - _lastCoalesce).TotalSeconds < 1.0 && _undo.Count > 0;
        if (!merge) _undo.Push(before);
        _redo.Clear();
        _coalesceTag = coalesceTag;
        _lastCoalesce = now;
        // A very long session keeps the last 200 steps.
        if (_undo.Count > 200)
        {
            var keep = _undo.Take(200).Reverse().ToList();
            _undo.Clear();
            foreach (var s in keep) _undo.Push(s);
        }
    }

    public EditState? Undo(EditState current)
    {
        if (_undo.Count == 0) return null;
        _redo.Push(current);
        _coalesceTag = null;
        return _undo.Pop();
    }

    public EditState? Redo(EditState current)
    {
        if (_redo.Count == 0) return null;
        _undo.Push(current);
        _coalesceTag = null;
        return _redo.Pop();
    }
}

/// <summary>Turning selections into edits.</summary>
static class EditActions
{
    /// <summary>Strip width (in drawing pixels) used to delete a traced line so a re-trace doesn't bring it back.</summary>
    static double DeleteBrush(double width, double height) => Math.Max(3, 4 * Math.Max(width, height) / 1600.0);

    /// <summary>
    /// Deletes a traced path: a line is struck out where it runs; a filled shape (or a hole) is removed with a pick
    /// inside it, so the whole shape goes (or the hole is filled) whatever the settings.
    /// </summary>
    public static void DeleteTraced(EditState edit, VectorPath path, double width, double height)
    {
        if (path.Role != PathRole.Stroke && InteriorPoint(path) is { } inside)
        {
            edit.Picks.Add(new PickPoint(inside.X / width, inside.Y / height, PickKind.Remove));
            return;
        }
        edit.Regions.Add(EditGeometry.SuppressAlong(path.Flatten(0.5), width, height, DeleteBrush(width, height)));
    }

    /// <summary>Makes a traced path yours: an editable copy, and the traced one struck out.</summary>
    public static ManualPath TakeOver(EditState edit, VectorPath path, double width, double height)
    {
        var mine = ManualPath.FromVectorPath(path, width, height);
        edit.Regions.Add(EditGeometry.SuppressAlong(path.Flatten(0.5), width, height, DeleteBrush(width, height)));
        edit.Manual.Add(mine);
        return mine;
    }

    /// <summary>A point just inside a closed path (pixels), or null for open lines.</summary>
    public static Vec2? InteriorPoint(VectorPath path)
    {
        if (path.Circle is { } c) return c.Center;
        if (!path.Closed) return null;
        var pts = path.Flatten(0.5);
        if (pts.Count < 3) return null;
        // Try points a little to either side of the longest edges' midpoints; the first that's inside wins.
        var edges = Enumerable.Range(0, pts.Count)
            .Select(i => (A: pts[i], B: pts[(i + 1) % pts.Count]))
            .OrderByDescending(e => Vec2.Distance(e.A, e.B))
            .Take(8);
        foreach (var (a, b) in edges)
        {
            var mid = (a + b) / 2;
            var normal = (b - a).Perp().Normalized();
            foreach (var d in new[] { 1.5, -1.5, 3.0, -3.0, 6.0, -6.0 })
            {
                var p = mid + normal * d;
                if (Inside(pts, p)) return p;
            }
        }
        return null;
    }

    static bool Inside(IReadOnlyList<Vec2> poly, Vec2 p)
    {
        var inside = false;
        for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
        {
            if ((poly[i].Y > p.Y) != (poly[j].Y > p.Y) &&
                p.X < (poly[j].X - poly[i].X) * (p.Y - poly[i].Y) / (poly[j].Y - poly[i].Y) + poly[i].X)
                inside = !inside;
        }
        return inside;
    }
}
