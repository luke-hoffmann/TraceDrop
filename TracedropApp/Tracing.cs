using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using OpenCvSharp;
using Tracedrop.Core;
using Tracedrop.Core.Ai;
using Tracedrop.Core.Export;
using Tracedrop.Core.Imaging;

namespace TracedropApp;

enum ItemState { Waiting, Tracing, Done, Failed }

/// <summary>One picture in the list. Fields are owned by the UI thread except where noted.</summary>
sealed class TraceItem : IDisposable
{
    public TraceItem(string sourcePath, string name, string stem, TraceSettings settings, string? preset)
    {
        SourcePath = sourcePath;
        Name = name;
        Stem = stem;
        Settings = settings;
        PresetName = preset;
    }

    public string SourcePath { get; }
    public string Name { get; }
    /// <summary>File name (without extension) of the outputs; unique within the session.</summary>
    public string Stem { get; }

    // Written by the UI thread, snapshotted by the worker under the lock.
    public readonly object Gate = new();
    public TraceSettings Settings;
    public string? PresetName;
    public int Version;

    // Worker-owned.
    public TraceJob? Job;
    public bool PhotoSent;
    public int DoneVersion = -1;
    public int PreviewVersion = -1;
    public string ExportedFormats = "";

    // UI-owned results.
    public ItemState State = ItemState.Waiting;
    public TraceResult? Result;
    public string? Error;
    public Bitmap? Thumbnail;
    public Bitmap? Photo;
    public Bitmap? Prepared;
    public Bitmap? Detected;
    public readonly Dictionary<ExportFormat, string> Outputs = new();
    public bool Removed;

    public void DisposePreview()
    {
        Photo?.Dispose(); Photo = null;
        Prepared?.Dispose(); Prepared = null;
        Detected?.Dispose(); Detected = null;
    }

    public void Dispose()
    {
        DisposePreview();
        Thumbnail?.Dispose();
        Thumbnail = null;
    }
}

/// <summary>What the worker hands back to the UI after a run.</summary>
sealed record TraceUpdate(
    TraceItem Item,
    int Version,
    TraceResult? Result,
    string? Error,
    Bitmap? Thumbnail,
    Bitmap? Photo,
    Bitmap? Prepared,
    Bitmap? Detected,
    IReadOnlyDictionary<ExportFormat, string>? Outputs,
    bool Started = false);

/// <summary>
/// Single background thread that traces pictures one at a time: the selected picture first (live preview),
/// then any other picture whose settings changed. Only the selected picture keeps its cached job (and memory);
/// others are re-loaded from disk when needed.
/// </summary>
sealed class TraceWorker : IDisposable
{
    readonly List<TraceItem> _items;
    readonly Func<IReadOnlyList<ExportFormat>> _formats;
    readonly string _outputFolder;
    readonly Action<TraceUpdate> _post;
    readonly SemaphoreSlim _signal = new(0);
    readonly CancellationTokenSource _stop = new();
    readonly Thread _thread;
    volatile TraceItem? _selected;
    TraceItem? _running;
    CancellationTokenSource? _runCancel;
    long _runStarted;
    readonly object _runGate = new();

