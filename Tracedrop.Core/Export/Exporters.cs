using System.Globalization;
using System.Text;
using netDxf;
using netDxf.Entities;
using netDxf.Header;
using netDxf.Tables;
using Tracedrop.Core.Vector;
using DxfVector2 = netDxf.Vector2;
using DxfVector3 = netDxf.Vector3;

namespace Tracedrop.Core.Export;

public enum ExportFormat
{
    Dxf,
    Svg,
    Pdf
}

public static class Exporters
{
    public static string Extension(ExportFormat format) => format switch
    {
        ExportFormat.Dxf => ".dxf",
        ExportFormat.Svg => ".svg",
        ExportFormat.Pdf => ".pdf",
        _ => throw new ArgumentOutOfRangeException(nameof(format))
    };

    /// <summary>Writes the drawing (already in output units, y up) in the given format.</summary>
    public static void Write(VectorDrawing drawingInUnits, OutputUnits units, ExportFormat format, Stream output)
    {
        switch (format)
        {
            case ExportFormat.Dxf: Dxf.Write(drawingInUnits, units, output); break;
            case ExportFormat.Svg: Svg.Write(drawingInUnits, units, output); break;
            case ExportFormat.Pdf: Pdf.Write(drawingInUnits, units, output); break;
        }
    }

    public static void WriteFile(VectorDrawing drawingInUnits, OutputUnits units, ExportFormat format, string path)
    {
        // Write next to the target and swap in, so a file being dragged is never half-written.
        var temp = path + ".tmp";
        using (var stream = File.Create(temp)) Write(drawingInUnits, units, format, stream);
        File.Move(temp, path, overwrite: true);
    }

    internal static string F(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);
}

/// <summary>
/// DXF (AutoCAD 2000 format, opens in SolidWorks, Fusion, AutoCAD, Inkscape, LightBurn). Lines and arcs become one
/// LWPOLYLINE per outline with bulges, so a closed outline is a single closed entity that extrudes directly;
/// splines become SPLINE entities; snapped circles become CIRCLE. Layers: OUTLINE, HOLES, LINES, COLOR_n.
/// </summary>
public static class Dxf
{
    public static void Write(VectorDrawing d, OutputUnits units, Stream output)
    {
        var doc = new DxfDocument(DxfVersion.AutoCad2000);
        doc.DrawingVariables.InsUnits = units == OutputUnits.Inches ? netDxf.Units.DrawingUnits.Inches : netDxf.Units.DrawingUnits.Millimeters;

        var layers = new Dictionary<string, Layer>(StringComparer.OrdinalIgnoreCase);
        Layer LayerFor(VectorPath path)
        {
            if (layers.TryGetValue(path.Layer, out var layer)) return layer;
            layer = new Layer(path.Layer)
            {
                Color = path.Layer switch
                {
                    "OUTLINE" => AciColor.Default,
                    "HOLES" => AciColor.Blue,
                    "LINES" => AciColor.Default,
                    _ => new AciColor(path.Color.R, path.Color.G, path.Color.B)
                }
            };
            layers[path.Layer] = layer;
            return layer;
        }

        foreach (var path in d.Paths)
        {
            var layer = LayerFor(path);
            if (path.Circle is { } c)
            {
                doc.Entities.Add(new Circle(new DxfVector3(c.Center.X, c.Center.Y, 0), c.Radius) { Layer = layer });
                continue;
            }
            if (path.Segments.Count == 0) continue;

            if (path.Segments.All(s => s is CubicSegment))
            {
                AddSpline(doc, path, layer);
                continue;
            }
            if (path.Segments.Any(s => s is CubicSegment))
            {
                // Mixed: polyline runs between spline runs.
                foreach (var group in Runs(path.Segments))
                {
                    if (group[0] is CubicSegment) AddSpline(doc, new VectorPath { Segments = group, Closed = false }, layer);
                    else AddPolyline(doc, group, false, layer);
                }
                continue;
            }
            AddPolyline(doc, path.Segments, path.Closed, layer);
        }

        doc.Save(output);
    }

