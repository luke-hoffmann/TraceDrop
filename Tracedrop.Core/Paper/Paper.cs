using OpenCvSharp;

namespace Tracedrop.Core.Paper;

/// <summary>The sheet of paper under the tools, used as the scale reference.</summary>
public enum PaperSize
{
    /// <summary>Letter or A4, told apart by the sheet's proportions.</summary>
    Auto,
    Letter,
    A4,
    Legal,
    A3,
    Tabloid
}

public static class Papers
{
    /// <summary>Short and long side in millimetres.</summary>
    public static (double Short, double Long) Millimetres(PaperSize size) => size switch
    {
        PaperSize.A4 => (210, 297),
        PaperSize.Legal => (215.9, 355.6),
        PaperSize.A3 => (297, 420),
        PaperSize.Tabloid => (279.4, 431.8),
        _ => (215.9, 279.4)
    };

    public static string Name(PaperSize size) => size switch
    {
        PaperSize.Auto => "Letter or A4",
        PaperSize.Letter => "Letter",
        PaperSize.A4 => "A4",
        PaperSize.Legal => "Legal",
        PaperSize.A3 => "A3",
        PaperSize.Tabloid => "Tabloid",
        _ => size.ToString()
    };

    /// <summary>Auto: Letter (1.29) or A4 (1.41), whichever the sheet's long/short ratio in the photo is closer to.</summary>
    public static PaperSize Resolve(PaperSize size, Point2f[] corners)
    {
        if (size != PaperSize.Auto) return size;
        var (shortSide, longSide) = Sides(corners);
        var ratio = longSide / Math.Max(1e-6, shortSide);
        return Math.Abs(ratio - 279.4 / 215.9) <= Math.Abs(ratio - 297.0 / 210) ? PaperSize.Letter : PaperSize.A4;
    }

