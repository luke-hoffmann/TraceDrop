using System.Diagnostics;
using Tracedrop.Core;
using Tracedrop.Core.Ai;
using Tracedrop.Core.Export;
using Tracedrop.Core.Imaging;
using Tracedrop.Core.Portable;

namespace TracedropApp;

/// <summary>
/// The Tracedrop window: settings on the left, live preview in the middle, pictures on the right.
/// Traced files live in a temporary session folder until they are dragged or saved somewhere; closing the
/// window deletes it (a hidden watcher process makes sure of that even after a crash) and stores the settings
/// inside Tracedrop.exe itself.
/// </summary>
sealed partial class MainForm : Form
{
    readonly string _exePath;
    readonly string _sessionPath;
    readonly string _outputPath;
    readonly string _pastePath;
    readonly string[] _startupFiles;
    AppConfig _config;
    readonly bool _configFromExe;

    readonly List<TraceItem> _items = new();
    readonly TraceWorker _worker;
    readonly System.Windows.Forms.Timer _configTimer = new() { Interval = 800 };
    readonly System.Windows.Forms.Timer _statusTimer = new() { Interval = 15000 };
    Process? _cleanupProcess;
    bool _closing;

    // Header
    readonly Label _deviceLabel = NewLabel("", UiFonts.Small, Palette.Muted);

    // Preview pane
    readonly PreviewView _preview = new();
    readonly Segmented _viewMode = new(null, "Result", "Lines", "Detected", "Prepared", "Original");
    readonly DragChip _dragChip = new();
    readonly Label _statsLabel = NewLabel("", UiFonts.Small, Palette.Muted);
    readonly LinkLabel _clearPicksLink = new();
    readonly Label _pickHint = NewLabel("Click a shape to keep only it  ·  right-click a shape or hole to remove it  ·  scroll to zoom", UiFonts.Caption, Palette.Muted);

    public MainForm(string exePath, string[] startupFiles)
    {
        _exePath = exePath;
        _startupFiles = startupFiles;
        (_config, _configFromExe) = PortableStore.Load(exePath);

        _sessionPath = Path.Combine(Path.GetTempPath(), "Tracedrop-" + Guid.NewGuid().ToString("N"));
        _outputPath = Path.Combine(_sessionPath, "out");
        _pastePath = Path.Combine(_sessionPath, "pasted");
        Directory.CreateDirectory(_outputPath);
        Directory.CreateDirectory(_pastePath);

        SuspendLayout();
        Text = "Tracedrop";
        BackColor = Palette.Background;
        ForeColor = Palette.Ink;
        Font = UiFonts.Body;
        DoubleBuffered = true;
        KeyPreview = true;
        AllowDrop = true;
        StartPosition = FormStartPosition.CenterScreen;
        Padding = new Padding(20, 12, 20, 16);

        var preview = BuildPreviewPane();
        var gapRight = new Panel { Dock = DockStyle.Right, Width = 16 };
        var items = BuildItemsPane();
        var gapLeft = new Panel { Dock = DockStyle.Left, Width = 16 };
        var settings = BuildSettingsCard();
        var header = BuildHeader();
        settings.TabIndex = 0;
        preview.TabIndex = 1;
        items.TabIndex = 2;
        Controls.Add(preview);
        Controls.Add(gapRight);
        Controls.Add(items);
        Controls.Add(gapLeft);
        Controls.Add(settings);
        Controls.Add(header);

        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        ResumeLayout(false);

        _worker = new TraceWorker(_items, () => _config.Formats, _outputPath, update =>
        {
            if (IsHandleCreated && !IsDisposed) BeginInvoke(() => OnTraceUpdate(update));
        });

        _configTimer.Tick += (_, _) => { _configTimer.Stop(); WritePendingConfig(); };
        _statusTimer.Tick += (_, _) => ShowStatus("", Palette.Muted);
        DragEnter += Window_DragEnter;
        DragDrop += Window_DragDrop;
        KeyDown += Window_KeyDown;
        Shown += MainForm_Shown;
        FormClosing += MainForm_FormClosing;

        LoadSettingsIntoUi(_config.LastSettings, _config.LastPreset);
        UpdateEmptyState();
    }