    static IEnumerable<List<Segment>> Runs(List<Segment> segments)
    {
        var current = new List<Segment>();
        foreach (var s in segments)
        {
            if (current.Count > 0 && (current[0] is CubicSegment) != (s is CubicSegment))
            {
                yield return current;
                current = new List<Segment>();
            }
            current.Add(s);
        }
        if (current.Count > 0) yield return current;
    }

    static void AddPolyline(DxfDocument doc, List<Segment> segments, bool closed, Layer layer)
    {
        var vertices = new List<Polyline2DVertex>();
        foreach (var s in segments)
        {
            var bulge = s is ArcSegment arc ? arc.Bulge : 0;
            vertices.Add(new Polyline2DVertex(new DxfVector2(s.Start.X, s.Start.Y), bulge));
        }
        if (!closed) vertices.Add(new Polyline2DVertex(new DxfVector2(segments[^1].End.X, segments[^1].End.Y), 0));
        if (vertices.Count < 2) return;
        doc.Entities.Add(new Polyline2D(vertices, closed) { Layer = layer });
    }

    /// <summary>A chain of cubic Beziers as one clamped degree-3 B-spline (each Bezier is a span with knot multiplicity 3).</summary>
    static void AddSpline(DxfDocument doc, VectorPath path, Layer layer)
    {
        var cubics = path.Segments.Cast<CubicSegment>().ToList();
        var controls = new List<DxfVector3> { new(cubics[0].Start.X, cubics[0].Start.Y, 0) };
        foreach (var c in cubics)
        {
            controls.Add(new DxfVector3(c.Control1.X, c.Control1.Y, 0));
            controls.Add(new DxfVector3(c.Control2.X, c.Control2.Y, 0));
            controls.Add(new DxfVector3(c.End.X, c.End.Y, 0));
        }
        var knots = new List<double> { 0, 0, 0, 0 };
        for (var i = 1; i < cubics.Count; i++) { knots.Add(i); knots.Add(i); knots.Add(i); }
        knots.AddRange(new double[] { cubics.Count, cubics.Count, cubics.Count, cubics.Count });
        var spline = new Spline(controls, null, knots, 3, false) { Layer = layer };
        doc.Entities.Add(spline);
    }
}

