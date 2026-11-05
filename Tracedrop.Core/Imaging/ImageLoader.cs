using ImageMagick;
using OpenCvSharp;

namespace Tracedrop.Core.Imaging;

/// <summary>A decoded picture: upright BGR pixels (transparent areas composited onto white) and, when the file had real transparency, its alpha.</summary>
public sealed class LoadedImage : IDisposable
{
    public LoadedImage(Mat bgr, Mat? alpha)
    {
        Bgr = bgr;
        Alpha = alpha;
    }

    public Mat Bgr { get; }

    /// <summary>8-bit alpha (255 = opaque), or null when the picture is fully opaque.</summary>
    public Mat? Alpha { get; }

    public int Width => Bgr.Width;
    public int Height => Bgr.Height;

    public void Dispose()
    {
        Bgr.Dispose();
        Alpha?.Dispose();
    }
}

public static class ImageLoader
{
    /// <summary>Extensions offered in the file picker. Anything else is still tried when dropped.</summary>
    public static readonly string[] KnownExtensions =
    {
        ".png", ".jpg", ".jpeg", ".jpe", ".jfif", ".heic", ".heif", ".hif", ".avif", ".webp", ".bmp", ".dib",
        ".tif", ".tiff", ".gif", ".psd", ".jp2", ".tga", ".pbm", ".pgm", ".ppm", ".pnm", ".ico", ".jxl", ".dng"
    };

