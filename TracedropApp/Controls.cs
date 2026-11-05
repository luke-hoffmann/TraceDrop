using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace TracedropApp;

/// <summary>Lays its visible children out top to bottom at full width, and scrolls when they don't fit.</summary>
sealed class StackPanel : Panel
{
    public StackPanel()
    {
        AutoScroll = true;
        DoubleBuffered = true;
        BackColor = Palette.Surface;
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        var width = ClientSize.Width - Padding.Horizontal;
        var y = Padding.Top + AutoScrollPosition.Y;
        foreach (Control c in Controls)
        {
            if (!c.Visible) continue;
            y += c.Margin.Top;
            c.SetBounds(Padding.Left + c.Margin.Left + AutoScrollPosition.X, y, Math.Max(10, width - c.Margin.Horizontal), c.Height);
            y += c.Height + c.Margin.Bottom;
        }
        var total = y - AutoScrollPosition.Y + Padding.Bottom;
        if (AutoScrollMinSize.Height != total) AutoScrollMinSize = new Size(0, total);
        base.OnLayout(levent);
    }

    protected override void OnResize(EventArgs eventargs)
    {
        base.OnResize(eventargs);
        PerformLayout();
    }
}

/// <summary>A label and value on one line with a custom track underneath. Double-click resets to the default.</summary>
sealed class SliderRow : Control
{
    double _value;
    bool _dragging;
    bool _hover;

