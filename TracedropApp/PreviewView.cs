using System.ComponentModel;
using System.Drawing.Drawing2D;
using Tracedrop.Core.Editing;
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
/// The canvas: the picture with the AI's lines and your own on top, and the editing tools (select, edit points,
/// pen, Find lines, Erase lines, hand). Wheel zooms, Space or the middle button pans, double-click on empty
/// space fits. Edits are reported through <see cref="EditCommitted"/>; the owner keeps undo and re-traces.
/// This file is the view and drawing; PreviewView.Editing.cs has the tools.
/// </summary>
sealed partial class PreviewView : Control
{
    /// <summary>A traced (AI) path with its points in drawing pixels, for drawing and hit testing.</summary>
    sealed record Traced(int Index, VectorPath Path, PointF[] Points, RectangleF Bounds);

    Bitmap? _photo;
    Bitmap? _prepared;
    Bitmap? _detected;
    VectorDrawing? _drawing;
    SizeF _drawingSize;
    GraphicsPath? _outlines;
    GraphicsPath? _holes;
    GraphicsPath? _strokes;
    readonly List<Traced> _traced = new();
    PreviewMode _mode = PreviewMode.Result;

    double _scale = 1;
    PointF _offset;
    bool _fitted = true;
    string? _busyText;
    readonly System.Windows.Forms.Timer _spinner = new() { Interval = 60 };
    int _spinPhase;

    static readonly Color FindColor = Color.FromArgb(0, 150, 136);
    static readonly Color EraseColor = Color.FromArgb(214, 64, 64);
    static readonly Color AiInk = Color.FromArgb(214, 32, 61);
    static readonly Color HoleInk = Color.FromArgb(150, 52, 168);
    static readonly Color MineInk = Palette.Accent;

