using OpenCvSharp;

namespace Tracedrop.Core.Detect;

/// <summary>
/// Tools on a sheet of paper: whatever differs from the paper. The paper's own shade (with its lighting fall-off)
/// is modelled as a smooth surface fitted to the sheet, so a soft shadow or a gradient across it isn't mistaken
/// for a tool, while anything darker or more colourful is.
/// </summary>
public static class PaperObjects
{
    /// <summary>
    /// Probability 0..1 (CV_32F) that a pixel is a tool, over the straightened photo. Inside the sheet: the paper
    /// contrast, combined with the AI subject mask when that found the tools rather than the sheet. Outside the
    /// sheet (a tool sticking out): the AI subject mask only, if there is one.
    /// </summary>
    public static Mat Probability(Mat bgr, Rect2d paper, Mat? ai)
    {
        var w = bgr.Cols;
        var h = bgr.Rows;
        var sigma = Math.Max(0.8, paper.Width / 1000.0);
        using var smooth = new Mat();
        Cv2.GaussianBlur(bgr, smooth, new Size(0, 0), sigma);
        using var lab8 = new Mat();
        Cv2.CvtColor(smooth, lab8, ColorConversionCodes.BGR2Lab);
        using var lab = new Mat();
        lab8.ConvertTo(lab, MatType.CV_32FC3);

        // Samples across the sheet (inset from its edges), then a robust quadratic fit per channel.
        var inset = 0.04;
        var samples = new List<(double X, double Y, Vec3f V)>();
        const int grid = 48;
        for (var gy = 0; gy < grid; gy++)
            for (var gx = 0; gx < grid; gx++)
            {
                var x = paper.X + paper.Width * (inset + (1 - 2 * inset) * (gx + 0.5) / grid);
                var y = paper.Y + paper.Height * (inset + (1 - 2 * inset) * (gy + 0.5) / grid);
                var ix = (int)Math.Clamp(x, 0, w - 1);
                var iy = (int)Math.Clamp(y, 0, h - 1);
                samples.Add((ix, iy, lab.At<Vec3f>(iy, ix)));
            }
        // Start from the brightest half (the paper, not the tools), then refit on what agrees with the fit.
        var median = samples.Select(s => s.V.Item0).OrderBy(v => v).ElementAt(samples.Count / 2);
        var keep = samples.Where(s => s.V.Item0 >= median - 5).ToList();
        double[]? fitL = null, fitA = null, fitB = null;
        for (var round = 0; round < 3 && keep.Count >= 12; round++)
        {
            fitL = Fit(keep, s => s.V.Item0, paper);
            fitA = Fit(keep, s => s.V.Item1, paper);
            fitB = Fit(keep, s => s.V.Item2, paper);
            var (l, a, b) = (fitL, fitA, fitB);
            keep = samples.Where(s =>
            {
                var dl = s.V.Item0 - Eval(l, s.X, s.Y, paper);
                var dc = Math.Sqrt(Math.Pow(s.V.Item1 - Eval(a, s.X, s.Y, paper), 2) + Math.Pow(s.V.Item2 - Eval(b, s.X, s.Y, paper), 2));
                return Math.Abs(dl) < 10 && dc < 7;
            }).ToList();
        }

        var probability = new Mat(h, w, MatType.CV_32F, Scalar.All(0));
        using var contrast = new Mat(h, w, MatType.CV_32F, Scalar.All(0));
        if (fitL is not null && fitA is not null && fitB is not null)
        {
            var x0 = (int)Math.Max(0, Math.Floor(paper.X + paper.Width * 0.006));
            var y0 = (int)Math.Max(0, Math.Floor(paper.Y + paper.Height * 0.006));
            var x1 = (int)Math.Min(w, Math.Ceiling(paper.X + paper.Width * 0.994));
            var y1 = (int)Math.Min(h, Math.Ceiling(paper.Y + paper.Height * 0.994));
            var indexer = lab.GetGenericIndexer<Vec3f>();
            var output = contrast.GetGenericIndexer<float>();
            Parallel.For(y0, y1, y =>
            {
                for (var x = x0; x < x1; x++)
                {
                    var v = indexer[y, x];
                    var dl = Eval(fitL, x, y, paper) - v.Item0;          // darker than the paper: positive
                    var da = v.Item1 - Eval(fitA, x, y, paper);
                    var db = v.Item2 - Eval(fitB, x, y, paper);
                    var chroma = Math.Sqrt(da * da + db * db);
                    // Darker counts fully, lighter (a shiny highlight) half, colour 1.8x.
                    output[y, x] = (float)Math.Max(Math.Max(dl, -dl * 0.5), chroma * 1.8);
                }
            });
            // The edge of a tool is where the picture is halfway between paper and tool: the contrast over the
            // strongest contrast nearby (at least 36, so a soft shadow on its own stays below half).
            using var nearby = new Mat();
            var r = Math.Max(3, (int)Math.Round(paper.Width / 150.0));
            using (var kernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(2 * r + 1, 2 * r + 1)))
                Cv2.Dilate(contrast, nearby, kernel);
            Cv2.Max(nearby, Scalar.All(36), nearby);
            Cv2.Divide(contrast, nearby, probability);
            Cv2.Min(probability, Scalar.All(1), probability);
            Cv2.Max(probability, Scalar.All(0), probability);
        }

