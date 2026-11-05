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

    /// <summary>Run the AI models on the graphics card. Switched off automatically if the GPU fails.</summary>
    public bool UseGpu { get; set; } = true;

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
/// after the bundle.
/// <para>
/// A running exe can't be written. So when Tracedrop closes, the config waits in a small pending file next to
/// the temp folder, and once nothing runs from the exe any more a hidden helper (Windows PowerShell, built in)
/// trims the old config off the end of the exe and appends the new one in place: only the last few kilobytes
/// change, the program and its icon are untouched, and there is never a second copy. If that helper can't
/// run (blocked by policy, exe on read-only media), the pending file is used next time and baking is retried.
/// </para>
/// </summary>
public static class PortableStore
{
    public const string MarkerText = "TRACEDROP-CFG-v1";
    static readonly byte[] Marker = Encoding.ASCII.GetBytes(MarkerText);
    const int TrailerLength = 8 + 16;
    const long MaxPayload = 4 * 1024 * 1024;

    /// <summary>Superseded exe from the older rename-and-swap baking; removed whenever found.</summary>
    public static string OldPath(string exePath) => exePath + ".old";

    /// <summary>Where settings wait until they're baked into this exe (one file per exe location).</summary>
    public static string PendingPath(string exePath)
    {
        var full = Path.GetFullPath(exePath).ToLowerInvariant();
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(full)))[..16];
        return Path.Combine(Path.GetTempPath(), $"Tracedrop-settings-{hash}.json");
    }

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

    /// <summary>
    /// Settings for this exe: pending ones not yet baked in (newest), else the ones inside the exe, else defaults.
    /// Also tidies up a leftover .old exe from older versions.
    /// </summary>
    public static (AppConfig Config, bool FromExe) Load(string exePath)
    {
        TryDelete(OldPath(exePath));
        var pending = PendingPath(exePath);
        if (File.Exists(pending))
        {
            try { return (AppConfig.FromJson(File.ReadAllText(pending)), false); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        }
        var embedded = ReadEmbedded(exePath);
        return embedded is not null ? (embedded, true) : (new AppConfig(), false);
    }

    /// <summary>Writes the settings that should end up in the exe. Returns false if they're already in it.</summary>
    public static bool SavePending(string exePath, AppConfig config)
    {
        var embedded = ReadEmbedded(exePath);
        var pending = PendingPath(exePath);
        if (embedded is not null && embedded.ToJson() == config.ToJson())
        {
            TryDelete(pending);
            return false;
        }
        File.WriteAllText(pending, config.ToJson());
        return true;
    }

    /// <summary>
    /// Replaces the config at the end of <paramref name="exePath"/> in place (the exe must not be running).
    /// Only the bytes after the program change. The same algorithm as <see cref="BakeScript"/>.
    /// </summary>
    public static void WriteInPlace(string exePath, AppConfig config)
    {
        var payload = Encoding.UTF8.GetBytes(config.ToJson());
        if (payload.Length > MaxPayload) throw new InvalidOperationException("Settings are too large to store.");
        using var file = new FileStream(exePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var (start, length) = FindPayload(file);
        var programLength = length > 0 ? start : file.Length;
        file.SetLength(programLength);
        file.Position = programLength;
        file.Write(payload);
        file.Write(BitConverter.GetBytes((long)payload.Length));
        file.Write(Marker);
        file.Flush(flushToDisk: true);
    }

    /// <summary>
    /// A Windows PowerShell script that waits for process <paramref name="waitForPid"/> to exit, then bakes the
    /// pending settings into the exe in place (as <see cref="WriteInPlace"/>) and deletes the pending file.
    /// Retries for a while if the exe is briefly locked (antivirus scan, Tracedrop started again).
    /// </summary>
    public static string BakeScript(string exePath, string pendingPath, int waitForPid)
    {
        static string Q(string s) => "'" + s.Replace("'", "''") + "'";
        return string.Join("\n", new[]
        {
            "$ErrorActionPreference = 'Stop'",
            $"try {{ Wait-Process -Id {waitForPid} -Timeout 120 -ErrorAction SilentlyContinue }} catch {{ }}",
            $"$exe = {Q(exePath)}; $json = {Q(pendingPath)}",
            "if (-not (Test-Path -LiteralPath $json)) { exit 0 }",
            "$payload = [IO.File]::ReadAllBytes($json)",
            $"$marker = [Text.Encoding]::ASCII.GetBytes('{MarkerText}')",
            "$fs = $null",
            "for ($i = 0; $i -lt 60 -and -not $fs; $i++) { try { $fs = [IO.File]::Open($exe, 'Open', 'ReadWrite', 'None') } catch { Start-Sleep -Milliseconds 500 } }",
            "if (-not $fs) { exit 1 }",
            "try {",
            "  $len = $fs.Length; $program = $len",
            "  if ($len -ge 24) {",
            "    $t = New-Object byte[] 24; $fs.Position = $len - 24; [void]$fs.Read($t, 0, 24)",
            $"    if ([Text.Encoding]::ASCII.GetString($t, 8, 16) -eq '{MarkerText}') {{",
            "      $n = [BitConverter]::ToInt64($t, 0)",
            "      if ($n -gt 0 -and $n -le $len - 24) { $program = $len - 24 - $n }",
            "    }",
            "  }",
            "  $fs.SetLength($program); $fs.Position = $program",
            "  $fs.Write($payload, 0, $payload.Length)",
            "  $fs.Write([BitConverter]::GetBytes([long]$payload.Length), 0, 8)",
            "  $fs.Write($marker, 0, 16)",
            "  $fs.Flush()",
            "} finally { $fs.Close() }",
            "Remove-Item -LiteralPath $json -Force"
        });
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
}
