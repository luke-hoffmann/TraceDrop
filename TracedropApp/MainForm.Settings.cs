using Tracedrop.Core;
using Tracedrop.Core.Export;
using Tracedrop.Core.Portable;

namespace TracedropApp;

// The settings card: builds the controls, keeps them in sync with the selected picture's settings, presets.
sealed partial class MainForm
{
    readonly StackPanel _settingsStack = new();
    readonly ComboBox _presetBox = new() { DropDownStyle = ComboBoxStyle.DropDownList, Font = UiFonts.Body, FlatStyle = FlatStyle.Flat };
    readonly RoundedButton _presetMenuButton = new("···", ButtonKind.Plain, 52);
    readonly ContextMenuStrip _presetMenu = new() { Font = UiFonts.Body, ShowImageMargin = false };
    readonly NoteLabel _presetNote = new();

    readonly Segmented _mode = new("Find", "Outline", "Shapes", "Edges", "Line art");
    readonly NoteLabel _modeNote = new();
    readonly SliderRow _detail = new("Detail", 0, 100, 50) { Hint = "key outlines → every detail" };
    readonly SliderRow _threshold = new("Sensitivity", 0, 100, 50);
    readonly ToggleRow _autoThreshold = new("Automatic cut level", true);
    readonly SliderRow _colors = new("Colours", 2, 8, 2, 1, v => v <= 2 ? "Black & white" : $"{v:0}");
    readonly SliderRow _smoothness = new("Smoothness", 0, 100, 50) { Hint = "follow every bump → round" };

    readonly SectionHeader _prepareHeader = new("Prepare picture");
    readonly SliderRow _blur = new("Blur", 0, 20, 0, 0.5, v => v == 0 ? "Off" : $"{v:0.#} px");
    readonly SliderRow _smoothTexture = new("Smooth texture", 0, 100, 0, 1, v => v == 0 ? "Off" : $"{v:0}");
    readonly SliderRow _despeckle = new("Remove specks", 0, 10, 0, 1, v => v == 0 ? "Off" : $"{v:0}");
    readonly SliderRow _contrast = new("Contrast", -100, 100, 0);
    readonly SliderRow _localContrast = new("Even out lighting", 0, 100, 0, 1, v => v == 0 ? "Off" : $"{v:0}");
    readonly SliderRow _brightness = new("Brightness", -100, 100, 0);
    readonly ToggleRow _invert = new("Invert (light on dark)");
    readonly ToggleRow _isolate = new("Isolate subject", false, "AI removes the background first");

    readonly SectionHeader _cleanHeader = new("Clean up");
    readonly SliderRow _closeGaps = new("Close gaps", 0, 40, 0, 1, v => v == 0 ? "Off" : $"{v:0} px");
    readonly ToggleRow _fillHoles = new("Fill holes", false, "Keep outer outlines only");
    readonly ToggleRow _keepLargest = new("Largest shape only");
    readonly ToggleRow _aiEdges = new("AI edges (TEED)", true, "Off = classic Canny edges");

    readonly SectionHeader _outputHeader = new("Output");
    readonly Segmented _curves = new("Curves", "Lines & arcs", "Splines", "Polylines");
    readonly SliderRow _corners = new("Corners", 0, 100, 50) { Hint = "round → sharp" };
    readonly ToggleRow _snapCircles = new("Snap round shapes to circles", true);
    readonly Segmented _strokes = new("Lines as", "Centerline", "Outline");
    readonly SliderRow _strokeWidth = new("Line width", 0, 40, 0, 0.5, v => v == 0 ? "As found" : $"{v:0.#} px");
    readonly NumberRow _size = new("Size (longest side)", 0.1m, 100000m, 100m, 2, "mm");
    readonly NumberRow _offset = new("Offset (+ grow, − shrink)", -50m, 50m, 0m, 2, "mm");
    readonly Segmented _units = new("Units", "Millimetres", "Inches");
    readonly DropdownRow _resolution = new("Resolution", "Fast (1024)", "Balanced (1600)", "Fine (2400)", "Max (3600)");
    readonly ToggleRow _formatDxf = new("DXF", true, "CAD: SolidWorks, Fusion, AutoCAD");
    readonly ToggleRow _formatSvg = new("SVG", false, "Laser, Inkscape, Illustrator, web");
    readonly ToggleRow _formatPdf = new("PDF", false, "Print, Illustrator, CorelDRAW");

