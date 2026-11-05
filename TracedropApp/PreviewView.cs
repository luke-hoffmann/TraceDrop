using System.ComponentModel;
using System.Drawing.Drawing2D;
using Tracedrop.Core;
using Tracedrop.Core.Vector;

namespace TracedropApp;

enum PreviewMode
{
    /// <summary>Vector over the faded photo.</summary>
    Result,
    /// <summary>Vector on white.</summary>
    Lines,
    /// <summary>What detection found (the mask that gets traced).</summary>
    Detected,
    /// <summary>The photo after the Prepare filters.</summary>
    Prepared,
    /// <summary>The photo as loaded.</summary>
    Original
}

/// <summary>
/// The big preview: picture plus traced vector, with wheel zoom, drag to pan, double-click to fit.
/// A click (without dragging) reports a pick: left = keep that shape, right = drop it.
/// </summary>
sealed class PreviewView : Control
{
    Bitmap? _photo;
    Bitmap? _prepared;
    Bitmap? _detected;
    VectorDrawing? _drawing;
    GraphicsPath? _outlines;
    GraphicsPath? _holes;
    GraphicsPath? _strokes;
    IReadOnlyList<PickPoint> _picks = Array.Empty<PickPoint>();
    PreviewMode _mode = PreviewMode.Result;

    double _scale = 1;
    PointF _offset;
    bool _fitted = true;
    Point _downAt;
    PointF _downOffset;
    MouseButtons _downButton;
    bool _panning;
    string? _busyText;
    readonly System.Windows.Forms.Timer _spinner = new() { Interval = 60 };
    int _spinPhase;