    public SliderRow(string label, double min, double max, double value, double step = 1, Func<double, string>? format = null)
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        Label = label;
        Minimum = min;
        Maximum = max;
        Step = step;
        Format = format ?? (v => v.ToString("0"));
        DefaultValue = value;
        _value = value;
        Height = 50;
        TabStop = true;
        Cursor = Cursors.Hand;
        AccessibleRole = AccessibleRole.Slider;
        AccessibleName = label;
    }

    public string Label { get; set; }
    public double Minimum { get; }
    public double Maximum { get; }
    public double Step { get; }
    public double DefaultValue { get; set; }
    public Func<double, string> Format { get; set; }
    /// <summary>Optional hint drawn after the label in muted text.</summary>
    public string? Hint { get; set; }

    /// <summary>Fires on every change while dragging (live preview).</summary>
    public event EventHandler? ValueChanged;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public double Value
    {
        get => _value;
        set => SetValue(value, raise: false);
    }

    void SetValue(double v, bool raise)
    {
        v = Math.Clamp(Math.Round((v - Minimum) / Step) * Step + Minimum, Minimum, Maximum);
        if (Math.Abs(v - _value) < 1e-9) return;
        _value = v;
        AccessibleDescription = Format(v);
        Invalidate();
        if (raise) ValueChanged?.Invoke(this, EventArgs.Empty);
    }

    Rectangle Track => new(this.Px(8), Height - this.Px(16), Width - this.Px(16), this.Px(4));

    protected override void OnPaintBackground(PaintEventArgs pevent) { }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Palette.Surface);
        var textRect = new Rectangle(0, 0, Width, Height - this.Px(22));
        const TextFormatFlags flags = TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
        var labelColor = Enabled ? Palette.Ink : Palette.DisabledText;
        TextRenderer.DrawText(g, Label, UiFonts.Body, textRect, labelColor, flags | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
        if (Hint is { Length: > 0 } hint)
        {
            var w = TextRenderer.MeasureText(g, Label, UiFonts.Body, Size.Empty, flags).Width;
            TextRenderer.DrawText(g, hint, UiFonts.Caption, new Rectangle(w + this.Px(6), 0, Width - w - this.Px(60), textRect.Height), Palette.Muted, flags | TextFormatFlags.EndEllipsis);
        }
        TextRenderer.DrawText(g, Format(_value), UiFonts.BodyStrong, textRect, Enabled ? Palette.Green : Palette.DisabledText, flags | TextFormatFlags.Right);

        var track = Track;
        var t = (Maximum - Minimum) <= 0 ? 0 : (_value - Minimum) / (Maximum - Minimum);
        var thumbX = track.Left + (int)Math.Round(t * track.Width);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var path = UiKit.RoundedRect(track, track.Height / 2f))
        using (var brush = new SolidBrush(Palette.Border))
            g.FillPath(brush, path);
        // Sliders that go below zero fill from zero, so "0" reads as neutral.
        var zeroX = Minimum < 0 && Maximum > 0 ? track.Left + (int)Math.Round(-Minimum / (Maximum - Minimum) * track.Width) : track.Left;
        var filled = new RectangleF(Math.Min(zeroX, thumbX), track.Top, Math.Abs(thumbX - zeroX), track.Height);
        if (filled.Width > 0)
        {
            using var path = UiKit.RoundedRect(filled, track.Height / 2f);
            using var brush = new SolidBrush(Enabled ? Palette.Green : Palette.DisabledText);
            g.FillPath(brush, path);
        }
        var r = this.Px(_dragging || _hover || Focused ? 9 : 8);
        var cy = track.Top + track.Height / 2f;
        using (var shadow = new SolidBrush(Color.FromArgb(40, 0, 0, 0)))
            g.FillEllipse(shadow, thumbX - r, cy - r + 1, 2 * r, 2 * r);
        using (var white = new SolidBrush(Color.White))
            g.FillEllipse(white, thumbX - r, cy - r, 2 * r, 2 * r);
        using (var ring = new Pen(Enabled ? Palette.Green : Palette.DisabledText, Math.Max(2f, this.Px(2))))
            g.DrawEllipse(ring, thumbX - r + 1, cy - r + 1, 2 * r - 2, 2 * r - 2);
        if (Focused && ShowFocusCues)
        {
            using var focus = new Pen(Color.FromArgb(70, Palette.Green), this.Px(4));
            g.DrawEllipse(focus, thumbX - r - 3, cy - r - 3, 2 * r + 6, 2 * r + 6);
        }
    }

    double ValueAt(int x)
    {
        var track = Track;
        var t = Math.Clamp((x - track.Left) / (double)Math.Max(1, track.Width), 0, 1);
        return Minimum + t * (Maximum - Minimum);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left || !Enabled) return;
        Focus();
        _dragging = true;
        Capture = true;
        SetValue(ValueAt(e.X), raise: true);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging) SetValue(ValueAt(e.X), raise: true);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _dragging = false;
        Capture = false;
        Invalidate();
    }

    protected override void OnDoubleClick(EventArgs e)
    {
        base.OnDoubleClick(e);
        SetValue(DefaultValue, raise: true);
    }

    protected override bool IsInputKey(Keys keyData) =>
        keyData is Keys.Left or Keys.Right or Keys.Up or Keys.Down or Keys.Home or Keys.End || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var big = (Maximum - Minimum) / 10;
        switch (e.KeyCode)
        {
            case Keys.Left or Keys.Down: SetValue(_value - (e.Shift ? big : Step), true); break;
            case Keys.Right or Keys.Up: SetValue(_value + (e.Shift ? big : Step), true); break;
            case Keys.PageDown: SetValue(_value - big, true); break;
            case Keys.PageUp: SetValue(_value + big, true); break;
            case Keys.Home: SetValue(Minimum, true); break;
            case Keys.End: SetValue(Maximum, true); break;
            default: return;
        }
        e.Handled = true;
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
}

/// <summary>A row of mutually exclusive pill segments, with an optional label above.</summary>
sealed class Segmented : Control
{
    readonly string[] _options;
    int _selected;
    int _hover = -1;

