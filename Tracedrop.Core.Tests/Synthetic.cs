using OpenCvSharp;
using Tracedrop.Core.Imaging;

namespace Tracedrop.Core.Tests;

/// <summary>Pictures drawn in code, with known geometry.</summary>
static class Synthetic
{
    /// <summary>
    /// A dark plate (680×460 px, 40 px corner radius) with holes of radius 45, 30 and 60 and a 240×60 slot,
    /// on a light, slightly noisy table, 1200×900 px.
    /// </summary>
    public static LoadedImage Part()
    {
        var plate = new Mat(900, 1200, MatType.CV_8U, Scalar.All(0));
        Cv2.Rectangle(plate, new Rect(260, 220, 680, 460), Scalar.All(255), -1);
        foreach (var (cx, cy) in new[] { (260, 220), (940, 220), (260, 680), (940, 680) })
        {
            Cv2.Rectangle(plate, new Rect(cx - 40, cy - 40, 80, 80), Scalar.All(0), -1);
            Cv2.Circle(plate, new Point(cx + (cx < 600 ? 40 : -40), cy + (cy < 450 ? 40 : -40)), 40, Scalar.All(255), -1);
        }
        foreach (var (cx, cy, r) in new[] { (380, 340, 45), (820, 340, 30), (600, 560, 60) })
            Cv2.Circle(plate, new Point(cx, cy), r, Scalar.All(0), -1);
        Cv2.Rectangle(plate, new Rect(480, 300, 240, 60), Scalar.All(0), -1);

        var image = new Mat(900, 1200, MatType.CV_8UC3, new Scalar(205, 210, 214));
        image.SetTo(new Scalar(70, 75, 82), plate);
        using var noise = new Mat(900, 1200, MatType.CV_8UC3);
        Cv2.Randn(noise, Scalar.All(0), Scalar.All(5));
        Cv2.Add(image, noise, image);
        plate.Dispose();
        return new LoadedImage(image, null);
    }

    /// <summary>White page with black shapes: a 200 px square, a ring (outer r 100, inner r 50) and a bar.</summary>
    public static LoadedImage Shapes()
    {
        var image = new Mat(600, 900, MatType.CV_8UC3, Scalar.All(255));
        Cv2.Rectangle(image, new Rect(60, 60, 200, 200), Scalar.All(0), -1);
        Cv2.Circle(image, new Point(520, 180), 100, Scalar.All(0), -1, LineTypes.AntiAlias);
        Cv2.Circle(image, new Point(520, 180), 50, Scalar.All(255), -1, LineTypes.AntiAlias);
        Cv2.Rectangle(image, new Rect(100, 420, 700, 60), Scalar.All(0), -1);
        return new LoadedImage(image, null);
    }

    /// <summary>A pen drawing: an open wavy stroke and a closed loop, 4 px wide.</summary>
    public static LoadedImage Sketch()
    {
        var image = new Mat(600, 800, MatType.CV_8UC3, Scalar.All(250));
        var wave = Enumerable.Range(0, 120).Select(i => new Point(80 + i * 5, 200 + (int)(60 * Math.Sin(i / 12.0)))).ToArray();
        Cv2.Polylines(image, new[] { wave }, false, Scalar.All(20), 4, LineTypes.AntiAlias);
        Cv2.Ellipse(image, new Point(400, 430), new Size(160, 90), 15, 0, 360, Scalar.All(20), 4, LineTypes.AntiAlias);
        return new LoadedImage(image, null);
    }