    protected override void OnLoad(EventArgs e)
    {
        var area = Screen.FromPoint(MousePosition).WorkingArea;
        MinimumSize = new Size(Math.Min(this.Px(1000), area.Width), Math.Min(this.Px(620), area.Height));
        var width = Math.Min(this.Px(1360), area.Width - this.Px(32));
        var height = Math.Min(this.Px(860), area.Height - this.Px(32));
        Size = new Size(Math.Max(width, MinimumSize.Width), Math.Max(height, MinimumSize.Height));
        CenterToScreen();
        base.OnLoad(e);
    }

    static Label NewLabel(string text, Font font, Color color) => new()
    {
        Text = text,
        Font = font,
        ForeColor = color,
        AutoSize = true,
        UseMnemonic = false
    };

    #region Layout

    Control BuildHeader()
    {
        var title = NewLabel("Tracedrop", UiFonts.Display, Palette.Ink);
        title.Dock = DockStyle.Left;
        title.TextAlign = ContentAlignment.MiddleLeft;

        var subtitle = NewLabel("Pictures to clean vector outlines, on this PC", UiFonts.Small, Palette.Muted);
        subtitle.AutoSize = false;
        subtitle.Dock = DockStyle.Fill;
        subtitle.TextAlign = ContentAlignment.BottomLeft;
        subtitle.Padding = new Padding(12, 0, 0, 10);

        _deviceLabel.AutoSize = false;
        _deviceLabel.Dock = DockStyle.Right;
        _deviceLabel.Width = 320;
        _deviceLabel.TextAlign = ContentAlignment.BottomRight;
        _deviceLabel.Padding = new Padding(0, 0, 0, 10);

        var header = new Panel { Dock = DockStyle.Top, Height = 52 };
        header.Controls.Add(subtitle);
        header.Controls.Add(_deviceLabel);
        header.Controls.Add(title);
        return header;
    }

    Control BuildPreviewPane()
    {
        var pane = new Panel { Dock = DockStyle.Fill };

        _viewMode.Dock = DockStyle.Left;
        _viewMode.Width = 440;
        _viewMode.SelectedIndex = _config.OverlayOnPhoto ? 0 : 1;
        _viewMode.SelectedIndexChanged += (_, _) =>
        {
            _preview.Mode = (PreviewMode)_viewMode.SelectedIndex;
            if (_viewMode.SelectedIndex <= 1)
            {
                _config.OverlayOnPhoto = _viewMode.SelectedIndex == 0;
                SaveConfigSoon();
            }
        };
        _dragChip.Dock = DockStyle.Right;
        _dragChip.Width = 150;
        _dragChip.Visible = false;
        _dragChip.DataProvider = () => DragDataFor(SelectedItems());
        var topRow = new Panel { Dock = DockStyle.Top, Height = 40 };
        topRow.Controls.Add(_viewMode);
        topRow.Controls.Add(_dragChip);

        var card = new CardPanel { Dock = DockStyle.Fill, Padding = new Padding(4) };
        _preview.Dock = DockStyle.Fill;
        _preview.Mode = (PreviewMode)_viewMode.SelectedIndex;
        _preview.PickRequested += Preview_PickRequested;
        _preview.AllowDrop = true;
        _preview.DragEnter += Window_DragEnter;
        _preview.DragDrop += Window_DragDrop;
        card.Controls.Add(_preview);

        _statsLabel.AutoSize = false;
        _statsLabel.Dock = DockStyle.Fill;
        _statsLabel.TextAlign = ContentAlignment.MiddleLeft;
        _statsLabel.AutoEllipsis = true;
        _clearPicksLink.Text = "Clear picks";
        _clearPicksLink.LinkArea = new LinkArea(0, _clearPicksLink.Text.Length);
        _clearPicksLink.Font = UiFonts.Small;
        _clearPicksLink.AutoSize = false;
        _clearPicksLink.Width = 90;
        _clearPicksLink.Dock = DockStyle.Right;
        _clearPicksLink.TextAlign = ContentAlignment.MiddleRight;
        _clearPicksLink.LinkColor = Palette.Green;
        _clearPicksLink.ActiveLinkColor = Color.FromArgb(20, 80, 44);
        _clearPicksLink.LinkBehavior = LinkBehavior.HoverUnderline;
        _clearPicksLink.Visible = false;
        _clearPicksLink.LinkClicked += (_, _) => ClearPicks();
        var infoRow = new Panel { Dock = DockStyle.Bottom, Height = 38 };
        infoRow.Controls.Add(_statsLabel);
        infoRow.Controls.Add(_clearPicksLink);

        _pickHint.AutoSize = false;
        _pickHint.Dock = DockStyle.Bottom;
        _pickHint.Height = 20;
        _pickHint.TextAlign = ContentAlignment.MiddleLeft;

        var spacer = new Panel { Dock = DockStyle.Top, Height = 8 };
        pane.Controls.Add(card);
        pane.Controls.Add(spacer);
        pane.Controls.Add(topRow);
        pane.Controls.Add(infoRow);
        pane.Controls.Add(_pickHint);
        return pane;
    }