    static readonly HashSet<string> OpenCvFirst = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".jpe", ".jfif", ".bmp", ".dib", ".webp", ".tif", ".tiff", ".pbm", ".pgm", ".ppm", ".pnm", ".jp2"
    };

    public static bool LooksLikeImage(string path) =>
        KnownExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public static LoadedImage Load(string path) => Load(File.ReadAllBytes(path), Path.GetExtension(path));

    /// <summary>Decodes any supported format. Throws <see cref="InvalidDataException"/> when the bytes are not a picture.</summary>
    public static LoadedImage Load(byte[] bytes, string extensionHint = "")
    {
        if (bytes.Length == 0) throw new InvalidDataException("The file is empty.");

        if (OpenCvFirst.Contains(extensionHint) || extensionHint.Length == 0)
        {
            var decoded = TryOpenCv(bytes);
            if (decoded is not null) return decoded;
        }

        var viaMagick = TryMagick(bytes);
        if (viaMagick is not null) return viaMagick;

        var fallback = TryOpenCv(bytes);
        if (fallback is not null) return fallback;
        throw new InvalidDataException("This file isn't a picture Tracedrop can read.");
    }

    static LoadedImage? TryOpenCv(byte[] bytes)
    {
        // Unchanged keeps alpha and 16-bit data but ignores EXIF orientation, so orientation is applied separately below.
        Mat raw;
        try { raw = Cv2.ImDecode(bytes, ImreadModes.Unchanged | ImreadModes.AnyDepth); }
        catch { return null; }
        if (raw.Empty()) { raw.Dispose(); return null; }

        using (raw)
        {
            using var eightBit = To8Bit(raw);
            var result = Split(eightBit);
            var orientation = ReadExifOrientation(bytes);
            if (orientation > 1) ApplyOrientation(result, orientation);
            return result;
        }
    }

    static LoadedImage? TryMagick(byte[] bytes)
    {
        try
        {
            using var image = new MagickImage(bytes);
            image.AutoOrient();
            if (image.ColorSpace == ColorSpace.CMYK) image.TransformColorSpace(ColorProfiles.SRGB);
            var hasAlpha = image.HasAlpha;
            image.Depth = 8;
            var png = image.ToByteArray(hasAlpha ? MagickFormat.Png32 : MagickFormat.Png24);
            using var raw = Cv2.ImDecode(png, ImreadModes.Unchanged);
            if (raw.Empty()) return null;
            return Split(raw);
        }
        catch (MagickException)
        {
            return null;
        }
    }

    static Mat To8Bit(Mat raw)
    {
        if (raw.Depth() == MatType.CV_8U) return raw.Clone();
        var scale = raw.Depth() == MatType.CV_16U ? 1.0 / 257.0 : raw.Depth() == MatType.CV_32F ? 255.0 : 1.0;
        var converted = new Mat();
        raw.ConvertTo(converted, MatType.CV_8U, scale);
        return converted;
    }

    /// <summary>Turns 1/3/4-channel 8-bit pixels into BGR plus optional alpha (composited on white).</summary>
    static LoadedImage Split(Mat eightBit)
    {
        switch (eightBit.Channels())
        {
            case 1:
            {
                var bgr = new Mat();
                Cv2.CvtColor(eightBit, bgr, ColorConversionCodes.GRAY2BGR);
                return new LoadedImage(bgr, null);
            }
            case 3:
                return new LoadedImage(eightBit.Clone(), null);
            case 4:
            {
                var channels = Cv2.Split(eightBit);
                try
                {
                    var alpha = channels[3];
                    Cv2.MinMaxLoc(alpha, out double minAlpha, out _);
                    var bgr = new Mat();
                    Cv2.Merge(new[] { channels[0], channels[1], channels[2] }, bgr);
                    if (minAlpha >= 250) return new LoadedImage(bgr, null);

                    // Composite onto white so transparent areas read as empty paper.
                    using var a = new Mat();
                    alpha.ConvertTo(a, MatType.CV_32F, 1 / 255.0);
                    using var a3 = new Mat();
                    Cv2.Merge(new[] { a, a, a }, a3);
                    using var f = new Mat();
                    bgr.ConvertTo(f, MatType.CV_32FC3);
                    using var inv = new Mat();
                    Cv2.Subtract(Scalar.All(1.0), a3, inv);
                    using var white = new Mat();
                    Cv2.Multiply(inv, Scalar.All(255.0), white);
                    using var fg = new Mat();
                    Cv2.Multiply(f, a3, fg);
                    using var sum = new Mat();
                    Cv2.Add(fg, white, sum);
                    sum.ConvertTo(bgr, MatType.CV_8UC3);
                    return new LoadedImage(bgr, alpha.Clone());
                }
                finally
                {
                    foreach (var c in channels) c.Dispose();
                }
            }
            default:
                throw new InvalidDataException($"Unsupported channel count {eightBit.Channels()}.");
        }
    }

    static void ApplyOrientation(LoadedImage image, int orientation)
    {
        Transform(image.Bgr, orientation);
        if (image.Alpha is not null) Transform(image.Alpha, orientation);
    }

    static void Transform(Mat m, int orientation)
    {
        switch (orientation)
        {
            case 2: Cv2.Flip(m, m, FlipMode.Y); break;
            case 3: Cv2.Rotate(m, m, RotateFlags.Rotate180); break;
            case 4: Cv2.Flip(m, m, FlipMode.X); break;
            case 5: Cv2.Transpose(m, m); break;
            case 6: Cv2.Rotate(m, m, RotateFlags.Rotate90Clockwise); break;
            case 7: Cv2.Transpose(m, m); Cv2.Flip(m, m, FlipMode.XY); break;
            case 8: Cv2.Rotate(m, m, RotateFlags.Rotate90Counterclockwise); break;
        }
    }

    /// <summary>Reads the EXIF orientation tag (1..8) from a JPEG/TIFF/WebP-style Exif block, or 1 when there is none.</summary>
    internal static int ReadExifOrientation(byte[] bytes)
    {
        try
        {
            // Find "Exif\0\0" (JPEG APP1, WebP EXIF chunk) or a bare TIFF header at the start.
            var tiff = -1;
            if (bytes.Length > 8 && ((bytes[0] == 'I' && bytes[1] == 'I' && bytes[2] == 42) || (bytes[0] == 'M' && bytes[1] == 'M' && bytes[3] == 42)))
                tiff = 0;
            else
            {
                var limit = Math.Min(bytes.Length - 10, 256 * 1024);
                for (var i = 0; i < limit; i++)
                {
                    if (bytes[i] == 'E' && bytes[i + 1] == 'x' && bytes[i + 2] == 'i' && bytes[i + 3] == 'f' && bytes[i + 4] == 0 && bytes[i + 5] == 0)
                    {
                        tiff = i + 6;
                        break;
                    }
                }
            }
            if (tiff < 0 || tiff + 8 > bytes.Length) return 1;

            var little = bytes[tiff] == 'I';
            int U16(int at) => little ? bytes[at] | bytes[at + 1] << 8 : bytes[at] << 8 | bytes[at + 1];
            int U32(int at) => little
                ? bytes[at] | bytes[at + 1] << 8 | bytes[at + 2] << 16 | bytes[at + 3] << 24
                : bytes[at] << 24 | bytes[at + 1] << 16 | bytes[at + 2] << 8 | bytes[at + 3];

            var ifd = tiff + U32(tiff + 4);
            if (ifd + 2 > bytes.Length) return 1;
            var count = U16(ifd);
            for (var e = 0; e < count; e++)
            {
                var entry = ifd + 2 + e * 12;
                if (entry + 12 > bytes.Length) break;
                if (U16(entry) == 0x0112)
                {
                    var value = U16(entry + 8);
                    return value is >= 1 and <= 8 ? value : 1;
                }
            }
        }
        catch (IndexOutOfRangeException)
        {
        }
        return 1;
    }
}