    public PreviewView()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        BackColor = Palette.Surface;
        TabStop = true;
        _spinner.Tick += (_, _) => { _spinPhase = (_spinPhase + 1) % 12; Invalidate(new Rectangle(0, 0, Width, this.Px(48))); };
    }

    /// <summary>Width/height of the picture on screen (the photo); edits use 0..1 of it.</summary>
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

    /// <summary>Shows the drawing on white at this picture size while the photo itself is on its way.</summary>
    public void SetPlaceholder(Size pictureSize)
    {
        _photo = _prepared = _detected = null;
        if (pictureSize != PictureSize)
        {
            PictureSize = pictureSize;
            _fitted = true;
        }
        Invalidate();
    }

    /// <summary>The traced drawing (your own paths are drawn from the edits, not from here).</summary>
    public void SetDrawing(VectorDrawing? drawing)
    {
        if (ReferenceEquals(drawing, _drawing)) return;
        _drawing = drawing;
        _drawingSize = drawing is null ? SizeF.Empty : new SizeF((float)drawing.Width, (float)drawing.Height);
        _traced.Clear();
        _selAi.Clear();
        _hiddenAi.Clear();
        _hoverAi = -1;
        if (drawing is not null)
        {
            for (var i = 0; i < drawing.Paths.Count; i++)
            {
                var p = drawing.Paths[i];
                if (p.IsManual) continue;
                var points = p.Flatten(0.35).Select(v => new PointF((float)v.X, (float)v.Y)).ToArray();
                if (points.Length < 2) continue;
                if (p.Closed || p.Circle is not null) points = points.Append(points[0]).ToArray();
                var minX = points.Min(q => q.X);
                var minY = points.Min(q => q.Y);
                _traced.Add(new Traced(i, p, points, RectangleF.FromLTRB(minX, minY, points.Max(q => q.X), points.Max(q => q.Y))));
            }
        }
        RebuildTracedPaths();
        SelectionChanged?.Invoke();
        Invalidate();
    }

    /// <summary>Builds the drawing's outlines, holes and lines for painting, leaving out hidden (just deleted) paths.</summary>
    void RebuildTracedPaths()
    {
        _outlines?.Dispose(); _holes?.Dispose(); _strokes?.Dispose();
        _outlines = new GraphicsPath(FillMode.Alternate);
        _holes = new GraphicsPath(FillMode.Alternate);
        _strokes = new GraphicsPath(FillMode.Alternate);
        foreach (var t in _traced)
        {
            if (_hiddenAi.Contains(t.Index)) continue;
            var target = t.Path.Role switch { PathRole.Hole => _holes, PathRole.Stroke => _strokes, _ => _outlines };
            target.StartFigure();
            target.AddLines(t.Points);
        }
    }

    /// <summary>Hides traced paths at once (deleted or taken over); the re-trace confirms it a moment later.</summary>
    void HideTraced(IEnumerable<int> indices)
    {
        foreach (var i in indices) _hiddenAi.Add(i);
        if (_hiddenAi.Contains(_hoverAi)) _hoverAi = -1;
        _selAi.ExceptWith(_hiddenAi);
        RebuildTracedPaths();
        Invalidate();
    }

    public void Clear()
    {
        CancelPen();
        SetPictures(null, null, null);
        SetDrawing(null);
        SetEdits(EditState.Empty);
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

    // ------------------------------------------------------------------ coordinates
    // Screen: control pixels. Picture: photo pixels. Norm: 0..1 of the picture (edits). Drawing: traced pixels.

    PointF ToPicture(PointF p) => new((float)((p.X - _offset.X) / _scale), (float)((p.Y - _offset.Y) / _scale));

    PointF PictureToScreen(double x, double y) => new((float)(_offset.X + x * _scale), (float)(_offset.Y + y * _scale));

    PointF NormToScreen(NormPoint n) => PictureToScreen(n.X * PictureSize.Width, n.Y * PictureSize.Height);

    NormPoint ScreenToNorm(PointF p)
    {
        var q = ToPicture(p);
        return new NormPoint(q.X / (double)PictureSize.Width, q.Y / (double)PictureSize.Height);
    }

    float DrawingScaleX => _drawingSize.Width > 0 ? PictureSize.Width / _drawingSize.Width : 1f;
    float DrawingScaleY => _drawingSize.Height > 0 ? PictureSize.Height / _drawingSize.Height : 1f;

    PointF ScreenToDrawing(PointF p)
    {
        var q = ToPicture(p);
        return new PointF(q.X / DrawingScaleX, q.Y / DrawingScaleY);
    }

    PointF DrawingToScreen(PointF d) => PictureToScreen(d.X * DrawingScaleX, d.Y * DrawingScaleY);

    /// <summary>A size given as a fraction of the picture's longest side, on screen.</summary>
    float FractionToScreen(double fraction) => (float)(fraction * Math.Max(PictureSize.Width, PictureSize.Height) * _scale);

    double ScreenToFraction(double pixels) => pixels / Math.Max(1e-9, Math.Max(PictureSize.Width, PictureSize.Height) * _scale);

    bool OnPicture(PointF screen)
    {
        var p = ToPicture(screen);
        return p.X >= 0 && p.Y >= 0 && p.X <= PictureSize.Width && p.Y <= PictureSize.Height;
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Invalidate();
    }

    protected override void OnPaintBackground(PaintEventArgs pevent) { }

    // ------------------------------------------------------------------ painting

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

        g.PixelOffsetMode = PixelOffsetMode.Default;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        DrawRegions(g);
        if (_mode is PreviewMode.Result or PreviewMode.Lines) DrawTraced(g);
        DrawMine(g);
        DrawToolOverlay(g);
        DrawBusy(g);
    }

    float LineWidth => Math.Max(1.4f, this.Px(2) * 0.9f);

    static Pen RoundPen(Color color, float width) =>
        new(color, width) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };

    void DrawTraced(Graphics g)
    {
        if (_drawing is null) return;
        var state = g.Save();
        g.TranslateTransform(_offset.X, _offset.Y);
        // The drawing's own pixel size may differ from the photo on screen (another Resolution, or a photo still
        // on its way): scale it onto the picture so the two always line up.
        var kx = DrawingScaleX;
        var ky = DrawingScaleY;
        g.ScaleTransform((float)_scale * kx, (float)_scale * ky);
        var width = (float)(LineWidth / (_scale * kx));
        var ink = _mode == PreviewMode.Lines ? Palette.Ink : AiInk;
        using (var pen = RoundPen(ink, width))
        {
            if (_outlines is not null) g.DrawPath(pen, _outlines);
            if (_strokes is not null) g.DrawPath(pen, _strokes);
        }
        using (var holePen = RoundPen(HoleInk, width))
        {
            if (_holes is not null) g.DrawPath(holePen, _holes);
        }
        g.Restore(state);

        // Hover and selection.
        if (_hoverAi >= 0 && !_selAi.Contains(_hoverAi) && _traced.FirstOrDefault(t => t.Index == _hoverAi) is { } hover)
        {
            using var hoverPen = RoundPen(Color.FromArgb(150, Palette.Accent), LineWidth + this.Px(2));
            DrawPolyline(g, hoverPen, hover.Points);
        }
        if (_selAi.Count > 0)
        {
            using var glow = RoundPen(Color.FromArgb(70, Palette.Accent), LineWidth + this.Px(6));
            using var line = RoundPen(Palette.Accent, LineWidth + this.Px(1));
            foreach (var t in _traced.Where(t => _selAi.Contains(t.Index)))
            {
                DrawPolyline(g, glow, t.Points);
                DrawPolyline(g, line, t.Points);
            }
        }
    }

    void DrawPolyline(Graphics g, Pen pen, PointF[] drawingPoints)
    {
        if (drawingPoints.Length < 2) return;
        g.DrawLines(pen, drawingPoints.Select(DrawingToScreen).ToArray());
    }

    /// <summary>Your own paths, in blue: with their points when selected or being edited.</summary>
    void DrawMine(Graphics g)
    {
        for (var i = 0; i < _edit.Manual.Count; i++)
        {
            var path = _edit.Manual[i];
            using var gp = ScreenPath(path);
            if (gp is null) continue;
            var selected = _selOwn.Contains(i) || (i == _active && _tool == EditTool.Points);
            if (selected)
            {
                using var glow = RoundPen(Color.FromArgb(60, MineInk), LineWidth + this.Px(6));
                g.DrawPath(glow, gp);
            }
            else if (i == _hoverOwn)
            {
                using var hover = RoundPen(Color.FromArgb(90, MineInk), LineWidth + this.Px(4));
                g.DrawPath(hover, gp);
            }
            using var pen = RoundPen(MineInk, LineWidth + (selected ? this.Px(1) : 0));
            g.DrawPath(pen, gp);
            if (i == _active && _tool == EditTool.Points) DrawNodes(g, path, handles: true, _activeNode);
            else if (_selOwn.Contains(i)) DrawNodes(g, path, handles: false, -1);
        }
    }

    GraphicsPath? ScreenPath(ManualPath path)
    {
        if (path.Nodes.Count < 2) return null;
        var gp = new GraphicsPath();
        var count = path.Closed ? path.Nodes.Count : path.Nodes.Count - 1;
        for (var k = 0; k < count; k++)
        {
            var a = path.Nodes[k];
            var b = path.Nodes[(k + 1) % path.Nodes.Count];
            var p0 = NormToScreen(a.Point);
            var p3 = NormToScreen(b.Point);
            if (a.Out is null && b.In is null) gp.AddLine(p0, p3);
            else gp.AddBezier(p0, NormToScreen(a.Out ?? a.Point), NormToScreen(b.In ?? b.Point), p3);
        }
        if (path.Closed) gp.CloseFigure();
        return gp;
    }

    int AnchorSize => this.Px(8);
    int HandleSize => this.Px(7);

    void DrawNodes(Graphics g, ManualPath path, bool handles, int selectedNode)
    {
        using var handleLine = new Pen(Color.FromArgb(190, MineInk), 1f);
        using var white = new SolidBrush(Color.White);
        using var outline = new Pen(MineInk, Math.Max(1.2f, this.Px(1) * 1.2f));
        using var fill = new SolidBrush(MineInk);
        var showAllHandles = path.Nodes.Count <= 60;
        for (var i = 0; i < path.Nodes.Count; i++)
        {
            var n = path.Nodes[i];
            var c = NormToScreen(n.Point);
            var near = selectedNode >= 0 && (Math.Abs(i - selectedNode) <= 1 || (path.Closed && Math.Abs(i - selectedNode) == path.Nodes.Count - 1));
            if (handles && (showAllHandles || near))
            {
                foreach (var hnd in new[] { n.In, n.Out })
                {
                    if (hnd is null) continue;
                    var hp = NormToScreen(hnd);
                    g.DrawLine(handleLine, c, hp);
                    var r = HandleSize / 2f;
                    g.FillEllipse(white, hp.X - r, hp.Y - r, 2 * r, 2 * r);
                    g.DrawEllipse(outline, hp.X - r, hp.Y - r, 2 * r, 2 * r);
                }
            }
            var a = AnchorSize / 2f;
            var box = new RectangleF(c.X - a, c.Y - a, 2 * a, 2 * a);
            g.FillRectangle(i == selectedNode ? fill : white, box);
            g.DrawRectangle(outline, box.X, box.Y, box.Width, box.Height);
        }
    }

    void DrawRegions(Graphics g)
    {
        for (var i = 0; i < _edit.Regions.Count; i++)
        {
            var r = _edit.Regions[i];
            if (r.Kind == RegionKind.Suppress) continue;
            DrawRegion(g, r, i == _selRegion, i == _hoverRegion);
        }
    }

    void DrawRegion(Graphics g, GuideRegion r, bool selected, bool hover, bool preview = false)
    {
        var color = r.Kind == RegionKind.Find ? FindColor : EraseColor;
        var fillAlpha = preview ? 30 : selected || hover ? 56 : 34;
        using var fill = new SolidBrush(Color.FromArgb(fillAlpha, color));
        using var outline = new Pen(Color.FromArgb(selected ? 255 : 200, color), selected ? Math.Max(2f, this.Px(2)) : Math.Max(1.2f, this.Px(1) * 1.4f))
        {
            DashPattern = new[] { 4f, 3f }
        };
        RectangleF bounds;
        switch (r.Shape)
        {
            case RegionShape.Circle when r.Points.Count > 0:
            {
                var c = NormToScreen(r.Points[0]);
                var radius = FractionToScreen(r.Size);
                bounds = new RectangleF(c.X - radius, c.Y - radius, 2 * radius, 2 * radius);
                g.FillEllipse(fill, bounds);
                g.DrawEllipse(outline, bounds);
                break;
            }
            case RegionShape.Rectangle when r.Points.Count > 1:
            {
                var a = NormToScreen(r.Points[0]);
                var b = NormToScreen(r.Points[1]);
                bounds = RectangleF.FromLTRB(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
                g.FillRectangle(fill, bounds);
                g.DrawRectangle(outline, bounds.X, bounds.Y, bounds.Width, bounds.Height);
                break;
            }
            case RegionShape.Brush when r.Points.Count > 0:
            {
                var pts = r.Points.Select(NormToScreen).ToArray();
                var width = Math.Max(1, 2 * FractionToScreen(r.Size));
                var alpha = preview ? 50 : selected || hover ? 80 : 60;
                using var brush = RoundPen(Color.FromArgb(alpha, color), width);
                using var dot = new SolidBrush(Color.FromArgb(alpha, color));
                if (pts.Length == 1) g.FillEllipse(dot, pts[0].X - width / 2, pts[0].Y - width / 2, width, width);
                else g.DrawLines(brush, pts);
                bounds = RectangleF.FromLTRB(pts.Min(p => p.X) - width / 2, pts.Min(p => p.Y) - width / 2, pts.Max(p => p.X) + width / 2, pts.Max(p => p.Y) + width / 2);
                break;
            }
            default:
                return;
        }

        if (r.Kind == RegionKind.Find && !preview) DrawAmountBadge(g, r.Amount, new PointF(bounds.Right, bounds.Top), color);
    }

    void DrawAmountBadge(Graphics g, int amount, PointF corner, Color color)
    {
        var text = amount > 0 ? "+" + amount : amount < 0 ? "−" + -amount : "0";
        var size = TextRenderer.MeasureText(g, text, UiFonts.CaptionStrong, Size.Empty, TextFormatFlags.NoPadding);
        var pill = new RectangleF(corner.X - size.Width / 2f - this.Px(7), corner.Y - size.Height / 2f - this.Px(3), size.Width + this.Px(14), size.Height + this.Px(6));
        using (var path = UiKit.RoundedRect(pill, pill.Height / 2))
        using (var brush = new SolidBrush(color))
            g.FillPath(brush, path);
        TextRenderer.DrawText(g, text, UiFonts.CaptionStrong, Rectangle.Round(pill), Color.White,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
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
        using (var disc = new SolidBrush(Palette.AccentPale))
            g.FillEllipse(disc, cx - icon / 2f, top, icon, icon);
        // The Tracedrop generator, drawn as an outline (an "outline generator").
        var k = icon / 64f;
        float X(float x) => cx + x * k;
        float Y(float y) => top + icon / 2f + y * k;
        using (var pen = RoundPen(Palette.Accent, Math.Max(1.6f, 2.4f * k)))
        using (var fill = new SolidBrush(Palette.Accent))
        {
            using (var tank = UiKit.RoundedRect(new RectangleF(X(-11), Y(-15), 22 * k, 7 * k), 3 * k)) g.DrawPath(pen, tank);
            using (var frame = UiKit.RoundedRect(new RectangleF(X(-18), Y(-5), 36 * k, 19 * k), 5 * k)) g.DrawPath(pen, frame);
            g.FillPolygon(fill, new[] { new PointF(X(2), Y(-2)), new PointF(X(-5), Y(6)), new PointF(X(-0.5f), Y(6)), new PointF(X(-2.5f), Y(12)), new PointF(X(5), Y(3)), new PointF(X(0.5f), Y(3)), new PointF(X(3), Y(-2)) });
            g.DrawEllipse(pen, X(-14), Y(14), 7 * k, 7 * k);
            g.DrawEllipse(pen, X(7), Y(14), 7 * k, 7 * k);
        }
        const TextFormatFlags flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix;
        var y = (int)(top + icon + this.Px(16));
        TextRenderer.DrawText(g, EmptyTitle, UiFonts.Heading, new Rectangle((int)zone.Left, y, (int)zone.Width, this.Px(28)), Palette.Ink, flags);
        TextRenderer.DrawText(g, EmptyBody, UiFonts.Small, new Rectangle((int)zone.Left + pad, y + this.Px(32), (int)zone.Width - 2 * pad, this.Px(40)), Palette.Muted, flags);
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
