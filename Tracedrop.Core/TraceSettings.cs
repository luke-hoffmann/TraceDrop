using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tracedrop.Core;

/// <summary>What the tracer looks for in the picture.</summary>
public enum TraceMode
{
    /// <summary>The subject's outline (and its holes), background removed. Objects, tools, parts, leaves.</summary>
    Silhouette,
    /// <summary>Areas split by brightness or colour. Logos, prints, stencils, dark-on-light drawings.</summary>
    Shapes,
    /// <summary>Every visible edge, found by the TEED edge network (or Canny). Detailed outlines of anything.</summary>
    Edges,
    /// <summary>A clean pen drawing of the picture. Faces, portraits, sketches.</summary>
    LineArt
}

/// <summary>How curves are written to the vector file.</summary>
public enum CurveStyle
{
    /// <summary>Straight lines and true circular arcs. Lightest and easiest to dimension in CAD.</summary>
    LinesAndArcs,
    /// <summary>Smooth splines (cubic Bezier / B-spline).</summary>
    Splines,
    /// <summary>Straight segments only.</summary>
    Polylines
}

/// <summary>How thin lines (edges, line art) are written.</summary>
public enum StrokeStyle
{
    /// <summary>One line down the middle of each stroke. Engraving, plotting, toolpaths.</summary>
    Centerline,
    /// <summary>A closed outline around each stroke, so it can be extruded or cut.</summary>
    Outline
}

/// <summary>Which shapes are kept after tracing.</summary>
public enum KeepShapes
{
    All,
    Largest
}

public enum OutputUnits
{
    Millimeters,
    Inches
}

/// <summary>
/// Every knob of the pipeline. Plain data: serialised to JSON for presets and the portable settings store.
/// Ranges are documented per property; the UI uses the same ranges.
/// </summary>
public sealed class TraceSettings
{
    // ---------- Prepare (applied to the picture before anything is detected) ----------

    /// <summary>-100..100.</summary>
    public double Brightness { get; set; }

    /// <summary>-100..100.</summary>
    public double Contrast { get; set; }

    /// <summary>0..100. Evens out light and brings out detail in shadows (CLAHE).</summary>
    public double LocalContrast { get; set; }

    /// <summary>0..20 px. Plain Gaussian blur: removes fine texture and noise, softens everything.</summary>
    public double Blur { get; set; }

    /// <summary>0..100. Edge-preserving smoothing: flattens texture (wood grain, fabric, skin) but keeps outlines sharp.</summary>
    public double SmoothTexture { get; set; }

    /// <summary>0..10. Median filter radius: removes dust, specks and salt-and-pepper noise.</summary>
    public int Despeckle { get; set; }

    /// <summary>Swap dark and light.</summary>
    public bool Invert { get; set; }

    /// <summary>Blank out the background first (AI subject mask) so only the subject is traced.</summary>
    public bool IsolateSubject { get; set; }

    // ---------- Detect ----------

    public TraceMode Mode { get; set; } = TraceMode.Silhouette;

    /// <summary>0..100. The master "how much" control: low keeps only the key outlines, high keeps every detail.</summary>
    public double Detail { get; set; } = 50;

    /// <summary>0..100. Mode-specific cut level: brightness level (Shapes), edge strength (Edges/Line art), subject confidence (Silhouette).</summary>
    public double Threshold { get; set; } = 50;

    /// <summary>Shapes mode: pick the brightness cut automatically (Otsu).</summary>
    public bool AutoThreshold { get; set; } = true;

    /// <summary>Shapes mode: 2 = black/white, 3..8 = one layer per colour.</summary>
    public int Colors { get; set; } = 2;

    /// <summary>Edges mode: use the TEED network (true) or classic Canny (false).</summary>
    public bool UseAiEdges { get; set; } = true;

    // ---------- Clean up ----------

    /// <summary>0..40 px. Bridges small gaps so broken outlines close up.</summary>
    public double CloseGaps { get; set; }

    /// <summary>Fill holes inside shapes (keep only outer outlines).</summary>
    public bool FillHoles { get; set; }

