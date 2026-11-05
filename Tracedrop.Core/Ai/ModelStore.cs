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
/// Finds, loads and runs ONNX models. Looks, in order, at embedded resources named "Tracedrop.Models.&lt;file&gt;"
/// (the single-file exe), the folder in TRACEDROP_MODELS, and a "models" folder next to the app.
/// <para>
/// Models run on the GPU through DirectML when <see cref="PreferGpu"/> is on, otherwise on the CPU. GPUs fail in
/// ways the CPU never does: a driver reset when a large model runs too long on a weak or integrated GPU
/// ("The GPU device instance has been suspended", 0x887A0005), out of video memory, an operator the driver
/// can't run. Any failure on the GPU switches the whole store to the CPU for the rest of the session (a reset
/// GPU stays unusable for this process) and re-runs the step there, so a picture never fails because of the GPU.
/// </para>
/// </summary>
public sealed class ModelStore : IDisposable
{
    readonly object _gate = new();
    readonly Dictionary<string, InferenceSession?> _sessions = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, byte[]?> _bytes = new(StringComparer.OrdinalIgnoreCase);
    readonly List<string> _folders = new();
    bool _preferGpu = true;
    bool _reloadPending;
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

    /// <summary>Use the GPU when possible. Changing it reloads the models on next use.</summary>
    public bool PreferGpu
    {
        get => _preferGpu;
        set
        {
            lock (_gate)
            {
                if (_preferGpu == value) return;
                _preferGpu = value;
                if (value) GpuFailure = null;
                // Don't free sessions here: another thread may be running one. They're swapped on the next Get.
                _reloadPending = true;
            }
        }
    }

    /// <summary>
    /// Threads for heavy CPU work (AI on the CPU, OpenCV, fitting): leaves cores free so the window stays smooth.
    /// 16 logical processors → 9, 8 → 5, 4 → 2, 2 → 1.
    /// </summary>
    public static int ComputeThreads { get; } =
        Math.Max(1, Math.Min(Environment.ProcessorCount - 2, Environment.ProcessorCount / 2 + 1));

    /// <summary>The graphics card's name, or "CPU", once a model has loaded.</summary>
    public string? Device { get; private set; }

    /// <summary>Why the GPU was given up on in this session, or null.</summary>
    public string? GpuFailure { get; private set; }

    /// <summary>Raised (on the calling thread) when a GPU failure made the store switch to the CPU.</summary>
    public event Action<string>? SwitchedToCpu;

    /// <summary>Test hook: replaces session creation. Arguments: model bytes, whether the GPU is wanted.</summary>
    internal Func<byte[], bool, InferenceSession>? SessionFactory { get; set; }

    bool _usingGpu;

    /// <summary>True when the currently loaded sessions run on the GPU.</summary>
    public bool UsingGpu => _usingGpu;

    public bool IsAvailable(string name) => FindResource(name) is not null || FindFile(name) is not null;

