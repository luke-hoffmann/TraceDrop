namespace Tracedrop.Core;

/// <summary>A named set of settings. Built-in presets ship with the app; user presets live in the portable store.</summary>
public sealed record Preset(string Name, string Description, TraceSettings Settings, bool BuiltIn = false);

public static class Presets
{
    public static IReadOnlyList<Preset> BuiltIn { get; } = new[]
    {
        new Preset("Object / tool", "Outer outline and holes of an object on a plain surface. Foam inserts, shadow boards, templates.",
            new TraceSettings
            {
                Mode = TraceMode.Silhouette, Detail = 40, Threshold = 50, Smoothness = 55, CornerSharpness = 45,
                Curves = CurveStyle.LinesAndArcs, SnapCircles = true, Despeckle = 1
            }, true),

        new Preset("Part to copy", "Accurate edges, sharp corners and true circles for reverse-engineering a flat part.",
            new TraceSettings
            {
                Mode = TraceMode.Silhouette, Detail = 70, Threshold = 50, Smoothness = 30, CornerSharpness = 75,
                Curves = CurveStyle.LinesAndArcs, SnapCircles = true, Despeckle = 1
            }, true),

        new Preset("Logo / artwork", "Crisp filled shapes from graphics, prints and stencils.",
            new TraceSettings
            {
                Mode = TraceMode.Shapes, AutoThreshold = true, Colors = 2, Detail = 75, Smoothness = 35,
                CornerSharpness = 80, Curves = CurveStyle.LinesAndArcs, SnapCircles = true, WorkingSize = 2000
            }, true),

        new Preset("Sketch / drawing", "Pen and pencil lines as single centerlines.",
            new TraceSettings
            {
                Mode = TraceMode.LineArt, Detail = 55, Threshold = 50, Smoothness = 55, CornerSharpness = 50,
                Strokes = StrokeStyle.Centerline, Curves = CurveStyle.LinesAndArcs, LocalContrast = 30, Despeckle = 1
            }, true),

        new Preset("Face line drawing", "A clean pen portrait of a face for engraving.",
            new TraceSettings
            {
                Mode = TraceMode.LineArt, Detail = 45, Threshold = 50, Smoothness = 65, CornerSharpness = 35,
                Strokes = StrokeStyle.Centerline, Curves = CurveStyle.Splines, SmoothTexture = 40, IsolateSubject = true
            }, true),

        new Preset("Leaf / nature", "Organic outlines: leaves, branches, flowers, animals.",
            new TraceSettings
            {
                Mode = TraceMode.Silhouette, Detail = 60, Threshold = 50, Smoothness = 45, CornerSharpness = 55,
                Curves = CurveStyle.Splines, SnapCircles = false, Despeckle = 1
            }, true),

        new Preset("All edges", "Every visible edge as lines. Turn Detail down for just the main ones.",
            new TraceSettings
            {
                Mode = TraceMode.Edges, Detail = 55, Threshold = 50, Smoothness = 50, CornerSharpness = 50,
                Strokes = StrokeStyle.Centerline, Curves = CurveStyle.LinesAndArcs, SmoothTexture = 25
            }, true),
    };

    public static Preset Default => BuiltIn[0];

    public static Preset? Find(string name, IEnumerable<Preset>? userPresets = null) =>
        (userPresets ?? Enumerable.Empty<Preset>()).Concat(BuiltIn)
            .FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
}
