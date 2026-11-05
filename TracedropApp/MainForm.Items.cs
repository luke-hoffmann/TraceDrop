using System.Collections.Specialized;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Tracedrop.Core;
using Tracedrop.Core.Export;
using Tracedrop.Core.Imaging;

namespace TracedropApp;

// The pictures pane: list, adding (button, drop, paste, command line), drag-out, save, remove, worker updates.
sealed partial class MainForm
{
    readonly ItemListView _list = new();
    readonly RoundedButton _addButton = new("Add pictures", ButtonKind.Primary, 140);
    readonly RoundedButton _saveSelectedButton = new("Save…", ButtonKind.Secondary, 100);
    readonly RoundedButton _removeButton = new("Remove", ButtonKind.Plain, 86);
    readonly Label _resultLabel = NewLabel("", UiFonts.Small, Palette.Muted);
    readonly LinkLabel _revealLink = new();
    readonly ContextMenuStrip _itemMenu = new() { Font = UiFonts.Body, ShowImageMargin = false };
    readonly ImageList _rowSizer = new() { ColorDepth = ColorDepth.Depth32Bit };
    readonly Label _listHint = NewLabel("Drag a picture out to drop its file anywhere", UiFonts.Caption, Palette.Muted);
    string? _revealPath;
    string _lastSaveFolder = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    TraceItem? _shown;

    Control BuildItemsPane()
    {
        var pane = new Panel { Dock = DockStyle.Right, Width = 340 };

        var title = NewLabel("Pictures", UiFonts.Heading, Palette.Ink);
        title.Dock = DockStyle.Left;
        title.TextAlign = ContentAlignment.MiddleLeft;
        var headingRow = new Panel { Dock = DockStyle.Top, Height = 40 };
        headingRow.Controls.Add(title);

        _rowSizer.ImageSize = new Size(1, this.Px(60));
        _list.View = View.Details;
        _list.FullRowSelect = true;
        _list.MultiSelect = true;
        _list.HideSelection = false;
        _list.BorderStyle = BorderStyle.None;
        _list.BackColor = Palette.Surface;
        _list.HeaderStyle = ColumnHeaderStyle.None;
        _list.OwnerDraw = true;
        _list.SmallImageList = _rowSizer;
        _list.Columns.Add("Picture", 300);
        _list.AccessibleName = "Pictures";
        _list.Dock = DockStyle.Fill;
        _list.AllowDrop = true;
        _list.DrawItem += List_DrawItem;
        _list.DrawColumnHeader += (_, e) => e.DrawDefault = true;
        _list.ClientSizeChanged += (_, _) => _list.SetColumnWidths(Math.Max(100, _list.ClientSize.Width));
        _list.SelectedIndexChanged += (_, _) => OnSelectionChanged();
        _list.ItemDrag += List_ItemDrag;
        _list.KeyDown += List_KeyDown;
        _list.MouseDoubleClick += (_, e) => { if (_list.HitTest(e.Location).Item?.Tag is TraceItem item) OpenOutput(item); };
        _list.MouseDown += List_MouseDown;
        _list.DragEnter += Window_DragEnter;
        _list.DragDrop += Window_DragDrop;
        _list.ContextMenuStrip = _itemMenu;
        _itemMenu.Items.Add("Open", null, (_, _) => { foreach (var i in SelectedItems()) OpenOutput(i); });
        _itemMenu.Items.Add("Save…", null, async (_, _) => await SaveItemsAsync(SelectedItems()));
        _itemMenu.Items.Add(new ToolStripSeparator());
        _itemMenu.Items.Add("Remove", null, (_, _) => RemoveSelected());
        ((ToolStripMenuItem)_itemMenu.Items[0]).Font = UiFonts.BodyStrong;
        _itemMenu.Opening += (_, e) => { if (_list.SelectedItems.Count == 0) e.Cancel = true; };

        var listCard = new CardPanel { Dock = DockStyle.Fill, Padding = new Padding(6) };
        listCard.Controls.Add(_list);

        _listHint.AutoSize = false;
        _listHint.Dock = DockStyle.Bottom;
        _listHint.Height = 24;
        _listHint.TextAlign = ContentAlignment.MiddleLeft;

        var actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 50, WrapContents = false, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(0, 4, 0, 0) };
        foreach (var b in new[] { _addButton, _saveSelectedButton, _removeButton })
        {
            b.Margin = new Padding(0, 0, 6, 0);
            actions.Controls.Add(b);
        }
        _addButton.Click += (_, _) => BrowseForPictures();
        _saveSelectedButton.Click += async (_, _) => await SaveItemsAsync(SelectedItems().Any() ? SelectedItems() : _items.ToList());
        _removeButton.Click += (_, _) => RemoveSelected();