    static (double Short, double Long) Sides(Point2f[] c)
    {
        double D(Point2f a, Point2f b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
        var horizontal = (D(c[0], c[1]) + D(c[3], c[2])) / 2;
        var vertical = (D(c[0], c[3]) + D(c[1], c[2])) / 2;
        return (Math.Min(horizontal, vertical), Math.Max(horizontal, vertical));
    }

    /// <summary>True when the sheet lies with its long side left-right in the photo.</summary>
    public static bool IsLandscape(Point2f[] c)
    {
        double D(Point2f a, Point2f b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
        return D(c[0], c[1]) + D(c[3], c[2]) > D(c[0], c[3]) + D(c[1], c[2]);
    }
}

/// <summary>Finds the sheet of paper in a photo.</summary>
public static class PaperDetector
{
    /// <summary>
    /// The sheet's four corners in source pixels, ordered top-left, top-right, bottom-right, bottom-left as seen in
    /// the photo, or null when there is no clear sheet. The sheet is the largest bright, unsaturated area; tools on
    /// it (or across its edges) are fine. Corners are refined against the full-size picture's edges.
    /// </summary>
    public static Point2f[]? Detect(Mat bgr)
    {
        var scale = Math.Min(1.0, 1000.0 / Math.Max(bgr.Cols, bgr.Rows));
        using var small = new Mat();
        Cv2.Resize(bgr, small, new Size(Math.Max(1, (int)(bgr.Cols * scale)), Math.Max(1, (int)(bgr.Rows * scale))), 0, 0, InterpolationFlags.Area);
        using var hsv = new Mat();
        Cv2.CvtColor(small, hsv, ColorConversionCodes.BGR2HSV);
        var channels = Cv2.Split(hsv);
        using var sat = channels[1];
        using var val = channels[2];
        channels[0].Dispose();

        // Paper: bright (above the picture's own Otsu split of brightness) and nearly colourless.
        using var bright = new Mat();
        Cv2.GaussianBlur(val, val, new Size(5, 5), 0);
        Cv2.Threshold(val, bright, 0, 255, ThresholdTypes.Binary | ThresholdTypes.Otsu);
        using var pale = new Mat();
        Cv2.Threshold(sat, pale, 70, 255, ThresholdTypes.BinaryInv);
        using var mask = new Mat();
        Cv2.BitwiseAnd(bright, pale, mask);
        using (var open = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(5, 5)))
            Cv2.MorphologyEx(mask, mask, MorphTypes.Open, open);
        using (var close = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(15, 15)))
            Cv2.MorphologyEx(mask, mask, MorphTypes.Close, close);

        Cv2.FindContours(mask, out var contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
        var area = small.Cols * (double)small.Rows;
        var best = contours.Select(c => (Contour: c, Area: Cv2.ContourArea(c))).Where(c => c.Area > area * 0.04).OrderByDescending(c => c.Area).FirstOrDefault();
        if (best.Contour is null) return null;

        // Tools across the sheet's edge notch its outline: the convex hull closes the notches.
        var hull = Cv2.ConvexHull(best.Contour);
        var quad = FourCorners(hull);
        if (quad is null) return null;
        // A sheet fills most of its own quadrilateral; a random bright blob doesn't.
        var quadArea = Math.Abs(Cv2.ContourArea(quad.Select(p => new Point2f(p.X, p.Y))));
        if (quadArea <= 0 || best.Area / quadArea < 0.6) return null;

        var corners = Order(quad.Select(p => new Point2f((float)(p.X / scale), (float)(p.Y / scale))).ToArray());
        return Refine(bgr, corners, 1 / scale);
    }

    /// <summary>Four corners of a convex hull: the polygon approximation that lands on four points.</summary>
    static Point[]? FourCorners(Point[] hull)
    {
        var perimeter = Cv2.ArcLength(hull, true);
        for (var k = 0.01; k <= 0.12; k += 0.005)
        {
            var approx = Cv2.ApproxPolyDP(hull, k * perimeter, true);
            if (approx.Length == 4 && Cv2.IsContourConvex(approx)) return approx;
            if (approx.Length < 4) break;
        }
        // Fallback: the hull points furthest along the four diagonals.
        if (hull.Length < 4) return null;
        return new[]
        {
            hull.MinBy(p => p.X + p.Y), hull.MaxBy(p => p.X - p.Y), hull.MaxBy(p => p.X + p.Y), hull.MinBy(p => p.X - p.Y)
        };
    }

    /// <summary>Orders four points top-left, top-right, bottom-right, bottom-left (clockwise on screen).</summary>
    public static Point2f[] Order(Point2f[] p)
    {
        var cx = p.Average(q => q.X);
        var cy = p.Average(q => q.Y);
        var sorted = p.OrderBy(q => Math.Atan2(q.Y - cy, q.X - cx)).ToArray(); // clockwise from the left on screen
        // Start at the corner nearest the top-left of the picture.
        var start = Array.IndexOf(sorted, sorted.MinBy(q => q.X + q.Y));
        return Enumerable.Range(0, 4).Select(i => sorted[(start + i) % 4]).ToArray();
    }

    /// <summary>
    /// Fits each side of the sheet to the strongest brightness step near it in the full-size picture (robust line
    /// fit, ignoring where tools cover the edge), then intersects the sides: sub-pixel corners.
    /// </summary>
    static Point2f[] Refine(Mat bgr, Point2f[] corners, double searchScale)
    {
        using var gray = new Mat();
        Cv2.CvtColor(bgr, gray, ColorConversionCodes.BGR2GRAY);
        Cv2.GaussianBlur(gray, gray, new Size(0, 0), Math.Max(1.0, searchScale * 0.6));
        var reach = Math.Max(6, searchScale * 4);
        var lines = new (Point2d Point, Point2d Dir)?[4];
        for (var side = 0; side < 4; side++)
        {
            var a = corners[side];
            var b = corners[(side + 1) % 4];
            var dir = new Point2d(b.X - a.X, b.Y - a.Y);
            var length = Math.Sqrt(dir.X * dir.X + dir.Y * dir.Y);
            if (length < 10) return corners;
            dir = new Point2d(dir.X / length, dir.Y / length);
            var normal = new Point2d(-dir.Y, dir.X);
            var samples = new List<Point2d>();
            const int count = 60;
            for (var i = 3; i < count - 3; i++)
            {
                var t = i / (double)count;
                var p = new Point2d(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);
                // Strongest step along the normal.
                var bestStep = 0.0;
                var bestOffset = double.NaN;
                for (var d = -reach; d <= reach; d += 0.5)
                {
                    var v1 = Sample(gray, p.X + normal.X * (d - 1), p.Y + normal.Y * (d - 1));
                    var v2 = Sample(gray, p.X + normal.X * (d + 1), p.Y + normal.Y * (d + 1));
                    if (double.IsNaN(v1) || double.IsNaN(v2)) continue;
                    var step = Math.Abs(v2 - v1);
                    if (step > bestStep) { bestStep = step; bestOffset = d; }
                }
                if (bestStep >= 12 && !double.IsNaN(bestOffset)) samples.Add(new Point2d(p.X + normal.X * bestOffset, p.Y + normal.Y * bestOffset));
            }
            lines[side] = FitLine(samples);
        }
        if (lines.Any(l => l is null)) return corners;
        var refined = new Point2f[4];
        for (var i = 0; i < 4; i++)
        {
            // Corner i is where side i-1 (into it) meets side i (out of it).
            var p = Intersect(lines[(i + 3) % 4]!.Value, lines[i]!.Value);
            if (p is null) return corners;
            var moved = Math.Sqrt(Math.Pow(p.Value.X - corners[i].X, 2) + Math.Pow(p.Value.Y - corners[i].Y, 2));
            if (moved > reach * 2) return corners; // the fit wandered off: keep the coarse corners
            refined[i] = new Point2f((float)p.Value.X, (float)p.Value.Y);
        }
        return refined;
    }

    static double Sample(Mat gray, double x, double y)
    {
        var ix = (int)Math.Floor(x);
        var iy = (int)Math.Floor(y);
        if (ix < 0 || iy < 0 || ix + 1 >= gray.Cols || iy + 1 >= gray.Rows) return double.NaN;
        var fx = x - ix;
        var fy = y - iy;
        double V(int xx, int yy) => gray.At<byte>(yy, xx);
        return V(ix, iy) * (1 - fx) * (1 - fy) + V(ix + 1, iy) * fx * (1 - fy) + V(ix, iy + 1) * (1 - fx) * fy + V(ix + 1, iy + 1) * fx * fy;
    }

    /// <summary>Total-least-squares line through the points, dropping outliers (tools over the edge) twice.</summary>
    static (Point2d Point, Point2d Dir)? FitLine(List<Point2d> points)
    {
        for (var round = 0; round < 3; round++)
        {
            if (points.Count < 8) return null;
            var mx = points.Average(p => p.X);
            var my = points.Average(p => p.Y);
            double sxx = 0, sxy = 0, syy = 0;
            foreach (var p in points)
            {
                sxx += (p.X - mx) * (p.X - mx);
                sxy += (p.X - mx) * (p.Y - my);
                syy += (p.Y - my) * (p.Y - my);
            }
            var angle = 0.5 * Math.Atan2(2 * sxy, sxx - syy);
            var dir = new Point2d(Math.Cos(angle), Math.Sin(angle));
            if (round == 2) return (new Point2d(mx, my), dir);
            var residuals = points.Select(p => Math.Abs(-(p.X - mx) * dir.Y + (p.Y - my) * dir.X)).ToList();
            var median = residuals.OrderBy(r => r).ElementAt(residuals.Count / 2);
            var limit = Math.Max(1.0, 2.5 * median);
            points = points.Where((_, i) => residuals[i] <= limit).ToList();
        }
        return null;
    }

    static Point2d? Intersect((Point2d Point, Point2d Dir) a, (Point2d Point, Point2d Dir) b)
    {
        var cross = a.Dir.X * b.Dir.Y - a.Dir.Y * b.Dir.X;
        if (Math.Abs(cross) < 1e-6) return null;
        var dx = b.Point.X - a.Point.X;
        var dy = b.Point.Y - a.Point.Y;
        var t = (dx * b.Dir.Y - dy * b.Dir.X) / cross;
        return new Point2d(a.Point.X + a.Dir.X * t, a.Point.Y + a.Dir.Y * t);
    }
}

/// <summary>The photo seen straight down at a known scale, with the sheet axis-aligned.</summary>
public sealed class Rectified : IDisposable
{
    public required Mat Image { get; init; }
    public Mat? Alpha { get; init; }
    /// <summary>Millimetres per pixel of <see cref="Image"/>.</summary>
    public required double MillimetresPerPixel { get; init; }
    /// <summary>Where the sheet is in <see cref="Image"/> (pixels).</summary>
    public required Rect2d PaperRect { get; init; }

