namespace TracedropApp;

/// <summary>A small "type a name" dialog in the app's style.</summary>
sealed class PromptDialog : Form
{
    readonly TextBox _box = new() { Font = UiFonts.Body, BorderStyle = BorderStyle.FixedSingle };

    PromptDialog(string title, string message, string value)
    {
        Text = title;
        Font = UiFonts.Body;
        BackColor = Palette.Background;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(380, 158);
        Padding = new Padding(18);

        var label = new Label { Text = message, Font = UiFonts.Body, ForeColor = Palette.Ink, Dock = DockStyle.Top, Height = 28, UseMnemonic = false };
        _box.Text = value;
        _box.Dock = DockStyle.Top;
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 48, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
        var ok = new RoundedButton("Save", ButtonKind.Primary, 110) { DialogResult = DialogResult.OK, Margin = new Padding(8, 0, 0, 0) };
        var cancel = new RoundedButton("Cancel", ButtonKind.Plain, 100) { DialogResult = DialogResult.Cancel };
        buttons.Controls.Add(ok);
        buttons.Controls.Add(cancel);
        Controls.Add(_box);
        Controls.Add(label);
        Controls.Add(buttons);
        AcceptButton = ok;
        CancelButton = cancel;
        Shown += (_, _) => { _box.Focus(); _box.SelectAll(); };
    }

    public static string? Ask(IWin32Window owner, string title, string message, string value)
    {
        using var dialog = new PromptDialog(title, message, value);
        return dialog.ShowDialog(owner) == DialogResult.OK ? dialog._box.Text : null;
    }
}
