using System.Diagnostics;
using OpenCvSharp;
using Tracedrop.Core.Ai;
using Tracedrop.Core.Detect;
using Tracedrop.Core.Imaging;
using Tracedrop.Core.Vector;

namespace Tracedrop.Core;

/// <summary>What one run of the pipeline produced.</summary>
public sealed class TraceResult
{
    public required VectorDrawing Drawing { get; init; }
    /// <summary>Picture size the drawing's pixel coordinates refer to.</summary>
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required TraceSettings Settings { get; init; }
    /// <summary>Notes for the user, such as a model that isn't bundled and what was used instead.</summary>
    public required IReadOnlyList<string> Notes { get; init; }
    public required TimeSpan Elapsed { get; init; }
    /// <summary>Milliseconds spent per stage in this run (stages served from cache are absent).</summary>
    public required IReadOnlyDictionary<string, double> StageMs { get; init; }

    /// <summary>Output units per picture pixel: OutputSize over the traced shape's longest side (before offset).</summary>
    public required double UnitsPerPixel { get; init; }

    public VectorDrawing InUnits() => Drawing.ToOutputUnits(UnitsPerPixel);
}

/// <summary>
/// One picture and its cached intermediate results. Each stage (prepare → AI maps → detect → clean → vectorise)
/// remembers the settings it was computed with, so moving a slider only re-runs the stages after it: dragging
/// Smoothness re-fits in milliseconds while the AI maps are reused.
/// Not thread-safe: run one <see cref="Run"/> at a time per job.
/// </summary>
public sealed class TraceJob : IDisposable
{
    readonly LoadedImage _image;
    readonly ModelStore _models;

    Mat? _working;
    Mat? _workingAlpha;
    int _workingSize;

    Mat? _subject;           // AI subject probability on the working picture (not the prepared one)
    bool _subjectTried;

    string? _prepareKey;
    Mat? _prepared;

    string? _edgesKey;
    (Mat Fused, Mat Average)? _edges;

    string? _lineKey;
    Mat? _lineInk;

    string? _detectKey;
    Detection? _detected;

    string? _cleanKey;
    Detection? _cleaned;
    double _unitsPerPixel = 1;

    string? _traceKey;
    List<RawPath>? _raw;

    string? _vectorKey;
    VectorDrawing? _drawing;

    public TraceJob(LoadedImage image, ModelStore models)
    {
        _image = image;
        _models = models;
    }

    public int SourceWidth => _image.Width;
    public int SourceHeight => _image.Height;

    /// <summary>The working-size picture (after scaling, before Prepare). Valid after the first Run.</summary>
    public Mat? Working => _working;

    /// <summary>The prepared picture of the last run.</summary>
    public Mat? Prepared => _prepared;

    /// <summary>The cleaned detection of the last run (what gets vectorised).</summary>
    public Detection? Detected => _cleaned;