    public PreviewView()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        BackColor = Palette.Surface;
        TabStop = true;
        _spinner.Tick += (_, _) => { _spinPhase = (_spinPhase + 1) % 12; Invalidate(new Rectangle(0, 0, Width, this.Px(48))); };
    }

    /// <summary>Left click = keep the shape under the point, right click = drop it. Coordinates are 0..1 of the picture.</summary>
    public event Action<PickPoint>? PickRequested;

    /// <summary>Width/height of the picture the vector coordinates refer to.</summary>
    public Size PictureSize { get; private set; }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public PreviewMode Mode
    {
        get => _mode;
        set { _mode = value; Invalidate(); }
    }

    /// <summary>Shown as a chip with a spinner while non-null.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string? BusyText
    {
        get => _busyText;
        set
        {
            if (_busyText == value) return;
            _busyText = value;
            if (value is null) _spinner.Stop(); else _spinner.Start();
            Invalidate();
        }
    }

    /// <summary>Text drawn when there's nothing to show.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string EmptyTitle { get; set; } = "Drop pictures here";

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string EmptyBody { get; set; } = "PNG, JPEG, HEIC, WebP, TIFF and more. Or paste with Ctrl+V.";

    /// <summary>Replaces the pictures. Bitmaps are owned by the caller. A new picture size resets the zoom.</summary>
    public void SetPictures(Bitmap? photo, Bitmap? prepared, Bitmap? detected)
    {
        var size = photo?.Size ?? Size.Empty;
        _photo = photo;
        _prepared = prepared;
        _detected = detected;
        if (size != PictureSize)
        {
            PictureSize = size;
            _fitted = true;
        }
        Invalidate();
    }

    public void SetDrawing(VectorDrawing? drawing, IReadOnlyList<PickPoint> picks)
    {
        _picks = picks;
        if (!ReferenceEquals(drawing, _drawing))
        {
            _drawing = drawing;
            _outlines?.Dispose(); _holes?.Dispose(); _strokes?.Dispose();
            _outlines = _holes = _strokes = null;
            if (drawing is not null)
            {
                _outlines = new GraphicsPath(FillMode.Alternate);
                _holes = new GraphicsPath(FillMode.Alternate);
                _strokes = new GraphicsPath(FillMode.Alternate);
                foreach (var p in drawing.Paths)
                {
                    var points = p.Flatten(0.35).Select(v => new PointF((float)v.X, (float)v.Y)).ToArray();
                    if (points.Length < 2) continue;
                    var target = p.Role switch { PathRole.Hole => _holes, PathRole.Stroke => _strokes, _ => _outlines };
                    target.StartFigure();
                    target.AddLines(points);
                    if (p.Closed || p.Circle is not null) target.CloseFigure();
                }
            }
        }
        Invalidate();
    }

    public void Clear()
    {
        SetPictures(null, null, null);
        SetDrawing(null, Array.Empty<PickPoint>());
        BusyText = null;
    }

    public void FitToView()
    {
        _fitted = true;
        Invalidate();
    }

    void EnsureFit()
    {
        if (!_fitted || PictureSize.IsEmpty) return;
        var pad = this.Px(16);
        var availW = Math.Max(10, Width - 2 * pad);
        var availH = Math.Max(10, Height - 2 * pad);
        _scale = Math.Min(availW / (double)PictureSize.Width, availH / (double)PictureSize.Height);
        _offset = new PointF((float)((Width - PictureSize.Width * _scale) / 2), (float)((Height - PictureSize.Height * _scale) / 2));
    }

    PointF ToPicture(Point p) => new((float)((p.X - _offset.X) / _scale), (float)((p.Y - _offset.Y) / _scale));

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Invalidate();
    }

    protected override void OnPaintBackground(PaintEventArgs pevent) { }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        if (PictureSize.IsEmpty)
        {
            DrawEmpty(g);
            DrawBusy(g);
            return;
        }

        EnsureFit();
        var dest = new RectangleF(_offset.X, _offset.Y, (float)(PictureSize.Width * _scale), (float)(PictureSize.Height * _scale));
        g.InterpolationMode = _scale < 1 ? InterpolationMode.HighQualityBilinear : InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;

        var picture = _mode switch
        {
            PreviewMode.Result => _photo,
            PreviewMode.Detected => _detected ?? _photo,
            PreviewMode.Prepared => _prepared ?? _photo,
            PreviewMode.Original => _photo,
            _ => null
        };

        if (_mode == PreviewMode.Lines || picture is null)
        {
            using var white = new SolidBrush(Color.White);
            g.FillRectangle(white, dest);
            using var border = new Pen(Palette.Border);
            g.DrawRectangle(border, dest.X, dest.Y, dest.Width, dest.Height);
        }
        else if (_mode == PreviewMode.Result)
        {
            // Faded photo so the lines stand out.
            using var attributes = new System.Drawing.Imaging.ImageAttributes();
            var matrix = new System.Drawing.Imaging.ColorMatrix
            {
                Matrix00 = 0.45f, Matrix11 = 0.45f, Matrix22 = 0.45f, Matrix33 = 1f,
                Matrix40 = 0.53f, Matrix41 = 0.53f, Matrix42 = 0.53f, Matrix44 = 1f
            };
            attributes.SetColorMatrix(matrix);
            g.DrawImage(picture, Rectangle.Round(dest), 0, 0, picture.Width, picture.Height, GraphicsUnit.Pixel, attributes);
        }
        else
        {
            g.DrawImage(picture, dest);
        }

        if (_mode is PreviewMode.Result or PreviewMode.Lines && _drawing is not null)
        {
            var state = g.Save();
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TranslateTransform(_offset.X, _offset.Y);
            g.ScaleTransform((float)_scale, (float)_scale);
            var width = (float)(Math.Max(1.4, this.Px(2) * 0.9) / _scale);
            var ink = _mode == PreviewMode.Lines ? Palette.Ink : Color.FromArgb(214, 32, 61);
            using (var pen = new Pen(ink, width) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                if (_outlines is not null) g.DrawPath(pen, _outlines);
                if (_strokes is not null) g.DrawPath(pen, _strokes);
            }
            using (var holePen = new Pen(Color.FromArgb(18, 110, 190), width) { LineJoin = LineJoin.Round })
            {
                if (_holes is not null) g.DrawPath(holePen, _holes);
            }
            g.Restore(state);
        }

        DrawPicks(g);
        DrawBusy(g);
    }

    void DrawPicks(Graphics g)
    {
        if (_picks.Count == 0 || PictureSize.IsEmpty) return;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = this.Px(7);
        foreach (var p in _picks)
        {
            var x = (float)(_offset.X + p.X * PictureSize.Width * _scale);
            var y = (float)(_offset.Y + p.Y * PictureSize.Height * _scale);
            using var fill = new SolidBrush(p.Keep ? Palette.Green : Palette.Red);
            using var ring = new Pen(Color.White, Math.Max(2f, this.Px(2)));
            g.FillEllipse(fill, x - r, y - r, 2 * r, 2 * r);
            g.DrawEllipse(ring, x - r, y - r, 2 * r, 2 * r);
            using var mark = new Pen(Color.White, Math.Max(1.5f, this.Px(2))) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            var s = r * 0.45f;
            if (p.Keep) g.DrawLines(mark, new[] { new PointF(x - s, y), new PointF(x - s * 0.2f, y + s * 0.8f), new PointF(x + s, y - s * 0.7f) });
            else { g.DrawLine(mark, x - s, y - s, x + s, y + s); g.DrawLine(mark, x - s, y + s, x + s, y - s); }
        }
    }

    void DrawBusy(Graphics g)
    {
        if (_busyText is null) return;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var text = TextRenderer.MeasureText(g, _busyText, UiFonts.CaptionStrong, Size.Empty, TextFormatFlags.NoPadding);
        var chip = new RectangleF(this.Px(12), this.Px(12), text.Width + this.Px(42), text.Height + this.Px(14));
        using (var path = UiKit.RoundedRect(chip, chip.Height / 2))
        using (var brush = new SolidBrush(Color.FromArgb(230, Palette.Ink)))
            g.FillPath(brush, path);
        var cx = chip.Left + this.Px(16);
        var cy = chip.Top + chip.Height / 2;
        var radius = this.Px(6);
        for (var i = 0; i < 12; i++)
        {
            var a = (i - _spinPhase) * Math.PI / 6;
            var alpha = 40 + (int)(215 * ((12 - ((i - _spinPhase + 12) % 12)) / 12.0));
            using var dot = new SolidBrush(Color.FromArgb(Math.Clamp(alpha, 0, 255), Color.White));
            var d = this.Px(2);
            g.FillEllipse(dot, (float)(cx + radius * Math.Cos(a)) - d / 2f, (float)(cy + radius * Math.Sin(a)) - d / 2f, d, d);
        }
        TextRenderer.DrawText(g, _busyText, UiFonts.CaptionStrong, new Rectangle((int)(chip.Left + this.Px(28)), (int)chip.Top, (int)chip.Width - this.Px(32), (int)chip.Height),
            Color.White, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPadding);
    }

    void DrawEmpty(Graphics g)
    {
        var pad = this.Px(24);
        var zone = new RectangleF(pad, pad, Width - 2 * pad, Height - 2 * pad);
        if (zone.Width < 40 || zone.Height < 40) return;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var path = UiKit.RoundedRect(zone, this.Px(16)))
        using (var fill = new SolidBrush(Palette.Background))
        using (var pen = new Pen(Palette.ControlBorder, Math.Max(1.5f, this.Px(2))) { DashPattern = new[] { 4f, 3f } })
        {
            g.FillPath(fill, path);
            g.DrawPath(pen, path);
        }

        var icon = this.Px(64);
        var cx = zone.Left + zone.Width / 2;
        var top = zone.Top + zone.Height / 2 - this.Px(70);
        using (var disc = new SolidBrush(Palette.GreenPale))
            g.FillEllipse(disc, cx - icon / 2f, top, icon, icon);
        // A small "picture to outline" glyph: a mountain inside a frame, traced by a pen line.
        var k = icon / 64f;
        PointF P(float x, float y) => new(cx + x * k, top + icon / 2f + y * k);
        using (var pen = new Pen(Palette.Green, Math.Max(2f, 2.6f * k)) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round })
        {
            g.DrawLines(pen, new[] { P(-16, -12), P(16, -12), P(16, 12), P(-16, 12), P(-16, -12) });
            g.DrawLines(pen, new[] { P(-12, 8), P(-3, -3), P(3, 3), P(7, -1), P(12, 8) });
        }
        const TextFormatFlags flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix;
        var y = (int)(top + icon + this.Px(16));
        TextRenderer.DrawText(g, EmptyTitle, UiFonts.Heading, new Rectangle((int)zone.Left, y, (int)zone.Width, this.Px(28)), Palette.Ink, flags);
        TextRenderer.DrawText(g, EmptyBody, UiFonts.Small, new Rectangle((int)zone.Left + pad, y + this.Px(32), (int)zone.Width - 2 * pad, this.Px(40)), Palette.Muted, flags);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (PictureSize.IsEmpty) return;
        EnsureFit();
        var before = ToPicture(e.Location);
        var factor = Math.Pow(1.0015, e.Delta);
        var fit = Math.Min(Width / (double)PictureSize.Width, Height / (double)PictureSize.Height);
        _scale = Math.Clamp(_scale * factor, fit * 0.5, 40);
        _offset = new PointF((float)(e.X - before.X * _scale), (float)(e.Y - before.Y * _scale));
        _fitted = false;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        _downAt = e.Location;
        _downButton = e.Button;
        EnsureFit();
        _downOffset = _offset;
        _panning = false;
        Capture = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_downButton == MouseButtons.None || PictureSize.IsEmpty)
        {
            Cursor = PictureSize.IsEmpty ? Cursors.Default : Cursors.Cross;
            return;
        }
        var dx = e.X - _downAt.X;
        var dy = e.Y - _downAt.Y;
        if (!_panning && dx * dx + dy * dy > this.Px(5) * this.Px(5)) _panning = true;
        if (_panning)
        {
            Cursor = Cursors.SizeAll;
            _offset = new PointF(_downOffset.X + dx, _downOffset.Y + dy);
            _fitted = false;
            Invalidate();
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        Capture = false;
        var wasPan = _panning;
        var button = _downButton;
        _downButton = MouseButtons.None;
        _panning = false;
        Cursor = Cursors.Cross;
        if (wasPan || PictureSize.IsEmpty) return;
        if (button is not (MouseButtons.Left or MouseButtons.Right)) return;
        var p = ToPicture(e.Location);
        if (p.X < 0 || p.Y < 0 || p.X > PictureSize.Width || p.Y > PictureSize.Height) return;
        PickRequested?.Invoke(new PickPoint(p.X / PictureSize.Width, p.Y / PictureSize.Height, button == MouseButtons.Left));
    }

    protected override void OnDoubleClick(EventArgs e)
    {
        base.OnDoubleClick(e);
        FitToView();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _spinner.Dispose();
            _outlines?.Dispose();
            _holes?.Dispose();
            _strokes?.Dispose();
        }
        base.Dispose(disposing);
    }
}

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
        using (var brush = new SolidBrush(_hover ? Color.FromArgb(38, 124, 72) : Palette.Green))
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
