using OpenCvSharp;

namespace Tracedrop.Core.Detect;

/// <summary>Model outputs and the prepared picture that detection works from. The pipeline caches these between slider moves.</summary>
public sealed class Signals
{
    public required Mat Prepared { get; init; }
    /// <summary>Subject probability 0..1 (AI, or the file's own transparency), or null.</summary>
    public Mat? Subject { get; init; }
    /// <summary>True when <see cref="Subject"/> came from the file's alpha channel.</summary>
    public bool SubjectFromAlpha { get; init; }
    public Mat? EdgeFused { get; init; }
    public Mat? EdgeAverage { get; init; }
    /// <summary>Ink 0..1 from the line-drawing network, or null to use the built-in XDoG drawing.</summary>
    public Mat? LineInk { get; init; }
}

/// <summary>Turns the prepared picture (and model outputs) into raw regions or strokes. No clean-up happens here.</summary>
public static class Detector
{
    public static Detection Detect(Signals signals, TraceSettings s) => s.Mode switch
    {
        TraceMode.Silhouette => Silhouette(signals, s),
        TraceMode.Shapes => s.Colors <= 2 ? TwoTone(signals.Prepared, s) : ColorLayers(signals.Prepared, s),
        TraceMode.Edges => Edges(signals, s),
        TraceMode.LineArt => LineArt(signals, s),
        _ => throw new ArgumentOutOfRangeException(nameof(s))
    };

    /// <summary>Scale factor for pixel-sized parameters: they are tuned for a 1600 px picture.</summary>
    internal static double PixelScale(Mat m) => Math.Max(m.Cols, m.Rows) / 1600.0;

    // ---------------------------------------------------------------- Silhouette

    static Detection Silhouette(Signals signals, TraceSettings s)
    {
        // Threshold 50 = probability 0.5; higher demands more confidence (tighter outline).
        var cut = Math.Clamp(s.Threshold / 100.0, 0.02, 0.98);
        var mask = new Mat();
        if (signals.Subject is not null)
        {
            using var binary = new Mat();
            Cv2.Threshold(signals.Subject, binary, cut, 255, ThresholdTypes.Binary);
            binary.ConvertTo(mask, MatType.CV_8U);
        }
        else
        {
            using var classic = ClassicSubject(signals.Prepared, cut);
            classic.CopyTo(mask);
        }
        return Detection.FromRegions(new List<RegionLayer> { new("OUTLINE", mask, new Scalar(40, 40, 40)) });
    }

    /// <summary>
    /// Subject without a model: how different each pixel is from the background colour sampled around the border.
    /// Works well for an object on a plain surface; shadows count as subject only when they are dark.
    /// </summary>
    internal static Mat ClassicSubject(Mat bgr, double cut)
    {
        using var lab = new Mat();
        using var smooth = new Mat();
        Cv2.GaussianBlur(bgr, smooth, new Size(0, 0), 1.5 * Math.Max(0.5, PixelScale(bgr)));
        Cv2.CvtColor(smooth, lab, ColorConversionCodes.BGR2Lab);
        lab.ConvertTo(lab, MatType.CV_32FC3);

        // Background colour = per-channel median of a thin border band.
        var band = Math.Max(2, Math.Min(lab.Rows, lab.Cols) / 50);
        var samples = new List<Vec3f>();
        var step = Math.Max(1, (lab.Rows + lab.Cols) / 2000);
        for (var y = 0; y < lab.Rows; y += step)
        {
            for (var x = 0; x < lab.Cols; x += step)
            {
                if (y >= band && y < lab.Rows - band && x >= band && x < lab.Cols - band) { x = lab.Cols - band - 1; continue; }
                samples.Add(lab.At<Vec3f>(y, x));
            }
        }
        float Median(Func<Vec3f, float> pick)
        {
            var values = samples.Select(pick).OrderBy(v => v).ToList();
            return values.Count == 0 ? 0 : values[values.Count / 2];
        }
        var background = new Scalar(Median(v => v.Item0), Median(v => v.Item1), Median(v => v.Item2));

        using var diff = new Mat();
        Cv2.Subtract(lab, background, diff);
        // Lightness differences count less than colour differences, so soft shading doesn't read as subject.
        Cv2.Multiply(diff, new Scalar(0.6, 1.4, 1.4), diff);
        using var sq = new Mat();
        Cv2.Multiply(diff, diff, sq);
        var parts = Cv2.Split(sq);
        using var distance = new Mat();
        try
        {
            Cv2.Add(parts[0], parts[1], distance);
            Cv2.Add(distance, parts[2], distance);
        }
        finally
        {
            foreach (var p in parts) p.Dispose();
        }
        Cv2.Sqrt(distance, distance);

        using var d8 = new Mat();
        Cv2.Normalize(distance, d8, 0, 255, NormTypes.MinMax, (int)MatType.CV_8U);
        using var otsuOut = new Mat();
        var otsu = Cv2.Threshold(d8, otsuOut, 0, 255, ThresholdTypes.Binary | ThresholdTypes.Otsu);
        // Threshold slider moves the cut around Otsu's choice: 50 = Otsu.
        var level = Math.Clamp(otsu * (0.4 + 1.2 * cut), 2, 253);
        var mask = new Mat();
        Cv2.Threshold(d8, mask, level, 255, ThresholdTypes.Binary);
        return mask;
    }