    readonly RoundedButton _applyAllButton = new("Apply to all pictures", ButtonKind.Secondary, 200);

    static readonly int[] Resolutions = { 1024, 1600, 2400, 3600 };
    static readonly string[] ModeNotes =
    {
        "The subject's outline and its holes, background removed. Objects, tools, parts, leaves.",
        "Areas split by brightness or colour. Logos, prints, stencils, dark-on-light drawings.",
        "Every visible edge as lines. Turn Detail down for just the main ones.",
        "A clean pen drawing of the picture. Faces, portraits, sketches."
    };

    TraceSettings _template = new();
    bool _loading;
    string? _activePreset;
    bool _presetEdited;

    Control BuildSettingsCard()
    {
        var card = new CardPanel { Dock = DockStyle.Left, Width = 340, Padding = new Padding(6, 10, 2, 10) };
        _settingsStack.Dock = DockStyle.Fill;
        _settingsStack.Padding = new Padding(12, 2, 14, 12);
        card.Controls.Add(_settingsStack);

        var presetRow = new Panel { Height = 40, BackColor = Palette.Surface, Margin = new Padding(0, 0, 0, 0) };
        _presetBox.Dock = DockStyle.Fill;
        _presetBox.AccessibleName = "Preset";
        _presetBox.SelectedIndexChanged += PresetBox_SelectedIndexChanged;
        _presetMenuButton.Dock = DockStyle.Right;
        _presetMenuButton.AccessibleName = "Preset options";
        _presetMenuButton.Click += (_, _) => ShowPresetMenu();
        var presetGap = new Panel { Dock = DockStyle.Right, Width = 6 };
        presetRow.Controls.Add(_presetBox);
        presetRow.Controls.Add(presetGap);
        presetRow.Controls.Add(_presetMenuButton);
        var presetTitle = new Label { Text = "PRESET", Font = UiFonts.CaptionStrong, ForeColor = Palette.Muted, Height = 22, TextAlign = ContentAlignment.BottomLeft, BackColor = Palette.Surface, UseMnemonic = false };

        _mode.Margin = new Padding(0, 8, 0, 0);
        _applyAllButton.Margin = new Padding(0, 14, 0, 0);
        _applyAllButton.Click += (_, _) => ApplyToAll();

        var main = new Control[] { presetTitle, presetRow, _presetNote, _mode, _modeNote, _detail, _threshold, _autoThreshold, _colors, _smoothness };
        var prepare = new Control[] { _blur, _smoothTexture, _despeckle, _contrast, _localContrast, _brightness, _invert, _isolate };
        var clean = new Control[] { _closeGaps, _fillHoles, _keepLargest, _aiEdges };
        var output = new Control[] { _curves, _corners, _snapCircles, _strokes, _strokeWidth, _size, _offset, _units, _resolution, _formatDxf, _formatSvg, _formatPdf };

        _settingsStack.SuspendLayout();
        foreach (var c in main) _settingsStack.Controls.Add(c);
        AddSection(_prepareHeader, prepare);
        AddSection(_cleanHeader, clean);
        AddSection(_outputHeader, output);
        _settingsStack.Controls.Add(_applyAllButton);
        _settingsStack.ResumeLayout(true);

        foreach (var header in new[] { _prepareHeader, _cleanHeader, _outputHeader })
        {
            header.Expanded = !_config.CollapsedSections.Contains(header.Text);
            header.IsRelevant = IsRelevant;
            header.ExpandedChanged += (_, _) =>
            {
                _config.CollapsedSections.Remove(header.Text);
                if (!header.Expanded) _config.CollapsedSections.Add(header.Text);
                SaveConfigSoon();
            };
        }

        // Every control feeds the same handler; it rebuilds settings from the UI.
        foreach (var slider in new[] { _detail, _threshold, _colors, _smoothness, _blur, _smoothTexture, _despeckle, _contrast, _localContrast, _brightness, _closeGaps, _corners, _strokeWidth })
            slider.ValueChanged += (_, _) => OnUiChanged();
        foreach (var toggle in new[] { _autoThreshold, _invert, _isolate, _fillHoles, _keepLargest, _aiEdges, _snapCircles })
            toggle.CheckedChanged += (_, _) => OnUiChanged();
        foreach (var seg in new[] { _mode, _curves, _strokes, _units })
            seg.SelectedIndexChanged += (_, _) => OnUiChanged();
        _size.Box.ValueChanged += (_, _) => OnUiChanged();
        _offset.Box.ValueChanged += (_, _) => OnUiChanged();
        _resolution.Box.SelectedIndexChanged += (_, _) => OnUiChanged();
        foreach (var format in new[] { _formatDxf, _formatSvg, _formatPdf })
            format.CheckedChanged += (_, _) => OnFormatsChanged(format);

        _presetMenu.Items.Add("Save as new preset…", null, (_, _) => SaveAsPreset());
        _presetMenu.Items.Add("Update this preset", null, (_, _) => UpdatePreset());
        _presetMenu.Items.Add("Rename preset…", null, (_, _) => RenamePreset());
        _presetMenu.Items.Add("Delete preset", null, (_, _) => DeletePreset());
        _presetMenu.Items.Add(new ToolStripSeparator());
        _presetMenu.Items.Add("Reset to preset", null, (_, _) => ResetToPreset());

        _settingsStack.Resize += (_, _) => FitNotes();
        RefreshPresetBox();
        return card;
    }

