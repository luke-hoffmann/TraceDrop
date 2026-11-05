using OpenCvSharp;

namespace Tracedrop.Core.Detect;

/// <summary>One filled layer: a binary mask (255 = inside) and the colour it is drawn with.</summary>
public sealed class RegionLayer : IDisposable
{
    public RegionLayer(string name, Mat mask, Scalar color)
    {
        Name = name;
        Mask = mask;
        Color = color;
    }

    public string Name { get; }
    public Mat Mask { get; }
    /// <summary>BGR colour of the layer (used for SVG/PDF and the preview).</summary>
    public Scalar Color { get; }

    public void Dispose() => Mask.Dispose();
}

/// <summary>The result of detection: either filled regions, or a map of thin strokes (edges / ink lines).</summary>
public sealed class Detection : IDisposable
{
    Detection(List<RegionLayer>? regions, Mat? strokes)
    {
        Regions = regions ?? new();
        Strokes = strokes;
    }

    public static Detection FromRegions(List<RegionLayer> regions) => new(regions, null);
    public static Detection FromStrokes(Mat strokes) => new(null, strokes);

    public List<RegionLayer> Regions { get; }

    /// <summary>Binary (255 = ink) stroke mask, a few pixels wide, or null for region detections.</summary>
    public Mat? Strokes { get; }

    public bool IsStrokes => Strokes is not null;

    public int Width => Strokes?.Cols ?? (Regions.Count > 0 ? Regions[0].Mask.Cols : 0);
    public int Height => Strokes?.Rows ?? (Regions.Count > 0 ? Regions[0].Mask.Rows : 0);

    /// <summary>White picture with what was detected in dark ink (regions filled in their colour).</summary>
    public Mat RenderPreview()
    {
        var preview = new Mat(Height, Width, MatType.CV_8UC3, Scalar.All(255));
        if (Strokes is not null)
        {
            preview.SetTo(new Scalar(30, 30, 30), Strokes);
            return preview;
        }
        foreach (var layer in Regions)
        {
            var c = layer.Color;
            // Light colours would vanish on white: darken anything brighter than mid-grey.
            var luminance = 0.114 * c.Val0 + 0.587 * c.Val1 + 0.299 * c.Val2;
            var ink = luminance > 170 ? new Scalar(c.Val0 * 0.6, c.Val1 * 0.6, c.Val2 * 0.6) : c;
            preview.SetTo(ink, layer.Mask);
        }
        return preview;
    }

    public void Dispose()
    {
        foreach (var r in Regions) r.Dispose();
        Strokes?.Dispose();
    }
}