    public TraceResult Run(TraceSettings settings, CancellationToken cancel = default)
    {
        var s = settings.Clone().Normalized();
        var notes = new List<string>();
        var clock = Stopwatch.StartNew();
        var stages = new Dictionary<string, double>();
        var lap = Stopwatch.StartNew();
        void Lap(string name) { stages[name] = stages.GetValueOrDefault(name) + lap.Elapsed.TotalMilliseconds; lap.Restart(); }

        // 1. Working size.
        if (_working is null || _workingSize != s.WorkingSize)
        {
            DisposeFrom(Stage.Working);
            _working = Prepare.ToWorkingSize(_image.Bgr, s.WorkingSize);
            if (_image.Alpha is not null)
            {
                _workingAlpha = new Mat();
                Cv2.Resize(_image.Alpha, _workingAlpha, _working.Size(), 0, 0, InterpolationFlags.Linear);
                // A small picture scaled up turns hard alpha edges into stair steps: soften them in proportion.
                var upscale = _working.Cols / (double)_image.Width;
                if (upscale > 1.5) Cv2.GaussianBlur(_workingAlpha, _workingAlpha, new OpenCvSharp.Size(0, 0), 0.45 * upscale);
            }
            _workingSize = s.WorkingSize;
        }
        Lap("load");
        cancel.ThrowIfCancellationRequested();

        // 2. Subject mask, if anything needs it.
        var needsSubject = s.Mode == TraceMode.Silhouette || s.IsolateSubject;
        Mat? subject = null;
        var subjectFromAlpha = false;
        if (needsSubject)
        {
            if (_workingAlpha is not null)
            {
                subject = new Mat();
                _workingAlpha.ConvertTo(subject, MatType.CV_32F, 1 / 255.0);
                subjectFromAlpha = true;
            }
            else
            {
                if (!_subjectTried)
                {
                    _subjectTried = true;
                    _subject = AiModels.Subject(_models, _working);
                }
                subject = _subject?.Clone();
                if (subject is null)
                    notes.Add(s.Mode == TraceMode.Silhouette
                        ? "No background-removal model: plain-background method used (best on an even surface)."
                        : "Isolate subject needs the background removal model, which isn't bundled.");
            }
        }
        Lap("subject");
        cancel.ThrowIfCancellationRequested();

        try
        {
            // 3. Prepare.
            var prepareKey = Key(s.WorkingSize, s.Brightness, s.Contrast, s.LocalContrast, s.Blur, s.SmoothTexture, s.Despeckle, s.Invert,
                s.IsolateSubject && subject is not null);
            if (_prepareKey != prepareKey || _prepared is null)
            {
                DisposeFrom(Stage.Prepared);
                _prepared = Prepare.Apply(_working, s);
                if (s.IsolateSubject && subject is not null) Prepare.WhiteOutBackground(_prepared, subject);
                _prepareKey = prepareKey;
            }
            Lap("prepare");
            cancel.ThrowIfCancellationRequested();

            // 4. AI maps on the prepared picture.
            if (s.Mode == TraceMode.LineArt)
            {
                if (_lineKey != prepareKey)
                {
                    _lineInk?.Dispose();
                    _lineInk = AiModels.LineArt(_models, _prepared, 1024);
                    _lineKey = prepareKey;
                }
            }
            Lap("line model");
            var needsEdges = (s.Mode == TraceMode.Edges && s.UseAiEdges) || (s.Mode == TraceMode.LineArt && _lineInk is null);
            if (needsEdges)
            {
                if (_edgesKey != prepareKey)
                {
                    DisposeEdges();
                    _edges = AiModels.Edges(_models, _prepared);
                    _edgesKey = prepareKey;
                }
                if (_edges is null && s.Mode == TraceMode.Edges) notes.Add("Edge model not bundled: using classic Canny edges.");
            }
            cancel.ThrowIfCancellationRequested();

            Lap("edge model");
            // 5. Detect.
            var detectKey = Key(prepareKey, needsEdges && _edges is not null, _lineInk is not null, s.Mode, s.Detail, s.Threshold, s.AutoThreshold, s.Colors, s.UseAiEdges, subject is not null, subjectFromAlpha);
            if (_detectKey != detectKey || _detected is null)
            {
                DisposeFrom(Stage.Detected);
                var signals = new Signals
                {
                    Prepared = _prepared,
                    Subject = subject,
                    SubjectFromAlpha = subjectFromAlpha,
                    EdgeFused = needsEdges ? _edges?.Fused : null,
                    EdgeAverage = needsEdges ? _edges?.Average : null,
                    LineInk = _lineInk
                };
                _detected = Detector.Detect(signals, s);
                _detectKey = detectKey;
            }
            Lap("detect");
            cancel.ThrowIfCancellationRequested();

            // 6. Clean up, then size: OutputSize is the traced shape's longest side before any offset.
            var cleanKey = Key(detectKey, s.CloseGaps, s.FillHoles, s.Keep, string.Join(";", s.Picks.Select(p => $"{p.X:0.####},{p.Y:0.####},{p.Keep}")),
                s.Offset, s.OutputSize);
            if (_cleanKey != cleanKey || _cleaned is null)
            {
                DisposeFrom(Stage.Cleaned);
                using var cleaned = Cleanup.Apply(_detected, s);
                var box = Cleanup.Extents(cleaned);
                var longest = box is { } b ? Math.Max(b.Width, b.Height) : Math.Max(_working.Cols, _working.Rows);
                _unitsPerPixel = s.OutputSize / Math.Max(1, longest);
                _cleaned = Math.Abs(s.Offset) > 1e-9 ? Cleanup.Offset(cleaned, s.Offset / _unitsPerPixel) : Cleanup.Offset(cleaned, 0);
                _cleanKey = cleanKey;
            }
            Lap("clean");
            cancel.ThrowIfCancellationRequested();

            // 7. Trace outlines / centerlines (cached: fitting sliders don't re-trace).
            var traceKey = Key(cleanKey, s.Strokes, s.StrokeWidth, s.Detail);
            if (_traceKey != traceKey || _raw is null)
            {
                _raw = Vectorizer.Extract(_cleaned, s, cancel);
                _traceKey = traceKey;
                _drawing = null;
            }
            Lap("trace");

            // 8. Fit lines, arcs, splines.
            var vectorKey = Key(traceKey, s.Smoothness, s.CornerSharpness, s.Curves, s.SnapCircles);
            if (_vectorKey != vectorKey || _drawing is null)
            {
                _drawing = Vectorizer.Fit(_raw, _working.Cols, _working.Rows, s, cancel);
                _vectorKey = vectorKey;
            }
            Lap("fit");

            return new TraceResult
            {
                Drawing = _drawing,
                Width = _working.Cols,
                Height = _working.Rows,
                Settings = s,
                UnitsPerPixel = _unitsPerPixel,
                Notes = notes,
                Elapsed = clock.Elapsed,
                StageMs = stages.Where(kv => kv.Value >= 0.5).ToDictionary(kv => kv.Key, kv => kv.Value)
            };
        }
        finally
        {
            subject?.Dispose();
        }
    }

    static string Key(params object[] parts) =>
        string.Join("|", parts.Select(p => p is double d ? d.ToString("R", System.Globalization.CultureInfo.InvariantCulture) : p?.ToString() ?? ""));

    enum Stage { Working, Prepared, Detected, Cleaned }

    void DisposeFrom(Stage stage)
    {
        if (stage <= Stage.Working)
        {
            _working?.Dispose(); _working = null;
            _workingAlpha?.Dispose(); _workingAlpha = null;
            _subject?.Dispose(); _subject = null; _subjectTried = false;
        }
        if (stage <= Stage.Prepared)
        {
            _prepared?.Dispose(); _prepared = null; _prepareKey = null;
            DisposeEdges();
            _lineInk?.Dispose(); _lineInk = null; _lineKey = null;
        }
        if (stage <= Stage.Detected)
        {
            _detected?.Dispose(); _detected = null; _detectKey = null;
        }
        if (stage <= Stage.Cleaned)
        {
            _cleaned?.Dispose(); _cleaned = null; _cleanKey = null;
            _raw = null; _traceKey = null;
            _drawing = null; _vectorKey = null;
        }
    }

    void DisposeEdges()
    {
        if (_edges is { } e) { e.Fused.Dispose(); e.Average.Dispose(); }
        _edges = null;
        _edgesKey = null;
    }

    public void Dispose()
    {
        DisposeFrom(Stage.Working);
        _image.Dispose();
    }
}