    /// <summary>The loaded session, or null if the model isn't bundled. Prefer <see cref="Run{T}"/>, which recovers from GPU failures.</summary>
    public InferenceSession? Get(string name)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_reloadPending)
            {
                DisposeSessions();
                _reloadPending = false;
            }
            if (_sessions.TryGetValue(name, out var cached)) return cached;
            var session = Load(name);
            _sessions[name] = session;
            return session;
        }
    }

    /// <summary>
    /// Runs <paramref name="body"/> with the model's session. Returns default when the model isn't bundled.
    /// If it fails on the GPU, switches to the CPU and runs it again; a failure on the CPU is a real error and is thrown.
    /// </summary>
    public T? Run<T>(string name, Func<InferenceSession, T> body) where T : class
    {
        var session = Get(name);
        if (session is null) return null;
        var onGpu = _usingGpu;
        try
        {
            return body(session);
        }
        catch (Exception ex) when (onGpu && ex is not OperationCanceledException)
        {
            FallBackToCpu(ex);
            var cpu = Get(name);
            return cpu is null ? null : body(cpu);
        }
    }

    void FallBackToCpu(Exception ex)
    {
        string reason;
        lock (_gate)
        {
            if (!_preferGpu) return; // another call already switched
            _preferGpu = false;
            reason = Describe(ex);
            GpuFailure = reason;
            DisposeSessions();
        }
        SwitchedToCpu?.Invoke(reason);
    }

    /// <summary>A short, readable reason for a GPU failure.</summary>
    internal static string Describe(Exception ex)
    {
        var text = ex.Message;
        if (text.Contains("887A0005", StringComparison.OrdinalIgnoreCase) || text.Contains("suspended", StringComparison.OrdinalIgnoreCase) || text.Contains("removed", StringComparison.OrdinalIgnoreCase))
            return "the graphics driver reset while running an AI model";
        if (text.Contains("No DirectX 12", StringComparison.OrdinalIgnoreCase))
            return "no DirectX 12 graphics card was found";
        if (text.Contains("memory", StringComparison.OrdinalIgnoreCase) || text.Contains("887A0006", StringComparison.OrdinalIgnoreCase))
            return "the graphics card ran out of memory";
        return "the graphics card couldn't run an AI model";
    }

    InferenceSession? Load(string name)
    {
        if (!_bytes.TryGetValue(name, out var bytes))
        {
            bytes = ReadModel(name);
            _bytes[name] = bytes;
        }
        if (bytes is null) return null;

        if (_preferGpu)
        {
            try
            {
                var adapter = SessionFactory is null ? GpuPicker.Best() : new GpuPicker.Adapter(0, "Test GPU", 1, false);
                if (adapter is null) throw new InvalidOperationException("No DirectX 12 graphics card.");
                var session = SessionFactory?.Invoke(bytes, true) ?? CreateGpuSession(bytes, adapter.Index);
                _usingGpu = true;
                Device = adapter.Name.Length > 0 ? adapter.Name : "GPU";
                return session;
            }
            catch (Exception ex)
            {
                // No DirectML in this build, no DirectX 12 GPU, or a model the driver can't compile: CPU it is.
                _preferGpu = false;
                GpuFailure ??= Describe(ex);
                DisposeSessions(); // keep every model on the same device
            }
        }

        var cpuSession = SessionFactory?.Invoke(bytes, false) ?? CreateCpuSession(bytes);
        _usingGpu = false;
        Device = "CPU";
        return cpuSession;
    }

    static InferenceSession CreateGpuSession(byte[] bytes, int adapterIndex)
    {
        using var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            // DirectML requires memory patterns off and sequential execution.
            EnableMemoryPattern = false,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL
        };
        options.AppendExecutionProvider_DML(adapterIndex);
        return new InferenceSession(bytes, options);
    }

    static InferenceSession CreateCpuSession(byte[] bytes)
    {
        using var options = new SessionOptions
        {
            // ENABLE_ALL adds CPU layout transforms (NCHWc) that, together with memory patterns, make the first
            // runs at every new picture size 3-4x slower (TEED at 1600 px, ORT 1.24: 5.1 s first run vs 1.3 s
            // with EXTENDED and no memory pattern; steady state is the same). Pictures change size constantly here.
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_EXTENDED,
            EnableMemoryPattern = false,
            IntraOpNumThreads = ComputeThreads,
            InterOpNumThreads = 1
        };
        // Idle ORT threads normally spin at 100% waiting for work, which starves the window's own thread and
        // makes the app feel frozen while a model runs. Let them sleep instead.
        options.AddSessionConfigEntry("session.intra_op.allow_spinning", "0");
        options.AddSessionConfigEntry("session.inter_op.allow_spinning", "0");
        return new InferenceSession(bytes, options);
    }

    byte[]? ReadModel(string name)
    {
        var resource = FindResource(name);
        if (resource is not null)
        {
            using var stream = resource.Value.Assembly.GetManifestResourceStream(resource.Value.Name)!;
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            return memory.ToArray();
        }
        var file = FindFile(name);
        return file is null ? null : File.ReadAllBytes(file);
    }

    void DisposeSessions()
    {
        foreach (var session in _sessions.Values) session?.Dispose();
        _sessions.Clear();
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
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            DisposeSessions();
        }
    }
}
