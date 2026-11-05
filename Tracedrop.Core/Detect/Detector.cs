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
    /// <summary>Close-up line strength for each Focus area (stroke modes).</summary>
    public IReadOnlyList<FocusMap> Focus { get; init; } = Array.Empty<FocusMap>();
}

/// <summary>Turns the prepared picture (and model outputs) into raw regions or strokes. No clean-up happens here.</summary>
public static class Detector
{
    public static Detection Detect(Signals signals, TraceSettings s)
    {
        using var addMask = AddMask(signals.Prepared.Size(), s.Picks);
        var detection = s.Mode switch
        {
            TraceMode.Silhouette => Silhouette(signals, s),
            TraceMode.Shapes => s.Colors <= 2 ? TwoTone(signals.Prepared, s) : ColorLayers(signals.Prepared, s),
            TraceMode.Edges => Edges(signals, s, addMask),
            TraceMode.LineArt => LineArt(signals, s, addMask),
            _ => throw new ArgumentOutOfRangeException(nameof(s))
        };
        if (!detection.IsStrokes && addMask is not null) AddAreas(detection.Regions, signals, s.Picks);
        return detection;
    }

    // ---------------------------------------------------------------- "Add area" clicks

    /// <summary>Soft circles for the Add clicks (1 inside, fading out at the rim, CV_32F), or null when there are none.</summary>
    internal static Mat? AddMask(Size size, IEnumerable<PickPoint> picks)
    {
        var adds = picks.Where(p => p.Kind == PickKind.Add).ToList();
        if (adds.Count == 0) return null;
        var mask = new Mat(size, MatType.CV_32F, Scalar.All(0));
        var longest = Math.Max(size.Width, size.Height);
        var feather = 0.0;
        foreach (var p in adds)
        {
            var r = AddRadiusPixels(p, longest);
            Cv2.Circle(mask, Seed(p, size), (int)Math.Round(r), Scalar.All(1), -1, LineTypes.AntiAlias);
            feather = Math.Max(feather, r * 0.12);
        }
        Cv2.GaussianBlur(mask, mask, new Size(0, 0), Math.Max(1.5, feather));
        return mask;
    }

    static double AddRadiusPixels(PickPoint p, int longest) =>
        Math.Max(4, (p.Radius > 0 ? p.Radius : PickPoint.DefaultAddRadius) * longest);

    static Point Seed(PickPoint p, Size size) =>
        new((int)Math.Clamp(Math.Round(p.X * (size.Width - 1)), 0, size.Width - 1), (int)Math.Clamp(Math.Round(p.Y * (size.Height - 1)), 0, size.Height - 1));

    /// <summary>Looks harder inside the Add circles: strength is raised up to 3.3x so faint lines there pass the threshold.</summary>
    static void BoostInside(Mat strength, Mat addMask)
    {
        using var denominator = new Mat();
        addMask.ConvertTo(denominator, MatType.CV_32F, -0.7, 1.0);
        Cv2.Divide(strength, denominator, strength);
        Cv2.Threshold(strength, strength, 1, 1, ThresholdTypes.Trunc);
    }

