using OpenCvSharp;
using Tracedrop.Core.Ai;

namespace Tracedrop.Core.Detect;

/// <summary>Line strength found by looking closely at one circled area: CV_32F 0..1 at the size of <see cref="Roi"/>.</summary>
public sealed class FocusMap : IDisposable
{
    public FocusMap(Rect roi, Mat strength)
    {
        Roi = roi;
        Strength = strength;
    }

    public Rect Roi { get; }
    public Mat Strength { get; }

    public void Dispose() => Strength.Dispose();
}

/// <summary>
/// "Focus area": low-contrast detail (glasses on skin, eyes in shadow) is lost when the whole picture is analysed
/// at once. For a circled area this crops it, enlarges it 2-4x, evens out its contrast, and runs the edge model
/// (plus the line-drawing model and the built-in drawing in Line art) on just that crop. The result is merged back
/// into the picture's line strength inside the circle, where the threshold is also relaxed.
/// </summary>
public static class Focus
{
    const int CropSide = 768;

    public static FocusMap Compute(Mat prepared, PickPoint pick, ModelStore models, TraceSettings s)
    {
        var size = prepared.Size();
        var longest = Math.Max(size.Width, size.Height);
        var r = Math.Max(6, (pick.Radius > 0 ? pick.Radius : PickPoint.DefaultAddRadius) * longest);
        var cx = pick.X * (size.Width - 1);
        var cy = pick.Y * (size.Height - 1);
        var pad = r * 1.1;
        var x0 = (int)Math.Clamp(Math.Floor(cx - pad), 0, size.Width - 1);
        var y0 = (int)Math.Clamp(Math.Floor(cy - pad), 0, size.Height - 1);
        var x1 = (int)Math.Clamp(Math.Ceiling(cx + pad), x0 + 1, size.Width);
        var y1 = (int)Math.Clamp(Math.Ceiling(cy + pad), y0 + 1, size.Height);
        var roi = new Rect(x0, y0, x1 - x0, y1 - y0);

        using var crop = new Mat(prepared, roi);
        var scale = Math.Clamp(CropSide / (double)Math.Max(roi.Width, roi.Height), 1, 4);
        using var enlarged = new Mat();
        Cv2.Resize(crop, enlarged, new Size(Math.Max(8, (int)Math.Round(roi.Width * scale)), Math.Max(8, (int)Math.Round(roi.Height * scale))), 0, 0, InterpolationFlags.Cubic);
        EvenOutContrast(enlarged);

        using var strength = new Mat(enlarged.Size(), MatType.CV_32F, Scalar.All(0));
        if (AiModels.Edges(models, enlarged) is { } edges)
        {
            using (edges.Fused)
            using (edges.Average)
            {
                Cv2.Max(edges.Fused, edges.Average, edges.Fused);
                Cv2.Max(strength, edges.Fused, strength);
            }
        }
        else
        {
            using var gray = new Mat();
            Cv2.CvtColor(enlarged, gray, ColorConversionCodes.BGR2GRAY);
            using var canny = new Mat();
            Cv2.Canny(gray, canny, 20, 60, 3, true);
            using var cannyF = new Mat();
            canny.ConvertTo(cannyF, MatType.CV_32F, 1 / 255.0);
            Cv2.Max(strength, cannyF, strength);
        }

        if (s.Mode == TraceMode.LineArt)
        {
            using var drawn = Detector.Xdog(enlarged, Math.Min(1, s.Detail / 100.0 + 0.3));
            drawn.ConvertTo(drawn, MatType.CV_32F, 0.9);
            Cv2.Max(strength, drawn, strength);
            if (s.UseAiLineArt && AiModels.LineArt(models, enlarged) is { } ink)
            {
                using (ink) Cv2.Max(strength, ink, strength);
            }
        }

        // Judge strength against what's inside the circle, not the whole picture: the faint frames and eye lines
        // become "strong" here while true strong edges just saturate.
        using (var inside = new Mat(strength.Size(), MatType.CV_8U, Scalar.All(0)))
        {
            Cv2.Circle(inside, new Point((int)Math.Round((cx - x0) * scale), (int)Math.Round((cy - y0) * scale)), (int)Math.Round(r * scale), Scalar.All(255), -1);
            using var values = new Mat();
            strength.CopyTo(values, inside);
            values.GetArray(out float[] all);
            var lit = all.Where(v => v > 0.02f).OrderBy(v => v).ToArray();
            if (lit.Length > 50)
            {
                var reference = Math.Max(0.12, lit[(int)(lit.Length * 0.9)]);
                strength.ConvertTo(strength, MatType.CV_32F, 1.0 / reference);
                Cv2.Threshold(strength, strength, 1, 1, ThresholdTypes.Trunc);
            }
        }

        // Back to the crop's size, and only inside the circle (soft edge so it blends with the rest).
        var result = new Mat();
        Cv2.Resize(strength, result, roi.Size, 0, 0, InterpolationFlags.Area);
        using var circle = new Mat(roi.Size, MatType.CV_32F, Scalar.All(0));
        Cv2.Circle(circle, new Point((int)Math.Round(cx - x0), (int)Math.Round(cy - y0)), (int)Math.Round(r), Scalar.All(1), -1, LineTypes.AntiAlias);
        Cv2.GaussianBlur(circle, circle, new Size(0, 0), Math.Max(1.5, r * 0.12));
        Cv2.Multiply(result, circle, result);
        return new FocusMap(roi, result);
    }

    /// <summary>Strong local contrast on lightness: faint frames and eye lines stand out against skin.</summary>
    static void EvenOutContrast(Mat bgr)
    {
        using var lab = new Mat();
        Cv2.CvtColor(bgr, lab, ColorConversionCodes.BGR2Lab);
        var channels = Cv2.Split(lab);
        try
        {
            using var clahe = Cv2.CreateCLAHE(3.0, new Size(4, 4));
            clahe.Apply(channels[0], channels[0]);
            Cv2.Merge(channels, lab);
        }
        finally
        {
            foreach (var c in channels) c.Dispose();
        }
        Cv2.CvtColor(lab, bgr, ColorConversionCodes.Lab2BGR);
    }

    /// <summary>Raises <paramref name="strength"/> (picture-sized) to at least each focus map inside its area.</summary>
    internal static void MergeInto(Mat strength, IEnumerable<FocusMap> maps)
    {
        foreach (var map in maps)
        {
            if (map.Roi.Right > strength.Cols || map.Roi.Bottom > strength.Rows) continue;
            using var view = new Mat(strength, map.Roi);
            Cv2.Max(view, map.Strength, view);
        }
    }
}
