using System.Diagnostics;
using OpenCvSharp;
using Tracedrop.Core.Ai;
using Tracedrop.Core.Detect;
using Tracedrop.Core.Editing;
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
    LoadedImage? _image;
    readonly Func<LoadedImage>? _reload;
    int _sourceWidth;
    int _sourceHeight;
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

    // Zoomed-in re-traces of "Find lines here" areas (stroke modes), by area and settings.
    readonly Dictionary<string, List<RawPath>> _findTraces = new();

    string? _detectKey;
    Detection? _detected;

    string? _cleanKey;
    Detection? _cleaned;
    double _unitsPerPixel = 1;

    string? _traceKey;
    List<RawPath>? _raw;

    string? _vectorKey;
    VectorDrawing? _drawing;

    /// <param name="reload">
    /// Optional way to load the picture again. When given, the full-size picture is let go once the working-size
    /// copy exists (a 12 MP photo is ~36 MB) and reloaded only if Resolution changes.
    /// </param>
    public TraceJob(LoadedImage image, ModelStore models, Func<LoadedImage>? reload = null)
    {
        _reload = reload;
        _sourceWidth = image.Width;
        _sourceHeight = image.Height;
        _image = image;
        _models = models;
    }

    public int SourceWidth => _sourceWidth;
    public int SourceHeight => _sourceHeight;

    /// <summary>Approximate memory held by the cached stages, in bytes (for the app's cache budget).</summary>
    public long EstimatedBytes
    {
        get
        {
            static long M(Mat? m) => m is null || m.IsDisposed ? 0 : m.Total() * m.ElemSize();
            static long D(Detection? d) => d is null ? 0 : M(d.Strokes) + d.Regions.Sum(r => M(r.Mask));
            var total = M(_image?.Bgr) + M(_image?.Alpha) + M(_working) + M(_workingAlpha) + M(_subject) + M(_prepared)
                        + M(_lineInk) + D(_detected) + D(_cleaned);
            if (_edges is { } e) total += M(e.Fused) + M(e.Average);
            if (_raw is not null) total += _raw.Sum(r => (long)r.Points.Count * 16 + 64);
            return total;
        }
    }

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
            _image ??= _reload?.Invoke() ?? throw new InvalidOperationException("The picture is no longer loaded.");
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
            if (_reload is not null)
            {
                _image?.Dispose();
                _image = null;
            }
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
            if (s.Mode == TraceMode.LineArt && s.UseAiLineArt)
            {
                if (_lineKey != prepareKey)
                {
                    _lineInk?.Dispose();
                    _lineInk = AiModels.LineArt(_models, _prepared);
                    _lineKey = prepareKey;
                }
            }
            Lap("line model");
            var lineInk = s.Mode == TraceMode.LineArt && s.UseAiLineArt ? _lineInk : null;
            var needsEdges = (s.Mode == TraceMode.Edges && s.UseAiEdges) || (s.Mode == TraceMode.LineArt && lineInk is null);
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
            var strokes = s.Mode is TraceMode.Edges or TraceMode.LineArt;
            var finds = s.Regions.Where(r => r.Kind == RegionKind.Find).ToList();

            // 5. Detect. In Outline and Shapes modes, "Find lines here" areas are re-detected zoomed in and pasted in.
            var detectKey = Key(prepareKey, needsEdges && _edges is not null, lineInk is not null, s.Mode, s.Detail, s.Threshold, s.AutoThreshold,
                s.Colors, s.UseAiEdges, subject is not null, subjectFromAlpha, strokes ? "" : string.Join("/", finds.Select(r => r.Key())));
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
                    LineInk = lineInk
                };
                _detected = Detector.Detect(signals, s);
                if (!strokes) foreach (var find in finds) FindShapes(_detected, find, signals, s);
                _detectKey = detectKey;
            }
            Lap("detect");
            cancel.ThrowIfCancellationRequested();

            // 6. Clean up (Erase areas cut shapes here), then size: OutputSize is the traced shape's longest side before any offset.
            var cleanKey = Key(detectKey, s.CloseGaps, s.FillHoles, s.Keep,
                string.Join(";", s.Picks.Select(p => $"{p.X:0.####},{p.Y:0.####},{p.Kind}")),
                strokes ? "" : string.Join("/", s.Regions.Where(r => r.Kind == RegionKind.Erase).Select(r => r.Key())),
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

            // 7. Trace outlines / centerlines, then apply the areas: zoomed re-traces replace what's inside Find
            //    areas (line modes), Erase areas remove lines (line modes), deleted lines stay deleted (all modes).
            var traceKey = Key(cleanKey, s.Strokes, s.StrokeWidth, s.Detail,
                string.Join("/", s.Regions.Where(r => strokes || r.Kind == RegionKind.Suppress).Select(r => r.Key())));
            if (_traceKey != traceKey || _raw is null)
            {
                var raw = Vectorizer.Extract(_cleaned, s, cancel);
                if (strokes && finds.Count > 0) raw = MergeFindTraces(raw, finds, s, prepareKey, needsEdges, lineInk is not null, cancel);
                _raw = ApplyEraseAndSuppress(raw, s, strokes);
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

            // 9. The user's own paths go on top, exactly as drawn.
            var drawing = _drawing;
            if (s.Manual.Count > 0)
            {
                var all = _drawing.Paths.ToList();
                for (var i = 0; i < s.Manual.Count; i++) all.Add(s.Manual[i].ToVectorPath(_working.Cols, _working.Rows, i));
                drawing = new VectorDrawing(_drawing.Width, _drawing.Height, all);
            }

            return new TraceResult
            {
                Drawing = drawing,
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

    /// <summary>
    /// Outline / Shapes modes: re-detects a "Find lines here" area zoomed in (with its own Amount) and replaces the
    /// shapes inside the area with that close-up.
    /// </summary>
    void FindShapes(Detection main, GuideRegion find, Signals signals, TraceSettings s)
    {
        var size = _prepared!.Size();
        var roi = Inflate(find.Bounds(size), size);
        if (roi.Width < 4 || roi.Height < 4) return;
        var scale = Math.Clamp(1200.0 / Math.Max(roi.Width, roi.Height), 1, 3);
        var upSize = new OpenCvSharp.Size(Math.Max(8, (int)Math.Round(roi.Width * scale)), Math.Max(8, (int)Math.Round(roi.Height * scale)));

        using var cropView = new Mat(_prepared, roi);
        using var crop = new Mat();
        Cv2.Resize(cropView, crop, upSize, 0, 0, InterpolationFlags.Cubic);
        StretchForFind(crop, find.Amount);
        Mat? subjectCrop = null;
        if (signals.Subject is not null)
        {
            using var view = new Mat(signals.Subject, roi);
            subjectCrop = new Mat();
            Cv2.Resize(view, subjectCrop, upSize, 0, 0, InterpolationFlags.Linear);
        }
        try
        {
            var local = s.WithoutEdits();
            local.Threshold = s.Mode == TraceMode.Silhouette
                ? Math.Clamp(s.Threshold - 8 * find.Amount, 2, 98)   // more = less confidence needed
                : Math.Clamp(s.Threshold + 6 * find.Amount, 0, 100); // more = include lighter
            using var close = Detector.Detect(new Signals { Prepared = crop, Subject = subjectCrop, SubjectFromAlpha = signals.SubjectFromAlpha }, local);
            using var area = find.Mask(size);
            using var areaRoi = new Mat(area, roi);
            foreach (var layer in main.Regions)
            {
                var source = close.Regions.Count == main.Regions.Count
                    ? close.Regions[main.Regions.IndexOf(layer)]
                    : close.Regions.OrderBy(r => Math.Pow(r.Color.Val0 - layer.Color.Val0, 2) + Math.Pow(r.Color.Val1 - layer.Color.Val1, 2) + Math.Pow(r.Color.Val2 - layer.Color.Val2, 2)).FirstOrDefault();
                if (source is null) continue;
                using var down = new Mat();
                Cv2.Resize(source.Mask, down, roi.Size, 0, 0, InterpolationFlags.Area);
                Cv2.Threshold(down, down, 127, 255, ThresholdTypes.Binary);
                using var target = new Mat(layer.Mask, roi);
                down.CopyTo(target, areaRoi);
            }
        }
        finally
        {
            subjectCrop?.Dispose();
        }
    }

    /// <summary>
    /// Line modes: traces each "Find lines here" area again zoomed in (2-4x, same settings plus its Amount) so small
    /// features become large enough to keep, then uses those lines inside the area and the normal ones outside.
    /// </summary>
    List<RawPath> MergeFindTraces(List<RawPath> main, List<GuideRegion> finds, TraceSettings s, string prepareKey, bool useEdges, bool useLineModel,
        CancellationToken cancel)
    {
        var size = _prepared!.Size();
        using var union = new Mat(size, MatType.CV_8U, Scalar.All(0));
        foreach (var f in finds) f.Draw(union, Scalar.All(255));
        bool Inside(Vec2 p) => At(union, p);

        var merged = new List<RawPath>();
        foreach (var path in main)
            foreach (var (pts, closed) in EditGeometry.Split(path.Points, path.Closed, Inside, keepInside: false))
                merged.Add(path with { Points = pts, Closed = closed });

        var wanted = new HashSet<string>();
        foreach (var find in finds)
        {
            cancel.ThrowIfCancellationRequested();
            var key = Key(prepareKey, s.Mode, s.Detail, s.Threshold, s.UseAiEdges, s.UseAiLineArt, useEdges, useLineModel, s.Strokes, s.StrokeWidth, s.CloseGaps, find.Key());
            wanted.Add(key);
            if (!_findTraces.TryGetValue(key, out var close))
            {
                close = TraceCloseUp(find, s, useEdges, useLineModel, cancel);
                _findTraces[key] = close;
            }
            using var area = find.Mask(size);
            foreach (var path in close)
                foreach (var (pts, closed) in EditGeometry.Split(path.Points, path.Closed, p => At(area, p), keepInside: true))
                    merged.Add(path with { Points = pts, Closed = closed });
        }
        foreach (var stale in _findTraces.Keys.Where(k => !wanted.Contains(k)).ToList()) _findTraces.Remove(stale);
        return merged;
    }

    List<RawPath> TraceCloseUp(GuideRegion find, TraceSettings s, bool useEdges, bool useLineModel, CancellationToken cancel)
    {
        var size = _prepared!.Size();
        var roi = Inflate(find.Bounds(size), size);
        if (roi.Width < 4 || roi.Height < 4) return new List<RawPath>();
        var scale = Math.Clamp(1400.0 / Math.Max(roi.Width, roi.Height), 1, 4);
        var upSize = new OpenCvSharp.Size(Math.Max(8, (int)Math.Round(roi.Width * scale)), Math.Max(8, (int)Math.Round(roi.Height * scale)));
        using var cropView = new Mat(_prepared, roi);
        using var crop = new Mat();
        Cv2.Resize(cropView, crop, upSize, 0, 0, InterpolationFlags.Cubic);
        StretchForFind(crop, find.Amount);

        var local = s.WithoutEdits();
        local.Threshold = Math.Clamp(s.Threshold + 8 * find.Amount, 0, 100);
        local.Detail = Math.Clamp(s.Detail + 5 * find.Amount, 0, 100);

        var edges = useEdges ? AiModels.Edges(_models, crop) : null;
        using var lineInk = useLineModel ? AiModels.LineArt(_models, crop) : null;
        try
        {
            cancel.ThrowIfCancellationRequested();
            using var detected = Detector.Detect(new Signals
            {
                Prepared = crop, EdgeFused = edges?.Fused, EdgeAverage = edges?.Average, LineInk = lineInk,
                Scale = Detector.PixelScale(_prepared) * scale
            }, local);
            // The full picture's feature-size rules, applied to the enlarged crop: small things here now pass them.
            using var cleaned = Cleanup.Apply(detected, local, reference: size);
            var raw = Vectorizer.Extract(cleaned, local, cancel, reference: size);
            return raw.Select(r => r with { Points = r.Points.Select(p => new Vec2(p.X / scale + roi.X, p.Y / scale + roi.Y)).ToList() }).ToList();
        }
        finally
        {
            if (edges is { } e) { e.Fused.Dispose(); e.Average.Dispose(); }
        }
    }

    /// <summary>
    /// "More lines here": spreads the close-up's own range of tones so faint lines stand out to the detectors. Each
    /// step of Amount allows twice the contrast gain (+1 = 2x .. +6 = 64x). Only the detectors see this; the picture
    /// shown and the rest of the trace are untouched.
    /// </summary>
    static void StretchForFind(Mat bgr, int amount)
    {
        if (amount <= 0) return;
        using var gray = new Mat();
        Cv2.CvtColor(bgr, gray, ColorConversionCodes.BGR2GRAY);
        using var small = new Mat();
        Cv2.Resize(gray, small, new OpenCvSharp.Size(Math.Max(1, gray.Cols / 2), Math.Max(1, gray.Rows / 2)), 0, 0, InterpolationFlags.Area);
        small.ConvertTo(small, MatType.CV_32F);
        var lo = Ai.AiModels.Percentile(small, 0.01);
        var hi = Ai.AiModels.Percentile(small, 0.99);
        var gain = Math.Clamp(220.0 / Math.Max(1, hi - lo), 1, Math.Pow(2, amount));
        if (gain <= 1.01) return;
        var mid = (lo + hi) / 2;
        bgr.ConvertTo(bgr, -1, gain, 127.5 - mid * gain);
    }

    /// <summary>Erase areas remove lines (line modes); deleted lines (Suppress) are dropped whole in every mode.</summary>
    List<RawPath> ApplyEraseAndSuppress(List<RawPath> raw, TraceSettings s, bool strokes)
    {
        var size = _prepared!.Size();
        var erases = strokes ? s.Regions.Where(r => r.Kind == RegionKind.Erase).ToList() : new List<GuideRegion>();
        var suppress = s.Regions.Where(r => r.Kind == RegionKind.Suppress).ToList();
        if (erases.Count == 0 && suppress.Count == 0) return raw;

        using var eraseMask = new Mat(size, MatType.CV_8U, Scalar.All(0));
        foreach (var r in erases) r.Draw(eraseMask, Scalar.All(255));
        using var suppressMask = new Mat(size, MatType.CV_8U, Scalar.All(0));
        foreach (var r in suppress) r.Draw(suppressMask, Scalar.All(255));
        var minLength = Math.Max(4, Math.Sqrt(Cleanup.MinFeatureArea(s.Detail, size.Width, size.Height)));

        var result = new List<RawPath>();
        foreach (var path in raw)
        {
            // A deleted line: drop the whole path when most of it lies along what was deleted.
            if (suppress.Count > 0 && EditGeometry.FractionInside(path.Points, p => At(suppressMask, p)) >= 0.6) continue;
            if (erases.Count == 0) { result.Add(path); continue; }
            foreach (var (pts, closed) in EditGeometry.Split(path.Points, path.Closed, p => At(eraseMask, p), keepInside: false))
                if (closed || Geometry2D.PolylineLength(pts, false) >= minLength) result.Add(path with { Points = pts, Closed = closed });
        }
        return result;
    }

    static bool At(Mat mask, Vec2 p)
    {
        var x = (int)p.X;
        var y = (int)p.Y;
        return x >= 0 && y >= 0 && x < mask.Cols && y < mask.Rows && mask.At<byte>(y, x) != 0;
    }

    static Rect Inflate(Rect r, OpenCvSharp.Size size)
    {
        if (r.Width <= 0 || r.Height <= 0) return r;
        var m = Math.Max(8, (int)(Math.Max(r.Width, r.Height) * 0.08));
        var x0 = Math.Max(0, r.X - m);
        var y0 = Math.Max(0, r.Y - m);
        var x1 = Math.Min(size.Width, r.Right + m);
        var y1 = Math.Min(size.Height, r.Bottom + m);
        return new Rect(x0, y0, x1 - x0, y1 - y0);
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
            _findTraces.Clear();
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
        _image?.Dispose();
        _image = null;
    }
}
