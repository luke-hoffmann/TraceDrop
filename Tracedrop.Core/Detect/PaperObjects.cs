using OpenCvSharp;

namespace Tracedrop.Core.Detect;

/// <summary>
/// Tools on a sheet of paper: whatever differs from the paper. The paper's own shade (with its lighting fall-off)
/// is modelled as a smooth surface fitted to the sheet, so a gradient across it isn't mistaken for a tool.
/// Shadows are told apart by colour: a shadow is the paper's own colour, only darker, and (in practice) never much
/// darker; tools are either coloured or clearly dark.
/// </summary>
public static class PaperObjects
{
    /// <summary>Grey darkening up to this (Lab L units, 0..255) is taken for shadow, not tool.</summary>
    const double ShadowDarkness = 45;

    /// <summary>
    /// Probability 0..1 (CV_32F) that a pixel is a tool, over the straightened photo. Inside the sheet: the paper
    /// contrast; with an AI subject mask, only near what the AI also sees as a subject (which drops shadows and
    /// marks on the paper), plus whatever the AI is sure of. Outside the sheet: the AI mask only, if there is one.
    /// </summary>
    public static Mat Probability(Mat bgr, Rect2d paper, Mat? ai, double mmPerPixel = 0)
    {
        var w = bgr.Cols;
        var h = bgr.Rows;
        var pxPerMm = mmPerPixel > 0 ? 1 / mmPerPixel : paper.Width / 210.0;
        using var smooth = new Mat();
        Cv2.GaussianBlur(bgr, smooth, new Size(0, 0), Math.Max(0.8, 0.15 * pxPerMm));
        using var lab8 = new Mat();
        Cv2.CvtColor(smooth, lab8, ColorConversionCodes.BGR2Lab);
        using var lab = new Mat();
        lab8.ConvertTo(lab, MatType.CV_32FC3);

        var (fitL, fitA, fitB) = FitSheet(lab, paper);
        var probability = new Mat(h, w, MatType.CV_32F, Scalar.All(0));
        if (fitL is null || fitA is null || fitB is null) return probability;

        // Only the sheet itself, less a band along its edges (the edge of the sheet is not a tool).
        var band = 2.5 * pxPerMm;
        var x0 = (int)Math.Clamp(Math.Floor(paper.X + band), 0, w);
        var y0 = (int)Math.Clamp(Math.Floor(paper.Y + band), 0, h);
        var x1 = (int)Math.Clamp(Math.Ceiling(paper.Right - band), 0, w);
        var y1 = (int)Math.Clamp(Math.Ceiling(paper.Bottom - band), 0, h);
        if (x1 - x0 < 8 || y1 - y0 < 8) return probability;
        var roi = new Rect(x0, y0, x1 - x0, y1 - y0);

        using var labRoi = new Mat(lab, roi);
        var channels = Cv2.Split(labRoi);
        using var l = channels[0];
        using var a = channels[1];
        using var b = channels[2];
        using var bgL = Surface(fitL, roi, paper);
        using var bgA = Surface(fitA, roi, paper);
        using var bgB = Surface(fitB, roi, paper);

        // Darker than the paper, colour difference, lighter (a shiny highlight).
        using var darker = new Mat();
        Cv2.Subtract(bgL, l, darker);
        using var da = new Mat();
        Cv2.Subtract(a, bgA, da);
        using var db = new Mat();
        Cv2.Subtract(b, bgB, db);
        using var chroma = new Mat();
        Cv2.Magnitude(da, db, chroma);

        using var contrast = new Mat();
        // Colour counts 1.8x; grey darkening only beyond what a shadow does; lighter half.
        Cv2.Multiply(chroma, Scalar.All(1.8), contrast);
        using (var dark = new Mat())
        {
            Cv2.Subtract(darker, Scalar.All(ShadowDarkness), dark);
            Cv2.Multiply(dark, Scalar.All(1.6), dark);
            Cv2.Max(contrast, dark, contrast);
        }
        using (var light = new Mat())
        {
            Cv2.Multiply(darker, Scalar.All(-0.5), light);
            Cv2.Max(contrast, light, contrast);
        }
        Cv2.Max(contrast, Scalar.All(0), contrast);

        // The edge of a tool is where the picture is halfway between paper and tool: the contrast over the
        // strongest contrast nearby (at least 40, so faint stains on their own stay below half).
        using var nearby = new Mat();
        var r = Math.Max(3, (int)Math.Round(1.5 * pxPerMm));
        using (var kernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(2 * r + 1, 2 * r + 1)))
            Cv2.Dilate(contrast, nearby, kernel);
        Cv2.Max(nearby, Scalar.All(40), nearby);
        using var inside = new Mat();
        Cv2.Divide(contrast, nearby, inside);
        Cv2.Min(inside, Scalar.All(1), inside);
        using (var target = new Mat(probability, roi)) inside.CopyTo(target);

