using System.Text;
using System.Text.Json;
using Tracedrop.Core.Export;

namespace Tracedrop.Core.Portable;

/// <summary>Everything Tracedrop remembers between runs. Stored inside Tracedrop.exe itself (see <see cref="PortableStore"/>).</summary>
public sealed class AppConfig
{
    public int Version { get; set; } = 1;

    /// <summary>Settings in use when the app last closed.</summary>
    public TraceSettings LastSettings { get; set; } = Presets.Default.Settings.Clone();

    /// <summary>Name of the preset last picked (built-in or user), or null if settings were edited since.</summary>
    public string? LastPreset { get; set; } = Presets.Default.Name;

    public List<UserPreset> UserPresets { get; set; } = new();

    /// <summary>Formats written for every traced picture; the first one is what dragging a row gives.</summary>
    public List<ExportFormat> Formats { get; set; } = new() { ExportFormat.Dxf };

    /// <summary>Show the vector over the photo (true) or on white (false).</summary>
    public bool OverlayOnPhoto { get; set; } = true;

    /// <summary>Collapsed state of the settings sections, by title.</summary>
    public List<string> CollapsedSections { get; set; } = new();

    public string ToJson() => JsonSerializer.Serialize(this, Json.Options);

    public static AppConfig FromJson(string json)
    {
        var config = JsonSerializer.Deserialize<AppConfig>(json, Json.Options) ?? new AppConfig();
        config.LastSettings = (config.LastSettings ?? Presets.Default.Settings.Clone()).Normalized();
        config.UserPresets ??= new();
        config.UserPresets.RemoveAll(p => p is null || string.IsNullOrWhiteSpace(p.Name) || p.Settings is null);
        foreach (var p in config.UserPresets) p.Settings = p.Settings.Normalized();
        config.Formats ??= new();
        config.Formats = config.Formats.Where(Enum.IsDefined).Distinct().ToList();
        if (config.Formats.Count == 0) config.Formats.Add(ExportFormat.Dxf);
        config.CollapsedSections ??= new();
        return config;
    }

    public IEnumerable<Preset> AllPresets() =>
        UserPresets.Select(p => new Preset(p.Name, p.Description ?? "Your preset", p.Settings)).Concat(Presets.BuiltIn);
}

public sealed class UserPreset
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public TraceSettings Settings { get; set; } = new();
}

/// <summary>
/// Keeps the config inside the exe so one file carries it from PC to PC. The config is appended after the
/// program as [UTF-8 JSON][int64 length][16-byte marker]; Windows and the .NET single-file host ignore data
/// after the bundle. A running exe can't be written, but it can be renamed: <see cref="Bake"/> writes a copy
/// with the new config next to it and swaps the two names. When the folder is read-only (Program Files, a
/// locked USB stick) the config goes to a per-user fallback file instead.
/// </summary>
public static class PortableStore
{
    static readonly byte[] Marker = Encoding.ASCII.GetBytes("TRACEDROP-CFG-v1");
    const int TrailerLength = 8 + 16;
    const long MaxPayload = 4 * 1024 * 1024;

    /// <summary>Per-user fallback when the exe can't be rewritten.</summary>
    public static string FallbackPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tracedrop", "settings.json");

    /// <summary>Where a superseded exe is parked until it's no longer running.</summary>
    public static string OldPath(string exePath) => exePath + ".old";

    /// <summary>Config appended to the file, or null when there is none (or it's damaged).</summary>
    public static AppConfig? ReadEmbedded(string exePath)
    {
        try
        {
            using var file = new FileStream(exePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var (start, length) = FindPayload(file);
            if (length <= 0) return null;
            var bytes = new byte[length];
            file.Position = start;
            file.ReadExactly(bytes);
            return AppConfig.FromJson(Encoding.UTF8.GetString(bytes));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Embedded config if there is one, else the fallback file, else defaults. Also removes a leftover .old exe.</summary>
    public static (AppConfig Config, bool FromExe) Load(string exePath)
    {
        TryDelete(OldPath(exePath));
        var embedded = ReadEmbedded(exePath);
        if (embedded is not null)
        {
            // A fallback written later than the exe (exe was read-only at the time) wins.
            if (File.Exists(FallbackPath) && File.GetLastWriteTimeUtc(FallbackPath) > File.GetLastWriteTimeUtc(exePath))
            {
                var newer = ReadFallback();
                if (newer is not null) return (newer, false);
            }
            return (embedded, true);
        }
        return (ReadFallback() ?? new AppConfig(), false);
    }

    static AppConfig? ReadFallback()
    {
        try { return File.Exists(FallbackPath) ? AppConfig.FromJson(File.ReadAllText(FallbackPath)) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    /// <summary>Copies <paramref name="sourceExe"/> (without any old config) to <paramref name="destination"/> with <paramref name="config"/> appended.</summary>
    public static void WriteCopy(string sourceExe, string destination, AppConfig config)
    {
        var payload = Encoding.UTF8.GetBytes(config.ToJson());
        if (payload.Length > MaxPayload) throw new InvalidOperationException("Settings are too large to store.");
        using var source = new FileStream(sourceExe, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var (start, length) = FindPayload(source);
        var programLength = length > 0 ? start : source.Length;

        using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
        source.Position = 0;
        var buffer = new byte[1 << 20];
        var remaining = programLength;
        while (remaining > 0)
        {
            var read = source.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (read <= 0) throw new IOException("Unexpected end of the program file.");
            output.Write(buffer, 0, read);
            remaining -= read;
        }
        output.Write(payload);
        output.Write(BitConverter.GetBytes((long)payload.Length));
        output.Write(Marker);
        output.Flush(flushToDisk: true);
    }

    /// <summary>
    /// Stores <paramref name="config"/> inside <paramref name="exePath"/> (which may be running). Returns true on
    /// success; on failure saves to <see cref="FallbackPath"/> and returns false.
    /// </summary>
    public static bool Bake(string exePath, AppConfig config)
    {
        var current = ReadEmbedded(exePath);
        if (current is not null && current.ToJson() == config.ToJson())
        {
            TryDelete(FallbackPath); // the exe is the source of truth again
            return true;
        }

        var temp = exePath + ".new";
        var old = OldPath(exePath);
        try
        {
            WriteCopy(exePath, temp, config);
            TryDelete(old);
            File.Move(exePath, old);           // allowed even while the exe runs
            try
            {
                File.Move(temp, exePath);
            }
            catch
            {
                File.Move(old, exePath);       // put the original back
                throw;
            }
            TryHide(old);
            TryDelete(old);                    // succeeds when nothing runs from it any more
            TryDelete(FallbackPath);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            TryDelete(temp);
            SaveFallback(config);
            return false;
        }
    }

    public static void SaveFallback(AppConfig config)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FallbackPath)!);
            File.WriteAllText(FallbackPath, config.ToJson());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nowhere writable: settings last for this session only.
        }
    }

    static (long Start, long Length) FindPayload(FileStream file)
    {
        if (file.Length < TrailerLength) return (0, 0);
        var trailer = new byte[TrailerLength];
        file.Position = file.Length - TrailerLength;
        file.ReadExactly(trailer);
        if (!trailer.AsSpan(8).SequenceEqual(Marker)) return (0, 0);
        var length = BitConverter.ToInt64(trailer, 0);
        if (length <= 0 || length > MaxPayload || length > file.Length - TrailerLength) return (0, 0);
        return (file.Length - TrailerLength - length, length);
    }

    static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    static void TryHide(string path)
    {
        try { if (File.Exists(path)) File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Hidden); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
