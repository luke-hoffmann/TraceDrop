using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace TracedropApp;

static class Palette
{
    public static readonly Color Background = Color.FromArgb(246, 247, 244);
    public static readonly Color Surface = Color.White;
    public static readonly Color Ink = Color.FromArgb(25, 28, 24);
    public static readonly Color Muted = Color.FromArgb(102, 110, 100);
    public static readonly Color Green = Color.FromArgb(30, 106, 61);
    public static readonly Color GreenPale = Color.FromArgb(224, 244, 231);
    public static readonly Color Red = Color.FromArgb(168, 57, 47);

    public static readonly Color Border = Color.FromArgb(222, 227, 219);
    public static readonly Color ControlBorder = Color.FromArgb(205, 212, 202);
    public static readonly Color Hover = Color.FromArgb(242, 245, 240);
    public static readonly Color SelectionIdle = Color.FromArgb(214, 228, 219);
    public static readonly Color DisabledFill = Color.FromArgb(232, 235, 230);
    public static readonly Color DisabledText = Color.FromArgb(152, 160, 150);
    public static readonly Color Placeholder = Color.FromArgb(240, 242, 238);
}

/// <summary>Shared fonts. Static on purpose: they live as long as the app, and controls never dispose a font they were handed.</summary>
static class UiFonts
{
    public static readonly Font Body = new("Segoe UI", 10f);
    public static readonly Font BodyStrong = new("Segoe UI Semibold", 10f);
    public static readonly Font Small = new("Segoe UI", 9f);
    public static readonly Font Caption = new("Segoe UI", 8.5f);
    public static readonly Font CaptionStrong = new("Segoe UI Semibold", 8f);
    public static readonly Font Tile = new("Segoe UI Semibold", 7f);
    public static readonly Font Heading = new("Segoe UI Semibold", 12.5f);
    public static readonly Font Display = new("Segoe UI Semibold", 20f);
    public static readonly Font Button = new("Segoe UI Semibold", 10f);
}

static class UiKit
{
    /// <summary>Converts a 96-dpi design value to real pixels. Only for custom painting and runtime sizes; the form's AutoScaleMode already scales designed control sizes.</summary>
    public static int Px(this Control control, int logical) => (int)Math.Round(logical * control.DeviceDpi / 96.0);