    // ---------------------------------------------------------------- Shapes

    static Detection TwoTone(Mat bgr, TraceSettings s)
    {
        using var gray = new Mat();
        Cv2.CvtColor(bgr, gray, ColorConversionCodes.BGR2GRAY);
        var mask = new Mat();
        if (s.AutoThreshold)
            Cv2.Threshold(gray, mask, 0, 255, ThresholdTypes.BinaryInv | ThresholdTypes.Otsu);
        else
            Cv2.Threshold(gray, mask, s.Threshold * 2.55, 255, ThresholdTypes.BinaryInv);
        return Detection.FromRegions(new List<RegionLayer> { new("SHAPES", mask, new Scalar(30, 30, 30)) });
    }

    static Detection ColorLayers(Mat bgr, TraceSettings s)
    {
        var k = Math.Clamp(s.Colors, 2, 8);
        using var lab = new Mat();
        Cv2.CvtColor(bgr, lab, ColorConversionCodes.BGR2Lab);

        // Cluster a downscaled copy (fast), then assign every pixel to its nearest centre.
        using var small = new Mat();
        var shrink = Math.Min(1.0, 400.0 / Math.Max(lab.Cols, lab.Rows));
        Cv2.Resize(lab, small, new Size(Math.Max(1, (int)(lab.Cols * shrink)), Math.Max(1, (int)(lab.Rows * shrink))), 0, 0, InterpolationFlags.Area);
        using var samples = small.Reshape(1, small.Rows * small.Cols);
        using var samplesF = new Mat();
        samples.ConvertTo(samplesF, MatType.CV_32F);
        using var labels = new Mat();
        using var centers = new Mat();
        Cv2.SetTheRNG(12345);
        Cv2.Kmeans(samplesF, k, labels, new TermCriteria(CriteriaTypes.Eps | CriteriaTypes.MaxIter, 30, 0.5), 4, KMeansFlags.PpCenters, centers);

        var centerList = new List<Vec3f>();
        for (var i = 0; i < k; i++) centerList.Add(new Vec3f(centers.At<float>(i, 0), centers.At<float>(i, 1), centers.At<float>(i, 2)));

        using var labF = new Mat();
        lab.ConvertTo(labF, MatType.CV_32FC3);
        using var best = new Mat(lab.Rows, lab.Cols, MatType.CV_32F, Scalar.All(float.MaxValue));
        using var assignment = new Mat(lab.Rows, lab.Cols, MatType.CV_8U, Scalar.All(0));
        for (var i = 0; i < k; i++)
        {
            using var diff = new Mat();
            Cv2.Subtract(labF, new Scalar(centerList[i].Item0, centerList[i].Item1, centerList[i].Item2), diff);
            using var sq = new Mat();
            Cv2.Multiply(diff, diff, sq);
            using var flat = sq.Reshape(1, sq.Rows * sq.Cols);
            using var summed = new Mat();
            Cv2.Reduce(flat, summed, ReduceDimension.Column, ReduceTypes.Sum, (int)MatType.CV_32F);
            using var distImage = summed.Reshape(1, lab.Rows);
            using var closer = new Mat();
            Cv2.Compare(distImage, best, closer, CmpTypes.LT);
            distImage.CopyTo(best, closer);
            assignment.SetTo(Scalar.All(i), closer);
        }

        // The colour that covers most of the border is the background and isn't traced.
        var borderCounts = new int[k];
        for (var x = 0; x < assignment.Cols; x++) { borderCounts[assignment.At<byte>(0, x)]++; borderCounts[assignment.At<byte>(assignment.Rows - 1, x)]++; }
        for (var y = 0; y < assignment.Rows; y++) { borderCounts[assignment.At<byte>(y, 0)]++; borderCounts[assignment.At<byte>(y, assignment.Cols - 1)]++; }
        var background = Array.IndexOf(borderCounts, borderCounts.Max());

        var layers = new List<RegionLayer>();
        var order = Enumerable.Range(0, k).Where(i => i != background).OrderBy(i => centerList[i].Item0).ToList(); // dark first
        var n = 1;
        foreach (var i in order)
        {
            var mask = new Mat();
            Cv2.Compare(assignment, Scalar.All(i), mask, CmpTypes.EQ);
            using var swatchLab = new Mat(1, 1, MatType.CV_8UC3, new Scalar(centerList[i].Item0, centerList[i].Item1, centerList[i].Item2));
            using var swatch = new Mat();
            Cv2.CvtColor(swatchLab, swatch, ColorConversionCodes.Lab2BGR);
            var c = swatch.At<Vec3b>(0, 0);
            layers.Add(new RegionLayer($"COLOR_{n++}", mask, new Scalar(c.Item0, c.Item1, c.Item2)));
        }
        return Detection.FromRegions(layers);
    }