        _resultLabel.Margin = new Padding(0, 6, 0, 0);
        _resultLabel.MaximumSize = new Size(300, 0);
        _revealLink.Text = "Show in folder";
        _revealLink.LinkArea = new LinkArea(0, _revealLink.Text.Length);
        _revealLink.Font = UiFonts.Small;
        _revealLink.AutoSize = true;
        _revealLink.Margin = new Padding(6, 6, 0, 0);
        _revealLink.LinkColor = Palette.Green;
        _revealLink.ActiveLinkColor = Color.FromArgb(20, 80, 44);
        _revealLink.LinkBehavior = LinkBehavior.HoverUnderline;
        _revealLink.Visible = false;
        _revealLink.LinkClicked += (_, _) => RevealInExplorer();
        var statusRow = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 30, WrapContents = false };
        statusRow.Controls.Add(_resultLabel);
        statusRow.Controls.Add(_revealLink);

        var footer = NewLabel("Closing Tracedrop deletes these files. Your settings are kept in Tracedrop.exe.", UiFonts.Caption, Palette.Muted);
        footer.AutoSize = false;
        footer.Dock = DockStyle.Bottom;
        footer.Height = 36;
        footer.TextAlign = ContentAlignment.MiddleLeft;

        var gap = new Panel { Dock = DockStyle.Bottom, Height = 6 };
        pane.Controls.Add(listCard);
        pane.Controls.Add(_listHint);
        pane.Controls.Add(gap);
        pane.Controls.Add(actions);
        pane.Controls.Add(statusRow);
        pane.Controls.Add(footer);
        pane.Controls.Add(headingRow);
        UpdateActionStates();
        return pane;
    }

    List<TraceItem> SelectedItems() => _list.SelectedItems.Cast<ListViewItem>().Select(i => (TraceItem)i.Tag!).ToList();

    TraceItem? PrimaryItem() =>
        (_list.FocusedItem is { Selected: true } f ? f.Tag : _list.SelectedItems.Count > 0 ? _list.SelectedItems[0].Tag : null) as TraceItem;

    #region Adding pictures

    void BrowseForPictures()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Add pictures",
            Multiselect = true,
            Filter = "Pictures|" + string.Join(";", ImageLoader.KnownExtensions.Select(e => "*" + e)) + "|All files|*.*"
        };
        if (dialog.ShowDialog(this) == DialogResult.OK) AddPictures(dialog.FileNames);
    }

    void AddPictures(IEnumerable<string> paths)
    {
        var files = new List<string>();
        foreach (var path in paths)
        {
            if (Directory.Exists(path))
            {
                try { files.AddRange(Directory.EnumerateFiles(path).Where(ImageLoader.LooksLikeImage).OrderBy(f => f)); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            else if (File.Exists(path)) files.Add(path);
        }
        // Our own output files dragged back in are ignored.
        files = files.Where(f => !Path.GetFullPath(f).StartsWith(_outputPath, StringComparison.OrdinalIgnoreCase)).ToList();
        if (files.Count == 0) return;

        var added = new List<TraceItem>();
        lock (_items)
        {
            foreach (var file in files)
            {
                var item = new TraceItem(file, Path.GetFileName(file), UniqueStem(Path.GetFileNameWithoutExtension(file)), _template.Clone(), _presetEdited ? null : _activePreset);
                _items.Add(item);
                added.Add(item);
            }
        }
        _list.BeginUpdate();
        foreach (var item in added) _list.Items.Add(new ListViewItem(item.Name) { Tag = item });
        _list.SelectedItems.Clear();
        var last = _list.Items[_list.Items.Count - added.Count];
        last.Selected = true;
        last.Focused = true;
        last.EnsureVisible();
        _list.EndUpdate();
        UpdateEmptyState();
        _worker.Signal();
    }

    string UniqueStem(string stem)
    {
        var cleaned = new string(stem.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray()).Trim();
        if (cleaned.Length == 0) cleaned = "Picture";
        var candidate = cleaned;
        var n = 2;
        while (_items.Any(i => string.Equals(i.Stem, candidate, StringComparison.OrdinalIgnoreCase))) candidate = $"{cleaned} ({n++})";
        return candidate;
    }

    void Window_DragEnter(object? sender, DragEventArgs e)
    {
        if (e.Data is null) return;
        if (e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent(DataFormats.Bitmap))
        {
            // Ignore our own files coming back (dragging a row over the window).
            if (e.Data.GetData(DataFormats.FileDrop) is string[] paths && paths.All(p => p.StartsWith(_outputPath, StringComparison.OrdinalIgnoreCase)))
            {
                e.Effect = DragDropEffects.None;
                return;
            }
            e.Effect = DragDropEffects.Copy;
        }
    }

    void Window_DragDrop(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetData(DataFormats.FileDrop) is string[] paths) AddPictures(paths);
        else if (e.Data?.GetData(DataFormats.Bitmap) is Image image) AddPastedImage(image);
    }

    void Window_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Control && e.KeyCode == Keys.V && ActiveControl is not TextBoxBase and not NumericUpDown)
        {
            Paste();
            e.Handled = e.SuppressKeyPress = true;
        }
        else if (e.Control && e.KeyCode == Keys.O)
        {
            BrowseForPictures();
            e.Handled = e.SuppressKeyPress = true;
        }
        else if (e.KeyCode == Keys.F && ActiveControl is PreviewView)
        {
            _preview.FitToView();
            e.Handled = true;
        }
    }

    void Paste()
    {
        try
        {
            if (Clipboard.ContainsFileDropList())
            {
                AddPictures(Clipboard.GetFileDropList().Cast<string>());
                return;
            }
            if (Clipboard.ContainsImage() && Clipboard.GetImage() is { } image)
            {
                using (image) AddPastedImage(image);
                return;
            }
            ShowStatus("Nothing to paste: copy a picture or picture files first.", Palette.Muted);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException or InvalidOperationException)
        {
            ShowStatus("Could not read the clipboard. Please try again.", Palette.Red);
        }
    }

    void AddPastedImage(Image image)
    {
        var path = Path.Combine(_pastePath, $"Pasted {DateTime.Now:yyyy-MM-dd 'at' HH.mm.ss}.png");
        var n = 2;
        while (File.Exists(path)) path = Path.Combine(_pastePath, $"Pasted {DateTime.Now:yyyy-MM-dd 'at' HH.mm.ss} ({n++}).png");
        image.Save(path, ImageFormat.Png);
        AddPictures(new[] { path });
    }

    #endregion

    #region Selection and preview

    void OnSelectionChanged()
    {
        var item = PrimaryItem();
        UpdateActionStates();
        if (ReferenceEquals(item, _shown)) return;

        // Only the shown picture keeps its full-size bitmaps.
        if (_shown is not null && !_shown.Removed)
        {
            _shown.DisposePreview();
        }
        _preview.SetPictures(null, null, null);
        _shown = item;
        _worker.Selected = item;
        if (item is not null)
        {
            TraceSettings settings;
            string? preset;
            lock (item.Gate)
            {
                settings = item.Settings.Clone();
                preset = item.PresetName;
            }
            var edited = preset is null || !SameSettings(settings, _config.AllPresets().FirstOrDefault(p => p.Name == preset)?.Settings);
            LoadSettingsIntoUi(settings, preset, edited && preset is not null);
            if (preset is null) { _activePreset = null; RefreshPresetBox(); }
        }
        ShowItem(item);
    }

    static bool SameSettings(TraceSettings a, TraceSettings? b)
    {
        if (b is null) return false;
        var x = a.Clone(); x.Picks = new();
        var y = b.Clone(); y.Picks = new();
        return x.ToJson() == y.ToJson();
    }

    /// <summary>Refreshes the preview, stats and drag chip for the shown picture.</summary>
    void ShowItem(TraceItem? item)
    {
        if (item is null)
        {
            _preview.Clear();
            _statsLabel.Text = "";
            _clearPicksLink.Visible = false;
            UpdateDragChip();
            return;
        }
        if (item.Photo is not null) _preview.SetPictures(item.Photo, item.Prepared, item.Detected);
        List<PickPoint> picks;
        lock (item.Gate) picks = item.Settings.Picks.ToList();
        _preview.SetDrawing(item.Result?.Drawing, picks);
        _preview.BusyText = item.State == ItemState.Tracing || (item.Photo is null && item.State != ItemState.Failed) ? "Tracing…" : null;
        _clearPicksLink.Visible = picks.Count > 0;
        _statsLabel.ForeColor = item.Error is null ? Palette.Muted : Palette.Red;
        _statsLabel.Text = item.Error ?? Stats(item);
        _preview.EmptyTitle = item.Error is null ? "Drop pictures here" : "Couldn't trace this picture";
        _preview.EmptyBody = item.Error ?? "PNG, JPEG, HEIC, WebP, TIFF and more. Or paste with Ctrl+V.";
        UpdateDragChip();
    }

    static string Stats(TraceItem item)
    {
        if (item.Result is not { } r) return item.State == ItemState.Failed ? "" : "Tracing…";
        var d = r.Drawing;
        var units = r.InUnits();
        var parts = new List<string>
        {
            d.OpenCount > 0 && d.ClosedCount > 0 ? $"{d.ClosedCount} shapes, {d.OpenCount} lines"
                : d.OpenCount > 0 ? $"{d.OpenCount} lines" : $"{d.ClosedCount} shapes",
            $"{d.EntityCount} entities",
            $"{Format.Size(units.Width, r.Settings.Units)} × {Format.Size(units.Height, r.Settings.Units)}",
            Format.Duration(r.Elapsed)
        };
        var text = string.Join("  ·  ", parts);
        if (r.Notes.Count > 0) text += "  ·  " + r.Notes[0];
        return text;
    }

    void Preview_PickRequested(PickPoint pick)
    {
        var item = PrimaryItem();
        if (item is null || item.Result is null) return;
        lock (item.Gate)
        {
            item.Settings.Picks.Add(pick);
            item.Version++;
        }
        _worker.Signal(item);
        ShowItem(item);
    }

    void ClearPicks()
    {
        var item = PrimaryItem();
        if (item is null) return;
        lock (item.Gate)
        {
            if (item.Settings.Picks.Count == 0) return;
            item.Settings.Picks.Clear();
            item.Version++;
        }
        _worker.Signal(item);
        ShowItem(item);
    }

    void UpdateEmptyState()
    {
        _listHint.Visible = _items.Count > 0;
        _pickHint.Visible = _items.Count > 0;
        if (_items.Count == 0) ShowItem(null);
        Text = _items.Count > 0 ? $"Tracedrop ({_items.Count})" : "Tracedrop";
        UpdateActionStates();
    }

    void UpdateDragChip()
    {
        var item = PrimaryItem();
        var count = SelectedItems().Count(i => i.Outputs.Count > 0);
        _dragChip.Visible = item?.Outputs.Count > 0;
        var format = _config.Formats.Count == 1 ? Exporters.Extension(_config.Formats[0]).TrimStart('.').ToUpperInvariant() : "files";
        _dragChip.Text = count > 1 ? $"Drag {count} {format}" : $"Drag {format}";
    }

    #endregion

    #region Worker updates

    void OnTraceUpdate(TraceUpdate u)
    {
        var item = u.Item;
        if (item.Removed || _closing)
        {
            u.Thumbnail?.Dispose(); u.Photo?.Dispose(); u.Prepared?.Dispose(); u.Detected?.Dispose();
            return;
        }

        if (u.Started)
        {
            item.State = ItemState.Tracing;
            if (ReferenceEquals(item, _shown)) _preview.BusyText = "Tracing…";
            RedrawItem(item);
            return;
        }

        if (u.Thumbnail is not null) { item.Thumbnail?.Dispose(); item.Thumbnail = u.Thumbnail; }
        var shown = ReferenceEquals(item, _shown);
        if (shown)
        {
            if (u.Photo is not null) { item.Photo?.Dispose(); item.Photo = u.Photo; }
            if (u.Prepared is not null) { item.Prepared?.Dispose(); item.Prepared = u.Prepared; }
            if (u.Detected is not null) { item.Detected?.Dispose(); item.Detected = u.Detected; }
        }
        else
        {
            u.Photo?.Dispose(); u.Prepared?.Dispose(); u.Detected?.Dispose();
        }

        if (u.Error is not null)
        {
            item.Error = u.Error;
            item.State = ItemState.Failed;
        }
        else if (u.Result is not null)
        {
            item.Result = u.Result;
            item.Error = null;
            item.State = item.Version == u.Version ? ItemState.Done : ItemState.Tracing;
        }
        if (u.Outputs is not null)
        {
            item.Outputs.Clear();
            foreach (var kv in u.Outputs) item.Outputs[kv.Key] = kv.Value;
        }

        RedrawItem(item);
        if (shown) ShowItem(item);
        UpdateDeviceLabel();
        UpdateActionStates();
    }

    void RedrawItem(TraceItem item)
    {
        foreach (ListViewItem row in _list.Items)
        {
            if (!ReferenceEquals(row.Tag, item)) continue;
            _list.RedrawItems(row.Index, row.Index, false);
            break;
        }
    }

    #endregion

    #region List drawing

    void List_DrawItem(object? sender, DrawListViewItemEventArgs e)
    {
        if (e.Item.Tag is not TraceItem item) return;
        var g = e.Graphics;
        var row = new Rectangle(0, e.Bounds.Top, _list.ClientSize.Width, e.Bounds.Height);
        using (var back = new SolidBrush(Palette.Surface)) g.FillRectangle(back, row);

        var focused = _list.Focused;
        var selected = e.Item.Selected;
        var onGreen = selected && focused;
        var plate = Rectangle.Inflate(row, -this.Px(3), -this.Px(2));
        Color? plateColor = selected ? (focused ? Palette.Green : Palette.SelectionIdle) : ReferenceEquals(e.Item, _list.HotItem) ? Palette.Hover : null;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        if (plateColor is { } fill)
        {
            using var path = UiKit.RoundedRect(plate, this.Px(10));
            using var brush = new SolidBrush(fill);
            g.FillPath(brush, path);
        }
        else
        {
            using var separator = new Pen(Color.FromArgb(236, 239, 233));
            g.DrawLine(separator, plate.Left + this.Px(10), row.Bottom - 1, plate.Right - this.Px(10), row.Bottom - 1);
        }

        var tileSize = this.Px(44);
        var tile = new Rectangle(plate.Left + this.Px(8), row.Top + (row.Height - tileSize) / 2, tileSize, tileSize);
        using (var path = UiKit.RoundedRect(tile, this.Px(8)))
        {
            using var tileBrush = new SolidBrush(onGreen ? Color.FromArgb(60, Color.White) : Palette.Placeholder);
            g.FillPath(tileBrush, path);
            if (item.Thumbnail is { } thumb)
            {
                var state = g.Save();
                g.SetClip(path);
                var scale = Math.Max(tile.Width / (float)thumb.Width, tile.Height / (float)thumb.Height);
                var w = thumb.Width * scale;
                var h = thumb.Height * scale;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(thumb, tile.Left + (tile.Width - w) / 2, tile.Top + (tile.Height - h) / 2, w, h);
                g.Restore(state);
            }
        }
        g.SmoothingMode = SmoothingMode.Default;

        var textLeft = tile.Right + this.Px(10);
        var textWidth = plate.Right - textLeft - this.Px(8);
        var ink = onGreen ? Color.White : Palette.Ink;
        var muted = onGreen ? Color.FromArgb(222, 241, 229) : Palette.Muted;
        const TextFormatFlags flags = TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.Left;
        TextRenderer.DrawText(g, item.Name, UiFonts.BodyStrong, new Rectangle(textLeft, row.Top + this.Px(10), textWidth, this.Px(20)), ink, flags);
        string status;
        Color statusColor = muted;
        if (item.State == ItemState.Failed) { status = item.Error ?? "Failed"; statusColor = onGreen ? Color.White : Palette.Red; }
        else if (item.State == ItemState.Tracing || item.Result is null) status = "Tracing…";
        else
        {
            var d = item.Result.Drawing;
            var units = item.Result.InUnits();
            var shapes = d.OpenCount >= d.ClosedCount && d.OpenCount > 0 ? $"{d.OpenCount} lines" : $"{d.ClosedCount} shapes";
            var formats = string.Join(" ", item.Outputs.Keys.OrderBy(k => k).Select(k => Exporters.Extension(k).TrimStart('.').ToUpperInvariant()));
            status = $"{formats}  ·  {shapes}  ·  {Format.Size(Math.Max(units.Width, units.Height), item.Result.Settings.Units)}";
        }
        TextRenderer.DrawText(g, status, UiFonts.Small, new Rectangle(textLeft, row.Top + this.Px(32), textWidth, this.Px(18)), statusColor, flags);
    }

    #endregion

    #region Drag, open, save, remove

    DataObject? DragDataFor(IEnumerable<TraceItem> items)
    {
        var paths = new StringCollection();
        foreach (var item in items)
            foreach (var path in item.Outputs.OrderBy(kv => kv.Key).Select(kv => kv.Value))
                if (File.Exists(path)) paths.Add(path);
        if (paths.Count == 0) return null;
        var data = new DataObject();
        data.SetFileDropList(paths);
        return data;
    }

    void List_ItemDrag(object? sender, ItemDragEventArgs e)
    {
        var data = DragDataFor(SelectedItems());
        if (data is not null) _list.DoDragDrop(data, DragDropEffects.Copy);
    }

    void List_MouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Right) return;
        var row = _list.HitTest(e.Location).Item;
        if (row is not null && row.Selected) return;
        _list.SelectedItems.Clear();
        if (row is null) return;
        row.Selected = true;
        row.Focused = true;
    }

    void List_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Delete) { RemoveSelected(); e.Handled = true; }
        else if (e.KeyCode == Keys.A && e.Control)
        {
            _list.BeginUpdate();
            foreach (ListViewItem row in _list.Items) row.Selected = true;
            _list.EndUpdate();
            e.Handled = true;
        }
        else if (e.KeyCode == Keys.Enter) { foreach (var i in SelectedItems()) OpenOutput(i); e.Handled = true; }
    }

    void OpenOutput(TraceItem item)
    {
        var path = item.Outputs.OrderBy(kv => kv.Key).Select(kv => kv.Value).FirstOrDefault(File.Exists);
        if (path is null) return;
        try { using var _ = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { ShowStatus($"No app opens {Path.GetExtension(path)} files here: {ex.Message}", Palette.Red); }
    }

    void RemoveSelected()
    {
        var selected = SelectedItems();
        if (selected.Count == 0) return;
        var firstIndex = _list.SelectedIndices.Count > 0 ? _list.SelectedIndices[0] : 0;
        _list.BeginUpdate();
        foreach (var item in selected)
        {
            item.Removed = true;
            lock (_items) _items.Remove(item);
            foreach (ListViewItem row in _list.Items)
                if (ReferenceEquals(row.Tag, item)) { _list.Items.Remove(row); break; }
            foreach (var path in item.Outputs.Values)
            {
                try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            if (ReferenceEquals(item, _shown)) { _shown = null; _preview.Clear(); }
            item.Dispose();
        }
        if (_list.Items.Count > 0)
        {
            var next = _list.Items[Math.Min(firstIndex, _list.Items.Count - 1)];
            next.Selected = true;
            next.Focused = true;
        }
        _list.EndUpdate();
        UpdateEmptyState();
        ShowStatus($"Removed {selected.Count} picture{(selected.Count == 1 ? "" : "s")}.", Palette.Muted);
    }

    async Task SaveItemsAsync(List<TraceItem> items)
    {
        var sources = items.SelectMany(i => i.Outputs.Values).Where(File.Exists).ToList();
        if (sources.Count == 0)
        {
            ShowStatus("Nothing to save yet.", Palette.Muted);
            return;
        }
        using var picker = new FolderBrowserDialog
        {
            Description = "Choose where the vector files should be saved",
            UseDescriptionForTitle = true,
            SelectedPath = _lastSaveFolder,
            ShowNewFolderButton = true
        };
        if (picker.ShowDialog(this) != DialogResult.OK) return;
        var folder = picker.SelectedPath;
        _lastSaveFolder = folder;
        var saved = new List<string>();
        string? failure = null;
        await Task.Run(() =>
        {
            foreach (var source in sources)
            {
                try
                {
                    var destination = AvailablePath(folder, Path.GetFileName(source));
                    File.Copy(source, destination, overwrite: false);
                    saved.Add(destination);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    failure ??= $"{Path.GetFileName(source)}: {ex.Message}";
                }
            }
        });
        if (IsDisposed) return;
        var folderName = Path.GetFileName(folder.TrimEnd('\\', '/'));
        if (folderName.Length == 0) folderName = folder;
        if (failure is not null) ShowStatus($"Saved {saved.Count} of {sources.Count}. {failure}", Palette.Red);
        else ShowStatus($"Saved {saved.Count} file{(saved.Count == 1 ? "" : "s")} to {folderName}", Palette.Green, saved.FirstOrDefault());
    }

    static string AvailablePath(string folder, string name)
    {
        var candidate = Path.Combine(folder, name);
        if (!File.Exists(candidate)) return candidate;
        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        for (var i = 1; i < 10000; i++)
        {
            candidate = Path.Combine(folder, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
        return Path.Combine(folder, $"{stem}-{Guid.NewGuid():N}{ext}");
    }

    void UpdateActionStates()
    {
        var any = _items.Count > 0;
        var selected = _list.SelectedItems.Count > 0;
        _saveSelectedButton.Enabled = any && _items.Any(i => i.Outputs.Count > 0);
        _saveSelectedButton.Text = selected && _list.SelectedItems.Count < _items.Count ? "Save…" : "Save all…";
        _removeButton.Enabled = selected;
        _applyAllButton.Enabled = _items.Count > 1;
        UpdateDragChip();
    }

    void ShowStatus(string text, Color color, string? revealPath = null)
    {
        _statusTimer.Stop();
        _resultLabel.Text = revealPath is null ? text : text + "  ·";
        _resultLabel.ForeColor = color;
        _revealPath = revealPath;
        _revealLink.Visible = revealPath is not null;
        if (text.Length > 0) _statusTimer.Start();
    }

    void RevealInExplorer()
    {
        if (_revealPath is not { } path) return;
        try
        {
            var arguments = File.Exists(path) ? $"/select,\"{path}\"" : $"\"{Path.GetDirectoryName(path)}\"";
            using var explorer = Process.Start(new ProcessStartInfo("explorer.exe") { Arguments = arguments, UseShellExecute = false });
        }
        catch (Exception ex)
        {
            ShowStatus($"Could not open File Explorer: {ex.Message}", Palette.Red);
        }
    }

    #endregion
}

/// <summary>Owner-drawn list with hover tracking and a single full-width column.</summary>
sealed class ItemListView : ListView
{
    bool _settingColumns;

    public ItemListView() => DoubleBuffered = true;

    public ListViewItem? HotItem { get; private set; }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.UseExplorerTheme(Handle);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        SetHot(HitTest(e.Location).Item);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        SetHot(null);
    }

    void SetHot(ListViewItem? item)
    {
        if (ReferenceEquals(item, HotItem)) return;
        var previous = HotItem;
        HotItem = item;
        if (previous is not null && ReferenceEquals(previous.ListView, this) && previous.Index >= 0) RedrawItems(previous.Index, previous.Index, false);
        if (item is not null && item.Index >= 0) RedrawItems(item.Index, item.Index, false);
    }

    public void SetColumnWidths(params int[] widths)
    {
        _settingColumns = true;
        try
        {
            for (var i = 0; i < widths.Length && i < Columns.Count; i++)
                if (Columns[i].Width != widths[i]) Columns[i].Width = widths[i];
        }
        finally { _settingColumns = false; }
    }

    protected override void OnColumnWidthChanging(ColumnWidthChangingEventArgs e)
    {
        if (!_settingColumns) e.Cancel = true;
        base.OnColumnWidthChanging(e);
    }

    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
}
