using Tracedrop.Core.Portable;

namespace TracedropApp;

/// <summary>
/// Runs as a hidden copy of this same exe, invoked with (--cleanup-watcher, ownerPid, sessionPath, exePath).
/// Waits for the window's process to exit (normal close, force-kill or crash), then
///  1. hands the settings the app left in the session folder to a hidden helper that writes them into the end
///     of Tracedrop.exe once this process has exited too (see PortableStore), and
///  2. deletes the temporary session folder, so traced files never outlive the app.
/// If another program still has a file open, deletion is retried with a growing delay for about two minutes.
/// </summary>
static class CleanupWatcher
{
    public const string PendingConfigName = "config.json";
    const int GiveUpAfterMs = 120_000;
    const int MaxDelayMs = 5_000;

    public static int Run(string ownerPidText, string sessionPath, string exePath)
    {
        if (!int.TryParse(ownerPidText, out var ownerPid) || ownerPid < 1) return 2;
        if (!IsOurSessionFolder(sessionPath)) return 2;

        while (OwnerIsRunning(ownerPid)) Thread.Sleep(250);
        Finish(exePath, sessionPath);
        return 0;
    }

    /// <summary>Bakes the pending settings into the exe and deletes the session folder.</summary>
    public static void Finish(string exePath, string sessionPath)
    {
        if (!IsOurSessionFolder(sessionPath)) return;
        var full = Path.GetFullPath(sessionPath);

        try
        {
            var pending = Path.Combine(full, PendingConfigName);
            if (File.Exists(pending) && File.Exists(exePath))
            {
                var config = AppConfig.FromJson(File.ReadAllText(pending));
                if (PortableStore.SavePending(exePath, config)) StartBake(exePath);
            }
        }
        catch
        {
            // Settings are a convenience; never block cleanup on them.
        }

        var deadline = Environment.TickCount64 + GiveUpAfterMs;
        var delay = 250;
        while (true)
        {
            try
            {
                if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
                return;
            }
            catch
            {
                ClearReadOnly(full);
            }
            if (Environment.TickCount64 >= deadline) return;
            Thread.Sleep(delay);
            delay = Math.Min(delay * 2, MaxDelayMs);
        }
    }

    /// <summary>
    /// Starts the hidden in-place bake. It waits for this process to exit (this process runs from the exe, which
    /// can't be written until it's gone). If PowerShell can't start, the pending settings are used next launch.
    /// </summary>
    static void StartBake(string exePath)
    {
        try
        {
            var script = PortableStore.BakeScript(exePath, PortableStore.PendingPath(exePath), Environment.ProcessId);
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden
            };
            foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden", "-Command", script })
                startInfo.ArgumentList.Add(arg);
            using var _ = System.Diagnostics.Process.Start(startInfo);
        }
        catch
        {
            // No PowerShell: the pending file stays and is used (and baked) next time.
        }
    }

    /// <summary>Only ever delete "%TEMP%\Tracedrop-*": never anything a bad argument could point at.</summary>
    static bool IsOurSessionFolder(string sessionPath)
    {
        try
        {
            var tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
            var full = Path.GetFullPath(sessionPath);
            var parent = Path.GetDirectoryName(full)?.TrimEnd(Path.DirectorySeparatorChar);
            return parent is not null && string.Equals(parent, tempRoot, StringComparison.OrdinalIgnoreCase)
                   && Path.GetFileName(full).StartsWith("Tracedrop-", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    static void ClearReadOnly(string folder)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(file, FileAttributes.Normal); } catch { /* best effort */ }
            }
        }
        catch { /* best effort */ }
    }

    static bool OwnerIsRunning(int pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }
}
