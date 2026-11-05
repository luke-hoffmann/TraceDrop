using System.Drawing.Drawing2D;

namespace TracedropApp;

/// <summary>A pill you drag out of the window to drop the current picture's vector file anywhere.</summary>
sealed class DragChip : Control
{
    bool _hover;
    Point _downAt;
    bool _down;

    public DragChip()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        Cursor = Cursors.Hand;
        Text = "Drag DXF";
        Size = new Size(132, 36);
        AccessibleName = "Drag the vector file out of Tracedrop";
    }

    /// <summary>Asks the owner for the data to drag; null cancels.</summary>
    public Func<DataObject?>? DataProvider { get; set; }

    protected override void OnPaintBackground(PaintEventArgs pevent) { pevent.Graphics.Clear(Parent?.BackColor ?? Palette.Surface); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
        using (var path = UiKit.RoundedRect(r, r.Height / 2))
        using (var brush = new SolidBrush(_hover ? Palette.AccentHover : Palette.Accent))
            g.FillPath(brush, path);
        // Arrow out of a tray.
        var k = this.Px(1);
        var x = this.Px(16);
        var cy = Height / 2f;
        using (var pen = new Pen(Color.White, Math.Max(1.6f, 1.8f * k)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
        {
            g.DrawLine(pen, x, cy + 4 * k, x, cy - 6 * k);
            g.DrawLines(pen, new[] { new PointF(x - 4 * k, cy - 2 * k), new PointF(x, cy - 6 * k), new PointF(x + 4 * k, cy - 2 * k) });
            g.DrawLines(pen, new[] { new PointF(x - 7 * k, cy + 1 * k), new PointF(x - 7 * k, cy + 7 * k), new PointF(x + 7 * k, cy + 7 * k), new PointF(x + 7 * k, cy + 1 * k) });
        }
        TextRenderer.DrawText(g, Text, UiFonts.BodyStrong, new Rectangle(this.Px(30), 0, Width - this.Px(36), Height), Color.White,
            TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        _down = true;
        _downAt = e.Location;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_down) return;
        var drag = SystemInformation.DragSize;
        if (Math.Abs(e.X - _downAt.X) < drag.Width && Math.Abs(e.Y - _downAt.Y) < drag.Height) return;
        _down = false;
        var data = DataProvider?.Invoke();
        if (data is not null) DoDragDrop(data, DragDropEffects.Copy);
    }

    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); _down = false; }
    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }
}
