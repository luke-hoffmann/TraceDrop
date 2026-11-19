using System.ComponentModel;
using System.Drawing.Drawing2D;
using Clipper2Lib;
using Tracedrop.Core.Editing;
using Tracedrop.Core.Gridfinity;
using Tracedrop.Core.Vector;

namespace TracedropApp;

/// <summary>What a click does in the bin view.</summary>
enum BinTool
{
    /// <summary>Drag tools, finger holes and labels; scroll over a tool turns it.</summary>
    Move,
    /// <summary>Click beside a tool to add a finger hole.</summary>
    FingerHole,
    /// <summary>Click to add an engraved label.</summary>
    Label
}

/// <summary>
/// The bin from above: grid, walls, every tool's pocket and finger holes, labels. Drag a tool to move it, scroll
/// over it to turn it, drag or scroll finger holes and labels. Every finished change is reported as a new layout.
/// </summary>
sealed class BinView : Control
{
    BinDesign? _design;
    readonly List<PlacedTool> _tools = new();
    readonly List<BinLabel> _labels = new();
    int _selTool = -1;
    int _selHole = -1;
    int _selLabel = -1;
    int _hoverTool = -1;
    BinTool _tool = BinTool.Move;

    double _scale = 1;
    PointF _offset;
    bool _fitted = true;
    enum Drag { None, Tool, Hole, Label, Pan }
    Drag _drag;
    Point _downAt;
    PointF _downOffset;
    Vec2 _lastMm;
    bool _moved;

    static readonly Color BinFill = Color.FromArgb(236, 241, 249);
    static readonly Color GridLine = Color.FromArgb(205, 214, 228);
    static readonly Color PocketFill = Color.FromArgb(198, 214, 240);
    static readonly Color ToolInk = Color.FromArgb(40, 52, 70);
    static readonly Color Problem = Color.FromArgb(214, 64, 64);

