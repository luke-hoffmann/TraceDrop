using System.Runtime.InteropServices;
using OpenCvSharp;
using Tracedrop.Core.Detect;

namespace Tracedrop.Core.Vector;

/// <summary>A traced but not yet fitted outline or line, in picture pixels.</summary>
public sealed record RawPath(List<Vec2> Points, bool Closed, PathRole Role, string Layer, (byte R, byte G, byte B) Color);

/// <summary>
/// Cleaned detection → fitted vector paths, in picture pixels. Two halves so the app can cache the first:
/// <see cref="Extract"/> traces outlines/centerlines (depends on the detection), <see cref="Fit"/> turns them
/// into lines, arcs and splines (depends only on Smoothness, Corners, Curves, Snap circles).
/// </summary>
public static class Vectorizer
{
    public static FitOptions Options(TraceSettings s, int width, int height)
    {
        var scale = Math.Max(0.5, Math.Max(width, height) / 1600.0);
        var smooth = s.Smoothness / 100.0;
        return new FitOptions
        {
            SmoothSigma = (0.5 + 3.5 * smooth) * scale,
            Tolerance = (0.25 + 2.25 * smooth) * scale,
            CornerAngle = 115 - 95 * (s.CornerSharpness / 100.0),
            Style = s.Curves,
            SnapCircles = s.SnapCircles
        };
    }

    public static VectorDrawing Build(Detection cleaned, TraceSettings s, CancellationToken cancel = default) =>
        Fit(Extract(cleaned, s, cancel), cleaned.Width, cleaned.Height, s, cancel);

    /// <summary>Traces the cleaned detection into raw point paths (outer outlines first, then holes, then lines).</summary>
    public static List<RawPath> Extract(Detection cleaned, TraceSettings s, CancellationToken cancel = default)
    {
        var width = cleaned.Width;
        var height = cleaned.Height;
        var paths = new List<RawPath>();
        if (cleaned.IsStrokes)
        {
            using var strokes = cleaned.Strokes!.Clone();
            if (s.Strokes == StrokeStyle.Centerline)
                paths.AddRange(Centerlines(strokes, s));
            else
            {
                if (s.StrokeWidth > 0.25) UniformWidth(strokes, s.StrokeWidth * Math.Max(0.5, Math.Max(width, height) / 1600.0));
                paths.AddRange(Outlines(strokes, "LINES", "LINES", (20, 20, 20)));
            }
        }
        else
        {
            var single = cleaned.Regions.Count == 1;
            foreach (var layer in cleaned.Regions)
            {
                cancel.ThrowIfCancellationRequested();
                var color = ((byte)layer.Color.Val2, (byte)layer.Color.Val1, (byte)layer.Color.Val0);
                // One-layer drawings put holes on their own layer (easy to select in CAD); colour layers keep theirs.
                paths.AddRange(Outlines(layer.Mask, single ? "OUTLINE" : layer.Name, single ? "HOLES" : layer.Name, color));
            }
        }
        // Outer outlines first, then holes, then lines: easier to read in CAD's entity list.
        return paths.OrderBy(p => p.Role).ToList();
    }

    /// <summary>Fits every raw path in parallel; output order matches input order.</summary>
    public static VectorDrawing Fit(IReadOnlyList<RawPath> raw, int width, int height, TraceSettings s, CancellationToken cancel = default)
    {
        var options = Options(s, width, height);
        var fitted = new VectorPath?[raw.Count];
        Parallel.For(0, raw.Count, new ParallelOptions { CancellationToken = cancel }, i =>
        {
            var r = raw[i];
            var f = Fitter.Fit(r.Points, r.Closed, options);
            if (f.Segments.Count == 0 && f.Circle is null) return;
            fitted[i] = new VectorPath
            {
                Segments = f.Segments,
                Circle = f.Circle,
                Closed = r.Closed,
                Role = r.Role,
                Layer = r.Layer,
                Color = r.Color
            };
        });
        return new VectorDrawing(width, height, fitted.Where(p => p is not null).Select(p => p!).ToList());
    }

    /// <summary>Outer outlines and holes of a binary mask, as sub-pixel loops.</summary>
    static IEnumerable<RawPath> Outlines(Mat mask, string outerLayer, string holeLayer, (byte, byte, byte) color)
    {
        using var field = new Mat();
        mask.ConvertTo(field, MatType.CV_32F, 1 / 255.0);
        // A touch of blur turns the pixel staircase into sub-pixel ramps for marching squares.
        Cv2.GaussianBlur(field, field, new Size(0, 0), 0.75);
        var values = new float[field.Rows * field.Cols];
        using (var continuous = field.Clone()) Marshal.Copy(continuous.Data, values, 0, values.Length);

        foreach (var loop in MarchingSquares.Trace(values, field.Cols, field.Rows))
        {
            var area = Geometry2D.SignedArea(loop);
            if (Math.Abs(area) < 2) continue;
            var hole = area < 0;
            yield return new RawPath(loop, true, hole ? PathRole.Hole : PathRole.Outer, hole ? holeLayer : outerLayer, color);
        }
    }

    static IEnumerable<RawPath> Centerlines(Mat strokes, TraceSettings s)
    {
        var width = strokes.Cols;
        var height = strokes.Rows;
        strokes.GetArray(out byte[] pixels);
        var minArea = Cleanup.MinFeatureArea(s.Detail, width, height);
        var minLength = Math.Max(3, Math.Sqrt(minArea) * 1.6);
        var spur = Math.Max(3, (8 - 6 * s.Detail / 100.0) * Math.Max(0.5, Math.Max(width, height) / 1600.0));
        foreach (var chain in Skeleton.Centerlines(pixels, width, height, spur, minLength))
            yield return new RawPath(chain.Points, chain.Closed, PathRole.Stroke, "LINES", (20, 20, 20));
    }

    /// <summary>Replaces strokes by their skeleton grown to an even width (pixels).</summary>
    static void UniformWidth(Mat strokes, double width)
    {
        strokes.GetArray(out byte[] pixels);
        Skeleton.Thin(pixels, strokes.Cols, strokes.Rows);
        for (var i = 0; i < pixels.Length; i++) pixels[i] = pixels[i] != 0 ? (byte)255 : (byte)0;
        strokes.SetArray(pixels);
        var r = Math.Max(1, (int)Math.Round(width / 2));
        using var kernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(2 * r + 1, 2 * r + 1));
        Cv2.Dilate(strokes, strokes, kernel);
    }
}