        if (ai is not null) CombineWithAi(probability, ai, paper, roi, pxPerMm);
        return probability;
    }

    /// <summary>
    /// With the AI's subject mask: inside the sheet, keep the paper contrast only within 4 mm of what the AI calls
    /// subject (no shadows far from tools, no marks on the paper) and add what the AI is sure of; outside the sheet,
    /// the AI alone. If the AI took the whole sheet for the subject it is ignored inside the sheet.
    /// </summary>
    static void CombineWithAi(Mat probability, Mat ai, Rect2d paper, Rect roi, double pxPerMm)
    {
        var w = probability.Cols;
        var h = probability.Rows;
        double cover;
        using (var sheet = new Mat(ai, roi))
        using (var on = new Mat())
        {
            Cv2.Threshold(sheet, on, 0.5, 1, ThresholdTypes.Binary);
            cover = Cv2.Mean(on).Val0;
        }
        var sheetRect = new Rect((int)Math.Max(0, paper.X), (int)Math.Max(0, paper.Y), 0, 0);
        sheetRect.Width = (int)Math.Min(w - sheetRect.X, paper.Width);
        sheetRect.Height = (int)Math.Min(h - sheetRect.Y, paper.Height);
        using var sheetMask = new Mat(h, w, MatType.CV_8U, Scalar.All(0));
        if (sheetRect.Width > 0 && sheetRect.Height > 0) sheetMask[sheetRect].SetTo(Scalar.All(255));
        using var outside = new Mat();
        Cv2.BitwiseNot(sheetMask, outside);
        ai.CopyTo(probability, outside); // beyond the sheet only the AI can tell
        if (cover >= 0.5) return;

        using var near = new Mat();
        Cv2.Threshold(ai, near, 0.3, 255, ThresholdTypes.Binary);
        near.ConvertTo(near, MatType.CV_8U);
        var r = Math.Max(2, (int)Math.Round(4 * pxPerMm));
        using (var kernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(2 * r + 1, 2 * r + 1)))
            Cv2.Dilate(near, near, kernel);
        using var far = new Mat();
        Cv2.BitwiseNot(near, far);
        using var farInSheet = new Mat();
        Cv2.BitwiseAnd(far, sheetMask, farInSheet);
        probability.SetTo(Scalar.All(0), farInSheet);
        using var sure = new Mat();
        Cv2.Max(probability, ai, sure);
        sure.CopyTo(probability, sheetMask);
    }

    /// <summary>Robust quadratic fits of the sheet's L, a and b over a grid of samples (tools left out).</summary>
    static (double[]? L, double[]? A, double[]? B) FitSheet(Mat lab, Rect2d paper)
    {
        var w = lab.Cols;
        var h = lab.Rows;
        const double inset = 0.04;
        const int grid = 48;
        var samples = new List<(double X, double Y, Vec3f V)>();
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
        return (fitL, fitA, fitB);
    }

    /// <summary>The fitted surface evaluated over a region of the picture (CV_32F).</summary>
    static Mat Surface(double[] c, Rect roi, Rect2d paper)
    {
        // u along x, v along y, both centred on the sheet and normalised to its size.
        using var u = new Mat(1, roi.Width, MatType.CV_32F);
        using var v = new Mat(roi.Height, 1, MatType.CV_32F);
        for (var x = 0; x < roi.Width; x++) u.Set(0, x, (float)((roi.X + x - paper.X) / Math.Max(1, paper.Width) - 0.5));
        for (var y = 0; y < roi.Height; y++) v.Set(y, 0, (float)((roi.Y + y - paper.Y) / Math.Max(1, paper.Height) - 0.5));
        using var uu = new Mat();
        Cv2.Repeat(u, roi.Height, 1, uu);
        using var vv = new Mat();
        Cv2.Repeat(v, 1, roi.Width, vv);
        var s = new Mat(roi.Height, roi.Width, MatType.CV_32F, Scalar.All(c[0]));
        using var term = new Mat();
        Cv2.ScaleAdd(uu, c[1], s, s);
        Cv2.ScaleAdd(vv, c[2], s, s);
        Cv2.Multiply(uu, uu, term);
        Cv2.ScaleAdd(term, c[3], s, s);
        Cv2.Multiply(uu, vv, term);
        Cv2.ScaleAdd(term, c[4], s, s);
        Cv2.Multiply(vv, vv, term);
        Cv2.ScaleAdd(term, c[5], s, s);
        return s;
    }

    // Quadratic surface c0 + c1 u + c2 v + c3 u² + c4 uv + c5 v², in coordinates normalised to the sheet.
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