    public BinView()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        BackColor = Palette.Surface;
        TabStop = true;
    }

    /// <summary>A finished change: the whole new layout (every tool frozen where it is), with a tag for merging undo steps.</summary>
    public event Action<BinLayout, object?>? LayoutChanged;

    /// <summary>The Label tool was clicked here (bin mm): ask for the text.</summary>
    public event Action<Vec2>? LabelRequested;

    /// <summary>A label was double-clicked: ask for new text.</summary>
    public event Action<int, string>? LabelEditRequested;

    public event Action? SelectionChanged;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public BinTool Tool
    {
        get => _tool;
        set { _tool = value; Cursor = value == BinTool.Move ? Cursors.Default : Cursors.Cross; Invalidate(); }
    }

    public BinDesign? Design => _design;

    /// <summary>The selected tool, or null.</summary>
    public PlacedTool? SelectedTool => _selTool >= 0 && _selTool < _tools.Count ? _tools[_selTool] : null;

    public bool HasSelection => _selTool >= 0 || _selLabel >= 0;

    public void SetDesign(BinDesign? design)
    {
        if (_drag is Drag.Tool or Drag.Hole or Drag.Label) return; // the drag's working copy wins
        var sizeChanged = design is null || _design is null || design.GridX != _design.GridX || design.GridY != _design.GridY;
        _design = design;
        _tools.Clear();
        _labels.Clear();
        if (design is not null)
        {
            _tools.AddRange(design.Tools);
            _labels.AddRange(design.Labels);
        }
        if (_selTool >= _tools.Count) _selTool = -1;
        if (_selLabel >= _labels.Count) _selLabel = -1;
        if (_selTool < 0 || _selHole >= _tools[_selTool].FingerHoles.Count) _selHole = -1;
        if (sizeChanged) _fitted = true;
        SelectionChanged?.Invoke();
        Invalidate();
    }

    public void FitToView()
    {
        _fitted = true;
        Invalidate();
    }

    // ------------------------------------------------------------------ coordinates (mm, y up)

    void EnsureFit()
    {
        if (!_fitted || _design is null) return;
        var (x0, y0, x1, y1) = Extent();
        var pad = this.Px(28);
        _scale = Math.Min((Width - 2 * pad) / Math.Max(1, x1 - x0), (Height - 2 * pad) / Math.Max(1, y1 - y0));
        _scale = Math.Max(0.05, _scale);
        _offset = new PointF((float)(Width / 2.0 - (x0 + x1) / 2 * _scale), (float)(Height / 2.0 + (y0 + y1) / 2 * _scale));
    }

    (double X0, double Y0, double X1, double Y1) Extent()
    {
        var d = _design!;
        double x0 = 0, y0 = 0, x1 = d.Width, y1 = d.Depth;
        foreach (var t in _tools)
        {
            var b = Designer.Bounds(t, d.Settings);
            x0 = Math.Min(x0, b.Min.X); y0 = Math.Min(y0, b.Min.Y); x1 = Math.Max(x1, b.Max.X); y1 = Math.Max(y1, b.Max.Y);
        }
        return (x0 - 4, y0 - 4, x1 + 4, y1 + 4);
    }

    PointF ToScreen(Vec2 mm) => new((float)(_offset.X + mm.X * _scale), (float)(_offset.Y - mm.Y * _scale));
    Vec2 ToMm(PointF p) => new((p.X - _offset.X) / _scale, (_offset.Y - p.Y) / _scale);

    // ------------------------------------------------------------------ painting

    protected override void OnPaintBackground(PaintEventArgs pevent) { }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        if (_design is null)
        {
            const TextFormatFlags flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak;
            TextRenderer.DrawText(g, EmptyText, UiFonts.Body, ClientRectangle, Palette.Muted, flags);
            return;
        }
        EnsureFit();
        var d = _design;

        // The bin, its grid and the line pockets must stay inside.
        using (var bin = ScreenPath(RoundRect(0, 0, d.Width, d.Depth, Spec.OuterRadius)))
        using (var fill = new SolidBrush(BinFill))
        using (var edge = new Pen(Color.FromArgb(150, 165, 190), Math.Max(1.5f, this.Px(2))))
        {
            g.FillPath(fill, bin);
            using var grid = new Pen(GridLine, 1f) { DashPattern = new[] { 4f, 4f } };
            for (var i = 1; i < d.GridX; i++) g.DrawLine(grid, ToScreen(new Vec2(i * Spec.Pitch - Spec.Gap / 2, 0)), ToScreen(new Vec2(i * Spec.Pitch - Spec.Gap / 2, d.Depth)));
            for (var j = 1; j < d.GridY; j++) g.DrawLine(grid, ToScreen(new Vec2(0, j * Spec.Pitch - Spec.Gap / 2)), ToScreen(new Vec2(d.Width, j * Spec.Pitch - Spec.Gap / 2)));
            g.DrawPath(edge, bin);
        }
        using (var wall = ScreenPath(RoundRect(d.Wall, d.Wall, d.Width - d.Wall, d.Depth - d.Wall, Math.Max(0.5, Spec.OuterRadius - d.Wall))))
        using (var pen = new Pen(Color.FromArgb(170, 182, 200), 1f) { DashPattern = new[] { 2f, 3f } })
            g.DrawPath(pen, wall);

        // Pockets, tool outlines, finger holes.
        for (var i = 0; i < _tools.Count; i++)
        {
            var t = _tools[i];
            var outside = IsOutside(t);
            using (var pocket = ScreenPaths(Designer.Pocket(t, d.Settings)))
            using (var fill = new SolidBrush(outside ? Color.FromArgb(70, Problem) : PocketFill))
            using (var edge = new Pen(i == _selTool ? Palette.Accent : outside ? Problem : Color.FromArgb(120, 146, 190), i == _selTool ? Math.Max(2f, this.Px(2)) : 1.2f))
            {
                g.FillPath(fill, pocket);
                g.DrawPath(edge, pocket);
            }
            using (var outline = new GraphicsPath())
            {
                outline.AddPolygon(t.OutlineInBin().Select(ToScreen).ToArray());
                using var ink = new Pen(i == _hoverTool || i == _selTool ? Palette.Accent : ToolInk, 1.3f);
                g.DrawPath(ink, outline);
            }
            for (var h = 0; h < t.FingerHoles.Count; h++)
            {
                if (i != _selTool) continue;
                var hole = t.FingerHoles[h];
                var c = ToScreen(t.ToBin(new Vec2(hole.X, hole.Y)));
                var r = (float)(hole.Diameter / 2 * _scale);
                using var pen = new Pen(h == _selHole ? Palette.Accent : Color.FromArgb(120, Palette.Accent), h == _selHole ? 2f : 1f) { DashPattern = new[] { 3f, 2f } };
                g.DrawEllipse(pen, c.X - r, c.Y - r, 2 * r, 2 * r);
            }
            // Name and depth in the middle.
            var centre = ToScreen(new Vec2(t.Placement.X, t.Placement.Y));
            var text = $"{t.Tool.Name}\n{t.Depth:0.#} mm deep";
            var size = TextRenderer.MeasureText(g, text, UiFonts.Caption);
            if (size.Width < t.Tool.Width * _scale * 1.2 || i == _selTool)
                TextRenderer.DrawText(g, text, UiFonts.Caption, new Rectangle((int)(centre.X - size.Width / 2), (int)(centre.Y - size.Height / 2), size.Width, size.Height),
                    i == _selTool ? Palette.Accent : Palette.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding);
        }

        // Labels.
        for (var i = 0; i < _labels.Count; i++)
        {
            using var region = ScreenPaths(Designer.LabelRegion(_labels[i]));
            using var brush = new SolidBrush(i == _selLabel ? Palette.Accent : Color.FromArgb(90, 100, 120));
            g.FillPath(brush, region);
            if (i == _selLabel)
            {
                var b = LabelBox(_labels[i]);
                using var pen = new Pen(Palette.Accent, 1f) { DashPattern = new[] { 3f, 2f } };
                g.DrawPolygon(pen, b.Select(ToScreen).ToArray());
            }
        }

        // Size of the bin, under it.
        var caption = $"{d.GridX} × {d.GridY} grid · {d.HeightUnits} units high ({d.Height:0} mm{(d.Settings.StackingLip ? " + lip" : "")}) · {d.Width:0.#} × {d.Depth:0.#} mm";
        var bottom = ToScreen(new Vec2(d.Width / 2, 0));
        var captionSize = TextRenderer.MeasureText(g, caption, UiFonts.Small);
        TextRenderer.DrawText(g, caption, UiFonts.Small, new Point((int)(bottom.X - captionSize.Width / 2), (int)bottom.Y + this.Px(6)), Palette.Muted);
    }

    public string EmptyText { get; set; } = "Turn on “Make a Gridfinity bin” in the settings to design a bin from the traced tools.";

    bool IsOutside(PlacedTool t)
    {
        var d = _design!;
        var b = Designer.Bounds(t, d.Settings);
        return b.Min.X < d.Wall - 0.01 || b.Min.Y < d.Wall - 0.01 || b.Max.X > d.Width - d.Wall + 0.01 || b.Max.Y > d.Depth - d.Wall + 0.01;
    }

    GraphicsPath ScreenPath(IReadOnlyList<Vec2> mm)
    {
        var gp = new GraphicsPath();
        if (mm.Count >= 3) gp.AddPolygon(mm.Select(ToScreen).ToArray());
        return gp;
    }

    GraphicsPath ScreenPaths(Paths64 region)
    {
        var gp = new GraphicsPath(FillMode.Winding);
        foreach (var p in region)
            if (p.Count >= 3) gp.AddPolygon(p.Select(q => ToScreen(new Vec2(q.X / Designer.Scale, q.Y / Designer.Scale))).ToArray());
        return gp;
    }

    static List<Vec2> RoundRect(double x0, double y0, double x1, double y1, double r)
    {
        var pts = new List<Vec2>();
        foreach (var (cx, cy, start) in new[] { (x1 - r, y0 + r, -90.0), (x1 - r, y1 - r, 0.0), (x0 + r, y1 - r, 90.0), (x0 + r, y0 + r, 180.0) })
            for (var i = 0; i <= 6; i++)
            {
                var a = (start + 15.0 * i) * Math.PI / 180;
                pts.Add(new Vec2(cx + r * Math.Cos(a), cy + r * Math.Sin(a)));
            }
        return pts;
    }

    static Vec2[] LabelBox(BinLabel l)
    {
        var w = StrokeFont.Width(l.Text, l.Height) / 2 + l.Height * 0.2;
        var h = l.Height / 2 + l.Height * 0.2;
        var a = l.Rotation * Math.PI / 180;
        var (c, s) = (Math.Cos(a), Math.Sin(a));
        return new[] { (-w, -h), (w, -h), (w, h), (-w, h) }.Select(p => new Vec2(l.X + p.Item1 * c - p.Item2 * s, l.Y + p.Item1 * s + p.Item2 * c)).ToArray();
    }

    // ------------------------------------------------------------------ hit testing

    int HitTool(Vec2 mm)
    {
        var tolerance = 2.5 + (_design?.Settings.Clearance ?? 0);
        for (var i = _tools.Count - 1; i >= 0; i--)
        {
            var outline = _tools[i].OutlineInBin();
            if (Inside(outline, mm)) return i;
            for (var k = 0; k < outline.Count; k++)
                if (SegmentDistance(mm, outline[k], outline[(k + 1) % outline.Count]) <= tolerance) return i;
        }
        return -1;
    }

    int HitHole(Vec2 mm)
    {
        if (_selTool < 0) return -1;
        var t = _tools[_selTool];
        for (var h = 0; h < t.FingerHoles.Count; h++)
        {
            var c = t.ToBin(new Vec2(t.FingerHoles[h].X, t.FingerHoles[h].Y));
            if (Vec2.Distance(c, mm) <= t.FingerHoles[h].Diameter / 2) return h;
        }
        return -1;
    }

    int HitLabel(Vec2 mm)
    {
        for (var i = _labels.Count - 1; i >= 0; i--)
            if (Inside(LabelBox(_labels[i]), mm)) return i;
        return -1;
    }

    static bool Inside(IReadOnlyList<Vec2> poly, Vec2 p)
    {
        var inside = false;
        for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
            if ((poly[i].Y > p.Y) != (poly[j].Y > p.Y) && p.X < (poly[j].X - poly[i].X) * (p.Y - poly[i].Y) / (poly[j].Y - poly[i].Y) + poly[i].X)
                inside = !inside;
        return inside;
    }

    static double SegmentDistance(Vec2 p, Vec2 a, Vec2 b)
    {
        var ab = b - a;
        var t = ab.LengthSquared < 1e-12 ? 0 : Math.Clamp(Vec2.Dot(p - a, ab) / ab.LengthSquared, 0, 1);
        return Vec2.Distance(p, a + ab * t);
    }

    // ------------------------------------------------------------------ editing

    BinLayout CurrentLayout() => new()
    {
        Tools = _tools.Select(t => t.Placement with { FingerHoles = t.FingerHoles.ToList() })
            .Concat(_design?.LeftOut.Select(t => new ToolPlacement { Anchor = t.Anchor, Removed = true }) ?? Enumerable.Empty<ToolPlacement>()).ToList(),
        Labels = _labels.ToList()
    };

    void Commit(object? tag = null)
    {
        LayoutChanged?.Invoke(CurrentLayout(), tag);
        Invalidate();
    }

    void UpdateTool(int i, Func<ToolPlacement, ToolPlacement> change, List<FingerHole>? holes = null)
    {
        var t = _tools[i];
        var placement = change(t.Placement);
        var h = holes ?? t.FingerHoles;
        _tools[i] = t with { Placement = placement with { FingerHoles = h.ToList() }, FingerHoles = h.ToList(), Depth = placement.Depth > 0 ? placement.Depth : _design!.Settings.Depth };
    }

    public void RotateSelected(double degrees)
    {
        if (_selTool < 0) return;
        UpdateTool(_selTool, p => p with { Rotation = Math.Round(((p.Rotation + degrees) % 360 + 360) % 360, 3) });
        Commit(("rotate", _selTool));
    }

    /// <summary>Delete: a finger hole or label goes; a tool is left out of the bin.</summary>
    public void DeleteSelected()
    {
        if (_selLabel >= 0)
        {
            _labels.RemoveAt(_selLabel);
            _selLabel = -1;
            Commit();
        }
        else if (_selTool >= 0 && _selHole >= 0)
        {
            var holes = _tools[_selTool].FingerHoles.ToList();
            holes.RemoveAt(_selHole);
            UpdateTool(_selTool, p => p, holes);
            _selHole = -1;
            Commit();
        }
        else if (_selTool >= 0) LeaveOutSelected();
        SelectionChanged?.Invoke();
    }

    public void LeaveOutSelected()
    {
        if (_selTool < 0) return;
        var layout = CurrentLayout();
        layout.Tools[_selTool] = layout.Tools[_selTool] with { Removed = true };
        _selTool = -1;
        _selHole = -1;
        LayoutChanged?.Invoke(layout, null);
        SelectionChanged?.Invoke();
    }

    public void SetSelectedDepth(double depth)
    {
        if (_selTool < 0) return;
        UpdateTool(_selTool, p => p with { Depth = depth });
        Commit(("depth", _selTool));
    }

    public void AddLabel(string text, Vec2 at)
    {
        _labels.Add(new BinLabel(text, at.X, at.Y, 6));
        _selLabel = _labels.Count - 1;
        _selTool = -1;
        Commit();
        SelectionChanged?.Invoke();
    }

    public void SetLabelText(int index, string text)
    {
        if (index < 0 || index >= _labels.Count) return;
        _labels[index] = _labels[index] with { Text = text };
        Commit();
    }

    // ------------------------------------------------------------------ mouse and keys

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        if (_design is null) return;
        EnsureFit();
        _downAt = e.Location;
        _downOffset = _offset;
        _moved = false;
        var mm = ToMm(e.Location);
        _lastMm = mm;
        if (e.Button is MouseButtons.Middle or MouseButtons.Right) { _drag = Drag.Pan; return; }
        if (e.Button != MouseButtons.Left) return;

        switch (_tool)
        {
            case BinTool.FingerHole:
            {
                var i = HitTool(mm);
                if (i < 0) { i = NearestTool(mm); if (i < 0) return; }
                var t = _tools[i];
                var local = t.ToLocal(mm);
                var holes = t.FingerHoles.ToList();
                holes.Add(new FingerHole(Math.Round(local.X, 2), Math.Round(local.Y, 2), _design.Settings.FingerHoleSize));
                UpdateTool(i, p => p, holes);
                _selTool = i;
                _selHole = holes.Count - 1;
                _selLabel = -1;
                Commit();
                SelectionChanged?.Invoke();
                return;
            }
            case BinTool.Label:
                LabelRequested?.Invoke(mm);
                return;
        }

        var hole = HitHole(mm);
        if (hole >= 0) { _selHole = hole; _drag = Drag.Hole; Invalidate(); return; }
        var label = HitLabel(mm);
        if (label >= 0) { _selLabel = label; _selTool = -1; _selHole = -1; _drag = Drag.Label; SelectionChanged?.Invoke(); Invalidate(); return; }
        var tool = HitTool(mm);
        _selLabel = -1;
        _selHole = -1;
        _selTool = tool;
        _drag = tool >= 0 ? Drag.Tool : Drag.Pan;
        SelectionChanged?.Invoke();
        Invalidate();
    }

    int NearestTool(Vec2 mm)
    {
        var best = -1;
        var bestDistance = 25.0;
        for (var i = 0; i < _tools.Count; i++)
        {
            var outline = _tools[i].OutlineInBin();
            for (var k = 0; k < outline.Count; k++)
            {
                var d = SegmentDistance(mm, outline[k], outline[(k + 1) % outline.Count]);
                if (d < bestDistance) { bestDistance = d; best = i; }
            }
        }
        return best;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_design is null) return;
        var mm = ToMm(e.Location);
        if (_drag == Drag.None)
        {
            var hover = HitTool(mm);
            if (hover != _hoverTool) { _hoverTool = hover; Invalidate(); }
            Cursor = _tool != BinTool.Move ? Cursors.Cross : hover >= 0 || HitLabel(mm) >= 0 ? Cursors.SizeAll : Cursors.Default;
            return;
        }
        var dx = e.X - _downAt.X;
        var dy = e.Y - _downAt.Y;
        if (!_moved && dx * dx + dy * dy < this.Px(3) * this.Px(3)) return;
        _moved = true;
        var step = mm - _lastMm;
        _lastMm = mm;
        switch (_drag)
        {
            case Drag.Pan:
                _offset = new PointF(_downOffset.X + dx, _downOffset.Y + dy);
                _fitted = false;
                break;
            case Drag.Tool when _selTool >= 0:
                UpdateTool(_selTool, p => p with { X = Math.Round(p.X + step.X, 3), Y = Math.Round(p.Y + step.Y, 3) });
                break;
            case Drag.Hole when _selTool >= 0 && _selHole >= 0:
            {
                var t = _tools[_selTool];
                var local = t.ToLocal(mm);
                var holes = t.FingerHoles.ToList();
                holes[_selHole] = holes[_selHole] with { X = Math.Round(local.X, 2), Y = Math.Round(local.Y, 2) };
                UpdateTool(_selTool, p => p, holes);
                break;
            }
            case Drag.Label when _selLabel >= 0:
                _labels[_selLabel] = _labels[_selLabel] with { X = Math.Round(_labels[_selLabel].X + step.X, 3), Y = Math.Round(_labels[_selLabel].Y + step.Y, 3) };
                break;
        }
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        var drag = _drag;
        _drag = Drag.None;
        if (_moved && drag is Drag.Tool or Drag.Hole or Drag.Label) Commit();
    }

    protected override void OnDoubleClick(EventArgs e)
    {
        base.OnDoubleClick(e);
        if (_design is null) return;
        var mm = ToMm(PointToClient(MousePosition));
        var label = HitLabel(mm);
        if (label >= 0) LabelEditRequested?.Invoke(label, _labels[label].Text);
        else if (HitTool(mm) < 0) FitToView();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_design is null) return;
        var mm = ToMm(e.Location);
        var steps = Math.Sign(e.Delta);
        var ctrl = (ModifierKeys & Keys.Control) != 0;
        if (!ctrl && steps != 0)
        {
            // Scroll over a finger hole: its size. Over a label: its size. Over a tool: turn it (5°, Shift 1°).
            var hole = HitHole(mm);
            if (hole >= 0)
            {
                var holes = _tools[_selTool].FingerHoles.ToList();
                holes[hole] = holes[hole] with { Diameter = Math.Clamp(holes[hole].Diameter + steps, 6, 60) };
                UpdateTool(_selTool, p => p, holes);
                _selHole = hole;
                Commit(("hole", _selTool, hole));
                return;
            }
            var label = HitLabel(mm);
            if (label >= 0)
            {
                _labels[label] = _labels[label] with { Height = Math.Clamp(_labels[label].Height + 0.5 * steps, 3, 30) };
                _selLabel = label;
                Commit(("label", label));
                return;
            }
            var tool = HitTool(mm);
            if (tool >= 0)
            {
                _selTool = tool;
                var degrees = (ModifierKeys & Keys.Shift) != 0 ? 1 : 5;
                UpdateTool(tool, p => p with { Rotation = Math.Round(((p.Rotation + steps * degrees) % 360 + 360) % 360, 3) });
                Commit(("rotate", tool));
                SelectionChanged?.Invoke();
                return;
            }
        }
        EnsureFit();
        var before = mm;
        _scale = Math.Clamp(_scale * Math.Pow(1.0015, e.Delta), 0.05, 80);
        _offset = new PointF((float)(e.X - before.X * _scale), (float)(e.Y + before.Y * _scale));
        _fitted = false;
        Invalidate();
    }

    protected override bool IsInputKey(Keys keyData) =>
        (keyData & Keys.KeyCode) is Keys.Left or Keys.Right or Keys.Up or Keys.Down || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        switch (e.KeyCode)
        {
            case Keys.Delete:
            case Keys.Back:
                DeleteSelected();
                e.Handled = true;
                break;
            case Keys.R:
                RotateSelected(e.Shift ? -90 : 90);
                e.Handled = true;
                break;
            case Keys.Left or Keys.Right or Keys.Up or Keys.Down when _selTool >= 0 || _selLabel >= 0:
            {
                var step = e.Shift ? 5.0 : 0.5;
                var (dx, dy) = e.KeyCode switch { Keys.Left => (-step, 0.0), Keys.Right => (step, 0.0), Keys.Up => (0.0, step), _ => (0.0, -step) };
                if (_selLabel >= 0) _labels[_selLabel] = _labels[_selLabel] with { X = _labels[_selLabel].X + dx, Y = _labels[_selLabel].Y + dy };
                else UpdateTool(_selTool, p => p with { X = Math.Round(p.X + dx, 3), Y = Math.Round(p.Y + dy, 3) });
                Commit("nudge");
                e.Handled = true;
                break;
            }
            case Keys.Escape:
                _selTool = _selHole = _selLabel = -1;
                SelectionChanged?.Invoke();
                Invalidate();
                break;
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hoverTool < 0) return;
        _hoverTool = -1;
        Invalidate();
    }
}