    void AddSection(SectionHeader header, Control[] content)
    {
        _settingsStack.Controls.Add(header);
        foreach (var c in content)
        {
            header.Content.Add(c);
            _settingsStack.Controls.Add(c);
        }
    }

    /// <summary>Mode-specific rows are hidden when they don't apply.</summary>
    bool IsRelevant(Control c)
    {
        var mode = (TraceMode)_mode.SelectedIndex;
        var strokes = mode is TraceMode.Edges or TraceMode.LineArt;
        if (c == _autoThreshold || c == _colors) return mode == TraceMode.Shapes;
        if (c == _isolate) return mode != TraceMode.Silhouette;
        if (c == _aiEdges) return mode == TraceMode.Edges;
        if (c == _fillHoles) return !strokes;
        if (c == _strokes) return strokes;
        if (c == _strokeWidth) return strokes && _strokes.SelectedIndex == 1;
        if (c == _offset) return !strokes;
        if (c == _snapCircles) return true;
        return true;
    }

    void UpdateRelevance()
    {
        _settingsStack.SuspendLayout();
        var mode = (TraceMode)_mode.SelectedIndex;
        _autoThreshold.Visible = mode == TraceMode.Shapes;
        _colors.Visible = mode == TraceMode.Shapes;
        foreach (var header in new[] { _prepareHeader, _cleanHeader, _outputHeader }) header.ApplyVisibility();
        _threshold.Label = mode switch
        {
            TraceMode.Silhouette => "Tightness",
            TraceMode.Shapes => "Cut level",
            _ => "Sensitivity"
        };
        _threshold.Hint = mode switch
        {
            TraceMode.Silhouette => "looser → tighter outline",
            TraceMode.Shapes => "dark only → include lighter",
            _ => "strong lines → faint lines too"
        };
        _threshold.Enabled = !(mode == TraceMode.Shapes && _autoThreshold.Checked);
        _threshold.Invalidate();
        _modeNote.Text = ModeNotes[(int)mode];
        var units = _units.SelectedIndex == 1 ? "in" : "mm";
        _size.Unit = units;
        _offset.Unit = units;
        _settingsStack.ResumeLayout(true);
        FitNotes();
    }