    #endregion

    #region Startup and shutdown

    void MainForm_Shown(object? sender, EventArgs e)
    {
        StartCleanupWatcher();
        if (!_configFromExe && File.Exists(PortableStore.FallbackPath))
            ShowStatus("Settings loaded from this PC (Tracedrop.exe couldn't be updated last time).", Palette.Muted);
        if (_startupFiles.Length > 0) AddPictures(_startupFiles);
        UpdateDeviceLabel();
    }

    void MainForm_FormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_closing) return;
        _closing = true;
        _configTimer.Stop();
        CaptureConfigFromUi();
        WritePendingConfig();
        _worker.Dispose();
        foreach (var item in _items) item.Dispose();
        _preview.Clear();
        // The watcher (already running) bakes the settings into the exe and deletes the session folder
        // once this process has exited. Without a watcher, do both here.
        if (_cleanupProcess is null || _cleanupProcess.HasExited)
        {
            CleanupWatcher.Finish(_exePath, _sessionPath);
        }
    }

    void StartCleanupWatcher()
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = _exePath,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            startInfo.ArgumentList.Add("--cleanup-watcher");
            startInfo.ArgumentList.Add(Environment.ProcessId.ToString());
            startInfo.ArgumentList.Add(_sessionPath);
            startInfo.ArgumentList.Add(_exePath);
            _cleanupProcess = Process.Start(startInfo);
        }
        catch
        {
            _cleanupProcess = null; // closing will clean up in-process instead
        }
    }

    void SaveConfigSoon()
    {
        _configTimer.Stop();
        _configTimer.Start();
    }

    /// <summary>Writes the config the watcher will bake into the exe when the app exits.</summary>
    void WritePendingConfig()
    {
        try
        {
            File.WriteAllText(Path.Combine(_sessionPath, CleanupWatcher.PendingConfigName), _config.ToJson());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Retried on the next change and at exit.
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _configTimer.Dispose();
            _statusTimer.Dispose();
        }
        base.Dispose(disposing);
    }

    #endregion

    void UpdateDeviceLabel()
    {
        var models = new List<string>();
        if (ModelStore.Shared.IsAvailable(ModelNames.Edges)) models.Add("edges");
        if (ModelStore.Shared.IsAvailable(ModelNames.Subject)) models.Add("background removal");
        if (ModelStore.Shared.IsAvailable(ModelNames.LineArt)) models.Add("line drawing");
        var device = ModelStore.Shared.Device;
        _deviceLabel.Text = models.Count == 0
            ? "AI models not bundled: classic methods only"
            : $"AI: {string.Join(", ", models)}" + (device is null ? "" : $"  ·  {device}");
    }
}
