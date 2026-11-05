using System.Globalization;
using System.Text.Json;
using OpenCvSharp;
using Tracedrop.Core;
using Tracedrop.Core.Ai;
using Tracedrop.Core.Export;
using Tracedrop.Core.Imaging;
using Tracedrop.Core.Vector;

// Command-line tracer: the same engine as the app, for scripting, batch jobs and development.
//   tracedrop-cli <image>... [--preset NAME] [--set Key=Value]... [--out DIR] [--formats dxf,svg,pdf]
//                 [--render] [--models DIR] [--cpu] [--list-presets]

var inputs = new List<string>();
var presetName = Presets.Default.Name;
var overrides = new List<(string Key, string Value)>();
var outDir = Directory.GetCurrentDirectory();
var formats = new List<ExportFormat> { ExportFormat.Dxf, ExportFormat.Svg };
var render = false;
string? modelDir = null;
var cpu = false;

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--preset": presetName = args[++i]; break;
        case "--set":
        {
            var kv = args[++i].Split('=', 2);
            overrides.Add((kv[0], kv.Length > 1 ? kv[1] : "true"));
            break;
        }
        case "--out": outDir = args[++i]; break;
        case "--formats":
            formats = args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(f => Enum.Parse<ExportFormat>(f.Trim(), ignoreCase: true)).ToList();
            break;
        case "--render": render = true; break;
        case "--models": modelDir = args[++i]; break;
        case "--cpu": cpu = true; break;
        case "--bench-edges":
        {
            using var store = new ModelStore(args[++i]) { PreferGpu = false };
            using var img = ImageLoader.Load(args[++i]);
            var warm = System.Diagnostics.Stopwatch.StartNew();
            using (var tiny = new Mat(64, 64, MatType.CV_8UC3, Scalar.All(128))) { var w = AiModels.Edges(store, tiny); w?.Fused.Dispose(); w?.Average.Dispose(); }
            Console.WriteLine($"warm-up 64x64: {warm.ElapsedMilliseconds} ms");
            for (var run = 0; run < 3; run++)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var e = AiModels.Edges(store, img.Bgr);
                Console.WriteLine($"edges {img.Width}x{img.Height}: {sw.ElapsedMilliseconds} ms");
                e?.Fused.Dispose(); e?.Average.Dispose();
            }
            return 0;
        }
        case "--list-presets":
            foreach (var p in Presets.BuiltIn) Console.WriteLine($"{p.Name,-20} {p.Description}");
            return 0;
        case "-h":
        case "--help":
            Console.WriteLine("tracedrop-cli <image>... [--preset NAME] [--set Key=Value]... [--out DIR] [--formats dxf,svg,pdf] [--render] [--models DIR] [--cpu] [--list-presets]");
            return 0;
        default: inputs.Add(args[i]); break;
    }
}

if (inputs.Count == 0)
{
    Console.Error.WriteLine("No input pictures. Try --help.");
    return 2;
}

var preset = Presets.Find(presetName);
if (preset is null)
{
    Console.Error.WriteLine($"Unknown preset '{presetName}'. Try --list-presets.");
    return 2;
}

var settings = ApplyOverrides(preset.Settings.Clone(), overrides);
Directory.CreateDirectory(outDir);
using var models = modelDir is null ? new ModelStore() : new ModelStore(modelDir);
models.PreferGpu = !cpu;

var failures = 0;
foreach (var input in inputs)
{
    try
    {
        using var job = new TraceJob(ImageLoader.Load(input), models);
        var result = job.Run(settings);
        var stem = Path.GetFileNameWithoutExtension(input);
        var inUnits = result.InUnits();
        foreach (var format in formats)
            Exporters.WriteFile(inUnits, result.Settings.Units, format, Path.Combine(outDir, stem + Exporters.Extension(format)));
        if (render)
        {
            Render(job, result, Path.Combine(outDir, stem + ".preview.png"));
            using var detected = job.Detected!.RenderPreview();
            Cv2.ImWrite(Path.Combine(outDir, stem + ".detected.png"), detected);
        }
        Console.WriteLine($"{Path.GetFileName(input)}: {result.Drawing.Paths.Count} paths, {result.Drawing.EntityCount} entities " +
                          $"({result.Drawing.ClosedCount} closed, {result.Drawing.OpenCount} open) in {result.Elapsed.TotalMilliseconds:0} ms" +
                          (models.Device is { } d ? $" on {d}" : ""));
        foreach (var note in result.Notes) Console.WriteLine("  note: " + note);
        Console.WriteLine("  stages: " + string.Join(", ", result.StageMs.Select(kv => $"{kv.Key} {kv.Value:0}ms")));
    }
    catch (Exception ex)
    {
        failures++;
        Console.Error.WriteLine($"{Path.GetFileName(input)}: {ex.Message}");
    }
}
return failures == 0 ? 0 : 1;

static TraceSettings ApplyOverrides(TraceSettings s, List<(string Key, string Value)> overrides)
{
    if (overrides.Count == 0) return s;
    using var doc = JsonDocument.Parse(s.ToJson());
    var dict = doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone(), StringComparer.OrdinalIgnoreCase);
    foreach (var (key, value) in overrides)
    {
        var name = dict.Keys.FirstOrDefault(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase))
                   ?? throw new ArgumentException($"Unknown setting '{key}'.");
        object? parsed = double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number
            : bool.TryParse(value, out var flag) ? flag
            : value;
        dict[name] = parsed;
    }
    return TraceSettings.FromJson(JsonSerializer.Serialize(dict));
}

static void Render(TraceJob job, TraceResult result, string path)
{
    using var canvas = new Mat();
    // Faded picture underneath, vector on top: outlines dark blue, holes orange, lines red, circles green.
    job.Working!.ConvertTo(canvas, MatType.CV_8UC3, 0.35, 165);
    foreach (var p in result.Drawing.Paths)
    {
        var color = p.Circle is not null ? new Scalar(40, 160, 40) : p.Role switch
        {
            PathRole.Hole => new Scalar(0, 120, 230),
            PathRole.Stroke => new Scalar(30, 30, 200),
            _ => new Scalar(140, 40, 20)
        };
        var points = p.Flatten(0.25).Select(v => new Point((int)Math.Round(v.X * 4), (int)Math.Round(v.Y * 4))).ToArray();
        if (points.Length >= 2) Cv2.Polylines(canvas, new[] { points }, false, color, 2, LineTypes.AntiAlias, 2);
        // Segment joints as small dots, so the number of entities is visible.
        foreach (var s in p.Segments)
            Cv2.Circle(canvas, new Point((int)(s.Start.X * 4), (int)(s.Start.Y * 4)), 6, new Scalar(0, 0, 0), -1, LineTypes.AntiAlias, 2);
    }
    Cv2.ImWrite(path, canvas);
}