    public static GraphicsPath RoundedRect(RectangleF bounds, float radius)
    {
        var path = new GraphicsPath();
        var d = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
        if (d <= 0)
        {
            path.AddRectangle(bounds);
            return path;
        }
        path.AddArc(bounds.Left, bounds.Top, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Top, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>The colour behind a control (first opaque ancestor), so anti-aliased corners blend into it.</summary>
    public static Color BackdropOf(Control control)
    {
        for (var parent = control.Parent; parent is not null; parent = parent.Parent)
        {
            if (parent.BackColor.A == 255) return parent.BackColor;
        }
        return Palette.Background;
    }
}

static class Native
{
    const uint FLASHW_TRAY = 0x2;
    const uint FLASHW_TIMERNOFG = 0xC;
    const int LVM_GETEDITCONTROL = 0x1018;
    const int EM_SETSEL = 0xB1;

    [StructLayout(LayoutKind.Sequential)]
    struct FLASHWINFO
    {
        public uint cbSize;
        public IntPtr hwnd;
        public uint dwFlags;
        public uint uCount;
        public uint dwTimeout;
    }

    [DllImport("user32.dll")]
    static extern bool FlashWindowEx(ref FLASHWINFO info);

    [DllImport("user32.dll")]
    static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    static extern int SetWindowTheme(IntPtr hWnd, string subAppName, string? subIdList);

    /// <summary>Highlights the taskbar button until the window comes to the foreground.</summary>
    public static void FlashTaskbar(Form form)
    {
        if (!form.IsHandleCreated) return;
        var info = new FLASHWINFO
        {
            cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(),
            hwnd = form.Handle,
            dwFlags = FLASHW_TRAY | FLASHW_TIMERNOFG,
            uCount = uint.MaxValue,
            dwTimeout = 0
        };
        FlashWindowEx(ref info);
    }

    /// <summary>Modern scroll bars for a list view.</summary>
    public static void UseExplorerTheme(IntPtr handle)
    {
        try { SetWindowTheme(handle, "Explorer", null); } catch { /* cosmetic only */ }
    }

    /// <summary>Selects the first <paramref name="length"/> characters of the list view's in-place label editor, if one is open.</summary>
    public static void SelectInLabelEditor(IntPtr listViewHandle, int length)
    {
        var edit = SendMessage(listViewHandle, LVM_GETEDITCONTROL, IntPtr.Zero, IntPtr.Zero);
        if (edit != IntPtr.Zero) SendMessage(edit, EM_SETSEL, IntPtr.Zero, (IntPtr)length);
    }
}

enum ButtonKind { Primary, Secondary, Plain }

/// <summary>
/// Owner-painted button: an anti-aliased rounded shape filled over the parent's colour, with hover, pressed,
/// disabled and keyboard-focus states. The shape is inset from the control edge to leave room for the focus ring,
/// so a 46px control paints a 40px button.
/// </summary>
sealed class RoundedButton : Button
{
    const int Radius = 10;
    const int Inset = 3;

    ButtonKind _kind;
    bool _hover;
    bool _mouseDown;
    bool _spaceDown;

    public RoundedButton(string text, ButtonKind kind, int width = 120)
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        _kind = kind;
        Text = text;
        AccessibleName = text;
        Font = UiFonts.Button;
        Cursor = Cursors.Hand;
        FlatStyle = FlatStyle.Flat;
        UseVisualStyleBackColor = false;
        Margin = Padding.Empty;
        Size = new Size(width, 46);
    }

    public ButtonKind Kind
    {
        get => _kind;
        set { _kind = value; Invalidate(); }
    }

    (Color Fill, Color Text, Color Border) CurrentColors()
    {
        if (!Enabled)
        {
            return _kind == ButtonKind.Plain
                ? (Palette.Surface, Palette.DisabledText, Palette.Border)
                : (Palette.DisabledFill, Palette.DisabledText, Color.Transparent);
        }

        var down = _spaceDown || (_mouseDown && _hover);
        return _kind switch
        {
            ButtonKind.Primary => (
                down ? Color.FromArgb(20, 80, 44) : _hover ? Color.FromArgb(38, 124, 72) : Palette.Green,
                Color.White,
                Color.Transparent),
            ButtonKind.Secondary => (
                down ? Color.FromArgb(184, 224, 199) : _hover ? Color.FromArgb(207, 236, 218) : Palette.GreenPale,
                Palette.Green,
                Color.Transparent),
            _ => (
                down ? Color.FromArgb(231, 235, 228) : _hover ? Palette.Hover : Palette.Surface,
                Palette.Ink,
                _hover || down ? Color.FromArgb(178, 188, 175) : Palette.ControlBorder)
        };
    }

    protected override void OnPaintBackground(PaintEventArgs pevent)
    {
        // OnPaint clears to the parent's colour itself.
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(UiKit.BackdropOf(this));

        var inset = this.Px(Inset);
        var shape = new RectangleF(inset + 0.5f, inset + 0.5f, Width - 2 * inset - 1f, Height - 2 * inset - 1f);
        if (shape.Width < 4 || shape.Height < 4) return;

        var (fill, textColor, border) = CurrentColors();
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var path = UiKit.RoundedRect(shape, this.Px(Radius)))
        using (var brush = new SolidBrush(fill))
        {
            g.FillPath(brush, path);
            if (border.A != 0)
            {
                using var pen = new Pen(border);
                g.DrawPath(pen, path);
            }
        }

        if (Enabled && Focused && ShowFocusCues)
        {
            var ringWidth = Math.Max(2f, this.Px(2));
            var ring = new RectangleF(ringWidth / 2, ringWidth / 2, Width - ringWidth, Height - ringWidth);
            using var ringPath = UiKit.RoundedRect(ring, this.Px(Radius + Inset - 1));
            using var ringPen = new Pen(Color.FromArgb(150, Palette.Green), ringWidth);
            g.DrawPath(ringPen, ringPath);
        }
        g.SmoothingMode = SmoothingMode.Default;

        var textBounds = Rectangle.Round(shape);
        if (Enabled && (_spaceDown || (_mouseDown && _hover))) textBounds.Offset(0, 1);
        TextRenderer.DrawText(g, Text, Font, textBounds, textColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
            TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnMouseDown(MouseEventArgs mevent)
    {
        if (mevent.Button == MouseButtons.Left) { _mouseDown = true; Invalidate(); }
        base.OnMouseDown(mevent);
    }

    protected override void OnMouseUp(MouseEventArgs mevent)
    {
        _mouseDown = false;
        Invalidate();
        base.OnMouseUp(mevent);
    }

    protected override void OnKeyDown(KeyEventArgs kevent)
    {
        if (kevent.KeyCode == Keys.Space) { _spaceDown = true; Invalidate(); }
        base.OnKeyDown(kevent);
    }

    protected override void OnKeyUp(KeyEventArgs kevent)
    {
        if (kevent.KeyCode == Keys.Space) { _spaceDown = false; Invalidate(); }
        base.OnKeyUp(kevent);
    }

    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { _spaceDown = false; Invalidate(); base.OnLostFocus(e); }
    protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }

    protected override void OnEnabledChanged(EventArgs e)
    {
        _hover = _mouseDown = _spaceDown = false;
        Invalidate();
        base.OnEnabledChanged(e);
    }
}

/// <summary>White rounded surface with a hairline border. Children sit inside its Padding.</summary>
sealed class CardPanel : Panel
{
    const int Radius = 14;

    public CardPanel()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Palette.Surface;
    }

    protected override void OnPaintBackground(PaintEventArgs e) => e.Graphics.Clear(UiKit.BackdropOf(this));

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = UiKit.RoundedRect(new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f), this.Px(Radius));
        using var brush = new SolidBrush(BackColor);
        using var pen = new Pen(Palette.Border);
        g.FillPath(brush, path);
        g.DrawPath(pen, path);
        base.OnPaint(e);
    }
}
