using System.ComponentModel;
using System.Drawing.Drawing2D;
using Tracedrop.Core.Editing;
using Tracedrop.Core.Vector;

namespace TracedropApp;

// The editing tools of the canvas. Every finished gesture becomes one EditCommitted(before, after) for undo.
sealed partial class PreviewView
{
    enum Gesture { None, Click, Pan, Marquee, MoveOwn, MoveRegion, DragNode, DragHandleIn, DragHandleOut, PenDrag, AreaCircle, AreaRect, AreaBrush, Right }

    EditState _edit = EditState.Empty;
    EditTool _tool = EditTool.Select;

    // Selection: traced paths by index in the drawing, your paths by index in the edits, one area.
    readonly HashSet<int> _selAi = new();
    readonly HashSet<int> _selOwn = new();
    int _selRegion = -1;
    // Traced paths deleted or taken over a moment ago, hidden until the re-trace arrives.
    readonly HashSet<int> _hiddenAi = new();
    int _hoverAi = -1;
    int _hoverOwn = -1;
    int _hoverRegion = -1;

    // Edit points: the path being edited and its selected point.
    int _active = -1;
    int _activeNode = -1;

    // Pen: the path being drawn (not part of the edits until finished).
    ManualPath? _pen;

    RegionShape _areaShape = RegionShape.Circle;
    double _areaSize = 0.05;   // circle radius / half a clicked square, as a fraction of the picture's longest side
    double _brushSize = 0.012; // brush radius
    bool _spaceHeld;

    Gesture _gesture;
    Point _downAt;
    Point _lastMove;
    Point? _hover;
    PointF _downOffset;
    EditState? _before;
    bool _moved;
    NormPoint _lastNorm = new(0, 0);
    readonly List<NormPoint> _brushPoints = new();

    /// <summary>An edit finished: the edits went from before to after. Same tag within a second = one undo step.</summary>
    public event Action<EditState, EditState, object?>? EditCommitted;

    /// <summary>The selection (or what it can do) changed.</summary>
    public event Action? SelectionChanged;