    public Segmented(string? label, params string[] options)
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        Label = label;
        _options = options;
        Height = label is null ? 38 : 62;
        TabStop = true;
        Cursor = Cursors.Hand;
        AccessibleRole = AccessibleRole.PageTabList;
        AccessibleName = label ?? string.Join(", ", options);
    }

    public string? Label { get; }

    /// <summary>Per-option tooltips shown under the control's hint area (optional).</summary>
    public string[]? Descriptions { get; set; }

    public event EventHandler? SelectedIndexChanged;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int SelectedIndex
    {
        get => _selected;
        set
        {
            value = Math.Clamp(value, 0, _options.Length - 1);
            if (value == _selected) return;
            _selected = value;
            Invalidate();
        }
    }

    Rectangle Bar => Label is null ? new Rectangle(0, 2, Width, Height - 4) : new Rectangle(0, this.Px(26), Width, Height - this.Px(28));

    Rectangle SegmentRect(int i)
    {
        var bar = Bar;
        var inner = Rectangle.Inflate(bar, -this.Px(3), -this.Px(3));
        var w = inner.Width / (double)_options.Length;
        return new Rectangle(inner.Left + (int)Math.Round(i * w), inner.Top, (int)Math.Round((i + 1) * w) - (int)Math.Round(i * w), inner.Height);
    }

    protected override void OnPaintBackground(PaintEventArgs pevent) { }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Palette.Surface);
        if (Label is not null)
            TextRenderer.DrawText(g, Label, UiFonts.Body, new Rectangle(0, 0, Width, this.Px(24)), Enabled ? Palette.Ink : Palette.DisabledText,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);

        g.SmoothingMode = SmoothingMode.AntiAlias;
        var bar = Bar;
        using (var path = UiKit.RoundedRect(new RectangleF(bar.X + 0.5f, bar.Y + 0.5f, bar.Width - 1, bar.Height - 1), this.Px(10)))
        using (var brush = new SolidBrush(Palette.Background))
        using (var pen = new Pen(Palette.Border))
        {
            g.FillPath(brush, path);
            g.DrawPath(pen, path);
        }
        for (var i = 0; i < _options.Length; i++)
        {
            var r = SegmentRect(i);
            var selected = i == _selected;
            if (selected || i == _hover)
            {
                using var path = UiKit.RoundedRect(r, this.Px(8));
                using var brush = new SolidBrush(selected ? (Enabled ? Palette.Green : Palette.DisabledText) : Palette.Hover);
                g.FillPath(brush, path);
            }
            var color = selected ? Color.White : Enabled ? Palette.Ink : Palette.DisabledText;
            TextRenderer.DrawText(g, _options[i], selected ? UiFonts.BodyStrong : UiFonts.Small, r, color,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        }
        if (Focused && ShowFocusCues)
        {
            using var pen = new Pen(Color.FromArgb(120, Palette.Green), 2);
            using var path = UiKit.RoundedRect(new RectangleF(bar.X + 1, bar.Y + 1, bar.Width - 2, bar.Height - 2), this.Px(10));
            g.DrawPath(pen, path);
        }
    }

    int HitSegment(Point p)
    {
        for (var i = 0; i < _options.Length; i++) if (SegmentRect(i).Contains(p)) return i;
        return -1;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left || !Enabled) return;
        Focus();
        var i = HitSegment(e.Location);
        if (i >= 0 && i != _selected)
        {
            SelectedIndex = i;
            SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var i = HitSegment(e.Location);
        if (i != _hover) { _hover = i; Invalidate(); }
    }

    protected override void OnMouseLeave(EventArgs e) { _hover = -1; Invalidate(); base.OnMouseLeave(e); }

    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Left or Keys.Right || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var next = e.KeyCode switch { Keys.Left => _selected - 1, Keys.Right => _selected + 1, _ => _selected };
        if (next == _selected || next < 0 || next >= _options.Length) return;
        SelectedIndex = next;
        SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
}

/// <summary>Label on the left, switch on the right.</summary>
sealed class ToggleRow : Control
{
    bool _checked;