    public TraceWorker(List<TraceItem> items, Func<IReadOnlyList<ExportFormat>> formats, string outputFolder, Action<TraceUpdate> post)
    {
        _items = items;
        _formats = formats;
        _outputFolder = outputFolder;
        _post = post;
        _thread = new Thread(Loop) { IsBackground = true, Name = "Tracedrop worker", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    public ModelStore Models { get; } = ModelStore.Shared;

    public TraceItem? Selected
    {
        get => _selected;
        set { _selected = value; Signal(); }
    }

    /// <summary>Wake the worker. If the item being traced has changed again and its run has gone on a while, restart it.</summary>
    public void Signal(TraceItem? changed = null)
    {
        if (changed is not null)
        {
            lock (_runGate)
            {
                // Quick runs (re-fits) finish and stream; long stale runs (AI maps) are cancelled.
                if (ReferenceEquals(_running, changed) && Environment.TickCount64 - _runStarted > 150) _runCancel?.Cancel();
            }
        }
        _signal.Release();
    }

    void Loop()
    {
        // Load the edge model once in the background so the first trace doesn't pay for it.
        try { using var tiny = new Mat(16, 16, MatType.CV_8UC3, Scalar.All(128)); var e = AiModels.Edges(Models, tiny); e?.Fused.Dispose(); e?.Average.Dispose(); }
        catch { /* reported when actually used */ }

        while (!_stop.IsCancellationRequested)
        {
            try { _signal.Wait(_stop.Token); }
            catch (OperationCanceledException) { return; }
            while (!_stop.IsCancellationRequested && Next() is { } work)
            {
                if (work.ExportOnly) Export(work.Item, work.Item.Result!);
                else Process(work.Item);
            }
            ReleaseIdleJobs();
        }
    }

    record Work(TraceItem Item, bool ExportOnly);

    Work? Next()
    {
        List<TraceItem> snapshot;
        lock (_items) snapshot = _items.ToList();
        var selected = _selected;
        bool Dirty(TraceItem i) => !i.Removed && (i.Version != i.DoneVersion || (ReferenceEquals(i, selected) && i.PreviewVersion != i.Version));
        if (selected is not null && Dirty(selected)) return new Work(selected, false);
        var dirty = snapshot.FirstOrDefault(Dirty);
        if (dirty is not null) return new Work(dirty, false);
        var formats = FormatKey(_formats());
        var stale = snapshot.FirstOrDefault(i => !i.Removed && i.Result is not null && i.ExportedFormats != formats && i.DoneVersion == i.Version);
        return stale is null ? null : new Work(stale, true);
    }

    static string FormatKey(IEnumerable<ExportFormat> formats) => string.Join(",", formats.OrderBy(f => f));

    void Process(TraceItem item)
    {
        TraceSettings settings;
        int version;
        lock (item.Gate)
        {
            settings = item.Settings.Clone();
            version = item.Version;
        }

        var cancel = new CancellationTokenSource();
        lock (_runGate)
        {
            _running = item;
            _runCancel = cancel;
            _runStarted = Environment.TickCount64;
        }
        _post(new TraceUpdate(item, version, null, null, null, null, null, null, null, Started: true));

        Bitmap? thumbnail = null;
        try
        {
            if (item.Job is null)
            {
                var image = ImageLoader.Load(item.SourcePath);
                item.Job = new TraceJob(image, Models);
                item.PhotoSent = false;
            }
            var result = item.Job.Run(settings, cancel.Token);
            if (item.Thumbnail is null && item.DoneVersion < 0) thumbnail = Thumb(item.Job.Working!);
            var outputs = ExportFiles(item, result);

            Bitmap? photo = null, prepared = null, detected = null;
            if (ReferenceEquals(item, _selected))
            {
                if (!item.PhotoSent) { photo = ToBitmap(item.Job.Working!); item.PhotoSent = true; }
                prepared = ToBitmap(item.Job.Prepared!);
                using var detectedMat = item.Job.Detected!.RenderPreview();
                detected = ToBitmap(detectedMat);
                item.PreviewVersion = version;
            }
            item.DoneVersion = version;
            _post(new TraceUpdate(item, version, result, null, thumbnail, photo, prepared, detected, outputs));
        }
        catch (OperationCanceledException)
        {
            thumbnail?.Dispose();
        }
        catch (Exception ex)
        {
            item.DoneVersion = version;
            item.PreviewVersion = version;
            _post(new TraceUpdate(item, version, null, Describe(ex), thumbnail, null, null, null, null));
        }
        finally
        {
            lock (_runGate)
            {
                _running = null;
                _runCancel = null;
            }
            cancel.Dispose();
            if (!ReferenceEquals(item, _selected) || item.Removed) ReleaseJob(item);
        }
    }

    static string Describe(Exception ex) => ex switch
    {
        InvalidDataException => ex.Message,
        FileNotFoundException or DirectoryNotFoundException => "The picture is no longer there.",
        OutOfMemoryException => "Not enough memory for this picture. Try a smaller Resolution.",
        _ => ex.Message
    };

    Dictionary<ExportFormat, string> ExportFiles(TraceItem item, TraceResult result)
    {
        var formats = _formats();
        var outputs = new Dictionary<ExportFormat, string>();
        var inUnits = result.InUnits();
        foreach (var format in formats)
        {
            var path = Path.Combine(_outputFolder, item.Stem + Exporters.Extension(format));
            Exporters.WriteFile(inUnits, result.Settings.Units, format, path);
            outputs[format] = path;
        }
        foreach (var format in Enum.GetValues<ExportFormat>().Except(formats))
        {
            var path = Path.Combine(_outputFolder, item.Stem + Exporters.Extension(format));
            try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        item.ExportedFormats = FormatKey(formats);
        return outputs;
    }

    void Export(TraceItem item, TraceResult result)
    {
        try
        {
            var outputs = ExportFiles(item, result);
            _post(new TraceUpdate(item, item.DoneVersion, result, null, null, null, null, null, outputs));
        }
        catch (Exception ex)
        {
            item.ExportedFormats = FormatKey(_formats());
            _post(new TraceUpdate(item, item.DoneVersion, null, "Could not write the file: " + ex.Message, null, null, null, null, null));
        }
    }

    static Bitmap Thumb(Mat bgr)
    {
        var scale = 96.0 / Math.Max(bgr.Cols, bgr.Rows);
        using var small = new Mat();
        Cv2.Resize(bgr, small, new OpenCvSharp.Size(Math.Max(1, (int)(bgr.Cols * scale)), Math.Max(1, (int)(bgr.Rows * scale))), 0, 0, InterpolationFlags.Area);
        return ToBitmap(small);
    }

    void ReleaseIdleJobs()
    {
        List<TraceItem> snapshot;
        lock (_items) snapshot = _items.ToList();
        foreach (var item in snapshot)
            if (!ReferenceEquals(item, _selected) && item.Job is not null) ReleaseJob(item);
    }

    static void ReleaseJob(TraceItem item)
    {
        item.Job?.Dispose();
        item.Job = null;
        item.PhotoSent = false;
        item.PreviewVersion = -1;
    }

    /// <summary>BGR or grey 8-bit mat → 24-bit bitmap.</summary>
    public static Bitmap ToBitmap(Mat mat)
    {
        using var bgr = new Mat();
        if (mat.Channels() == 1) Cv2.CvtColor(mat, bgr, ColorConversionCodes.GRAY2BGR);
        else if (mat.Channels() == 4) Cv2.CvtColor(mat, bgr, ColorConversionCodes.BGRA2BGR);
        else mat.CopyTo(bgr);
        var bitmap = new Bitmap(bgr.Cols, bgr.Rows, PixelFormat.Format24bppRgb);
        var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
        try
        {
            var row = new byte[bgr.Cols * 3];
            for (var y = 0; y < bgr.Rows; y++)
            {
                Marshal.Copy(bgr.Ptr(y), row, 0, row.Length);
                Marshal.Copy(row, 0, data.Scan0 + y * data.Stride, row.Length);
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
        return bitmap;
    }

    public void Dispose()
    {
        _stop.Cancel();
        lock (_runGate) _runCancel?.Cancel();
        _thread.Join(3000);
        _signal.Dispose();
        _stop.Dispose();
    }
}

static class Format
{
    public static string Size(double value, OutputUnits units) =>
        units == OutputUnits.Inches ? $"{value:0.###} in" : $"{value:0.#} mm";

    public static string Duration(TimeSpan t) => t.TotalSeconds >= 1 ? $"{t.TotalSeconds:0.0} s" : $"{t.TotalMilliseconds:0} ms";

    [Conditional("DEBUG")]
    public static void Log(string message) => Debug.WriteLine(message);
}