    void FitNotes()
    {
        foreach (var note in new[] { _modeNote, _presetNote })
        {
            note.Width = _settingsStack.ClientSize.Width - _settingsStack.Padding.Horizontal;
            note.FitHeight();
        }
        _settingsStack.PerformLayout();
    }

    /// <summary>Puts settings into the controls without triggering changes.</summary>
    void LoadSettingsIntoUi(TraceSettings s, string? presetName, bool edited = false)
    {
        _loading = true;
        try
        {
            _template = s.Clone();
            _template.Picks = new();
            _mode.SelectedIndex = (int)s.Mode;
            _detail.Value = s.Detail;
            _threshold.Value = s.Threshold;
            _autoThreshold.Checked = s.AutoThreshold;
            _colors.Value = s.Colors;
            _smoothness.Value = s.Smoothness;
            _blur.Value = s.Blur;
            _smoothTexture.Value = s.SmoothTexture;
            _despeckle.Value = s.Despeckle;
            _contrast.Value = s.Contrast;
            _localContrast.Value = s.LocalContrast;
            _brightness.Value = s.Brightness;
            _invert.Checked = s.Invert;
            _isolate.Checked = s.IsolateSubject;
            _closeGaps.Value = s.CloseGaps;
            _fillHoles.Checked = s.FillHoles;
            _keepLargest.Checked = s.Keep == KeepShapes.Largest;
            _aiEdges.Checked = s.UseAiEdges;
            _curves.SelectedIndex = (int)s.Curves;
            _corners.Value = s.CornerSharpness;
            _snapCircles.Checked = s.SnapCircles;
            _strokes.SelectedIndex = (int)s.Strokes;
            _strokeWidth.Value = s.StrokeWidth;
            _size.Box.Value = (decimal)Math.Clamp(s.OutputSize, 0.1, 100000);
            _offset.Box.Value = (decimal)Math.Clamp(s.Offset, -50, 50);
            _units.SelectedIndex = (int)s.Units;
            var res = Array.FindIndex(Resolutions, r => r >= s.WorkingSize);
            _resolution.Box.SelectedIndex = res < 0 ? Resolutions.Length - 1 : res;
            _formatDxf.Checked = _config.Formats.Contains(ExportFormat.Dxf);
            _formatSvg.Checked = _config.Formats.Contains(ExportFormat.Svg);
            _formatPdf.Checked = _config.Formats.Contains(ExportFormat.Pdf);

            _activePreset = presetName;
            _presetEdited = edited;
            RefreshPresetBox();
            UpdateRelevance();
        }
        finally
        {
            _loading = false;
        }
        UpdateDragChip();
    }

    /// <summary>Settings as shown in the controls (picks are per picture and not part of this).</summary>
    TraceSettings SettingsFromUi()
    {
        var s = _template.Clone();
        s.Mode = (TraceMode)_mode.SelectedIndex;
        s.Detail = _detail.Value;
        s.Threshold = _threshold.Value;
        s.AutoThreshold = _autoThreshold.Checked;
        s.Colors = (int)_colors.Value;
        s.Smoothness = _smoothness.Value;
        s.Blur = _blur.Value;
        s.SmoothTexture = _smoothTexture.Value;
        s.Despeckle = (int)_despeckle.Value;
        s.Contrast = _contrast.Value;
        s.LocalContrast = _localContrast.Value;
        s.Brightness = _brightness.Value;
        s.Invert = _invert.Checked;
        s.IsolateSubject = _isolate.Checked;
        s.CloseGaps = _closeGaps.Value;
        s.FillHoles = _fillHoles.Checked;
        s.Keep = _keepLargest.Checked ? KeepShapes.Largest : KeepShapes.All;
        s.UseAiEdges = _aiEdges.Checked;
        s.Curves = (CurveStyle)_curves.SelectedIndex;
        s.CornerSharpness = _corners.Value;
        s.SnapCircles = _snapCircles.Checked;
        s.Strokes = (StrokeStyle)_strokes.SelectedIndex;
        s.StrokeWidth = _strokeWidth.Value;
        s.OutputSize = (double)_size.Box.Value;
        s.Offset = (double)_offset.Box.Value;
        s.Units = (OutputUnits)_units.SelectedIndex;
        s.WorkingSize = Resolutions[Math.Max(0, _resolution.Box.SelectedIndex)];
        s.Picks = new();
        return s.Normalized();
    }