    public ToggleRow(string label, bool value = false, string? hint = null)
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        Label = label;
        Hint = hint;
        _checked = value;
        Height = hint is null ? 34 : 48;
        TabStop = true;
        Cursor = Cursors.Hand;
        AccessibleRole = AccessibleRole.CheckButton;
        AccessibleName = label;
    }

    public string Label { get; set; }
    public string? Hint { get; set; }
    public event EventHandler? CheckedChanged;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Checked
    {
        get => _checked;
        set { if (_checked == value) return; _checked = value; Invalidate(); }
    }

    protected override void OnPaintBackground(PaintEventArgs pevent) { }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Palette.Surface);
        var switchW = this.Px(36);
        var switchH = this.Px(20);
        var textW = Width - switchW - this.Px(10);
        const TextFormatFlags flags = TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine;
        if (Hint is null)
            TextRenderer.DrawText(g, Label, UiFonts.Body, new Rectangle(0, 0, textW, Height), Enabled ? Palette.Ink : Palette.DisabledText, flags | TextFormatFlags.VerticalCenter);
        else
        {
            TextRenderer.DrawText(g, Label, UiFonts.Body, new Rectangle(0, this.Px(4), textW, this.Px(20)), Enabled ? Palette.Ink : Palette.DisabledText, flags);
            TextRenderer.DrawText(g, Hint, UiFonts.Caption, new Rectangle(0, this.Px(25), textW, this.Px(18)), Palette.Muted, flags);
        }

        var r = new RectangleF(Width - switchW - 1, (Height - switchH) / 2f, switchW, switchH);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var path = UiKit.RoundedRect(r, switchH / 2f))
        using (var brush = new SolidBrush(_checked ? (Enabled ? Palette.Green : Palette.DisabledText) : Palette.ControlBorder))
            g.FillPath(brush, path);
        var knob = switchH - this.Px(6);
        var kx = _checked ? r.Right - knob - this.Px(3) : r.Left + this.Px(3);
        using (var white = new SolidBrush(Color.White))
            g.FillEllipse(white, kx, r.Top + this.Px(3), knob, knob);
        if (Focused && ShowFocusCues)
        {
            using var pen = new Pen(Color.FromArgb(110, Palette.Green), 2);
            using var path = UiKit.RoundedRect(RectangleF.Inflate(r, 2, 2), switchH / 2f + 2);
            g.DrawPath(pen, path);
        }
    }

    void Toggle()
    {
        if (!Enabled) return;
        Checked = !_checked;
        CheckedChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (e.Button == MouseButtons.Left) { Focus(); Toggle(); } }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode is Keys.Space or Keys.Enter) { Toggle(); e.Handled = true; }
    }

    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
}

/// <summary>Bold section title with a chevron; clicking toggles the visibility of the controls listed in <see cref="Content"/>.</summary>
sealed class SectionHeader : Control
{
    bool _expanded = true;
    bool _hover;

    public SectionHeader(string title)
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        Text = title;
        Height = 40;
        Margin = new Padding(0, 10, 0, 0);
        TabStop = true;
        Cursor = Cursors.Hand;
        AccessibleRole = AccessibleRole.OutlineButton;
        AccessibleName = title;
    }

    public List<Control> Content { get; } = new();
    public event EventHandler? ExpandedChanged;

    /// <summary>Content that must also be hidden when it isn't relevant (e.g. mode-specific rows), regardless of collapse state.</summary>
    public Func<Control, bool> IsRelevant { get; set; } = _ => true;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Expanded
    {
        get => _expanded;
        set
        {
            if (_expanded == value) return;
            _expanded = value;
            ApplyVisibility();
            Invalidate();
            ExpandedChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void ApplyVisibility()
    {
        Parent?.SuspendLayout();
        foreach (var c in Content) c.Visible = _expanded && IsRelevant(c);
        Parent?.ResumeLayout(true);
    }

    protected override void OnPaintBackground(PaintEventArgs pevent) { }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Palette.Surface);
        using (var line = new Pen(Palette.Border))
            g.DrawLine(line, 0, 0, Width, 0);
        TextRenderer.DrawText(g, Text.ToUpperInvariant(), UiFonts.CaptionStrong, new Rectangle(0, 0, Width - this.Px(24), Height),
            _hover ? Palette.Ink : Palette.Muted, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var cx = Width - this.Px(10);
        var cy = Height / 2f;
        var s = this.Px(4);
        using var pen = new Pen(_hover ? Palette.Ink : Palette.Muted, Math.Max(1.5f, this.Px(2))) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        if (_expanded) g.DrawLines(pen, new[] { new PointF(cx - s, cy - s / 2f), new PointF(cx, cy + s / 2f), new PointF(cx + s, cy - s / 2f) });
        else g.DrawLines(pen, new[] { new PointF(cx - s / 2f, cy - s), new PointF(cx + s / 2f, cy), new PointF(cx - s / 2f, cy + s) });
        if (Focused && ShowFocusCues)
        {
            using var focus = new Pen(Color.FromArgb(110, Palette.Green), 2);
            g.DrawRectangle(focus, 1, 2, Width - 3, Height - 4);
        }
    }

    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (e.Button == MouseButtons.Left) Expanded = !Expanded; }
    protected override void OnKeyDown(KeyEventArgs e) { base.OnKeyDown(e); if (e.KeyCode is Keys.Space or Keys.Enter) { Expanded = !Expanded; e.Handled = true; } }
    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
}