/// <summary>The bin in 3D, rendered in software. Drag to turn it, scroll to zoom, double-click to reset.</summary>
sealed class Bin3DView : Control
{
    Mesh? _mesh;
    Bitmap? _image;
    bool _dirty = true;
    double _yaw = -35;
    double _pitch = 38;
    double _zoom = 1;
    Point _downAt;
    double _downYaw;
    double _downPitch;
    bool _dragging;

    public Bin3DView()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        BackColor = Palette.Surface;
        Cursor = Cursors.SizeAll;
    }

    public string EmptyText { get; set; } = "Turn on “Make a Gridfinity bin” to see the bin in 3D.";

    public void SetMesh(Mesh? mesh)
    {
        if (ReferenceEquals(mesh, _mesh)) return;
        _mesh = mesh;
        _dirty = true;
        Invalidate();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        _dirty = true;
        Invalidate();
    }

    protected override void OnPaintBackground(PaintEventArgs pevent) { }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        if (_mesh is null || Width < 8 || Height < 8)
        {
            TextRenderer.DrawText(g, EmptyText, UiFonts.Body, ClientRectangle, Palette.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
            return;
        }
        if (_dirty || _image is null)
        {
            // While turning, render at half size for speed; full size when the mouse is let go.
            var factor = _dragging ? 2 : 1;
            var bg = BackColor;
            using var mat = MeshRenderer.Render(_mesh, Math.Max(8, Width / factor), Math.Max(8, Height / factor), _yaw, _pitch, _zoom,
                background: new OpenCvSharp.Scalar(bg.B, bg.G, bg.R));
            _image?.Dispose();
            _image = TraceWorker.ToBitmap(mat);
            _dirty = false;
        }
        g.InterpolationMode = InterpolationMode.HighQualityBilinear;
        g.DrawImage(_image, ClientRectangle);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        _downAt = e.Location;
        _downYaw = _yaw;
        _downPitch = _pitch;
        _dragging = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging || e.Button == MouseButtons.None) return;
        _yaw = _downYaw + (e.X - _downAt.X) * 0.4;
        _pitch = Math.Clamp(_downPitch + (e.Y - _downAt.Y) * 0.4, -85, 89);
        _dirty = true;
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _dragging = false;
        _dirty = true;
        Invalidate();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        _zoom = Math.Clamp(_zoom * Math.Pow(1.0015, e.Delta), 0.3, 8);
        _dirty = true;
        Invalidate();
    }

    protected override void OnDoubleClick(EventArgs e)
    {
        base.OnDoubleClick(e);
        (_yaw, _pitch, _zoom) = (-35, 38, 1);
        _dirty = true;
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _image?.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>
/// The photo as taken, with the sheet of paper outlined. Drag a corner onto the sheet's corner when it wasn't found
/// right; a magnifier shows the spot while dragging. Scroll zooms, right-drag pans.
/// </summary>
sealed class PaperView : Control
{
    Bitmap? _photo;
    List<NormPoint>? _corners;
    double _scale = 1;
    PointF _offset;
    bool _fitted = true;
    int _dragCorner = -1;
    bool _panning;
    Point _downAt;
    PointF _downOffset;
    Point _mouse;

    public PaperView()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        BackColor = Palette.Surface;
    }

    /// <summary>A corner was dragged: the four corners (0..1 of the photo; TL, TR, BR, BL).</summary>
    public event Action<List<NormPoint>>? CornersChanged;

    public string Caption { get; set; } = "";

    public void SetPhoto(Bitmap? photo)
    {
        if (!ReferenceEquals(photo, _photo) && (photo is null || _photo is null || photo.Size != _photo.Size)) _fitted = true;
        _photo = photo;
        Invalidate();
    }

    public void SetCorners(IReadOnlyList<NormPoint>? corners)
    {
        if (_dragCorner >= 0) return;
        _corners = corners?.ToList();
        Invalidate();
    }

    void EnsureFit()
    {
        if (!_fitted || _photo is null) return;
        var pad = this.Px(16);
        _scale = Math.Min((Width - 2 * pad) / (double)_photo.Width, (Height - 2 * pad) / (double)_photo.Height);
        _offset = new PointF((float)((Width - _photo.Width * _scale) / 2), (float)((Height - _photo.Height * _scale) / 2));
    }

    PointF ToScreen(NormPoint n) => new((float)(_offset.X + n.X * _photo!.Width * _scale), (float)(_offset.Y + n.Y * _photo.Height * _scale));
    NormPoint ToNorm(PointF p) => new((p.X - _offset.X) / _scale / _photo!.Width, (p.Y - _offset.Y) / _scale / _photo.Height);

    protected override void OnPaintBackground(PaintEventArgs pevent) { }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        if (_photo is null)
        {
            TextRenderer.DrawText(g, "The photo appears here once it's loaded.", UiFonts.Body, ClientRectangle, Palette.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            return;
        }
        EnsureFit();
        g.InterpolationMode = _scale < 1 ? InterpolationMode.HighQualityBilinear : InterpolationMode.NearestNeighbor;
        g.DrawImage(_photo, new RectangleF(_offset.X, _offset.Y, (float)(_photo.Width * _scale), (float)(_photo.Height * _scale)));
        g.SmoothingMode = SmoothingMode.AntiAlias;
        if (_corners is { Count: 4 })
        {
            var pts = _corners.Select(ToScreen).ToArray();
            using (var shade = new SolidBrush(Color.FromArgb(40, Palette.Accent)))
                g.FillPolygon(shade, pts);
            using (var line = new Pen(Palette.Accent, Math.Max(2f, this.Px(2))))
                g.DrawPolygon(line, pts);
            var r = this.Px(8);
            using var white = new SolidBrush(Color.White);
            using var ring = new Pen(Palette.Accent, Math.Max(2f, this.Px(2)));
            foreach (var p in pts)
            {
                g.FillEllipse(white, p.X - r, p.Y - r, 2 * r, 2 * r);
                g.DrawEllipse(ring, p.X - r, p.Y - r, 2 * r, 2 * r);
            }
            if (_dragCorner >= 0) DrawLoupe(g, pts[_dragCorner]);
        }
        if (Caption.Length > 0)
        {
            var size = TextRenderer.MeasureText(g, Caption, UiFonts.CaptionStrong);
            var chip = new RectangleF(this.Px(12), this.Px(12), size.Width + this.Px(20), size.Height + this.Px(10));
            using (var path = UiKit.RoundedRect(chip, chip.Height / 2))
            using (var brush = new SolidBrush(Color.FromArgb(225, Palette.Ink)))
                g.FillPath(brush, path);
            TextRenderer.DrawText(g, Caption, UiFonts.CaptionStrong, Rectangle.Round(chip), Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }

    /// <summary>A 4x magnifier of the spot under a dragged corner, in the top-left corner of the view.</summary>
    void DrawLoupe(Graphics g, PointF at)
    {
        var size = this.Px(150);
        var box = new Rectangle(Width - size - this.Px(12), this.Px(12), size, size);
        var zoom = 4.0;
        var srcW = size / (_scale * zoom);
        var cx = (at.X - _offset.X) / _scale;
        var cy = (at.Y - _offset.Y) / _scale;
        var src = new RectangleF((float)(cx - srcW / 2), (float)(cy - srcW / 2), (float)srcW, (float)srcW);
        var state = g.Save();
        using (var clip = new GraphicsPath())
        {
            clip.AddEllipse(box);
            g.SetClip(clip);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(_photo!, box, src, GraphicsUnit.Pixel);
            using var cross = new Pen(Palette.Accent, 1f);
            g.DrawLine(cross, box.Left + size / 2, box.Top, box.Left + size / 2, box.Bottom);
            g.DrawLine(cross, box.Left, box.Top + size / 2, box.Right, box.Top + size / 2);
        }
        g.Restore(state);
        using var ring = new Pen(Palette.Ink, 2f);
        g.DrawEllipse(ring, box);
    }

    int HitCorner(Point p)
    {
        if (_corners is not { Count: 4 } || _photo is null) return -1;
        var reach = this.Px(14);
        for (var i = 0; i < 4; i++)
        {
            var c = ToScreen(_corners[i]);
            if ((c.X - p.X) * (c.X - p.X) + (c.Y - p.Y) * (c.Y - p.Y) <= reach * reach) return i;
        }
        return -1;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        if (_photo is null) return;
        EnsureFit();
        _downAt = e.Location;
        _downOffset = _offset;
        if (e.Button == MouseButtons.Left) _dragCorner = HitCorner(e.Location);
        _panning = _dragCorner < 0;
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        _mouse = e.Location;
        if (_photo is null) return;
        if (_dragCorner >= 0 && _corners is { Count: 4 })
        {
            var n = ToNorm(e.Location);
            _corners[_dragCorner] = new NormPoint(Math.Clamp(n.X, 0, 1), Math.Clamp(n.Y, 0, 1));
            Invalidate();
            return;
        }
        if (_panning && e.Button != MouseButtons.None)
        {
            _offset = new PointF(_downOffset.X + e.X - _downAt.X, _downOffset.Y + e.Y - _downAt.Y);
            _fitted = false;
            Invalidate();
            return;
        }
        Cursor = HitCorner(e.Location) >= 0 ? Cursors.Hand : Cursors.Default;
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_dragCorner >= 0 && _corners is { Count: 4 })
        {
            _dragCorner = -1;
            CornersChanged?.Invoke(_corners.ToList());
        }
        _dragCorner = -1;
        _panning = false;
        Invalidate();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_photo is null) return;
        EnsureFit();
        var before = new PointF((float)((e.X - _offset.X) / _scale), (float)((e.Y - _offset.Y) / _scale));
        var fit = Math.Min(Width / (double)_photo.Width, Height / (double)_photo.Height);
        _scale = Math.Clamp(_scale * Math.Pow(1.0015, e.Delta), fit * 0.5, 40);
        _offset = new PointF((float)(e.X - before.X * _scale), (float)(e.Y - before.Y * _scale));
        _fitted = false;
        Invalidate();
    }

    protected override void OnDoubleClick(EventArgs e)
    {
        base.OnDoubleClick(e);
        _fitted = true;
        Invalidate();
    }
}