    void OnUiChanged()
    {
        if (_loading) return;
        var previousUnits = _template.Units;
        var s = SettingsFromUi();
        if (s.Units != previousUnits)
        {
            // Keep the physical size when switching units.
            var factor = s.Units == OutputUnits.Inches ? 1 / 25.4 : 25.4;
            _loading = true;
            _size.Box.Value = (decimal)Math.Clamp(Math.Round(s.OutputSize * factor, 3), 0.1, 100000);
            _offset.Box.Value = (decimal)Math.Clamp(Math.Round(s.Offset * factor, 3), -50, 50);
            _loading = false;
            s = SettingsFromUi();
        }
        _template = s;
        UpdateRelevance();
        if (!_presetEdited && _activePreset is not null)
        {
            _presetEdited = true;
            RefreshPresetBox();
        }
        ApplyToSelected(s);
        CaptureConfigFromUi();
        SaveConfigSoon();
    }

    /// <summary>Gives the selected pictures these settings, each keeping its own picks.</summary>
    void ApplyToSelected(TraceSettings s)
    {
        foreach (var item in SelectedItems())
        {
            lock (item.Gate)
            {
                var picks = item.Settings.Picks;
                item.Settings = s.Clone();
                item.Settings.Picks = picks;
                item.PresetName = _activePreset;
                item.Version++;
            }
            _worker.Signal(item);
        }
    }

    void ApplyToAll()
    {
        var s = SettingsFromUi();
        foreach (var item in _items)
        {
            lock (item.Gate)
            {
                var picks = item.Settings.Picks;
                item.Settings = s.Clone();
                item.Settings.Picks = picks;
                item.PresetName = _activePreset;
                item.Version++;
            }
        }
        _worker.Signal();
        ShowStatus($"Applied to {_items.Count} picture{(_items.Count == 1 ? "" : "s")}.", Palette.Muted);
    }

    void OnFormatsChanged(ToggleRow changed)
    {
        if (_loading) return;
        var formats = new List<ExportFormat>();
        if (_formatDxf.Checked) formats.Add(ExportFormat.Dxf);
        if (_formatSvg.Checked) formats.Add(ExportFormat.Svg);
        if (_formatPdf.Checked) formats.Add(ExportFormat.Pdf);
        if (formats.Count == 0)
        {
            // At least one format: turn the one just switched off back on.
            _loading = true;
            changed.Checked = true;
            _loading = false;
            return;
        }
        _config.Formats = formats;
        SaveConfigSoon();
        UpdateDragChip();
        _worker.Signal();
    }

    void CaptureConfigFromUi()
    {
        _config.LastSettings = _template.Clone();
        _config.LastSettings.Picks = new();
        _config.LastPreset = _presetEdited ? null : _activePreset;
    }

    #region Presets

    void RefreshPresetBox()
    {
        var wasLoading = _loading;
        _loading = true;
        try
        {
            _presetBox.BeginUpdate();
            _presetBox.Items.Clear();
            foreach (var p in _config.AllPresets())
                _presetBox.Items.Add(p.Name == _activePreset && _presetEdited ? p.Name + " (edited)" : p.Name);
            if (_activePreset is null)
            {
                _presetBox.Items.Insert(0, "Custom");
                _presetBox.SelectedIndex = 0;
            }
            else
            {
                var index = _config.AllPresets().ToList().FindIndex(p => p.Name == _activePreset);
                if (index < 0)
                {
                    _presetBox.Items.Insert(0, "Custom");
                    _presetBox.SelectedIndex = 0;
                    _activePreset = null;
                }
                else _presetBox.SelectedIndex = index;
            }
            _presetBox.EndUpdate();
            var preset = _activePreset is null ? null : Presets.Find(_activePreset, _config.AllPresets().Where(p => !p.BuiltIn));
            _presetNote.Text = preset?.Description ?? "Your own settings. Save them as a preset from the ··· menu.";
            FitNotes();
        }
        finally
        {
            _loading = wasLoading;
        }
    }