/// <summary>Label, a number box and a unit on one line.</summary>
sealed class NumberRow : Panel
{
    readonly Label _label;
    readonly Label _unit;

    public NumberRow(string label, decimal min, decimal max, decimal value, int decimals, string unit)
    {
        Height = 36;
        BackColor = Palette.Surface;
        _label = new Label { Text = label, Font = UiFonts.Body, ForeColor = Palette.Ink, AutoSize = false, TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Fill, UseMnemonic = false };
        Box = new NumericUpDown
        {
            Minimum = min,
            Maximum = max,
            DecimalPlaces = decimals,
            Increment = decimals > 0 ? 0.5m : 1,
            Value = Math.Clamp(value, min, max),
            Font = UiFonts.Body,
            Width = 86,
            Dock = DockStyle.Right,
            TextAlign = HorizontalAlignment.Right,
            BorderStyle = BorderStyle.FixedSingle
        };
        _unit = new Label { Text = unit, Font = UiFonts.Small, ForeColor = Palette.Muted, AutoSize = false, Width = 34, Dock = DockStyle.Right, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(6, 0, 0, 0), UseMnemonic = false };
        Controls.Add(_label);
        Controls.Add(Box);
        Controls.Add(_unit);
        Box.AccessibleName = label;
    }

    public NumericUpDown Box { get; }

    public string Unit
    {
        get => _unit.Text;
        set => _unit.Text = value;
    }
}

/// <summary>Label with a dropdown on the right.</summary>
sealed class DropdownRow : Panel
{
    public DropdownRow(string label, params string[] items)
    {
        Height = 36;
        BackColor = Palette.Surface;
        var text = new Label { Text = label, Font = UiFonts.Body, ForeColor = Palette.Ink, AutoSize = false, TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Fill, UseMnemonic = false };
        Box = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Font = UiFonts.Body, Width = 140, Dock = DockStyle.Right, FlatStyle = FlatStyle.Flat };
        Box.Items.AddRange(items);
        Box.AccessibleName = label;
        Controls.Add(text);
        Controls.Add(Box);
    }

    public ComboBox Box { get; }
}

/// <summary>A muted line of text that wraps (used for hints and notes).</summary>
sealed class NoteLabel : Label
{
    public NoteLabel(string text = "")
    {
        Text = text;
        Font = UiFonts.Caption;
        ForeColor = Palette.Muted;
        AutoSize = false;
        UseMnemonic = false;
        Height = 34;
        BackColor = Palette.Surface;
    }

    /// <summary>Grows to fit its wrapped text at the current width.</summary>
    public void FitHeight()
    {
        var size = TextRenderer.MeasureText(Text, Font, new Size(Math.Max(10, Width), int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
        Height = Text.Length == 0 ? 0 : size.Height + 4;
    }
}
