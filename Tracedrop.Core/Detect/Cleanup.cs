using OpenCvSharp;

namespace Tracedrop.Core.Detect;

/// <summary>Clean-up between detection and vectorising: close gaps, drop specks, fill holes, keep / pick shapes, offset.</summary>
public static class Cleanup
{
    /// <summary>
    /// Smallest kept feature, in pixels of area, for a picture of <paramref name="width"/>×<paramref name="height"/>.
    /// Detail 0 keeps only features above ~0.3% of the picture; detail 100 keeps anything above a few pixels.
    /// </summary>
    public static double MinFeatureArea(double detail, int width, int height)
    {
        var area = (double)width * height;
        var coarse = Math.Log(Math.Max(16, area * 0.003));
        var fine = Math.Log(3);
        return Math.Exp(coarse + (fine - coarse) * Math.Clamp(detail / 100.0, 0, 1));
    }

    public static Detection Apply(Detection raw, TraceSettings s)
    {
        return raw.IsStrokes ? CleanStrokes(raw.Strokes!, s) : CleanRegions(raw.Regions, s);
    }

    /// <summary>Pixel bounding box of everything detected, or null when nothing was.</summary>
    public static Rect? Extents(Detection d)
    {
        Rect? box = null;
        foreach (var mask in d.IsStrokes ? new[] { d.Strokes! } : d.Regions.Select(r => r.Mask).ToArray())
        {
            using var points = new Mat();
            Cv2.FindNonZero(mask, points);
            if (points.Empty()) continue;
            var r = Cv2.BoundingRect(points);
            box = box is { } b ? b | r : r;
        }
        return box;
    }

    /// <summary>Grows (+) or shrinks (-) every region by <paramref name="pixels"/>; strokes are left alone. Returns a new detection.</summary>
    public static Detection Offset(Detection d, double pixels)
    {
        if (d.IsStrokes) return Detection.FromStrokes(d.Strokes!.Clone());
        var layers = d.Regions.Select(r =>
        {
            var mask = r.Mask.Clone();
            OffsetMask(mask, pixels);
            return new RegionLayer(r.Name, mask, r.Color);
        }).ToList();
        return Detection.FromRegions(layers);
    }

