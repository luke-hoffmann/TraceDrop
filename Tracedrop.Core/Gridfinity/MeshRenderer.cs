using OpenCvSharp;

namespace Tracedrop.Core.Gridfinity;

/// <summary>
/// A small software renderer for previewing a bin: orthographic view from any angle, depth buffer, soft
/// two-light shading. No GPU, no 3D library: it runs anywhere the app does.
/// </summary>
public static class MeshRenderer
{
    /// <summary>
    /// Renders the mesh into a BGR picture. <paramref name="yaw"/> turns around the vertical axis, <paramref name="pitch"/>
    /// tilts down from horizontal (degrees). <paramref name="zoom"/> 1 fits the mesh.
    /// </summary>
    public static Mat Render(Mesh mesh, int width, int height, double yaw = -35, double pitch = 35, double zoom = 1,
        Scalar? color = null, Scalar? background = null)
    {
        var image = new Mat(height, width, MatType.CV_8UC3, background ?? new Scalar(250, 247, 245));
        if (mesh.TriangleCount == 0 || width < 4 || height < 4) return image;

        var (min, max) = mesh.Bounds();
        var centre = ((min.X + max.X) / 2, (min.Y + max.Y) / 2, (min.Z + max.Z) / 2);
        var radius = Math.Sqrt(Math.Pow(max.X - min.X, 2) + Math.Pow(max.Y - min.Y, 2) + Math.Pow(max.Z - min.Z, 2)) / 2;
        var scale = Math.Min(width, height) / (2.15 * Math.Max(1e-6, radius)) * zoom;

        // View basis: right, up and towards the viewer, from yaw and pitch.
        var ya = yaw * Math.PI / 180;
        var pa = pitch * Math.PI / 180;
        var forward = (X: Math.Sin(ya) * Math.Cos(pa), Y: Math.Cos(ya) * Math.Cos(pa), Z: -Math.Sin(pa)); // looking along
        var right = (X: Math.Cos(ya), Y: -Math.Sin(ya), Z: 0.0);
        var up = (X: right.Y * forward.Z - right.Z * forward.Y, Y: right.Z * forward.X - right.X * forward.Z, Z: right.X * forward.Y - right.Y * forward.X);
        if (up.Z < 0) up = (-up.X, -up.Y, -up.Z);

        var count = mesh.Vertices.Count;
        var sx = new double[count];
        var sy = new double[count];
        var sz = new double[count];
        for (var i = 0; i < count; i++)
        {
            var (x, y, z) = mesh.Vertices[i];
            x -= centre.Item1; y -= centre.Item2; z -= centre.Item3;
            sx[i] = width / 2.0 + (x * right.X + y * right.Y + z * right.Z) * scale;
            sy[i] = height / 2.0 - (x * up.X + y * up.Y + z * up.Z) * scale;
            sz[i] = x * forward.X + y * forward.Y + z * forward.Z; // larger = further away
        }

        var depth = new float[width * height];
        Array.Fill(depth, float.MaxValue);
        var baseColor = color ?? new Scalar(214, 150, 70); // BGR: a clear blue
        var light1 = Normalize((-0.4, -0.6, 0.9));
        var light2 = Normalize((0.7, 0.3, 0.4));
        var pixels = image.GetGenericIndexer<Vec3b>();

        for (var t = 0; t < mesh.Indices.Count; t += 3)
        {
            var a = mesh.Indices[t];
            var b = mesh.Indices[t + 1];
            var c = mesh.Indices[t + 2];
            // Back faces are hidden by the front ones anyway; skip them (counter-clockwise on screen = facing us).
            var area = (sx[b] - sx[a]) * (sy[c] - sy[a]) - (sy[b] - sy[a]) * (sx[c] - sx[a]);
            if (area >= 0) continue;

            var va = mesh.Vertices[a];
            var vb = mesh.Vertices[b];
            var vc = mesh.Vertices[c];
            var n = Normalize(Cross((vb.X - va.X, vb.Y - va.Y, vb.Z - va.Z), (vc.X - va.X, vc.Y - va.Y, vc.Z - va.Z)));
            var shade = 0.38 + 0.5 * Math.Max(0, Dot(n, light1)) + 0.22 * Math.Max(0, Dot(n, light2));
            var top = n.Z > 0.95 ? 1.06 : 1.0; // flat tops a touch lighter, so pocket floors read clearly
            var pixel = new Vec3b(
                (byte)Math.Clamp(baseColor.Val0 * shade * top, 0, 255),
                (byte)Math.Clamp(baseColor.Val1 * shade * top, 0, 255),
                (byte)Math.Clamp(baseColor.Val2 * shade * top, 0, 255));

            var minX = Math.Max(0, (int)Math.Floor(Math.Min(sx[a], Math.Min(sx[b], sx[c]))));
            var maxX = Math.Min(width - 1, (int)Math.Ceiling(Math.Max(sx[a], Math.Max(sx[b], sx[c]))));
            var minY = Math.Max(0, (int)Math.Floor(Math.Min(sy[a], Math.Min(sy[b], sy[c]))));
            var maxY = Math.Min(height - 1, (int)Math.Ceiling(Math.Max(sy[a], Math.Max(sy[b], sy[c]))));
            for (var py = minY; py <= maxY; py++)
            {
                for (var px = minX; px <= maxX; px++)
                {
                    var x = px + 0.5;
                    var y = py + 0.5;
                    var w0 = ((sx[b] - x) * (sy[c] - y) - (sy[b] - y) * (sx[c] - x)) / area;
                    var w1 = ((sx[c] - x) * (sy[a] - y) - (sy[c] - y) * (sx[a] - x)) / area;
                    var w2 = 1 - w0 - w1;
                    if (w0 < -1e-9 || w1 < -1e-9 || w2 < -1e-9) continue;
                    var z = (float)(w0 * sz[a] + w1 * sz[b] + w2 * sz[c]);
                    var index = py * width + px;
                    if (z >= depth[index]) continue;
                    depth[index] = z;
                    pixels[py, px] = pixel;
                }
            }
        }
        return image;
    }

    static (double X, double Y, double Z) Cross((double X, double Y, double Z) a, (double X, double Y, double Z) b) =>
        (a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

    static double Dot((double X, double Y, double Z) a, (double X, double Y, double Z) b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

    static (double X, double Y, double Z) Normalize((double X, double Y, double Z) v)
    {
        var l = Math.Sqrt(Dot(v, v));
        return l < 1e-12 ? (0, 0, 1) : (v.X / l, v.Y / l, v.Z / l);
    }
}