    // ---------------------------------------------------------------- Edges

    static Detection Edges(Signals signals, TraceSettings s)
    {
        var sensitivity = s.Threshold / 100.0;
        if (s.UseAiEdges && signals.EdgeFused is not null && signals.EdgeAverage is not null)
        {
            // More detail blends in TEED's softer average map, which keeps faint edges.
            var detail = s.Detail / 100.0;
            using var richer = new Mat();
            Cv2.Max(signals.EdgeFused, signals.EdgeAverage, richer);
            using var map = new Mat();
            Cv2.AddWeighted(signals.EdgeFused, 1 - detail, richer, detail, 0, map);
            return Detection.FromStrokes(Hysteresis(map, sensitivity));
        }

        using var gray = new Mat();
        Cv2.CvtColor(signals.Prepared, gray, ColorConversionCodes.BGR2GRAY);
        var high = 220 - 180 * sensitivity;
        var edges = new Mat();
        Cv2.Canny(gray, edges, high * 0.45, high, 3, true);
        // Canny lines are one pixel wide; thicken a little so gaps of a pixel join before thinning.
        using var kernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(3, 3));
        Cv2.Dilate(edges, edges, kernel);
        return Detection.FromStrokes(edges);
    }

    // ---------------------------------------------------------------- Line art

    static Detection LineArt(Signals signals, TraceSettings s)
    {
        var sensitivity = s.Threshold / 100.0;
        if (signals.LineInk is not null)
            return Detection.FromStrokes(Hysteresis(signals.LineInk, sensitivity));

        // No line-drawing model: combine the built-in XDoG drawing (dark lines, eyes, brows) with TEED's edges
        // (contours, jaw, nose) when the edge model is there. Either alone draws a face noticeably worse.
        using var ink = Xdog(signals.Prepared, s.Detail / 100.0);
        if (signals.EdgeFused is not null)
        {
            using var edges = new Mat();
            signals.EdgeFused.ConvertTo(edges, MatType.CV_32F, 0.85);
            Cv2.Max(ink, edges, ink);
        }
        return Detection.FromStrokes(Hysteresis(ink, sensitivity));
    }

    /// <summary>
    /// eXtended Difference of Gaussians (Winnemöller et al. 2012): a classic, model-free pen drawing.
    /// Returns ink strength 0..1 (CV_32F). <paramref name="detail"/> 0..1 shrinks the drawing scale for finer lines.
    /// </summary>
    internal static Mat Xdog(Mat bgr, double detail)
    {
        using var gray = new Mat();
        Cv2.CvtColor(bgr, gray, ColorConversionCodes.BGR2GRAY);
        using var g = new Mat();
        gray.ConvertTo(g, MatType.CV_32F, 1 / 255.0);
        var sigma = (2.6 - 1.9 * detail) * Math.Max(0.5, PixelScale(bgr));
        using var g1 = new Mat();
        using var g2 = new Mat();
        Cv2.GaussianBlur(g, g1, new Size(0, 0), sigma);
        Cv2.GaussianBlur(g, g2, new Size(0, 0), sigma * 1.6);
        using var dog = new Mat();
        Cv2.AddWeighted(g1, 1.0, g2, -0.985, 0, dog); // g1 - tau*g2
        // Ink where the DoG goes negative (dark side of an edge, or a dark line); scale by a robust maximum.
        var ink = new Mat();
        Cv2.Multiply(dog, Scalar.All(-1), ink);
        Cv2.Threshold(ink, ink, 0, 0, ThresholdTypes.Tozero);
        using var small = new Mat();
        Cv2.Resize(ink, small, new Size(Math.Max(1, ink.Cols / 4), Math.Max(1, ink.Rows / 4)), 0, 0, InterpolationFlags.Area);
        var top = Math.Max(1e-4, Ai.AiModels.Percentile(small, 0.995));
        ink.ConvertTo(ink, MatType.CV_32F, 1.0 / top);
        Cv2.Threshold(ink, ink, 1, 1, ThresholdTypes.Trunc);
        return ink;
    }

    /// <summary>
    /// Two-level threshold: keeps every faint stroke that touches a strong one (like Canny), so lines stay
    /// connected without picking up isolated noise. <paramref name="sensitivity"/> 0..1, higher = more strokes.
    /// </summary>
    internal static Mat Hysteresis(Mat strength, double sensitivity)
    {
        var high = 0.80 - 0.62 * sensitivity;
        var low = high * 0.45;
        using var strong = new Mat();
        using var weak = new Mat();
        Cv2.Threshold(strength, strong, high, 255, ThresholdTypes.Binary);
        Cv2.Threshold(strength, weak, low, 255, ThresholdTypes.Binary);
        using var strong8 = new Mat();
        using var weak8 = new Mat();
        strong.ConvertTo(strong8, MatType.CV_8U);
        weak.ConvertTo(weak8, MatType.CV_8U);

        using var labels = new Mat();
        var count = Cv2.ConnectedComponents(weak8, labels, PixelConnectivity.Connectivity8, MatType.CV_32S);
        var keep = new bool[count];
        using var strongLabels = new Mat();
        labels.CopyTo(strongLabels, strong8);
        strongLabels.GetArray(out int[] ids);
        foreach (var id in ids) if (id > 0) keep[id] = true;

        labels.GetArray(out int[] all);
        var result = new Mat(labels.Rows, labels.Cols, MatType.CV_8U);
        var bytes = new byte[all.Length];
        for (var i = 0; i < all.Length; i++) bytes[i] = keep[all[i]] ? (byte)255 : (byte)0;
        result.SetArray(bytes);
        return result;
    }
}