/// <summary>SVG sized in real units. Outlines are hairline strokes (what laser and CAD importers expect); filled shapes use even-odd fill when asked.</summary>
public static class Svg
{
    public static void Write(VectorDrawing d, OutputUnits units, Stream output, bool fill = false)
    {
        var unit = units == OutputUnits.Inches ? "in" : "mm";
        var stroke = units == OutputUnits.Inches ? 0.004 : 0.1;
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
        sb.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{F(d.Width)}{unit}\" height=\"{F(d.Height)}{unit}\" viewBox=\"0 0 {F(d.Width)} {F(d.Height)}\">\n");
        sb.Append("  <title>Traced with Tracedrop</title>\n");
        foreach (var group in d.Paths.GroupBy(p => p.Layer))
        {
            sb.Append($"  <g id=\"{Escape(group.Key)}\">\n");
            // SVG's y axis points down: flip back from CAD coordinates.
            var shapes = group.ToList();
            var color = shapes[0].Color;
            var hex = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
            if (fill && shapes.All(p => p.Closed || p.Circle is not null))
            {
                sb.Append($"    <path fill=\"{hex}\" fill-rule=\"evenodd\" stroke=\"none\" d=\"");
                foreach (var p in shapes) sb.Append(PathData(p, d.Height)).Append(' ');
                sb.Append("\"/>\n");
            }
            else
            {
                foreach (var p in shapes)
                    sb.Append($"    <path fill=\"none\" stroke=\"{hex}\" stroke-width=\"{F(stroke)}\" stroke-linejoin=\"round\" stroke-linecap=\"round\" d=\"{PathData(p, d.Height)}\"/>\n");
            }
            sb.Append("  </g>\n");
        }
        sb.Append("</svg>\n");
        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
        output.Write(bytes);
    }

    static string Escape(string s) => s.Replace("&", "&amp;").Replace("\"", "&quot;").Replace("<", "&lt;");
    static string F(double v) => Exporters.F(v);

    internal static string PathData(VectorPath p, double height)
    {
        Vec2 Y(Vec2 v) => new(v.X, height - v.Y);
        var sb = new StringBuilder();
        if (p.Circle is { } c)
        {
            var center = Y(c.Center);
            sb.Append($"M{F(center.X - c.Radius)},{F(center.Y)} a{F(c.Radius)},{F(c.Radius)} 0 1,0 {F(2 * c.Radius)},0 a{F(c.Radius)},{F(c.Radius)} 0 1,0 {F(-2 * c.Radius)},0 Z");
            return sb.ToString();
        }
        if (p.Segments.Count == 0) return "";
        var start = Y(p.Segments[0].Start);
        sb.Append($"M{F(start.X)},{F(start.Y)}");
        foreach (var s in p.Segments)
        {
            switch (s)
            {
                case LineSegment l:
                {
                    var e = Y(l.End);
                    sb.Append($" L{F(e.X)},{F(e.Y)}");
                    break;
                }
                case ArcSegment a:
                {
                    var flipped = new ArcSegment(Y(a.Start), Y(a.Mid), Y(a.End));
                    var (_, r) = flipped.Circle();
                    var sweep = flipped.Sweep;
                    var large = Math.Abs(sweep) > Math.PI ? 1 : 0;
                    // In SVG's y-down frame, sweep-flag 1 is a "positive angle" (clockwise on screen) = left turn in math terms.
                    var sweepFlag = sweep > 0 ? 1 : 0;
                    var e = flipped.End;
                    sb.Append($" A{F(r)},{F(r)} 0 {large},{sweepFlag} {F(e.X)},{F(e.Y)}");
                    break;
                }
                case CubicSegment b:
                {
                    var c1 = Y(b.Control1);
                    var c2 = Y(b.Control2);
                    var e = Y(b.End);
                    sb.Append($" C{F(c1.X)},{F(c1.Y)} {F(c2.X)},{F(c2.Y)} {F(e.X)},{F(e.Y)}");
                    break;
                }
            }
        }
        if (p.Closed) sb.Append(" Z");
        return sb.ToString();
    }
}