    public void Dispose()
    {
        Image.Dispose();
        Alpha?.Dispose();
    }
}

public static class Rectifier
{
    /// <summary>
    /// Warps the photo so the sheet is a flat, axis-aligned rectangle at a known scale. The area around the sheet
    /// is kept (up to half a sheet on every side, where the photo reaches) since tools may stick out over its edges.
    /// The longest side of the result is <paramref name="maxSide"/> pixels.
    /// </summary>
    public static Rectified Rectify(Mat bgr, Mat? alpha, Point2f[] corners, PaperSize size, int maxSide)
    {
        var resolved = Papers.Resolve(size, corners);
        var (shortSide, longSide) = Papers.Millimetres(resolved);
        var landscape = Papers.IsLandscape(corners);
        var paperW = landscape ? longSide : shortSide;
        var paperH = landscape ? shortSide : longSide;

        // Source pixels → millimetres with the sheet's top-left corner at (0, 0).
        var toMm = Cv2.GetPerspectiveTransform(corners, new[]
        {
            new Point2f(0, 0), new Point2f((float)paperW, 0), new Point2f((float)paperW, (float)paperH), new Point2f(0, (float)paperH)
        });

        // The canvas: the sheet plus half a sheet around it, trimmed to where the photo actually reaches.
        double x0 = -paperW * 0.5, y0 = -paperH * 0.5, x1 = paperW * 1.5, y1 = paperH * 1.5;
        var imageCorners = new[] { new Point2f(0, 0), new Point2f(bgr.Cols, 0), new Point2f(bgr.Cols, bgr.Rows), new Point2f(0, bgr.Rows) };
        var mapped = new List<Point2d>();
        foreach (var c in imageCorners)
        {
            var w = toMm.At<double>(2, 0) * c.X + toMm.At<double>(2, 1) * c.Y + toMm.At<double>(2, 2);
            if (w <= 1e-9) { mapped.Clear(); break; } // the photo reaches the horizon: keep the full margin
            mapped.Add(new Point2d((toMm.At<double>(0, 0) * c.X + toMm.At<double>(0, 1) * c.Y + toMm.At<double>(0, 2)) / w,
                (toMm.At<double>(1, 0) * c.X + toMm.At<double>(1, 1) * c.Y + toMm.At<double>(1, 2)) / w));
        }
        if (mapped.Count == 4)
        {
            x0 = Math.Max(x0, Math.Min(0, mapped.Min(p => p.X)));
            y0 = Math.Max(y0, Math.Min(0, mapped.Min(p => p.Y)));
            x1 = Math.Min(x1, Math.Max(paperW, mapped.Max(p => p.X)));
            y1 = Math.Min(y1, Math.Max(paperH, mapped.Max(p => p.Y)));
        }

        var pixelsPerMm = maxSide / Math.Max(x1 - x0, y1 - y0);
        var width = Math.Max(16, (int)Math.Round((x1 - x0) * pixelsPerMm));
        var height = Math.Max(16, (int)Math.Round((y1 - y0) * pixelsPerMm));
        using var place = Mat.Eye(3, 3, MatType.CV_64F).ToMat();
        place.Set(0, 0, pixelsPerMm);
        place.Set(1, 1, pixelsPerMm);
        place.Set(0, 2, -x0 * pixelsPerMm);
        place.Set(1, 2, -y0 * pixelsPerMm);
        using var transform = place * toMm;
        using var homography = transform.ToMat();

        var image = new Mat();
        Cv2.WarpPerspective(bgr, image, homography, new Size(width, height), InterpolationFlags.Linear, BorderTypes.Replicate);
        Mat? warpedAlpha = null;
        if (alpha is not null)
        {
            warpedAlpha = new Mat();
            Cv2.WarpPerspective(alpha, warpedAlpha, homography, new Size(width, height), InterpolationFlags.Linear, BorderTypes.Constant, Scalar.All(0));
        }
        toMm.Dispose();
        return new Rectified
        {
            Image = image,
            Alpha = warpedAlpha,
            MillimetresPerPixel = 1 / pixelsPerMm,
            PaperRect = new Rect2d(-x0 * pixelsPerMm, -y0 * pixelsPerMm, paperW * pixelsPerMm, paperH * pixelsPerMm)
        };
    }
}