    void PresetBox_SelectedIndexChanged(object? sender, EventArgs e)
    {
        if (_loading) return;
        var name = (_presetBox.SelectedItem as string)?.Replace(" (edited)", "");
        var preset = _config.AllPresets().FirstOrDefault(p => p.Name == name);
        if (preset is null) return;
        var s = preset.Settings.Clone();
        LoadSettingsIntoUi(s, preset.Name);
        ApplyToSelected(_template);
        CaptureConfigFromUi();
        SaveConfigSoon();
    }

    void ShowPresetMenu()
    {
        var isUser = _activePreset is not null && _config.UserPresets.Any(p => p.Name == _activePreset);
        _presetMenu.Items[1].Enabled = isUser && _presetEdited;
        _presetMenu.Items[1].Text = isUser ? $"Update “{_activePreset}”" : "Update this preset";
        _presetMenu.Items[2].Enabled = isUser;
        _presetMenu.Items[3].Enabled = isUser;
        _presetMenu.Items[5].Enabled = _activePreset is not null && _presetEdited;
        _presetMenu.Show(_presetMenuButton, new Point(0, _presetMenuButton.Height));
    }

    void SaveAsPreset()
    {
        var suggested = _activePreset is null ? "My preset" : _activePreset + " (mine)";
        var name = PromptDialog.Ask(this, "Save preset", "Name for these settings:", suggested);
        if (string.IsNullOrWhiteSpace(name)) return;
        name = name.Trim();
        if (Presets.BuiltIn.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show(this, "That name belongs to a built-in preset. Please pick another.", "Tracedrop", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        _config.UserPresets.RemoveAll(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        _config.UserPresets.Insert(0, new UserPreset { Name = name, Description = "Your preset", Settings = SettingsFromUi() });
        _activePreset = name;
        _presetEdited = false;
        RefreshPresetBox();
        CaptureConfigFromUi();
        SaveConfigSoon();
        ShowStatus($"Saved preset “{name}”. It travels with Tracedrop.exe.", Palette.Green);
    }

    void UpdatePreset()
    {
        var preset = _config.UserPresets.FirstOrDefault(p => p.Name == _activePreset);
        if (preset is null) return;
        preset.Settings = SettingsFromUi();
        _presetEdited = false;
        RefreshPresetBox();
        CaptureConfigFromUi();
        SaveConfigSoon();
        ShowStatus($"Updated preset “{preset.Name}”.", Palette.Green);
    }

    void RenamePreset()
    {
        var preset = _config.UserPresets.FirstOrDefault(p => p.Name == _activePreset);
        if (preset is null) return;
        var name = PromptDialog.Ask(this, "Rename preset", "New name:", preset.Name);
        if (string.IsNullOrWhiteSpace(name) || name.Trim() == preset.Name) return;
        name = name.Trim();
        if (_config.AllPresets().Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show(this, "A preset with that name already exists.", "Tracedrop", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        preset.Name = name;
        _activePreset = name;
        RefreshPresetBox();
        CaptureConfigFromUi();
        SaveConfigSoon();
    }

    void DeletePreset()
    {
        var preset = _config.UserPresets.FirstOrDefault(p => p.Name == _activePreset);
        if (preset is null) return;
        if (MessageBox.Show(this, $"Delete the preset “{preset.Name}”?", "Tracedrop", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        _config.UserPresets.Remove(preset);
        _activePreset = null;
        _presetEdited = false;
        RefreshPresetBox();
        CaptureConfigFromUi();
        SaveConfigSoon();
    }

    void ResetToPreset()
    {
        var preset = _config.AllPresets().FirstOrDefault(p => p.Name == _activePreset);
        if (preset is null) return;
        LoadSettingsIntoUi(preset.Settings.Clone(), preset.Name);
        ApplyToSelected(_template);
        CaptureConfigFromUi();
        SaveConfigSoon();
    }

    #endregion
}
