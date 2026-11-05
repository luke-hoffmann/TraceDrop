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
}