/// <summary>A one-page vector PDF at true size (opens in Illustrator, Inkscape, CorelDRAW, LightBurn, browsers).</summary>
public static class Pdf
{
    public static void Write(VectorDrawing d, OutputUnits units, Stream output)
    {
        var toPoints = units == OutputUnits.Inches ? 72.0 : 72.0 / 25.4;
        var margin = 0.0;
        var w = d.Width * toPoints + 2 * margin;
        var h = d.Height * toPoints + 2 * margin;

        var content = new StringBuilder();
        content.Append("0.25 w 1 J 1 j\n");
        foreach (var p in d.Paths)
        {
            content.Append($"{F(p.Color.R / 255.0)} {F(p.Color.G / 255.0)} {F(p.Color.B / 255.0)} RG\n");
            // PDF's y axis points up, like the drawing: no flip needed. Arcs are drawn as cubic Beziers.
            Vec2 T(Vec2 v) => new(v.X * toPoints + margin, v.Y * toPoints + margin);
            if (p.Circle is { } c)
            {
                AppendCircle(content, T(c.Center), c.Radius * toPoints);
                content.Append("S\n");
                continue;
            }
            if (p.Segments.Count == 0) continue;
            var s0 = T(p.Segments[0].Start);
            content.Append($"{F(s0.X)} {F(s0.Y)} m\n");
            foreach (var s in p.Segments)
            {
                switch (s)
                {
                    case LineSegment l:
                    {
                        var e = T(l.End);
                        content.Append($"{F(e.X)} {F(e.Y)} l\n");
                        break;
                    }
                    case CubicSegment b:
                    {
                        var c1 = T(b.Control1); var c2 = T(b.Control2); var e = T(b.End);
                        content.Append($"{F(c1.X)} {F(c1.Y)} {F(c2.X)} {F(c2.Y)} {F(e.X)} {F(e.Y)} c\n");
                        break;
                    }
                    case ArcSegment a:
                    {
                        foreach (var b in ArcToCubics(new ArcSegment(T(a.Start), T(a.Mid), T(a.End))))
                            content.Append($"{F(b.Control1.X)} {F(b.Control1.Y)} {F(b.Control2.X)} {F(b.Control2.Y)} {F(b.End.X)} {F(b.End.Y)} c\n");
                        break;
                    }
                }
            }
            content.Append(p.Closed ? "s\n" : "S\n");
        }

        var stream = Encoding.ASCII.GetBytes(content.ToString());
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {F(w)} {F(h)}] /Contents 4 0 R /Resources << >> >>",
            null!, // content stream, written separately
            "<< /Producer (Tracedrop) /Title (Traced outline) >>"
        };

        using var writer = new BinaryWriter(output, Encoding.ASCII, leaveOpen: true);
        var offsets = new List<long>();
        void Raw(string s) => writer.Write(Encoding.ASCII.GetBytes(s));
        Raw("%PDF-1.4\n%");
        writer.Write(new byte[] { 0xE2, 0xE3, 0xCF, 0xD3, (byte)'\n' }); // marks the file as binary for transfer tools
        for (var i = 0; i < objects.Count; i++)
        {
            writer.Flush();
            offsets.Add(output.Position);
            Raw($"{i + 1} 0 obj\n");
            if (i == 3)
            {
                Raw($"<< /Length {stream.Length} >>\nstream\n");
                writer.Write(stream);
                Raw("\nendstream\n");
            }
            else Raw(objects[i] + "\n");
            Raw("endobj\n");
        }
        writer.Flush();
        var xref = output.Position;
        Raw($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) Raw($"{o:D10} 00000 n \n");
        Raw($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R /Info 5 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        writer.Flush();
    }

    static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    static void AppendCircle(StringBuilder sb, Vec2 c, double r)
    {
        const double k = 0.5522847498;
        sb.Append($"{F(c.X + r)} {F(c.Y)} m\n");
        sb.Append($"{F(c.X + r)} {F(c.Y + k * r)} {F(c.X + k * r)} {F(c.Y + r)} {F(c.X)} {F(c.Y + r)} c\n");
        sb.Append($"{F(c.X - k * r)} {F(c.Y + r)} {F(c.X - r)} {F(c.Y + k * r)} {F(c.X - r)} {F(c.Y)} c\n");
        sb.Append($"{F(c.X - r)} {F(c.Y - k * r)} {F(c.X - k * r)} {F(c.Y - r)} {F(c.X)} {F(c.Y - r)} c\n");
        sb.Append($"{F(c.X + k * r)} {F(c.Y - r)} {F(c.X + r)} {F(c.Y - k * r)} {F(c.X + r)} {F(c.Y)} c\n");
    }

    /// <summary>Splits an arc into ≤90° pieces, each as a cubic Bezier.</summary>
    internal static IEnumerable<CubicSegment> ArcToCubics(ArcSegment arc)
    {
        var (center, radius) = arc.Circle();
        var sweep = arc.Sweep;
        var pieces = Math.Max(1, (int)Math.Ceiling(Math.Abs(sweep) / (Math.PI / 2)));
        var a0 = Math.Atan2(arc.Start.Y - center.Y, arc.Start.X - center.X);
        var step = sweep / pieces;
        var k = 4.0 / 3.0 * Math.Tan(step / 4);
        var start = arc.Start;
        for (var i = 0; i < pieces; i++)
        {
            var a1 = a0 + step;
            var end = i == pieces - 1 ? arc.End : new Vec2(center.X + radius * Math.Cos(a1), center.Y + radius * Math.Sin(a1));
            var t0 = new Vec2(-Math.Sin(a0), Math.Cos(a0));
            var t1 = new Vec2(-Math.Sin(a1), Math.Cos(a1));
            yield return new CubicSegment(start, start + t0 * (k * radius), end - t1 * (k * radius), end);
            start = end;
            a0 = a1;
        }
    }
}
