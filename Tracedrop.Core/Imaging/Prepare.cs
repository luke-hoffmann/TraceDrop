using OpenCvSharp;

namespace Tracedrop.Core.Imaging;

/// <summary>The "Prepare" filters: run on the working-size picture before anything is detected.</summary>
public static class Prepare
{
    /// <summary>Scales the picture so its longest side is <paramref name="workingSize"/> (small pictures grow at most 4x, for smoother outlines).</summary>
    public static Mat ToWorkingSize(Mat image, int workingSize, InterpolationFlags downscale = InterpolationFlags.Area)
    {
        var longest = Math.Max(image.Cols, image.Rows);
        var scale = Math.Min(4.0, workingSize / (double)longest);
        if (Math.Abs(scale - 1) < 0.01) return image.Clone();
        var size = new Size(Math.Max(1, (int)Math.Round(image.Cols * scale)), Math.Max(1, (int)Math.Round(image.Rows * scale)));
        var result = new Mat();
        Cv2.Resize(image, result, size, 0, 0, scale < 1 ? downscale : InterpolationFlags.Cubic);
        return result;
    }

    /// <summary>Applies despeckle, texture smoothing, blur, brightness/contrast, local contrast and invert. Returns a new BGR mat.</summary>
    public static Mat Apply(Mat bgr, TraceSettings s)
    {
        var current = bgr.Clone();
        var scale = Math.Max(current.Cols, current.Rows) / 1600.0; // filter strengths are tuned at 1600 px

        if (s.Despeckle > 0)
        {
            var k = 2 * Math.Max(1, (int)Math.Round(s.Despeckle * Math.Max(0.5, scale))) + 1;
            Cv2.MedianBlur(current, current, Math.Min(k, 31));
        }

        if (s.SmoothTexture > 0)
        {
            // Bilateral filtering, repeated for strong settings: flattens texture, keeps edges.
            var strength = s.SmoothTexture / 100.0;
            var sigmaColor = 12 + 70 * strength;
            var sigmaSpace = (2 + 8 * strength) * Math.Max(0.5, scale);
            var passes = 1 + (int)(strength * 2.5);
            for (var i = 0; i < passes; i++)
            {
                using var next = new Mat();
                Cv2.BilateralFilter(current, next, 0, sigmaColor, sigmaSpace);
                next.CopyTo(current);
            }
        }

        if (s.Blur > 0.05)
        {
            Cv2.GaussianBlur(current, current, new Size(0, 0), s.Blur * Math.Max(0.5, scale));
        }

        if (Math.Abs(s.Brightness) > 0.01 || Math.Abs(s.Contrast) > 0.01)
        {
            var gain = s.Contrast >= 0 ? 1 + s.Contrast / 40.0 : 1 + s.Contrast / 100.0;
            var offset = 128 * (1 - gain) + s.Brightness * 1.28;
            current.ConvertTo(current, MatType.CV_8UC3, gain, offset);
        }

        if (s.LocalContrast > 0.5)
        {
            using var lab = new Mat();
            Cv2.CvtColor(current, lab, ColorConversionCodes.BGR2Lab);
            var channels = Cv2.Split(lab);
            try
            {
                var tiles = Math.Max(2, (int)Math.Round(8 * Math.Max(0.5, Math.Min(2, scale))));
                using var clahe = Cv2.CreateCLAHE(1 + s.LocalContrast / 100.0 * 5, new Size(tiles, tiles));
                clahe.Apply(channels[0], channels[0]);
                Cv2.Merge(channels, lab);
            }
            finally
            {
                foreach (var c in channels) c.Dispose();
            }
            Cv2.CvtColor(lab, current, ColorConversionCodes.Lab2BGR);
        }

        if (s.Invert) Cv2.BitwiseNot(current, current);
        return current;
    }

    /// <summary>Whites out everything outside the subject (soft edge), so it reads as empty paper.</summary>
    public static void WhiteOutBackground(Mat bgr, Mat subjectProbability)
    {
        using var a = new Mat();
        // Sharpen the soft mask a little so a faint halo doesn't survive, then feather by one pixel.
        subjectProbability.ConvertTo(a, MatType.CV_32F, 2.0, -0.5);
        Cv2.Threshold(a, a, 1, 1, ThresholdTypes.Trunc);
        Cv2.Threshold(a, a, 0, 0, ThresholdTypes.Tozero);
        Cv2.GaussianBlur(a, a, new Size(0, 0), 1.0);
        using var a3 = new Mat();
        Cv2.Merge(new[] { a, a, a }, a3);
        using var f = new Mat();
        bgr.ConvertTo(f, MatType.CV_32FC3);
        using var fg = new Mat();
        Cv2.Multiply(f, a3, fg);
        using var inv = new Mat();
        Cv2.Subtract(Scalar.All(1.0), a3, inv);
        using var white = new Mat();
        Cv2.Multiply(inv, Scalar.All(255.0), white);
        using var sum = new Mat();
        Cv2.Add(fg, white, sum);
        sum.ConvertTo(bgr, MatType.CV_8UC3);
    }
}
