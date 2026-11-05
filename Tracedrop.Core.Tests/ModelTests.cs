using Tracedrop.Core.Ai;
using Tracedrop.Core.Vector;
using Xunit;
using Xunit.Abstractions;

namespace Tracedrop.Core.Tests;

/// <summary>
/// End-to-end checks of the bundled AI models. Each test runs only when its model has been fetched
/// (tools/models/fetch_models.py, which CI does before testing) and is a no-op otherwise.
/// </summary>
public class ModelTests
{
    readonly ITestOutputHelper _log;

    public ModelTests(ITestOutputHelper log) => _log = log;

    static string Folder => Environment.GetEnvironmentVariable("TRACEDROP_MODELS") ?? Path.Combine(RepoRoot(), "models");

    static ModelStore? StoreWith(string model) =>
        File.Exists(Path.Combine(Folder, model)) ? new ModelStore(Folder) { PreferGpu = false } : null;

    static TraceSettings Preset(string name) => Presets.Find(name)!.Settings.Clone();

    void Report(string name, TraceResult r) =>
        _log.WriteLine($"{name}: {r.Drawing.ClosedCount} closed, {r.Drawing.OpenCount} open, {r.Drawing.EntityCount} entities, " +
                       $"{string.Join(", ", r.StageMs.Select(kv => $"{kv.Key} {kv.Value:0}ms"))}");

    [Fact]
    public void Teed_edges_trace_shapes()
    {
        using var models = StoreWith(ModelNames.Edges);
        if (models is null) return;
        using var job = new TraceJob(Synthetic.Shapes(), models);
        var result = job.Run(Preset("All edges"));
        Report("TEED", result);
        Assert.Empty(result.Notes);
        Assert.True(result.Drawing.Paths.Count >= 4);
    }

    [Fact]
    public void A_gpu_failure_switches_to_the_cpu_and_the_step_still_succeeds()
    {
        if (!File.Exists(Path.Combine(Folder, ModelNames.Edges))) return;
        using var store = new ModelStore(Folder);
        // Both "devices" are real CPU sessions; the test makes every run on the "GPU" fail like a driver reset.
        store.SessionFactory = (bytes, gpu) => new Microsoft.ML.OnnxRuntime.InferenceSession(bytes);
        string? reason = null;
        store.SwitchedToCpu += r => reason = r;

        var gpuRuns = 0;
        var result = store.Run(ModelNames.Edges, session =>
        {
            if (store.UsingGpu)
            {
                gpuRuns++;
                throw new InvalidOperationException(
                    "[ErrorCode:Fail] DmlExecutionProvider ... 887A0005 The GPU device instance has been suspended.");
            }
            return "ran on cpu";
        });

        Assert.Equal("ran on cpu", result);
        Assert.Equal(1, gpuRuns);
        Assert.False(store.UsingGpu);
        Assert.False(store.PreferGpu);
        Assert.Equal("CPU", store.Device);
        Assert.Equal("the graphics driver reset while running an AI model", reason);
        Assert.Equal(reason, store.GpuFailure);

        // A whole trace through the store works after the switch.
        using var job = new TraceJob(Synthetic.Shapes(), store);
        Assert.NotEmpty(job.Run(Preset("All edges")).Drawing.Paths);
    }

    [Fact]
    public void Subject_model_cuts_out_the_part_with_its_holes()
    {
        using var models = StoreWith(ModelNames.Subject);
        if (models is null) return;
        using var job = new TraceJob(Synthetic.Part(), models);
        var result = job.Run(Preset("Part to copy"));
        Report("BiRefNet part", result);
        Assert.Empty(result.Notes); // the model was used, not the fallback
        var outer = Assert.Single(result.Drawing.Paths, p => p.Role == PathRole.Outer);
        // The plate is 680×460 source px; the outline must hug it (working scale 1600/1200).
        var scale = result.Width / 1200.0;
        var pts = outer.Flatten(0.5);
        var width = (pts.Max(p => p.X) - pts.Min(p => p.X)) / scale;
        var height = (pts.Max(p => p.Y) - pts.Min(p => p.Y)) / scale;
        _log.WriteLine($"  plate {width:0.0} × {height:0.0} px (true 680 × 460), holes {result.Drawing.Paths.Count(p => p.Role == PathRole.Hole)}");
        Assert.InRange(width, 680 * 0.97, 680 * 1.03);
        Assert.InRange(height, 460 * 0.97, 460 * 1.03);
    }

    [Fact]
    public void Isolate_subject_removes_the_background_before_edges()
    {
        using var models = StoreWith(ModelNames.Subject);
        if (models is null) return;
        using var job = new TraceJob(Synthetic.Part(), models);
        var s = Preset("All edges");
        s.IsolateSubject = true;
        var result = job.Run(s);
        Report("Isolate + edges", result);
        Assert.DoesNotContain(result.Notes, n => n.Contains("Isolate", StringComparison.OrdinalIgnoreCase));
        Assert.True(result.Drawing.Paths.Count >= 1);
    }

    [Fact]
    public void Line_drawing_model_draws_the_sketch()
    {
        using var models = StoreWith(ModelNames.LineArt);
        if (models is null) return;
        using var job = new TraceJob(Synthetic.Sketch(), models);
        var s = Preset("Sketch / drawing");
        s.UseAiLineArt = true;
        var result = job.Run(s);
        Report("Informative Drawings sketch", result);
        Assert.True(result.Drawing.Paths.Count >= 2);
        // The strokes follow the drawing: every path lies near where ink was drawn (rows 120..540 of 600).
        var scale = result.Width / 800.0;
        foreach (var p in result.Drawing.Paths)
            Assert.All(p.Flatten(1), v => Assert.InRange(v.Y / scale, 100, 560));
    }

    static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Tracedrop.sln"))) dir = dir.Parent;
        return dir?.FullName ?? AppContext.BaseDirectory;
    }
}
