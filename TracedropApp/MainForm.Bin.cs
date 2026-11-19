using Tracedrop.Core;
using Tracedrop.Core.Editing;
using Tracedrop.Core.Gridfinity;
using Tracedrop.Core.Paper;
using Tracedrop.Core.Vector;

namespace TracedropApp;

/// <summary>The views of the middle pane, in the order of the view switch.</summary>
enum PaneView
{
    Bin,
    ThreeD,
    Trace,
    Lines,
    Photo
}

// The Gridfinity side of the window: the bin, 3D and photo views, their options, and edits to layout and sheet corners.
sealed partial class MainForm
{
    readonly BinView _binView = new();
    readonly Bin3DView _view3d = new();
    readonly PaperView _paperView = new();

    readonly FlowLayoutPanel _binOptions = new() { Dock = DockStyle.Left, AutoSize = true, WrapContents = false, Padding = new Padding(0, 3, 0, 0) };
    readonly Segmented _binTool = new(null, "Move", "Finger slot", "Label");
    readonly RoundedButton _rotateButton = new("Turn 90°", ButtonKind.Secondary, 92);
    readonly RoundedButton _arrangeButton = new("Arrange", ButtonKind.Secondary, 90);
    readonly RoundedButton _putBackButton = new("Put back all", ButtonKind.Secondary, 116);
    readonly Label _depthLabel = NewLabel("Depth", UiFonts.Small, Palette.Muted);
    readonly NumericUpDown _toolDepth = new() { Minimum = 1, Maximum = 150, DecimalPlaces = 1, Increment = 1, Width = 64, Font = UiFonts.Body };
    bool _loadingDepth;

    readonly FlowLayoutPanel _photoOptions = new() { Dock = DockStyle.Left, AutoSize = true, WrapContents = false, Padding = new Padding(0, 3, 0, 0) };
    readonly RoundedButton _resetCornersButton = new("Use found corners", ButtonKind.Secondary, 160);

    PaneView CurrentView => (PaneView)_viewMode.SelectedIndex;

    void BuildBinViews(Control card)
    {
        foreach (var view in new Control[] { _binView, _view3d, _paperView })
        {
            view.Dock = DockStyle.Fill;
            view.Visible = false;
            view.AllowDrop = true;
            view.DragEnter += Window_DragEnter;
            view.DragDrop += Window_DragDrop;
            card.Controls.Add(view);
            view.BringToFront();
        }
        _binView.LayoutChanged += (layout, tag) => CommitEdits(e => e.WithLayout(layout), tag);
        _binView.SelectionChanged += UpdateEditorBar;
        _binView.LabelRequested += at =>
        {
            var text = PromptDialog.Ask(this, "Label", "Text to engrave on the bin:", "");
            if (!string.IsNullOrWhiteSpace(text)) _binView.AddLabel(text.Trim(), at);
        };
        _binView.LabelEditRequested += (index, current) =>
        {
            var text = PromptDialog.Ask(this, "Label", "Text to engrave on the bin:", current);
            if (!string.IsNullOrWhiteSpace(text)) _binView.SetLabelText(index, text.Trim());
        };
        _paperView.CornersChanged += corners => CommitEdits(e => e.WithPaperCorners(corners));

        _binTool.Width = 250;
        _binTool.Margin = new Padding(0, 0, 8, 0);
        _binTool.SelectedIndexChanged += (_, _) => { _binView.Tool = (BinTool)_binTool.SelectedIndex; _binView.Focus(); UpdateEditorBar(); };
        _binOptions.Controls.Add(_binTool);
        foreach (var b in new[] { _rotateButton, _arrangeButton, _putBackButton })
        {
            b.Height = 36;
            b.Margin = new Padding(0, 0, 6, 0);
            _binOptions.Controls.Add(b);
        }
        _depthLabel.Margin = new Padding(6, 9, 4, 0);
        _toolDepth.Margin = new Padding(0, 5, 0, 0);
        _binOptions.Controls.Add(_depthLabel);
        _binOptions.Controls.Add(_toolDepth);
        _rotateButton.Click += (_, _) => { _binView.RotateSelected(90); _binView.Focus(); };
        _arrangeButton.Click += (_, _) => CommitEdits(e => e.WithLayout(new BinLayout { Labels = e.Layout.Labels.ToList() }));
        _putBackButton.Click += (_, _) => CommitEdits(e =>
        {
            var layout = e.Layout.Clone();
            layout.Tools.RemoveAll(t => t.Removed);
            return e.WithLayout(layout);
        });
        _toolDepth.ValueChanged += (_, _) => { if (!_loadingDepth) _binView.SetSelectedDepth((double)_toolDepth.Value); };

        _resetCornersButton.Height = 36;
        _resetCornersButton.Click += (_, _) => CommitEdits(e => e.WithPaperCorners(new List<NormPoint>()));
        _photoOptions.Controls.Add(_resetCornersButton);

        _viewMode.SelectedIndex = Math.Clamp(_config.View, 0, 4);
    }

