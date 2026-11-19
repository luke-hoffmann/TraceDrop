using System.Globalization;
using System.IO.Compression;
using System.Text;
using Clipper2Lib;
using Tracedrop.Core.Export;
using Tracedrop.Core.Vector;

namespace Tracedrop.Core.Gridfinity;

/// <summary>Writes bins: STL and 3MF for printing, and the top-view layout (DXF / SVG / PDF) for foam or laser.</summary>
public static class BinExport
{
    public static void WriteMesh(Mesh mesh, ExportFormat format, Stream output, string name = "Gridfinity bin")
    {
        switch (format)
        {
            case ExportFormat.Stl: WriteStl(mesh, output, name); break;
            case ExportFormat.ThreeMf: Write3mf(mesh, output, name); break;
            default: throw new ArgumentOutOfRangeException(nameof(format), "Not a mesh format.");
        }
    }

    public static void WriteMeshFile(Mesh mesh, ExportFormat format, string path, string name = "Gridfinity bin")
    {
        var temp = path + ".tmp";
        using (var stream = File.Create(temp)) WriteMesh(mesh, format, stream, name);
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Binary STL in millimetres.</summary>
    public static void WriteStl(Mesh mesh, Stream output, string name)
    {
        using var w = new BinaryWriter(output, Encoding.ASCII, leaveOpen: true);
        var header = new byte[80];
        var text = Encoding.ASCII.GetBytes(("Tracedrop " + name).PadRight(80)[..80]);
        Array.Copy(text, header, 80);
        w.Write(header);
        w.Write((uint)mesh.TriangleCount);
        for (var i = 0; i < mesh.Indices.Count; i += 3)
        {
            var a = mesh.Vertices[mesh.Indices[i]];
            var b = mesh.Vertices[mesh.Indices[i + 1]];
            var c = mesh.Vertices[mesh.Indices[i + 2]];
            double ux = b.X - a.X, uy = b.Y - a.Y, uz = b.Z - a.Z, vx = c.X - a.X, vy = c.Y - a.Y, vz = c.Z - a.Z;
            double nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
            var len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
            if (len > 0) { nx /= len; ny /= len; nz /= len; }
            w.Write((float)nx); w.Write((float)ny); w.Write((float)nz);
            foreach (var v in new[] { a, b, c }) { w.Write((float)v.X); w.Write((float)v.Y); w.Write((float)v.Z); }
            w.Write((ushort)0);
        }
    }

    /// <summary>3MF (core spec): one mesh object in millimetres.</summary>
    public static void Write3mf(Mesh mesh, Stream output, string name)
    {
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        void Entry(string path, string content)
        {
            var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write(content);
        }
        Entry("[Content_Types].xml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
            "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
            "<Default Extension=\"model\" ContentType=\"application/vnd.ms-package.3dmanufacturing-3dmodel+xml\"/></Types>");
        Entry("_rels/.rels",
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Target=\"/3D/3dmodel.model\" Id=\"rel0\" Type=\"http://schemas.microsoft.com/3dmanufacturing/2013/01/3dmodel\"/></Relationships>");

        var sb = new StringBuilder(mesh.Vertices.Count * 60 + mesh.TriangleCount * 50);
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<model unit=\"millimeter\" xml:lang=\"en-US\" xmlns=\"http://schemas.microsoft.com/3dmanufacturing/core/2015/02\">\n");
        sb.Append(" <metadata name=\"Application\">Tracedrop</metadata>\n <resources>\n");
        sb.Append($"  <object id=\"1\" name=\"{System.Security.SecurityElement.Escape(name)}\" type=\"model\">\n   <mesh>\n    <vertices>\n");
        foreach (var v in mesh.Vertices)
            sb.Append("     <vertex x=\"").Append(F(v.X)).Append("\" y=\"").Append(F(v.Y)).Append("\" z=\"").Append(F(v.Z)).Append("\"/>\n");
        sb.Append("    </vertices>\n    <triangles>\n");
        for (var i = 0; i < mesh.Indices.Count; i += 3)
            sb.Append("     <triangle v1=\"").Append(mesh.Indices[i]).Append("\" v2=\"").Append(mesh.Indices[i + 1]).Append("\" v3=\"").Append(mesh.Indices[i + 2]).Append("\"/>\n");
        sb.Append("    </triangles>\n   </mesh>\n  </object>\n </resources>\n <build>\n  <item objectid=\"1\"/>\n </build>\n</model>\n");
        Entry("3D/3dmodel.model", sb.ToString());
    }

    static string F(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);

    /// <summary>
    /// The bin seen from above, true size in mm (y up): the bin's outline (layer BIN), each pocket with its finger
    /// holes (POCKETS) and the label strokes (LABELS). Good for cutting foam or a laser-cut insert.
    /// </summary>
    public static VectorDrawing Layout(BinDesign design)
    {
        var paths = new List<VectorPath>();
        void Add(IEnumerable<Path64> region, string layer, (byte, byte, byte) color, PathRole role)
        {
            foreach (var p in region)
            {
                if (p.Count < 3) continue;
                var pts = p.Select(q => new Vec2(q.X / Designer.Scale, q.Y / Designer.Scale)).ToList();
                var segments = new List<Segment>();
                for (var i = 0; i < pts.Count; i++) segments.Add(new LineSegment(pts[i], pts[(i + 1) % pts.Count]));
                paths.Add(new VectorPath { Segments = segments, Closed = true, Role = role, Layer = layer, Color = color });
            }
        }
        var outline = RoundedRect(0, 0, design.Width, design.Depth, Spec.OuterRadius);
        Add(new[] { outline }, "BIN", (0, 0, 0), PathRole.Outer);
        foreach (var t in design.Tools) Add(Designer.Pocket(t, design.Settings), "POCKETS", (200, 30, 60), PathRole.Hole);
        foreach (var l in design.Labels) Add(Designer.LabelRegion(l), "LABELS", (30, 90, 200), PathRole.Hole);
        return new VectorDrawing(design.Width, design.Depth, paths);
    }

    static Path64 RoundedRect(double x0, double y0, double x1, double y1, double r)
    {
        var path = new Path64();
        foreach (var (cx, cy, start) in new[] { (x1 - r, y0 + r, -90.0), (x1 - r, y1 - r, 0.0), (x0 + r, y1 - r, 90.0), (x0 + r, y0 + r, 180.0) })
            for (var i = 0; i <= 8; i++)
            {
                var a = (start + 90.0 * i / 8) * Math.PI / 180;
                path.Add(new Point64((long)Math.Round((cx + r * Math.Cos(a)) * Designer.Scale), (long)Math.Round((cy + r * Math.Sin(a)) * Designer.Scale)));
            }
        return path;
    }
}
