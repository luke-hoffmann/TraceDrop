using OpenCvSharp;

namespace Tracedrop.Core.Ai;

/// <summary>
/// The three networks, each wrapped to take and return plain OpenCV mats at the picture's size. Every call goes
/// through <see cref="ModelStore.Run{T}"/>, which re-runs it on the CPU if the GPU fails.
/// </summary>
public static class AiModels
{
    /// <summary>Largest side the line-drawing network runs at. It was trained at 256 px; 640 keeps fine lines
    /// while staying light enough for integrated GPUs.</summary>
    const int LineArtMaxSide = 640;

    /// <summary>
    /// TEED edge probabilities for a BGR picture: (fused, average), both CV_32F 0..1 at the picture's size,
    /// rescaled so the flat background reads 0. Null when the model isn't bundled.
    /// </summary>
    public static (Mat Fused, Mat Average)? Edges(ModelStore store, Mat bgr)
    {
        // TEED needs sides that are multiples of 8: pad by mirroring, then crop the result back.
        var padH = (8 - bgr.Rows % 8) % 8;
        var padW = (8 - bgr.Cols % 8) % 8;
        using var padded = new Mat();
        Cv2.CopyMakeBorder(bgr, padded, 0, padH, 0, padW, BorderTypes.Reflect101);
        using var f = new Mat();
        padded.ConvertTo(f, MatType.CV_32FC3);
        var tensor = OnnxImage.ToTensor(f);

        var maps = store.Run(ModelNames.Edges, session =>
        {
            using var results = session.Run(new[] { OnnxImage.Input(session, tensor) });
            var outputs = results.ToList();
            var fused = OnnxImage.PlaneToMat(outputs[0].AsTensor<float>());
            var average = OnnxImage.PlaneToMat(outputs[Math.Min(1, outputs.Count - 1)].AsTensor<float>());
            return new[] { fused, average };
        });
        if (maps is null) return null;

        using var fusedFull = maps[0];
        using var averageFull = maps[1];
        var roi = new Rect(0, 0, bgr.Cols, bgr.Rows);
        using var fusedView = fusedFull[roi];
        using var averageView = averageFull[roi];
        return (Baseline(fusedView), Baseline(averageView));
    }

    /// <summary>TEED's outputs sit on a non-zero floor (sigmoid of a bounded activation); stretch so the floor is 0 and strong edges are 1.</summary>
    static Mat Baseline(Mat map)
    {
        using var small = new Mat();
        Cv2.Resize(map, small, new Size(Math.Max(1, map.Cols / 4), Math.Max(1, map.Rows / 4)), 0, 0, InterpolationFlags.Area);
        var floor = Percentile(small, 0.5);
        var top = Math.Max(Percentile(small, 0.999), floor + 0.05);
        var result = new Mat();
        map.ConvertTo(result, MatType.CV_32F, 1.0 / (top - floor), -floor / (top - floor));
        Cv2.Threshold(result, result, 1, 1, ThresholdTypes.Trunc);
        Cv2.Threshold(result, result, 0, 0, ThresholdTypes.Tozero);
        return result;
    }

    internal static double Percentile(Mat floatMat, double q)
    {
        floatMat.GetArray(out float[] values);
        if (values.Length == 0) return 0;
        Array.Sort(values);
        return values[Math.Clamp((int)Math.Round(q * (values.Length - 1)), 0, values.Length - 1)];
    }

    /// <summary>Subject (foreground) probability, CV_32F 0..1 at the picture's size. Null when the model isn't bundled.</summary>
    public static Mat? Subject(ModelStore store, Mat bgr)
    {
        var map = store.Run(ModelNames.Subject, session =>
        {
            var (fh, fw) = OnnxImage.InputSize(session);
            var size = new Size(fw ?? 1024, fh ?? 1024);
            using var resized = new Mat();
            Cv2.Resize(bgr, resized, size, 0, 0, InterpolationFlags.Area);
            using var rgb = new Mat();
            Cv2.CvtColor(resized, rgb, ColorConversionCodes.BGR2RGB);
            using var f = new Mat();
            rgb.ConvertTo(f, MatType.CV_32FC3, 1 / 255.0);
            // ImageNet normalisation, as BiRefNet was trained with.
            Cv2.Subtract(f, new Scalar(0.485, 0.456, 0.406), f);
            Cv2.Divide(f, new Scalar(0.229, 0.224, 0.225), f);
            var tensor = OnnxImage.ToTensor(f);
            using var results = session.Run(new[] { OnnxImage.Input(session, tensor) });
            // BiRefNet exports may list several side outputs; the final prediction is the last one.
            return OnnxImage.PlaneToMat(results.Last().AsTensor<float>());
        });
        if (map is null) return null;

        using (map)
        {
            OnnxImage.SigmoidIfLogits(map);
            var full = new Mat();
            Cv2.Resize(map, full, new Size(bgr.Cols, bgr.Rows), 0, 0, InterpolationFlags.Linear);
            return full;
        }
    }

    /// <summary>
    /// Line darkness from the photo-to-drawing network: CV_32F 0..1 at the picture's size, 1 = ink.
    /// Null when the model isn't bundled.
    /// </summary>
    public static Mat? LineArt(ModelStore store, Mat bgr)
    {
        var drawing = store.Run(ModelNames.LineArt, session =>
        {
            var (fh, fw) = OnnxImage.InputSize(session);
            Size size;
            if (fh is { } h && fw is { } w) size = new Size(w, h);
            else
            {
                var scale = Math.Min(1.0, LineArtMaxSide / (double)Math.Max(bgr.Cols, bgr.Rows));
                size = new Size(Math.Max(8, (int)Math.Round(bgr.Cols * scale / 8) * 8), Math.Max(8, (int)Math.Round(bgr.Rows * scale / 8) * 8));
            }

            using var resized = new Mat();
            Cv2.Resize(bgr, resized, size, 0, 0, InterpolationFlags.Area);
            using var rgb = new Mat();
            Cv2.CvtColor(resized, rgb, ColorConversionCodes.BGR2RGB);
            using var f = new Mat();
            rgb.ConvertTo(f, MatType.CV_32FC3, 1 / 255.0);
            var tensor = OnnxImage.ToTensor(f);
            using var results = session.Run(new[] { OnnxImage.Input(session, tensor) });
            var output = results.First().AsTensor<float>();
            var channels = OnnxImage.Channels(output);
            var plane = OnnxImage.PlaneToMat(output);
            for (var c = 1; c < channels; c++)
            {
                using var extra = OnnxImage.PlaneToMat(output, c);
                Cv2.Add(plane, extra, plane);
            }
            if (channels > 1) Cv2.Multiply(plane, Scalar.All(1.0 / channels), plane);
            return plane;
        });
        if (drawing is null) return null;

        using (drawing)
        {
            Cv2.MinMaxLoc(drawing, out double min, out _);
            if (min < -0.05) drawing.ConvertTo(drawing, MatType.CV_32F, 0.5, 0.5); // tanh output: -1..1 → 0..1

            // The network draws dark lines on white; Tracedrop wants ink = 1.
            using var ink = new Mat();
            Cv2.Subtract(Scalar.All(1.0), drawing, ink);
            Cv2.Threshold(ink, ink, 1, 1, ThresholdTypes.Trunc);
            Cv2.Threshold(ink, ink, 0, 0, ThresholdTypes.Tozero);
            var full = new Mat();
            Cv2.Resize(ink, full, new Size(bgr.Cols, bgr.Rows), 0, 0, InterpolationFlags.Linear);
            return full;
        }
    }
}
