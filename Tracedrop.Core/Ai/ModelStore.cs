using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.ML.OnnxRuntime;

namespace Tracedrop.Core.Ai;

/// <summary>The AI models Tracedrop knows about. File names match tools/models/models.json.</summary>
public static class ModelNames
{
    /// <summary>TEED edge detector (MIT).</summary>
    public const string Edges = "teed.onnx";
    /// <summary>BiRefNet-lite subject segmentation (MIT).</summary>
    public const string Subject = "birefnet-lite.onnx";
    /// <summary>Informative Drawings photo-to-line-drawing generator (MIT).</summary>
    public const string LineArt = "informative-drawings.onnx";
}

/// <summary>
/// Finds and caches ONNX models. Looks, in order, at embedded resources named "Tracedrop.Models.&lt;file&gt;" in any
/// loaded assembly (the single-file exe), the folder in TRACEDROP_MODELS, and a "models" folder next to the app.
/// Sessions use DirectML (any DirectX 12 GPU) when the native runtime has it, otherwise the CPU.
/// </summary>
public sealed class ModelStore : IDisposable
{
    readonly ConcurrentDictionary<string, Lazy<InferenceSession?>> _sessions = new(StringComparer.OrdinalIgnoreCase);
    readonly List<string> _folders = new();
    bool _disposed;

    public ModelStore(params string[] extraFolders)
    {
        _folders.AddRange(extraFolders.Where(f => !string.IsNullOrWhiteSpace(f)));
        var env = Environment.GetEnvironmentVariable("TRACEDROP_MODELS");
        if (!string.IsNullOrWhiteSpace(env)) _folders.Add(env);
        _folders.Add(Path.Combine(AppContext.BaseDirectory, "models"));
    }

    /// <summary>Shared instance for the app.</summary>
    public static ModelStore Shared { get; } = new();

    /// <summary>Set to false to force CPU (tests, or a GPU driver that misbehaves).</summary>
    public bool PreferGpu { get; set; } = true;

    /// <summary>"GPU (DirectML)" or "CPU" once any model has loaded.</summary>
    public string? Device { get; private set; }

    public bool IsAvailable(string name) => FindResource(name) is not null || FindFile(name) is not null;

    /// <summary>The loaded session, or null if the model isn't bundled. Loading happens once, on first use.</summary>
    public InferenceSession? Get(string name)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _sessions.GetOrAdd(name, n => new Lazy<InferenceSession?>(() => Load(n), LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }

    InferenceSession? Load(string name)
    {
        byte[]? bytes = null;
        var resource = FindResource(name);
        if (resource is not null)
        {
            using var stream = resource.Value.Assembly.GetManifestResourceStream(resource.Value.Name)!;
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            bytes = memory.ToArray();
        }
        else
        {
            var file = FindFile(name);
            if (file is null) return null;
            bytes = File.ReadAllBytes(file);
        }

        if (PreferGpu)
        {
            try
            {
                var gpu = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
                // DirectML wants memory patterns off and sequential execution.
                gpu.EnableMemoryPattern = false;
                gpu.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
                gpu.AppendExecutionProvider_DML(0);
                var session = new InferenceSession(bytes, gpu);
                Device = "GPU (DirectML)";
                return session;
            }
            catch (Exception)
            {
                // No DirectML in this build, no DX12 GPU, or the model needs an op DirectML lacks: fall back to CPU.
            }
        }

        var cpu = new SessionOptions
        {
            // ENABLE_ALL adds CPU layout transforms (NCHWc) that, together with memory patterns, make the first
            // runs at every new picture size 3-4x slower (TEED at 1600 px, ORT 1.24: 5.1 s first run vs 1.3 s
            // with EXTENDED and no memory pattern; steady state is the same). Pictures change size constantly here.
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_EXTENDED,
            EnableMemoryPattern = false
        };
        var cpuSession = new InferenceSession(bytes, cpu);
        Device ??= "CPU";
        return cpuSession;
    }

    static (Assembly Assembly, string Name)? FindResource(string name)
    {
        var wanted = "Tracedrop.Models." + name;
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (assembly.IsDynamic) continue;
            string[] names;
            try { names = assembly.GetManifestResourceNames(); }
            catch { continue; }
            var match = names.FirstOrDefault(n => string.Equals(n, wanted, StringComparison.OrdinalIgnoreCase));
            if (match is not null) return (assembly, match);
        }
        // The models assembly may not be loaded yet: try it by name.
        try
        {
            var models = Assembly.Load("Tracedrop.Models");
            var match = models.GetManifestResourceNames().FirstOrDefault(n => string.Equals(n, wanted, StringComparison.OrdinalIgnoreCase));
            if (match is not null) return (models, match);
        }
        catch
        {
            // Not bundled (development or test run).
        }
        return null;
    }

    string? FindFile(string name)
    {
        foreach (var folder in _folders)
        {
            try
            {
                var path = Path.Combine(folder, name);
                if (File.Exists(path)) return path;
            }
            catch
            {
                // Ignore malformed folders.
            }
        }
        return null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var lazy in _sessions.Values)
        {
            if (lazy.IsValueCreated) lazy.Value?.Dispose();
        }
        _sessions.Clear();
    }
}