    static Detection CleanRegions(List<RegionLayer> layers, TraceSettings s)
    {
        var cleaned = new List<RegionLayer>();
        foreach (var layer in layers)
        {
            var mask = layer.Mask.Clone();
            var scale = Math.Max(0.5, Detector.PixelScale(mask));
            var minArea = MinFeatureArea(s.Detail, mask.Cols, mask.Rows);

            if (s.CloseGaps > 0.5)
            {
                var r = Math.Max(1, (int)Math.Round(s.CloseGaps * scale / 2));
                using var kernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(2 * r + 1, 2 * r + 1));
                Cv2.MorphologyEx(mask, mask, MorphTypes.Close, kernel);
            }

            RemoveSmallComponents(mask, minArea);           // specks
            using (var inverted = new Mat())                 // pin-holes: same rule applied to the background
            {
                Cv2.BitwiseNot(mask, inverted);
                RemoveSmallComponents(inverted, minArea, keepBorderTouching: true);
                Cv2.BitwiseNot(inverted, mask);
            }

            if (s.FillHoles) FillHoles(mask);
            ApplyPicks(mask, s.Picks);
            if (s.Keep == KeepShapes.Largest) KeepLargest(mask);
            cleaned.Add(new RegionLayer(layer.Name, mask, layer.Color));
        }
        return Detection.FromRegions(cleaned);
    }

    static Detection CleanStrokes(Mat strokes, TraceSettings s)
    {
        var mask = strokes.Clone();
        var scale = Math.Max(0.5, Detector.PixelScale(mask));
        if (s.CloseGaps > 0.5)
        {
            var r = Math.Max(1, (int)Math.Round(s.CloseGaps * scale / 2));
            using var kernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(2 * r + 1, 2 * r + 1));
            Cv2.MorphologyEx(mask, mask, MorphTypes.Close, kernel);
        }
        // Strokes are thin, so their "area" threshold is smaller than for filled regions.
        RemoveSmallComponents(mask, MinFeatureArea(s.Detail, mask.Cols, mask.Rows) * 0.35);
        ApplyPicks(mask, s.Picks, radius: (int)Math.Round(12 * scale));
        if (s.Keep == KeepShapes.Largest) KeepLargest(mask);
        return Detection.FromStrokes(mask);
    }

    /// <summary>Drops 8-connected components smaller than <paramref name="minArea"/>.</summary>
    internal static void RemoveSmallComponents(Mat mask, double minArea, bool keepBorderTouching = false)
    {
        using var labels = new Mat();
        using var stats = new Mat();
        using var centroids = new Mat();
        var count = Cv2.ConnectedComponentsWithStats(mask, labels, stats, centroids, PixelConnectivity.Connectivity8, MatType.CV_32S);
        if (count <= 1) return;
        var drop = new bool[count];
        var any = false;
        for (var i = 1; i < count; i++)
        {
            var area = stats.At<int>(i, (int)ConnectedComponentsTypes.Area);
            if (area >= minArea) continue;
            if (keepBorderTouching)
            {
                var x = stats.At<int>(i, (int)ConnectedComponentsTypes.Left);
                var y = stats.At<int>(i, (int)ConnectedComponentsTypes.Top);
                var w = stats.At<int>(i, (int)ConnectedComponentsTypes.Width);
                var h = stats.At<int>(i, (int)ConnectedComponentsTypes.Height);
                if (x == 0 || y == 0 || x + w >= mask.Cols || y + h >= mask.Rows) continue;
            }
            drop[i] = true;
            any = true;
        }
        if (!any) return;
        ClearLabels(mask, labels, drop);
    }

    static void ClearLabels(Mat mask, Mat labels, bool[] drop)
    {
        labels.GetArray(out int[] ids);
        mask.GetArray(out byte[] pixels);
        for (var i = 0; i < ids.Length; i++) if (drop[ids[i]]) pixels[i] = 0;
        mask.SetArray(pixels);
    }

    internal static void FillHoles(Mat mask)
    {
        // Flood the background from a 1-pixel frame; whatever it can't reach is a hole.
        using var padded = new Mat();
        Cv2.CopyMakeBorder(mask, padded, 1, 1, 1, 1, BorderTypes.Constant, Scalar.All(0));
        using var flood = padded.Clone();
        Cv2.FloodFill(flood, new Point(0, 0), Scalar.All(255));
        using var holes = new Mat();
        Cv2.BitwiseNot(flood, holes);
        Cv2.BitwiseOr(padded, holes, padded);
        using var inner = padded[new Rect(1, 1, mask.Cols, mask.Rows)];
        inner.CopyTo(mask);
    }

    static void KeepLargest(Mat mask)
    {
        using var labels = new Mat();
        using var stats = new Mat();
        using var centroids = new Mat();
        var count = Cv2.ConnectedComponentsWithStats(mask, labels, stats, centroids, PixelConnectivity.Connectivity8, MatType.CV_32S);
        if (count <= 2) return;
        var largest = 1;
        for (var i = 2; i < count; i++)
            if (stats.At<int>(i, (int)ConnectedComponentsTypes.Area) > stats.At<int>(largest, (int)ConnectedComponentsTypes.Area)) largest = i;
        var drop = Enumerable.Range(0, count).Select(i => i != 0 && i != largest).ToArray();
        ClearLabels(mask, labels, drop);
    }

    /// <summary>
    /// Clicked points: components under a "drop" click are removed; if there is any "keep" click, only the
    /// components under keep clicks survive. A click within <paramref name="radius"/> px of a component counts.
    /// </summary>
    internal static void ApplyPicks(Mat mask, IReadOnlyList<PickPoint> picks, int radius = 0)
    {
        if (picks.Count == 0) return;
        using var labels = new Mat();
        var count = Cv2.ConnectedComponents(mask, labels, PixelConnectivity.Connectivity8, MatType.CV_32S);
        if (count <= 1) return;

        int LabelNear(PickPoint p)
        {
            var cx = (int)Math.Round(p.X * (mask.Cols - 1));
            var cy = (int)Math.Round(p.Y * (mask.Rows - 1));
            var best = 0;
            var bestD = int.MaxValue;
            for (var dy = -radius; dy <= radius; dy++)
            {
                for (var dx = -radius; dx <= radius; dx++)
                {
                    int x = cx + dx, y = cy + dy;
                    if (x < 0 || y < 0 || x >= mask.Cols || y >= mask.Rows) continue;
                    var id = labels.At<int>(y, x);
                    var d = dx * dx + dy * dy;
                    if (id > 0 && d < bestD) { best = id; bestD = d; }
                }
            }
            return best;
        }

        var keepIds = picks.Where(p => p.Keep).Select(LabelNear).Where(id => id > 0).ToHashSet();
        var dropIds = picks.Where(p => !p.Keep).Select(LabelNear).Where(id => id > 0).ToHashSet();
        var anyKeep = picks.Any(p => p.Keep);
        var drop = new bool[count];
        for (var i = 1; i < count; i++) drop[i] = dropIds.Contains(i) || (anyKeep && !keepIds.Contains(i));
        ClearLabels(mask, labels, drop);

        // A "drop" click inside a hole (background enclosed by a shape) fills that hole.
        if (radius == 0) FillPickedHoles(mask, picks.Where(p => !p.Keep).ToList());
    }

    static void FillPickedHoles(Mat mask, List<PickPoint> drops)
    {
        if (drops.Count == 0) return;
        using var background = new Mat();
        Cv2.BitwiseNot(mask, background);
        using var labels = new Mat();
        using var stats = new Mat();
        using var centroids = new Mat();
        // 4-connectivity for the background, so holes touching a shape diagonally don't leak into the outside.
        var count = Cv2.ConnectedComponentsWithStats(background, labels, stats, centroids, PixelConnectivity.Connectivity4, MatType.CV_32S);
        var fill = new bool[count];
        foreach (var p in drops)
        {
            var x = (int)Math.Round(p.X * (mask.Cols - 1));
            var y = (int)Math.Round(p.Y * (mask.Rows - 1));
            if (x < 0 || y < 0 || x >= mask.Cols || y >= mask.Rows) continue;
            var id = labels.At<int>(y, x);
            if (id <= 0) continue;
            var left = stats.At<int>(id, (int)ConnectedComponentsTypes.Left);
            var top = stats.At<int>(id, (int)ConnectedComponentsTypes.Top);
            var w = stats.At<int>(id, (int)ConnectedComponentsTypes.Width);
            var h = stats.At<int>(id, (int)ConnectedComponentsTypes.Height);
            var touchesBorder = left == 0 || top == 0 || left + w >= mask.Cols || top + h >= mask.Rows;
            if (!touchesBorder) fill[id] = true;
        }
        if (!fill.Any(f => f)) return;
        labels.GetArray(out int[] ids);
        mask.GetArray(out byte[] pixels);
        for (var i = 0; i < ids.Length; i++) if (fill[ids[i]]) pixels[i] = 255;
        mask.SetArray(pixels);
    }

    /// <summary>Grows (positive) or shrinks (negative) the mask by an exact distance in pixels.</summary>
    internal static void OffsetMask(Mat mask, double pixels)
    {
        if (Math.Abs(pixels) < 0.25) return;
        using var source = new Mat();
        if (pixels > 0) Cv2.BitwiseNot(mask, source); else mask.CopyTo(source);
        using var distance = new Mat();
        Cv2.DistanceTransform(source, distance, DistanceTypes.L2, DistanceTransformMasks.Precise);
        using var within = new Mat();
        Cv2.Threshold(distance, within, Math.Abs(pixels), 255, ThresholdTypes.BinaryInv);
        using var within8 = new Mat();
        within.ConvertTo(within8, MatType.CV_8U);
        if (pixels > 0) Cv2.BitwiseOr(mask, within8, mask);         // background close to the shape joins it
        else
        {
            using var keep = new Mat();
            Cv2.BitwiseNot(within8, keep);                            // shape close to the background leaves it
            Cv2.BitwiseAnd(mask, keep, mask);
        }
    }
}