        if (ai is not null)
        {
            // Did the AI find tools, or take the whole sheet for the subject?
            var roi = new Rect((int)Math.Max(0, paper.X), (int)Math.Max(0, paper.Y),
                (int)Math.Min(w - Math.Max(0, paper.X), paper.Width), (int)Math.Min(h - Math.Max(0, paper.Y), paper.Height));
            double cover = 0;
            if (roi.Width > 0 && roi.Height > 0)
            {
                using var inside = new Mat(ai, roi);
                using var on = new Mat();
                Cv2.Threshold(inside, on, 0.5, 1, ThresholdTypes.Binary);
                cover = Cv2.Mean(on).Val0;
            }
            using var paperMask = new Mat(h, w, MatType.CV_8U, Scalar.All(0));
            if (roi.Width > 0 && roi.Height > 0) paperMask[roi].SetTo(Scalar.All(255));
            using var outside = new Mat();
            Cv2.BitwiseNot(paperMask, outside);
            ai.CopyTo(probability, outside); // beyond the sheet only the AI can tell
            if (cover < 0.5)
            {
                using var both = new Mat();
                Cv2.Max(probability, ai, both);
                both.CopyTo(probability, paperMask);
            }
        }
        return probability;
    }

    // Quadratic surface c0 + c1 x + c2 y + c3 x² + c4 xy + c5 y², in coordinates normalised to the sheet.
    static double Eval(double[] c, double x, double y, Rect2d paper)
    {
        var u = (x - paper.X) / Math.Max(1, paper.Width) - 0.5;
        var v = (y - paper.Y) / Math.Max(1, paper.Height) - 0.5;
        return c[0] + c[1] * u + c[2] * v + c[3] * u * u + c[4] * u * v + c[5] * v * v;
    }

    static double[] Fit(List<(double X, double Y, Vec3f V)> samples, Func<(double X, double Y, Vec3f V), double> value, Rect2d paper)
    {
        var ata = new double[6, 6];
        var atb = new double[6];
        foreach (var s in samples)
        {
            var u = (s.X - paper.X) / Math.Max(1, paper.Width) - 0.5;
            var v = (s.Y - paper.Y) / Math.Max(1, paper.Height) - 0.5;
            var row = new[] { 1, u, v, u * u, u * v, v * v };
            var b = value(s);
            for (var i = 0; i < 6; i++)
            {
                atb[i] += row[i] * b;
                for (var j = 0; j < 6; j++) ata[i, j] += row[i] * row[j];
            }
        }
        for (var i = 0; i < 6; i++) ata[i, i] += 1e-6; // keep it solvable
        return Solve(ata, atb);
    }

    static double[] Solve(double[,] a, double[] b)
    {
        var n = b.Length;
        var m = (double[,])a.Clone();
        var x = (double[])b.Clone();
        for (var col = 0; col < n; col++)
        {
            var pivot = col;
            for (var r = col + 1; r < n; r++) if (Math.Abs(m[r, col]) > Math.Abs(m[pivot, col])) pivot = r;
            if (pivot != col)
            {
                for (var k = 0; k < n; k++) (m[col, k], m[pivot, k]) = (m[pivot, k], m[col, k]);
                (x[col], x[pivot]) = (x[pivot], x[col]);
            }
            var d = m[col, col];
            if (Math.Abs(d) < 1e-12) continue;
            for (var r = 0; r < n; r++)
            {
                if (r == col) continue;
                var f = m[r, col] / d;
                if (f == 0) continue;
                for (var k = col; k < n; k++) m[r, k] -= f * m[col, k];
                x[r] -= f * x[col];
            }
        }
        for (var i = 0; i < n; i++) x[i] = Math.Abs(m[i, i]) < 1e-12 ? 0 : x[i] / m[i, i];
        return x;
    }
}