    public KeepShapes Keep { get; set; } = KeepShapes.All;

    // ---------- Vector ----------

    /// <summary>0..100. Higher = rounder, fewer entities; lower = follows every bump.</summary>
    public double Smoothness { get; set; } = 50;

    /// <summary>0..100. Higher keeps more sharp corners; lower rounds them off.</summary>
    public double CornerSharpness { get; set; } = 50;

    public CurveStyle Curves { get; set; } = CurveStyle.LinesAndArcs;

    public StrokeStyle Strokes { get; set; } = StrokeStyle.Centerline;

    /// <summary>Outline strokes: width of each stroke in px (0 = as detected).</summary>
    public double StrokeWidth { get; set; }

    /// <summary>Turn round holes and round shapes into true circles.</summary>
    public bool SnapCircles { get; set; } = true;

    /// <summary>Grow (+) or shrink (-) shapes, in output units. Clearance for inserts, kerf for cutting.</summary>
    public double Offset { get; set; }

    /// <summary>Longest side of the traced shape in output units (before Offset is added).</summary>
    public double OutputSize { get; set; } = 100;

    public OutputUnits Units { get; set; } = OutputUnits.Millimeters;

    /// <summary>Longest side, in pixels, the picture is traced at. Higher = more precise and slower.</summary>
    public int WorkingSize { get; set; } = 1600;

    /// <summary>Clicked points (0..1 image coordinates): positive keeps the shape under it, negative drops it.</summary>
    public List<PickPoint> Picks { get; set; } = new();

    public TraceSettings Clone()
    {
        var copy = (TraceSettings)MemberwiseClone();
        copy.Picks = Picks.Select(p => p with { }).ToList();
        return copy;
    }

    public string ToJson() => JsonSerializer.Serialize(this, Json.Options);

    public static TraceSettings FromJson(string json) =>
        (JsonSerializer.Deserialize<TraceSettings>(json, Json.Options) ?? new TraceSettings()).Normalized();

    /// <summary>Clamps every value into its documented range (settings may come from an older or hand-edited file).</summary>
    public TraceSettings Normalized()
    {
        Brightness = Math.Clamp(Brightness, -100, 100);
        Contrast = Math.Clamp(Contrast, -100, 100);
        LocalContrast = Math.Clamp(LocalContrast, 0, 100);
        Blur = Math.Clamp(Blur, 0, 20);
        SmoothTexture = Math.Clamp(SmoothTexture, 0, 100);
        Despeckle = Math.Clamp(Despeckle, 0, 10);
        Detail = Math.Clamp(Detail, 0, 100);
        Threshold = Math.Clamp(Threshold, 0, 100);
        Colors = Math.Clamp(Colors, 2, 8);
        CloseGaps = Math.Clamp(CloseGaps, 0, 40);
        Smoothness = Math.Clamp(Smoothness, 0, 100);
        CornerSharpness = Math.Clamp(CornerSharpness, 0, 100);
        StrokeWidth = Math.Clamp(StrokeWidth, 0, 40);
        Offset = Math.Clamp(Offset, -50, 50);
        OutputSize = Math.Clamp(OutputSize <= 0 ? 100 : OutputSize, 0.01, 100000);
        WorkingSize = Math.Clamp(WorkingSize, 256, 6000);
        Picks ??= new();
        if (!Enum.IsDefined(Mode)) Mode = TraceMode.Silhouette;
        if (!Enum.IsDefined(Curves)) Curves = CurveStyle.LinesAndArcs;
        if (!Enum.IsDefined(Strokes)) Strokes = StrokeStyle.Centerline;
        if (!Enum.IsDefined(Keep)) Keep = KeepShapes.All;
        if (!Enum.IsDefined(Units)) Units = OutputUnits.Millimeters;
        return this;
    }

}

/// <summary>A click on the preview: X/Y in 0..1 of the image, Keep=false means "drop the shape here".</summary>
public sealed record PickPoint(double X, double Y, bool Keep);

internal static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };
}