    /// <summary>Shows the view picked in the view switch.</summary>
    void ShowView()
    {
        var view = CurrentView;
        _preview.Mode = view == PaneView.Lines ? PreviewMode.Lines : PreviewMode.Result;
        var trace = view is PaneView.Trace or PaneView.Lines;
        _preview.Visible = trace;
        _palette.Visible = trace && _items.Count > 0;
        _binView.Visible = view == PaneView.Bin;
        _view3d.Visible = view == PaneView.ThreeD;
        _paperView.Visible = view == PaneView.Photo;
        if (_config.View != (int)view)
        {
            _config.View = (int)view;
            SaveConfigSoon();
        }
        UpdateEditorBar();
    }

    /// <summary>Puts the shown picture's bin, mesh and photo into the views.</summary>
    void ShowBin(TraceItem item)
    {
        _binView.SetDesign(item.Design);
        _view3d.SetMesh(item.Mesh);
        _paperView.SetPhoto(item.Source);
        TraceSettings settings;
        lock (item.Gate) settings = item.Settings.Clone();
        _binView.EmptyText = settings.Bin.Enabled
            ? item.Result is null ? "Making the bin…" : "No tools found: check the Trace view."
            : "Turn on “Make a Gridfinity bin” in the settings to design a bin from the traced tools.";
        IReadOnlyList<NormPoint>? corners = settings.PaperCorners.Count == 4 ? settings.PaperCorners : item.Result?.PaperCorners;
        string caption;
        if (!settings.UsePaper) caption = "Sheet of paper not used: turn on “Size from a sheet of paper”";
        else if (corners is null)
        {
            caption = "No sheet found: drag the corners onto the sheet's corners";
            corners = new[] { new NormPoint(0.25, 0.2), new NormPoint(0.75, 0.2), new NormPoint(0.75, 0.8), new NormPoint(0.25, 0.8) };
        }
        else
        {
            var size = item.Result is { } r && r.MillimetresPerPixel > 0 ? Papers.Name(r.PaperSize) : Papers.Name(settings.Paper);
            caption = settings.PaperCorners.Count == 4 ? $"{size} · corners set by hand" : $"{size} · found automatically";
        }
        _paperView.Caption = caption;
        _paperView.SetCorners(settings.UsePaper ? corners : null);
    }

    /// <summary>Changes the shown picture's edits (one undo step) and re-traces.</summary>
    void CommitEdits(Func<EditState, EditState> change, object? tag = null)
    {
        if (_shown is not { } item) return;
        var before = CurrentEdits(item);
        var after = change(before);
        if (after.Key() == before.Key()) return;
        item.History.Record(before, tag);
        SetItemEdits(item, after);
    }

    void UpdateBinOptions()
    {
        var view = CurrentView;
        _binOptions.Visible = view == PaneView.Bin;
        _photoOptions.Visible = view == PaneView.Photo;
        var selected = _binView.SelectedTool;
        _rotateButton.Enabled = selected is not null;
        _depthLabel.Visible = _toolDepth.Visible = selected is not null;
        if (selected is not null)
        {
            _loadingDepth = true;
            _toolDepth.Value = (decimal)Math.Clamp(selected.Depth, 1, 150);
            _loadingDepth = false;
        }
        var design = _binView.Design;
        _putBackButton.Visible = design?.LeftOut.Count > 0;
        _putBackButton.Text = design?.LeftOut.Count > 0 ? $"Put back {design.LeftOut.Count}" : "Put back all";
        _arrangeButton.AccessibleName = "Arrange the tools automatically";
        _arrangeButton.Enabled = design is not null;
        bool manual;
        if (_shown is not { } item) manual = false;
        else lock (item.Gate) manual = item.Settings.PaperCorners.Count == 4;
        _resetCornersButton.Enabled = manual;
    }

    static string ViewHint(PaneView view, BinTool binTool) => view switch
    {
        PaneView.Bin => binTool switch
        {
            BinTool.FingerSlot => "Finger slot:  drag across the tools to cut a bar for your fingers (Shift: straight), or click a tool for one across it. Scroll over a slot to size it.",
            BinTool.Label => "Label:  click where the text should go. Double-click a label to change it; scroll over it to size it.",
            _ => "Move:  drag tools, slots and labels (a slot's ends resize it). Scroll over a tool to turn it (Shift: 1°), R turns 90°, Delete removes."
        },
        PaneView.ThreeD => "3D:  drag to turn the bin, scroll to zoom, double-click to reset. This is exactly what the STL / 3MF contains.",
        PaneView.Photo => "Photo:  the sheet of paper sets the true size. If its outline is off, drag the corners onto the sheet's corners.",
        _ => ""
    };
}
