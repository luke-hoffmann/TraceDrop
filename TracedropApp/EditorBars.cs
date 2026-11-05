using System.Drawing.Drawing2D;

namespace TracedropApp;

/// <summary>The vertical tool strip at the left of the canvas, like an image editor's toolbox.</summary>
sealed class ToolPalette : Control
{
    static readonly (EditTool Tool, string Name, string Key)[] Tools =
    {
        (EditTool.Select, "Select", "V"),
        (EditTool.Points, "Edit points", "A"),
        (EditTool.Pen, "Pen", "P"),
        (EditTool.Find, "Find lines here", "F"),
        (EditTool.Erase, "Erase lines here", "E"),
        (EditTool.Hand, "Hand", "H")
    };

    readonly ToolTip _tip = new() { InitialDelay = 350, ReshowDelay = 100, ShowAlways = true };
    EditTool _selected = EditTool.Select;
    int _hover = -1;
    int _tipFor = -1;

    public ToolPalette()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Width = 46;
        Cursor = Cursors.Hand;
        AccessibleRole = AccessibleRole.ToolBar;
        AccessibleName = "Tools";
    }

    public event Action<EditTool>? ToolSelected;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public EditTool Selected
    {
        get => _selected;
        set { if (_selected != value) { _selected = value; Invalidate(); } }
    }

    Rectangle ButtonRect(int i)
    {
        var size = this.Px(36);
        var x = (Width - size) / 2;
        var y = this.Px(6) + i * (size + this.Px(4)) + (i >= 3 ? this.Px(8) : 0) + (i >= 5 ? this.Px(8) : 0);
        return new Rectangle(x, y, size, size);
    }

    int HitButton(Point p)
    {
        for (var i = 0; i < Tools.Length; i++) if (ButtonRect(i).Contains(p)) return i;
        return -1;
    }

    protected override void OnPaintBackground(PaintEventArgs pevent) { }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Palette.Surface);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var line = new Pen(Palette.Separator))
        {
            g.DrawLine(line, Width - 1, 0, Width - 1, Height);
            var y1 = ButtonRect(3).Top - this.Px(6);
            var y2 = ButtonRect(5).Top - this.Px(6);
            g.DrawLine(line, this.Px(10), y1, Width - this.Px(10), y1);
            g.DrawLine(line, this.Px(10), y2, Width - this.Px(10), y2);
        }
        for (var i = 0; i < Tools.Length; i++)
        {
            var r = ButtonRect(i);
            var selected = Tools[i].Tool == _selected;
            if (selected || i == _hover)
            {
                using var path = UiKit.RoundedRect(r, this.Px(8));
                using var brush = new SolidBrush(selected ? Palette.Accent : Palette.Hover);
                g.FillPath(brush, path);
            }
            DrawIcon(g, Tools[i].Tool, r, selected ? Color.White : Palette.Ink);
        }
    }

    void DrawIcon(Graphics g, EditTool tool, Rectangle r, Color color)
    {
        var k = r.Width / 36f;
        var cx = r.Left + r.Width / 2f;
        var cy = r.Top + r.Height / 2f;
        PointF P(float x, float y) => new(cx + x * k, cy + y * k);
        using var pen = new Pen(color, Math.Max(1.4f, 1.7f * k)) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
        using var fill = new SolidBrush(color);
        switch (tool)
        {
            case EditTool.Select:
                g.FillPolygon(fill, new[] { P(-6, -9), P(-6, 7), P(-2, 3), P(1, 10), P(4, 8.5f), P(1, 2), P(6, 2) });
                break;
            case EditTool.Points:
                g.DrawPolygon(pen, new[] { P(-6, -9), P(-6, 7), P(-2, 3), P(1, 10), P(4, 8.5f), P(1, 2), P(6, 2) });
                g.FillRectangle(fill, cx + 5 * k, cy - 10 * k, 5 * k, 5 * k);
                break;
            case EditTool.Pen:
                // A nib pointing down-left.
                g.DrawPolygon(pen, new[] { P(-8, 9), P(-6, 1), P(4, -9), P(9, -4), P(-1, 6) });
                g.DrawLine(pen, P(-8, 9), P(-3, 4));
                g.FillEllipse(fill, cx - 3.6f * k, cy + 2.4f * k, 2.4f * k, 2.4f * k);
                break;
            case EditTool.Find:
                g.DrawEllipse(pen, cx - 8 * k, cy - 8 * k, 12 * k, 12 * k);
                g.DrawLine(pen, P(2.5f, 2.5f), P(8, 8));
                g.DrawLine(pen, P(-5, -2), P(1, -2));
                g.DrawLine(pen, P(-2, -5), P(-2, 1));
                break;
            case EditTool.Erase:
            {
                var state = g.Save();
                g.TranslateTransform(cx, cy);
                g.RotateTransform(-40);
                using (var body = UiKit.RoundedRect(new RectangleF(-9 * k, -4.5f * k, 18 * k, 9 * k), 2 * k)) g.DrawPath(pen, body);
                g.FillRectangle(fill, -9 * k, -4.5f * k, 7 * k, 9 * k);
                g.Restore(state);
                g.DrawLine(pen, P(-2, 9), P(9, 9));
                break;
            }
            case EditTool.Hand:
            {
                using var palm = UiKit.RoundedRect(new RectangleF(cx - 6 * k, cy - 1 * k, 12 * k, 10 * k), 4 * k);
                g.DrawPath(pen, palm);
                foreach (var (x, top) in new[] { (-4.5f, -6f), (-1.5f, -9f), (1.5f, -9f), (4.5f, -7f) })
                    g.DrawLine(pen, P(x, top), P(x, 0));
                g.DrawLine(pen, P(-6, 4), P(-9, 0));
                break;
            }
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        var i = HitButton(e.Location);
        if (i < 0 || e.Button != MouseButtons.Left) return;
        Selected = Tools[i].Tool;
        ToolSelected?.Invoke(Tools[i].Tool);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var i = HitButton(e.Location);
        if (i != _hover) { _hover = i; Invalidate(); }
        if (i != _tipFor)
        {
            _tipFor = i;
            if (i >= 0) _tip.Show($"{Tools[i].Name} ({Tools[i].Key})", this, ButtonRect(i).Right + this.Px(6), ButtonRect(i).Top + this.Px(6), 2500);
            else _tip.Hide(this);
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = -1;
        _tipFor = -1;
        _tip.Hide(this);
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _tip.Dispose();
        base.Dispose(disposing);
    }
}

enum Glyph { Undo, Redo }

/// <summary>A small square button with a drawn icon (undo, redo).</summary>
sealed class IconButton : Control
{
    readonly Glyph _glyph;
    readonly ToolTip _tip = new();
    bool _hover;

    public IconButton(Glyph glyph, string tooltip)
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        _glyph = glyph;
        Size = new Size(34, 34);
        Cursor = Cursors.Hand;
        AccessibleRole = AccessibleRole.PushButton;
        AccessibleName = tooltip;
        _tip.SetToolTip(this, tooltip);
    }

    protected override void OnPaintBackground(PaintEventArgs pevent) { }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Palette.Surface);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new RectangleF(1, 1, Width - 2, Height - 2);
        if (_hover && Enabled)
        {
            using var path = UiKit.RoundedRect(r, this.Px(8));
            using var brush = new SolidBrush(Palette.Hover);
            g.FillPath(brush, path);
        }
        var color = Enabled ? Palette.Ink : Palette.DisabledText;
        var k = Math.Min(Width, Height) / 34f;
        var cx = Width / 2f;
        var cy = Height / 2f;
        using var pen = new Pen(color, Math.Max(1.5f, 1.8f * k)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        var flip = _glyph == Glyph.Redo ? -1 : 1;
        PointF P(float x, float y) => new(cx + flip * x * k, cy + y * k);
        // A hook arrow: back along the top, curving down.
        using var arc = new GraphicsPath();
        arc.AddBezier(P(-6, -3), P(2, -3), P(8, -2), P(7, 6));
        g.DrawPath(pen, arc);
        g.DrawLines(pen, new[] { P(-2, -7), P(-6, -3), P(-2, 1) });
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _tip.Dispose();
        base.Dispose(disposing);
    }
}