    /// <summary>
    /// Outline and Shapes modes: each Add click adds the similar-coloured area under it (kept inside its circle),
    /// plus, in Outline mode, anything the subject model gave even a little confidence to inside the circle.
    /// </summary>
    static void AddAreas(List<RegionLayer> layers, Signals signals, IEnumerable<PickPoint> picks)
    {
        if (layers.Count == 0) return;
        var prepared = signals.Prepared;
        var size = prepared.Size();
        var longest = Math.Max(size.Width, size.Height);
        using var lab = new Mat();
        Cv2.CvtColor(prepared, lab, ColorConversionCodes.BGR2Lab);
        foreach (var p in picks.Where(p => p.Kind == PickKind.Add))
        {
            var seed = Seed(p, size);
            var r = (int)Math.Round(AddRadiusPixels(p, longest));
            using var circle = new Mat(size, MatType.CV_8U, Scalar.All(0));
            Cv2.Circle(circle, seed, r, Scalar.All(255), -1);

            // Flood fill blocked everywhere outside the circle (the fill mask is 2 px larger than the picture).
            using var fill = new Mat(size.Height + 2, size.Width + 2, MatType.CV_8U, Scalar.All(1));
            using (var inner = fill[new Rect(1, 1, size.Width, size.Height)])
                inner.SetTo(Scalar.All(0), circle);
            var flags = (FloodFillFlags)((int)FloodFillFlags.Link8 | (int)FloodFillFlags.FixedRange | (int)FloodFillFlags.MaskOnly | (255 << 8));
            Cv2.FloodFill(lab, fill, seed, Scalar.All(0), out _, new Scalar(18, 10, 10), new Scalar(18, 10, 10), flags);
            using var region = new Mat();
            using (var inner = fill[new Rect(1, 1, size.Width, size.Height)])
                Cv2.Compare(inner, Scalar.All(255), region, CmpTypes.EQ);

            if (signals.Subject is not null)
            {
                using var likely = new Mat();
                Cv2.Threshold(signals.Subject, likely, 0.2, 255, ThresholdTypes.Binary);
                using var likely8 = new Mat();
                likely.ConvertTo(likely8, MatType.CV_8U);
                Cv2.BitwiseAnd(likely8, circle, likely8);
                Cv2.BitwiseOr(region, likely8, region);
            }

            // Into the layer whose colour is closest to what was clicked (the only layer, for one-layer modes).
            var target = layers[0];
            if (layers.Count > 1)
            {
                var c = prepared.At<Vec3b>(seed.Y, seed.X);
                target = layers.OrderBy(l => Math.Pow(l.Color.Val0 - c.Item0, 2) + Math.Pow(l.Color.Val1 - c.Item1, 2) + Math.Pow(l.Color.Val2 - c.Item2, 2)).First();
            }
            Cv2.BitwiseOr(target.Mask, region, target.Mask);
        }
    }

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
        int rows = lab.Rows, cols = lab.Cols;
        var band = Math.Max(2, Math.Min(rows, cols) / 50);
        var samples = new List<Vec3f>();
        var step = Math.Max(1, (rows + cols) / 2000);
        for (var y = 0; y < rows; y += step)
        {
            for (var x = 0; x < cols; x += step)
            {
                if (y >= band && y < rows - band && x >= band && x < cols - band) { x = cols - band - 1; continue; }
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
        int aRows = assignment.Rows, aCols = assignment.Cols;
        for (var x = 0; x < aCols; x++) { borderCounts[assignment.At<byte>(0, x)]++; borderCounts[assignment.At<byte>(aRows - 1, x)]++; }
        for (var y = 0; y < aRows; y++) { borderCounts[assignment.At<byte>(y, 0)]++; borderCounts[assignment.At<byte>(y, aCols - 1)]++; }
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

    static Detection Edges(Signals signals, TraceSettings s, Mat? addMask)
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
            if (addMask is not null)
            {
                // Inside Focus circles use the richer map (faint edges too), the close-up pass, and boost it.
                using var inside = MaskOf(addMask);
                richer.CopyTo(map, inside);
                Focus.MergeInto(map, signals.Focus);
                BoostInside(map, addMask);
            }
            return Detection.FromStrokes(Hysteresis(map, sensitivity));
        }

        using var gray = new Mat();
        Cv2.CvtColor(signals.Prepared, gray, ColorConversionCodes.BGR2GRAY);
        var high = 220 - 180 * sensitivity;
        var edges = new Mat();
        Cv2.Canny(gray, edges, high * 0.45, high, 3, true);
        if (addMask is not null)
        {
            // Inside Add circles, a much more sensitive Canny.
            using var more = new Mat();
            Cv2.Canny(gray, more, high * 0.45 * 0.35, high * 0.35, 3, true);
            using var inside = MaskOf(addMask);
            more.CopyTo(edges, inside);
            using var close = new Mat(edges.Size(), MatType.CV_32F, Scalar.All(0));
            Focus.MergeInto(close, signals.Focus);
            using var closeLines = new Mat();
            Cv2.Threshold(close, closeLines, 0.35, 255, ThresholdTypes.Binary);
            using var close8 = new Mat();
            closeLines.ConvertTo(close8, MatType.CV_8U);
            Cv2.BitwiseOr(edges, close8, edges);
        }
        // Canny lines are one pixel wide; thicken a little so gaps of a pixel join before thinning.
        using var kernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(3, 3));
        Cv2.Dilate(edges, edges, kernel);
        return Detection.FromStrokes(edges);
    }

    // ---------------------------------------------------------------- Line art

    static Detection LineArt(Signals signals, TraceSettings s, Mat? addMask)
    {
        var sensitivity = s.Threshold / 100.0;
        if (signals.LineInk is not null)
        {
            if (addMask is null) return Detection.FromStrokes(Hysteresis(signals.LineInk, sensitivity));
            // The network may leave something out entirely (eyes, say): inside Add circles also take the built-in
            // drawing, at a finer scale, and boost it all.
            using var ink = signals.LineInk.Clone();
            using var local = Xdog(signals.Prepared, Math.Min(1, s.Detail / 100.0 + 0.3));
            using var inside = MaskOf(addMask);
            using var both = new Mat();
            Cv2.Max(ink, local, both);
            both.CopyTo(ink, inside);
            Focus.MergeInto(ink, signals.Focus);
            BoostInside(ink, addMask);
            return Detection.FromStrokes(Hysteresis(ink, sensitivity));
        }

        // No line-drawing model: combine the built-in XDoG drawing (dark lines, eyes, brows) with TEED's edges
        // (contours, jaw, nose) when the edge model is there. Either alone draws a face noticeably worse.
        using var drawn = Xdog(signals.Prepared, s.Detail / 100.0);
        if (signals.EdgeFused is not null)
        {
            using var edges = new Mat();
            signals.EdgeFused.ConvertTo(edges, MatType.CV_32F, 0.85);
            Cv2.Max(drawn, edges, drawn);
        }
        if (addMask is not null)
        {
            Focus.MergeInto(drawn, signals.Focus);
            BoostInside(drawn, addMask);
        }
        return Detection.FromStrokes(Hysteresis(drawn, sensitivity));
    }

    /// <summary>8-bit mask of where a soft mask is above one half.</summary>
    static Mat MaskOf(Mat soft)
    {
        using var binary = new Mat();
        Cv2.Threshold(soft, binary, 0.5, 255, ThresholdTypes.Binary);
        var mask = new Mat();
        binary.ConvertTo(mask, MatType.CV_8U);
        return mask;
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