    /// <summary>The canvas switched tools itself (double-click to edit points, Pen finished with a tool key...).</summary>
    public event Action<EditTool>? ToolChanged;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public EditTool Tool
    {
        get => _tool;
        set
        {
            if (_tool == value) return;
            FinishPen();
            var previous = _tool;
            _tool = value;
            _hoverAi = _hoverOwn = _hoverRegion = -1;
            if (value == EditTool.Points && previous == EditTool.Select && _selOwn.Count == 1 && _selAi.Count == 0)
            {
                _active = _selOwn.First();
                _activeNode = -1;
            }
            if (value != EditTool.Points) { _active = -1; _activeNode = -1; }
            if (value is not (EditTool.Select or EditTool.Points)) ClearSelection();
            UpdateCursor();
            Invalidate();
            ToolChanged?.Invoke(value);
            SelectionChanged?.Invoke();
        }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public RegionShape AreaShape
    {
        get => _areaShape;
        set { _areaShape = value; Invalidate(); }
    }

    public bool HasSelection => _selAi.Count > 0 || _selOwn.Count > 0 || _selRegion >= 0;
    public int SelectedCount => _selAi.Count + _selOwn.Count + (_selRegion >= 0 ? 1 : 0);
    public bool CanEditSelection => _selAi.Count > 0 || _selOwn.Count > 0;
    public bool IsDrawingPath => _pen is not null;

    /// <summary>The current edits of the shown picture. Ignored mid-drag (the drag's working copy wins).</summary>
    public void SetEdits(EditState state)
    {
        if (_gesture is not (Gesture.None or Gesture.Pan or Gesture.Click or Gesture.Right)) return;
        _edit = state;
        _selOwn.RemoveWhere(i => i >= state.Manual.Count);
        if (_selRegion >= state.Regions.Count) _selRegion = -1;
        if (_active >= state.Manual.Count) { _active = -1; _activeNode = -1; }
        else if (_active >= 0 && _activeNode >= state.Manual[_active].Nodes.Count) _activeNode = -1;
        Invalidate();
    }

    void BeginEdit()
    {
        _before = _edit;
        _edit = _edit.Copy();
        _moved = false;
    }

    void EndEdit(object? tag = null)
    {
        if (_before is null) return;
        var before = _before;
        _before = null;
        if (_moved) Commit(before, _edit, tag);
        else _edit = before;
    }

    void Commit(EditState before, EditState after, object? tag = null)
    {
        _edit = after;
        EditCommitted?.Invoke(before, after, tag);
        SelectionChanged?.Invoke();
        Invalidate();
    }

    void ClearSelection()
    {
        if (!HasSelection) return;
        _selAi.Clear();
        _selOwn.Clear();
        _selRegion = -1;
        SelectionChanged?.Invoke();
    }

    // ------------------------------------------------------------------ selection actions

    double DrawingWidth => _drawingSize.Width > 0 ? _drawingSize.Width : PictureSize.Width;
    double DrawingHeight => _drawingSize.Height > 0 ? _drawingSize.Height : PictureSize.Height;

    /// <summary>Deletes what's selected: your paths go; traced lines and shapes are struck out so they stay gone.</summary>
    public void DeleteSelection()
    {
        if (!HasSelection) return;
        var before = _edit;
        var after = _edit.Copy();
        foreach (var i in _selOwn.OrderByDescending(i => i)) after.Manual.RemoveAt(i);
        if (_selRegion >= 0) after.Regions.RemoveAt(_selRegion);
        var deleted = _traced.Where(t => _selAi.Contains(t.Index)).ToList();
        foreach (var t in deleted) EditActions.DeleteTraced(after, t.Path, DrawingWidth, DrawingHeight);
        HideTraced(deleted.Select(t => t.Index));
        _selAi.Clear();
        _selOwn.Clear();
        _selRegion = -1;
        if (_active >= after.Manual.Count) _active = -1;
        Commit(before, after);
    }

    /// <summary>
    /// Keeps only what's selected. Shapes: picks inside them (other shapes go, whatever the settings). Lines: the
    /// selected lines become yours and everything else is erased (a Find area afterwards brings lines back there).
    /// </summary>
    public void KeepOnlySelection()
    {
        var selected = _traced.Where(t => _selAi.Contains(t.Index)).ToList();
        if (selected.Count == 0 && _selOwn.Count == 0) return;
        var before = _edit;
        var after = _edit.Copy();
        var shapes = selected.Count > 0 && selected.All(t => t.Path.Role != PathRole.Stroke);
        if (shapes)
        {
            foreach (var t in selected.Where(t => t.Path.Role == PathRole.Outer))
                if (EditActions.InteriorPoint(t.Path) is { } p)
                    after.Picks.Add(new Tracedrop.Core.PickPoint(p.X / DrawingWidth, p.Y / DrawingHeight, Tracedrop.Core.PickKind.Keep));
            HideTraced(_traced.Where(t => !_selAi.Contains(t.Index) && t.Path.Role == PathRole.Outer).Select(t => t.Index).ToList());
            _selAi.Clear();
        }
        else
        {
            _selOwn.Clear();
            foreach (var t in selected)
            {
                EditActions.TakeOver(after, t.Path, DrawingWidth, DrawingHeight);
                _selOwn.Add(after.Manual.Count - 1);
            }
            // Your paths you had selected stay; the ones you didn't select are left alone too (they're yours).
            after.Regions.Add(new GuideRegion
            {
                Kind = RegionKind.Erase, Shape = RegionShape.Rectangle,
                Points = { new NormPoint(-0.01, -0.01), new NormPoint(1.01, 1.01) }
            });
            _selAi.Clear();
            HideTraced(_traced.Select(t => t.Index).ToList());
        }
        Commit(before, after);
    }

    /// <summary>Edit points of the selection: traced lines are taken over as yours first.</summary>
    public void EditSelection()
    {
        var selected = _traced.Where(t => _selAi.Contains(t.Index)).ToList();
        if (selected.Count == 0)
        {
            if (_selOwn.Count == 0) return;
            var own = _selOwn.First();
            Tool = EditTool.Points;
            _active = own;
            _activeNode = -1;
            Invalidate();
            return;
        }
        var before = _edit;
        var after = _edit.Copy();
        var first = -1;
        foreach (var t in selected)
        {
            EditActions.TakeOver(after, t.Path, DrawingWidth, DrawingHeight);
            if (first < 0) first = after.Manual.Count - 1;
        }
        _selAi.Clear();
        HideTraced(selected.Select(t => t.Index).ToList());
        _selOwn.Clear();
        Commit(before, after);
        Tool = EditTool.Points;
        _active = first;
        _activeNode = -1;
        Invalidate();
    }

    public void SelectAll()
    {
        if (_tool != EditTool.Select) Tool = EditTool.Select;
        _selAi.Clear();
        foreach (var t in _traced) if (!_hiddenAi.Contains(t.Index)) _selAi.Add(t.Index);
        _selOwn.Clear();
        for (var i = 0; i < _edit.Manual.Count; i++) _selOwn.Add(i);
        SelectionChanged?.Invoke();
        Invalidate();
    }

    /// <summary>Adds the path being drawn with the Pen to the edits (when it has at least two points).</summary>
    public void FinishPen()
    {
        if (_pen is null) return;
        var pen = _pen;
        _pen = null;
        // Drop points that landed on top of the previous one (a double-click adds the last point twice).
        var nodes = new List<ManualNode>();
        foreach (var n in pen.Nodes)
        {
            if (nodes.Count > 0 && Distance(NormToScreen(nodes[^1].Point), NormToScreen(n.Point)) < 2) { nodes[^1] = nodes[^1] with { Out = n.Out ?? nodes[^1].Out }; continue; }
            nodes.Add(n);
        }
        if (pen.Closed && nodes.Count > 2 && Distance(NormToScreen(nodes[^1].Point), NormToScreen(nodes[0].Point)) < 2) nodes.RemoveAt(nodes.Count - 1);
        if (nodes.Count < 2) { Invalidate(); return; }
        var before = _edit;
        var after = _edit.Copy();
        after.Manual.Add(pen with { Nodes = nodes, Closed = pen.Closed && nodes.Count > 2 });
        Commit(before, after);
    }

    public void CancelPen()
    {
        if (_pen is null) return;
        _pen = null;
        Invalidate();
    }

    // ------------------------------------------------------------------ hit testing

    float Tolerance => this.Px(6);

    static float Distance(PointF a, PointF b) => (float)Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    static float SegmentDistance(PointF p, PointF a, PointF b)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        var len = dx * dx + dy * dy;
        var t = len < 1e-9f ? 0 : Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len, 0, 1);
        return Distance(p, new PointF(a.X + t * dx, a.Y + t * dy));
    }

    Vec2 PictureVec(PointF screen)
    {
        var p = ToPicture(screen);
        return new Vec2(p.X, p.Y);
    }

    /// <summary>Your path under the point (topmost), or -1.</summary>
    int HitOwn(PointF screen)
    {
        var target = PictureVec(screen);
        for (var i = _edit.Manual.Count - 1; i >= 0; i--)
        {
            var (_, _, d) = PathEdits.Closest(_edit.Manual[i], PictureSize.Width, PictureSize.Height, target);
            if (d * _scale <= Tolerance) return i;
        }
        return -1;
    }

    /// <summary>The traced path under the point (closest), or -1. Returns its index in the drawing.</summary>
    int HitAi(PointF screen)
    {
        if (_traced.Count == 0) return -1;
        var d = ScreenToDrawing(screen);
        var tol = (float)(Tolerance / (_scale * DrawingScaleX));
        var best = -1;
        var bestDistance = tol;
        foreach (var t in _traced)
        {
            if (_hiddenAi.Contains(t.Index)) continue;
            var b = t.Bounds;
            if (d.X < b.Left - tol || d.X > b.Right + tol || d.Y < b.Top - tol || d.Y > b.Bottom + tol) continue;
            for (var k = 0; k + 1 < t.Points.Length; k++)
            {
                var dist = SegmentDistance(d, t.Points[k], t.Points[k + 1]);
                if (dist < bestDistance)
                {
                    bestDistance = dist;
                    best = t.Index;
                }
            }
        }
        return best;
    }

    /// <summary>The Find / Erase area under the point (topmost), or -1.</summary>
    int HitRegion(PointF screen)
    {
        for (var i = _edit.Regions.Count - 1; i >= 0; i--)
        {
            var r = _edit.Regions[i];
            if (r.Kind == RegionKind.Suppress || r.Points.Count == 0) continue;
            switch (r.Shape)
            {
                case RegionShape.Circle when Distance(NormToScreen(r.Points[0]), screen) <= FractionToScreen(r.Size):
                    return i;
                case RegionShape.Rectangle when r.Points.Count > 1:
                {
                    var a = NormToScreen(r.Points[0]);
                    var b = NormToScreen(r.Points[1]);
                    // The whole-picture erase of "Keep only" is not picked by clicking inside it (only by its edge).
                    var whole = r.Points[0].X <= 0 && r.Points[0].Y <= 0 && r.Points[1].X >= 1 && r.Points[1].Y >= 1;
                    var rect = RectangleF.FromLTRB(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
                    if (!whole && rect.Contains(screen)) return i;
                    break;
                }
                case RegionShape.Brush:
                {
                    var pts = r.Points.Select(NormToScreen).ToArray();
                    var radius = Math.Max(Tolerance, FractionToScreen(r.Size));
                    if (pts.Length == 1 && Distance(pts[0], screen) <= radius) return i;
                    for (var k = 0; k + 1 < pts.Length; k++)
                        if (SegmentDistance(screen, pts[k], pts[k + 1]) <= radius) return i;
                    break;
                }
            }
        }
        return -1;
    }

    /// <summary>
    /// A point (0 = anchor) or handle (-1 in, +1 out) of the path being edited under the point: the closest one, with
    /// an anchor winning over a handle that sits on top of it (Alt picks the handle, to pull it out of the anchor).
    /// </summary>
    (int Node, int Handle) HitNode(PointF screen)
    {
        if (_active < 0 || _active >= _edit.Manual.Count) return (-1, 0);
        var path = _edit.Manual[_active];
        var reach = Math.Max(AnchorSize, this.Px(10));
        var preferHandles = (ModifierKeys & Keys.Alt) != 0;
        var best = (Node: -1, Handle: 0);
        var bestDistance = float.MaxValue;
        void Consider(int node, int handle, NormPoint? at)
        {
            if (at is null) return;
            var d = Distance(NormToScreen(at), screen);
            if (d > reach) return;
            // Anchors get a few pixels' head start, so a handle lying on its anchor doesn't steal the click.
            var score = handle == 0 ? d - (preferHandles ? -this.Px(3) : this.Px(3)) : d;
            if (score < bestDistance) { bestDistance = score; best = (node, handle); }
        }
        var showAll = path.Nodes.Count <= 60;
        for (var i = 0; i < path.Nodes.Count; i++)
        {
            var n = path.Nodes[i];
            Consider(i, 0, n.Point);
            if (showAll || Math.Abs(i - _activeNode) <= 1)
            {
                Consider(i, 1, n.Out);
                Consider(i, -1, n.In);
            }
        }
        return best;
    }

    // ------------------------------------------------------------------ mouse

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        if (PictureSize.IsEmpty) return;
        EnsureFit();
        _downAt = _lastMove = e.Location;
        _downOffset = _offset;
        _moved = false;
        Capture = true;

        if (e.Button == MouseButtons.Middle || (e.Button == MouseButtons.Left && (_tool == EditTool.Hand || _spaceHeld)))
        {
            _gesture = Gesture.Pan;
            Cursor = Cursors.SizeAll;
            return;
        }
        if (e.Button == MouseButtons.Right)
        {
            _gesture = Gesture.Right;
            return;
        }
        if (e.Button != MouseButtons.Left) return;

        switch (_tool)
        {
            case EditTool.Select: SelectDown(e.Location); break;
            case EditTool.Points: PointsDown(e.Location); break;
            case EditTool.Pen: PenDown(e.Location); break;
            case EditTool.Find:
            case EditTool.Erase:
                _lastNorm = ScreenToNorm(e.Location);
                _gesture = _areaShape switch { RegionShape.Rectangle => Gesture.AreaRect, RegionShape.Brush => Gesture.AreaBrush, _ => Gesture.AreaCircle };
                _brushPoints.Clear();
                _brushPoints.Add(_lastNorm);
                Invalidate();
                break;
        }
    }

    void SelectDown(Point at)
    {
        var shift = (ModifierKeys & Keys.Shift) != 0;
        var own = HitOwn(at);
        if (own >= 0)
        {
            if (shift && _selOwn.Contains(own)) _selOwn.Remove(own);
            else
            {
                if (!shift && !_selOwn.Contains(own)) { _selAi.Clear(); _selOwn.Clear(); _selRegion = -1; }
                _selOwn.Add(own);
            }
            BeginEdit();
            _gesture = Gesture.MoveOwn;
            _lastNorm = ScreenToNorm(at);
            SelectionChanged?.Invoke();
            Invalidate();
            return;
        }
        var ai = HitAi(at);
        if (ai >= 0)
        {
            if (shift && _selAi.Contains(ai)) _selAi.Remove(ai);
            else
            {
                if (!shift) { _selAi.Clear(); _selOwn.Clear(); _selRegion = -1; }
                _selAi.Add(ai);
            }
            _gesture = Gesture.Click;
            SelectionChanged?.Invoke();
            Invalidate();
            return;
        }
        var region = HitRegion(at);
        if (region >= 0)
        {
            _selAi.Clear();
            _selOwn.Clear();
            _selRegion = region;
            BeginEdit();
            _gesture = Gesture.MoveRegion;
            _lastNorm = ScreenToNorm(at);
            SelectionChanged?.Invoke();
            Invalidate();
            return;
        }
        if (!shift) ClearSelection();
        _gesture = Gesture.Marquee;
        Invalidate();
    }

    void PointsDown(Point at)
    {
        if (_active >= 0 && _active < _edit.Manual.Count)
        {
            var (node, handle) = HitNode(at);
            if (node >= 0)
            {
                _activeNode = node;
                if (handle == 0 && (ModifierKeys & Keys.Alt) != 0)
                {
                    var after = _edit.Copy();
                    after.Manual[_active] = PathEdits.ToggleSmooth(after.Manual[_active], node);
                    Commit(_edit, after);
                    _gesture = Gesture.Click;
                    return;
                }
                BeginEdit();
                _gesture = handle == 0 ? Gesture.DragNode : handle < 0 ? Gesture.DragHandleIn : Gesture.DragHandleOut;
                _lastNorm = ScreenToNorm(at);
                Invalidate();
                return;
            }
            // On the edited path's line: a new point there, ready to drag.
            var (segment, t, distance) = PathEdits.Closest(_edit.Manual[_active], PictureSize.Width, PictureSize.Height, PictureVec(at));
            if (segment >= 0 && distance * _scale <= Tolerance)
            {
                BeginEdit();
                _edit.Manual[_active] = PathEdits.InsertNode(_edit.Manual[_active], segment, t);
                _activeNode = segment + 1;
                _moved = true;
                _gesture = Gesture.DragNode;
                _lastNorm = ScreenToNorm(at);
                Invalidate();
                return;
            }
        }
        var own = HitOwn(at);
        if (own >= 0)
        {
            _active = own;
            _activeNode = -1;
            _gesture = Gesture.Click;
            Invalidate();
            return;
        }
        var ai = HitAi(at);
        if (ai >= 0 && _traced.FirstOrDefault(t => t.Index == ai) is { } traced)
        {
            // Clicking an AI line with Edit points makes it yours, ready to edit.
            var after = _edit.Copy();
            EditActions.TakeOver(after, traced.Path, DrawingWidth, DrawingHeight);
            HideTraced(new[] { ai });
            _active = after.Manual.Count - 1;
            _activeNode = -1;
            Commit(_edit, after);
            _gesture = Gesture.Click;
            return;
        }
        _active = -1;
        _activeNode = -1;
        _gesture = Gesture.Click;
        Invalidate();
    }

    void PenDown(Point at)
    {
        var norm = ScreenToNorm(at);
        if (_pen is null)
        {
            _pen = new ManualPath { Nodes = { new ManualNode(norm) } };
            _gesture = Gesture.PenDrag;
            Invalidate();
            return;
        }
        // Clicking the first point closes the path.
        if (_pen.Nodes.Count >= 2 && Distance(NormToScreen(_pen.Nodes[0].Point), at) <= Math.Max(AnchorSize, this.Px(10)))
        {
            _pen = _pen with { Closed = true };
            FinishPen();
            _gesture = Gesture.Click;
            return;
        }
        if ((ModifierKeys & Keys.Shift) != 0) norm = Constrain45(_pen.Nodes[^1].Point, norm);
        _pen = _pen with { Nodes = _pen.Nodes.Append(new ManualNode(norm)).ToList() };
        _gesture = Gesture.PenDrag;
        Invalidate();
    }

    /// <summary>Shift: the new point lies at a multiple of 45° from the previous one (on screen).</summary>
    NormPoint Constrain45(NormPoint from, NormPoint to)
    {
        var a = NormToScreen(from);
        var b = NormToScreen(to);
        var angle = Math.Atan2(b.Y - a.Y, b.X - a.X);
        var snapped = Math.Round(angle / (Math.PI / 4)) * (Math.PI / 4);
        var length = Distance(a, b);
        return ScreenToNorm(new PointF((float)(a.X + Math.Cos(snapped) * length), (float)(a.Y + Math.Sin(snapped) * length)));
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var previousHover = _hover;
        _hover = e.Location;
        if (PictureSize.IsEmpty) { Cursor = Cursors.Default; return; }
        if (_gesture == Gesture.None)
        {
            UpdateHover(e.Location, previousHover);
            return;
        }

        var dx = e.X - _downAt.X;
        var dy = e.Y - _downAt.Y;
        var far = dx * dx + dy * dy > this.Px(4) * this.Px(4);
        var norm = ScreenToNorm(e.Location);
        switch (_gesture)
        {
            case Gesture.Pan:
                _offset = new PointF(_downOffset.X + dx, _downOffset.Y + dy);
                _fitted = false;
                Invalidate();
                break;
            case Gesture.Marquee:
            case Gesture.AreaCircle:
            case Gesture.AreaRect:
                Invalidate();
                break;
            case Gesture.AreaBrush:
            {
                var last = NormToScreen(_brushPoints[^1]);
                if (Distance(last, e.Location) >= Math.Max(2, FractionToScreen(_brushSize) / 3))
                {
                    _brushPoints.Add(norm);
                    Invalidate();
                }
                break;
            }
            case Gesture.MoveOwn when far || _moved:
            {
                var (mx, my) = (norm.X - _lastNorm.X, norm.Y - _lastNorm.Y);
                foreach (var i in _selOwn) _edit.Manual[i] = PathEdits.Move(_edit.Manual[i], mx, my);
                _lastNorm = norm;
                _moved = true;
                Invalidate();
                break;
            }
            case Gesture.MoveRegion when (far || _moved) && _selRegion >= 0:
            {
                var (mx, my) = (norm.X - _lastNorm.X, norm.Y - _lastNorm.Y);
                var r = _edit.Regions[_selRegion];
                _edit.Regions[_selRegion] = r with { Points = r.Points.Select(p => new NormPoint(p.X + mx, p.Y + my)).ToList() };
                _lastNorm = norm;
                _moved = true;
                Invalidate();
                break;
            }
            case Gesture.DragNode when _active >= 0 && _activeNode >= 0:
            {
                var (mx, my) = (norm.X - _lastNorm.X, norm.Y - _lastNorm.Y);
                _edit.Manual[_active] = PathEdits.MoveNode(_edit.Manual[_active], _activeNode, mx, my);
                _lastNorm = norm;
                _moved = true;
                Invalidate();
                break;
            }
            case Gesture.DragHandleIn or Gesture.DragHandleOut when _active >= 0 && _activeNode >= 0:
            {
                var independent = (ModifierKeys & Keys.Alt) != 0;
                var aspect = PictureSize.Width / (double)Math.Max(1, PictureSize.Height);
                _edit.Manual[_active] = PathEdits.SetHandle(_edit.Manual[_active], _activeNode, _gesture == Gesture.DragHandleOut, norm, independent, aspect);
                _moved = true;
                Invalidate();
                break;
            }
            case Gesture.PenDrag when _pen is not null && far:
            {
                // Dragging while placing a point pulls out symmetric handles: a smooth curve through it.
                var last = _pen.Nodes[^1];
                var mirror = new NormPoint(2 * last.Point.X - norm.X, 2 * last.Point.Y - norm.Y);
                var nodes = _pen.Nodes.ToList();
                nodes[^1] = new ManualNode(last.Point, mirror, norm);
                _pen = _pen with { Nodes = nodes };
                Invalidate();
                break;
            }
        }
    }

    void UpdateHover(Point at, Point? previous)
    {
        var hoverAi = -1;
        var hoverOwn = -1;
        var hoverRegion = -1;
        if (!_spaceHeld)
        {
            switch (_tool)
            {
                case EditTool.Select:
                    hoverOwn = HitOwn(at);
                    if (hoverOwn < 0) hoverAi = HitAi(at);
                    if (hoverOwn < 0 && hoverAi < 0) hoverRegion = HitRegion(at);
                    break;
                case EditTool.Points:
                    hoverOwn = HitOwn(at);
                    if (hoverOwn < 0) hoverAi = HitAi(at);
                    break;
                case EditTool.Find:
                case EditTool.Erase:
                    hoverRegion = HitRegion(at);
                    break;
            }
        }
        var changed = hoverAi != _hoverAi || hoverOwn != _hoverOwn || hoverRegion != _hoverRegion;
        _hoverAi = hoverAi;
        _hoverOwn = hoverOwn;
        _hoverRegion = hoverRegion;
        UpdateCursor();
        if (changed) { Invalidate(); return; }

        // Tools that draw at the mouse repaint only around the old and new positions.
        var reach = _tool switch
        {
            EditTool.Find or EditTool.Erase => _areaShape == RegionShape.Brush ? FractionToScreen(_brushSize) : _areaShape == RegionShape.Circle ? FractionToScreen(_areaSize) : 0,
            _ => 0
        };
        if (_tool == EditTool.Pen && _pen is not null)
        {
            Invalidate();
            return;
        }
        if (reach <= 0) return;
        var pad = (int)Math.Ceiling(reach) + this.Px(4);
        Invalidate(new Rectangle(at.X - pad, at.Y - pad, 2 * pad, 2 * pad));
        if (previous is { } p) Invalidate(new Rectangle(p.X - pad, p.Y - pad, 2 * pad, 2 * pad));
    }

    void UpdateCursor()
    {
        if (PictureSize.IsEmpty) { Cursor = Cursors.Default; return; }
        if (_spaceHeld || _tool == EditTool.Hand) { Cursor = _gesture == Gesture.Pan ? Cursors.SizeAll : Cursors.Hand; return; }
        Cursor = _tool switch
        {
            EditTool.Select => _hoverAi >= 0 || _hoverOwn >= 0 ? Cursors.Hand : _hoverRegion >= 0 ? Cursors.SizeAll : Cursors.Default,
            EditTool.Points => _hoverAi >= 0 || _hoverOwn >= 0 ? Cursors.Hand : Cursors.Default,
            _ => Cursors.Cross
        };
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        Capture = false;
        var gesture = _gesture;
        _gesture = Gesture.None;
        if (PictureSize.IsEmpty) return;
        var dx = e.X - _downAt.X;
        var dy = e.Y - _downAt.Y;
        var dragged = dx * dx + dy * dy > this.Px(8) * this.Px(8);

        switch (gesture)
        {
            case Gesture.Pan:
                UpdateCursor();
                break;
            case Gesture.Right:
                if (dragged) break;
                var region = HitRegion(e.Location);
                if (region >= 0)
                {
                    // Right-click an area: remove it.
                    var after = _edit.Copy();
                    after.Regions.RemoveAt(region);
                    if (_selRegion == region) _selRegion = -1;
                    _hoverRegion = -1;
                    Commit(_edit, after);
                }
                else if (_pen is not null) FinishPen();
                break;
            case Gesture.Marquee:
                if (dragged) SelectInside(RectangleF.FromLTRB(Math.Min(_downAt.X, e.X), Math.Min(_downAt.Y, e.Y), Math.Max(_downAt.X, e.X), Math.Max(_downAt.Y, e.Y)));
                Invalidate();
                break;
            case Gesture.MoveOwn:
            case Gesture.MoveRegion:
            case Gesture.DragNode:
            case Gesture.DragHandleIn:
            case Gesture.DragHandleOut:
                EndEdit();
                break;
            case Gesture.AreaCircle:
            {
                var radius = dragged ? ScreenToFraction(Math.Sqrt(dx * dx + dy * dy)) : _areaSize;
                if (dragged) _areaSize = Math.Clamp(radius, 0.004, 0.6);
                AddRegion(new GuideRegion { Shape = RegionShape.Circle, Points = { ScreenToNorm(_downAt) }, Size = Math.Clamp(radius, 0.004, 0.6) });
                break;
            }
            case Gesture.AreaRect:
            {
                NormPoint a, b;
                if (dragged && Math.Abs(dx) > this.Px(4) && Math.Abs(dy) > this.Px(4))
                {
                    a = ScreenToNorm(_downAt);
                    b = ScreenToNorm(e.Location);
                }
                else
                {
                    var half = FractionToScreen(_areaSize);
                    a = ScreenToNorm(new PointF(_downAt.X - half, _downAt.Y - half));
                    b = ScreenToNorm(new PointF(_downAt.X + half, _downAt.Y + half));
                }
                AddRegion(new GuideRegion { Shape = RegionShape.Rectangle, Points = { a, b } });
                break;
            }
            case Gesture.AreaBrush:
                _brushPoints.Add(ScreenToNorm(e.Location));
                AddRegion(new GuideRegion { Shape = RegionShape.Brush, Points = _brushPoints.ToList(), Size = _brushSize });
                _brushPoints.Clear();
                break;
        }
        UpdateCursor();
    }

    void AddRegion(GuideRegion shape)
    {
        var region = shape with
        {
            Kind = _tool == EditTool.Find ? RegionKind.Find : RegionKind.Erase,
            // "Lines are missing here" usually means fainter ones: start at +2 (scroll to change).
            Amount = _tool == EditTool.Find ? 2 : 0
        };
        var after = _edit.Copy();
        after.Regions.Add(region);
        Commit(_edit, after);
    }

    /// <summary>Selects every traced path and your path that has a point inside the box (Shift adds).</summary>
    void SelectInside(RectangleF screen)
    {
        var a = ScreenToDrawing(screen.Location);
        var b = ScreenToDrawing(new PointF(screen.Right, screen.Bottom));
        var box = RectangleF.FromLTRB(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
        foreach (var t in _traced)
        {
            if (_hiddenAi.Contains(t.Index) || !t.Bounds.IntersectsWith(box)) continue;
            if (t.Points.Any(box.Contains)) _selAi.Add(t.Index);
        }
        for (var i = 0; i < _edit.Manual.Count; i++)
        {
            using var gp = ScreenPath(_edit.Manual[i]);
            if (gp is null) continue;
            gp.Flatten();
            if (gp.PathPoints.Any(screen.Contains)) _selOwn.Add(i);
        }
        SelectionChanged?.Invoke();
    }

    protected override void OnDoubleClick(EventArgs e)
    {
        base.OnDoubleClick(e);
        var at = PointToClient(MousePosition);
        switch (_tool)
        {
            case EditTool.Select when CanEditSelection && (HitOwn(at) >= 0 || HitAi(at) >= 0):
                EditSelection();
                return;
            case EditTool.Pen:
                FinishPen();
                return;
            case EditTool.Find:
            case EditTool.Erase:
                return;
        }
        if (_tool is EditTool.Select or EditTool.Hand) FitToView();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (PictureSize.IsEmpty) return;
        var ctrl = (ModifierKeys & Keys.Control) != 0;
        var steps = Math.Sign(e.Delta);

        if (ctrl && _tool is EditTool.Find or EditTool.Erase)
        {
            // Ctrl+scroll sizes the circle / brush.
            var factor = Math.Pow(1.0012, e.Delta);
            if (_areaShape == RegionShape.Brush) _brushSize = Math.Clamp(_brushSize * factor, 0.002, 0.2);
            else _areaSize = Math.Clamp(_areaSize * factor, 0.004, 0.6);
            Invalidate();
            return;
        }
        if (!ctrl && _tool == EditTool.Find && steps != 0)
        {
            // Scroll over a Find area: more (up) or fewer (down) lines there.
            var i = HitRegion(e.Location);
            if (i >= 0 && _edit.Regions[i].Kind == RegionKind.Find)
            {
                var r = _edit.Regions[i];
                var amount = Math.Clamp(r.Amount + steps, -4, 6);
                if (amount != r.Amount)
                {
                    var after = _edit.Copy();
                    after.Regions[i] = after.Regions[i] with { Amount = amount };
                    Commit(_edit, after, ("amount", i));
                }
                return;
            }
        }

        EnsureFit();
        var before = ToPicture(e.Location);
        var zoom = Math.Pow(1.0015, e.Delta);
        var fit = Math.Min(Width / (double)PictureSize.Width, Height / (double)PictureSize.Height);
        _scale = Math.Clamp(_scale * zoom, fit * 0.5, 60);
        _offset = new PointF((float)(e.X - before.X * _scale), (float)(e.Y - before.Y * _scale));
        _fitted = false;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = null;
        if (_hoverAi >= 0 || _hoverOwn >= 0 || _hoverRegion >= 0 || _tool is EditTool.Find or EditTool.Erase or EditTool.Pen)
        {
            _hoverAi = _hoverOwn = _hoverRegion = -1;
            Invalidate();
        }
    }

    // ------------------------------------------------------------------ keys

    protected override bool IsInputKey(Keys keyData) =>
        (keyData & Keys.KeyCode) is Keys.Left or Keys.Right or Keys.Up or Keys.Down or Keys.Enter or Keys.Escape or Keys.Space || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        switch (e.KeyCode)
        {
            case Keys.Space:
                if (!_spaceHeld) { _spaceHeld = true; UpdateCursor(); }
                e.Handled = true;
                break;
            case Keys.Delete:
            case Keys.Back:
                if (_pen is not null)
                {
                    var nodes = _pen.Nodes.Take(_pen.Nodes.Count - 1).ToList();
                    _pen = nodes.Count == 0 ? null : _pen with { Nodes = nodes };
                    Invalidate();
                }
                else if (_tool == EditTool.Points && _active >= 0 && _activeNode >= 0 && _active < _edit.Manual.Count)
                {
                    var after = _edit.Copy();
                    var path = PathEdits.RemoveNode(after.Manual[_active], _activeNode);
                    if (path is null) { after.Manual.RemoveAt(_active); _active = -1; }
                    else after.Manual[_active] = path;
                    _activeNode = -1;
                    Commit(_edit, after);
                }
                else DeleteSelection();
                e.Handled = true;
                break;
            case Keys.Enter:
                FinishPen();
                e.Handled = true;
                break;
            case Keys.Escape:
                if (_pen is not null) CancelPen();
                else if (_tool == EditTool.Points && _active >= 0) { _active = -1; _activeNode = -1; Invalidate(); }
                else { ClearSelection(); Invalidate(); }
                e.Handled = true;
                break;
            case Keys.OemOpenBrackets:
            case Keys.OemCloseBrackets:
            {
                var factor = e.KeyCode == Keys.OemCloseBrackets ? 1.25 : 0.8;
                if (_areaShape == RegionShape.Brush) _brushSize = Math.Clamp(_brushSize * factor, 0.002, 0.2);
                else _areaSize = Math.Clamp(_areaSize * factor, 0.004, 0.6);
                Invalidate();
                e.Handled = true;
                break;
            }
            case Keys.Left:
            case Keys.Right:
            case Keys.Up:
            case Keys.Down:
                Nudge(e.KeyCode, e.Shift ? 10 : 1);
                e.Handled = true;
                break;
            case Keys.A when e.Control:
                SelectAll();
                e.Handled = true;
                break;
        }
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (e.KeyCode == Keys.Space && _spaceHeld)
        {
            _spaceHeld = false;
            UpdateCursor();
        }
    }

    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);
        _spaceHeld = false;
    }

    /// <summary>Arrow keys move the selected point (Edit points) or your selected paths by a screen pixel (Shift: 10).</summary>
    void Nudge(Keys key, int pixels)
    {
        var (sx, sy) = key switch { Keys.Left => (-pixels, 0), Keys.Right => (pixels, 0), Keys.Up => (0, -pixels), _ => (0, pixels) };
        var dx = sx / (_scale * PictureSize.Width);
        var dy = sy / (_scale * PictureSize.Height);
        var after = _edit.Copy();
        if (_tool == EditTool.Points && _active >= 0 && _activeNode >= 0 && _active < after.Manual.Count)
            after.Manual[_active] = PathEdits.MoveNode(after.Manual[_active], _activeNode, dx, dy);
        else if (_selOwn.Count > 0)
            foreach (var i in _selOwn) after.Manual[i] = PathEdits.Move(after.Manual[i], dx, dy);
        else return;
        Commit(_edit, after, "nudge");
    }

    // ------------------------------------------------------------------ tool overlay

    void DrawToolOverlay(Graphics g)
    {
        switch (_gesture)
        {
            case Gesture.Marquee:
            {
                var r = RectangleF.FromLTRB(Math.Min(_downAt.X, _hover?.X ?? _downAt.X), Math.Min(_downAt.Y, _hover?.Y ?? _downAt.Y),
                    Math.Max(_downAt.X, _hover?.X ?? _downAt.X), Math.Max(_downAt.Y, _hover?.Y ?? _downAt.Y));
                using var fill = new SolidBrush(Color.FromArgb(24, Palette.Accent));
                using var pen = new Pen(Palette.Accent, 1f) { DashPattern = new[] { 3f, 2f } };
                g.FillRectangle(fill, r);
                g.DrawRectangle(pen, r.X, r.Y, r.Width, r.Height);
                return;
            }
            case Gesture.AreaCircle:
            {
                var to = _hover ?? _downAt;
                var d = Distance(_downAt, to);
                var radius = d > this.Px(8) ? ScreenToFraction(d) : _areaSize;
                DrawRegion(g, new GuideRegion { Kind = AreaKind, Shape = RegionShape.Circle, Points = { ScreenToNorm(_downAt) }, Size = radius }, true, false, preview: true);
                return;
            }
            case Gesture.AreaRect:
            {
                var to = _hover ?? _downAt;
                DrawRegion(g, new GuideRegion { Kind = AreaKind, Shape = RegionShape.Rectangle, Points = { ScreenToNorm(_downAt), ScreenToNorm(to) } }, true, false, preview: true);
                return;
            }
            case Gesture.AreaBrush:
                DrawRegion(g, new GuideRegion { Kind = AreaKind, Shape = RegionShape.Brush, Points = _brushPoints.ToList(), Size = _brushSize }, true, false, preview: true);
                return;
        }

        if (_pen is not null) DrawPen(g);

        // The size a click would make, following the mouse.
        if (_gesture == Gesture.None && _hover is { } at && !_spaceHeld && _tool is EditTool.Find or EditTool.Erase && _areaShape != RegionShape.Rectangle)
        {
            var radius = FractionToScreen(_areaShape == RegionShape.Brush ? _brushSize : _areaSize);
            var color = _tool == EditTool.Find ? FindColor : EraseColor;
            using var pen = new Pen(Color.FromArgb(170, color), 1f) { DashPattern = new[] { 2f, 2f } };
            g.DrawEllipse(pen, at.X - radius, at.Y - radius, 2 * radius, 2 * radius);
        }
    }

    RegionKind AreaKind => _tool == EditTool.Find ? RegionKind.Find : RegionKind.Erase;

    void DrawPen(Graphics g)
    {
        if (_pen is null) return;
        using (var gp = ScreenPath(_pen))
        using (var pen = RoundPen(MineInk, LineWidth + this.Px(1)))
            if (gp is not null) g.DrawPath(pen, gp);
        // Where the next click would go.
        if (_hover is { } at && _gesture == Gesture.None)
        {
            var last = NormToScreen(_pen.Nodes[^1].Point);
            var target = (ModifierKeys & Keys.Shift) != 0 ? NormToScreen(Constrain45(_pen.Nodes[^1].Point, ScreenToNorm(at))) : at;
            using var rubber = new Pen(Color.FromArgb(160, MineInk), 1f) { DashPattern = new[] { 3f, 3f } };
            g.DrawLine(rubber, last, target);
            if (_pen.Nodes.Count >= 2 && Distance(NormToScreen(_pen.Nodes[0].Point), at) <= Math.Max(AnchorSize, this.Px(10)))
            {
                var first = NormToScreen(_pen.Nodes[0].Point);
                var r = this.Px(9);
                using var ring = new Pen(MineInk, Math.Max(1.5f, this.Px(2)));
                g.DrawEllipse(ring, first.X - r, first.Y - r, 2 * r, 2 * r);
            }
        }
        DrawNodes(g, _pen, handles: true, _pen.Nodes.Count - 1);
    }
}