    /// <summary>
    /// A photo of three dark tools on a sheet of Letter paper on a wooden-ish table, seen at an angle: a 120 × 18 mm
    /// bar, a 30 mm disc and a 60 × 25 mm block turned 30°. Returns the sheet's true corners in the photo (TL, TR,
    /// BR, BL) and the tools' areas in mm².
    /// </summary>
    public static LoadedImage ToolsOnPaper(out Point2f[] corners, out double[] areas, bool shadows = true)
    {
        const double ppm = 6; // pixels per mm on the flat sheet
        var (pw, ph) = (215.9, 279.4);
        var sheet = new Mat((int)Math.Round(ph * ppm), (int)Math.Round(pw * ppm), MatType.CV_8UC3, new Scalar(232, 236, 238));
        // Light falling off across the sheet.
        using (var ramp = new Mat(sheet.Size(), MatType.CV_8UC3))
        {
            for (var y = 0; y < ramp.Rows; y++) ramp.Row(y).SetTo(Scalar.All(18.0 * y / ramp.Rows));
            Cv2.Subtract(sheet, ramp, sheet);
        }
        Point P(double x, double y) => new((int)Math.Round(x * ppm), (int)Math.Round(y * ppm));
        var bar = new[] { P(40, 50), P(160, 50), P(160, 68), P(40, 68) };
        var block = new RotatedRect(new Point2f((float)(70 * ppm), (float)(190 * ppm)), new Size2f(60 * ppm, 25 * ppm), 30);
        var blockPts = block.Points().Select(p => new Point((int)Math.Round(p.X), (int)Math.Round(p.Y))).ToArray();
        if (shadows)
        {
            // Soft shadows down and to the right of each tool.
            using var shadow = new Mat(sheet.Size(), MatType.CV_8UC1, Scalar.All(0));
            Cv2.FillPoly(shadow, new[] { bar.Select(p => p + new Point(12, 12)).ToArray() }, Scalar.All(255));
            Cv2.Circle(shadow, P(150, 150) + new Point(12, 12), (int)(15 * ppm), Scalar.All(255), -1);
            Cv2.FillPoly(shadow, new[] { blockPts.Select(p => p + new Point(12, 12)).ToArray() }, Scalar.All(255));
            Cv2.GaussianBlur(shadow, shadow, new Size(0, 0), 10);
            using var dark = new Mat();
            shadow.ConvertTo(dark, MatType.CV_8U, 14 / 255.0);
            using var dark3 = new Mat();
            Cv2.CvtColor(dark, dark3, ColorConversionCodes.GRAY2BGR);
            Cv2.Subtract(sheet, dark3, sheet);
        }
        Cv2.FillPoly(sheet, new[] { bar }, new Scalar(45, 48, 52), LineTypes.AntiAlias);
        Cv2.Circle(sheet, P(150, 150), (int)(15 * ppm), new Scalar(40, 70, 170), -1, LineTypes.AntiAlias); // red-orange disc
        Cv2.FillPoly(sheet, new[] { blockPts }, new Scalar(60, 60, 60), LineTypes.AntiAlias);
        areas = new[] { 120 * 18, Math.PI * 15 * 15, 60 * 25.0 };

        // Seen at an angle on a table.
        var photo = new Mat(2250, 3000, MatType.CV_8UC3, new Scalar(70, 105, 140));
        using (var grain = new Mat(photo.Size(), MatType.CV_8UC3))
        {
            Cv2.Randn(grain, Scalar.All(0), Scalar.All(6));
            Cv2.Add(photo, grain, photo);
        }
        corners = new[] { new Point2f(820, 260), new Point2f(2180, 330), new Point2f(2330, 2050), new Point2f(640, 1960) };
        var src = new[] { new Point2f(0, 0), new Point2f(sheet.Cols, 0), new Point2f(sheet.Cols, sheet.Rows), new Point2f(0, sheet.Rows) };
        using var h = Cv2.GetPerspectiveTransform(src, corners);
        Cv2.WarpPerspective(sheet, photo, h, photo.Size(), InterpolationFlags.Linear, BorderTypes.Transparent);
        using (var noise = new Mat(photo.Size(), MatType.CV_8UC3))
        {
            Cv2.Randn(noise, Scalar.All(0), Scalar.All(3));
            Cv2.Add(photo, noise, photo);
        }
        sheet.Dispose();
        return new LoadedImage(photo, null);
    }
}
