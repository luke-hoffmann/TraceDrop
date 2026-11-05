namespace TracedropApp;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length == 4 && args[0] == "--cleanup-watcher")
            return CleanupWatcher.Run(args[1], args[2], args[3]);

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // One window at a time: the settings stored inside the exe are written when it closes.
        using var mutex = new Mutex(true, "Local\\TracedropDesktop", out var createdNew);
        if (!createdNew)
        {
            MessageBox.Show("Tracedrop is already open.", "Tracedrop", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }

        var exePath = Environment.ProcessPath ?? Application.ExecutablePath;
        var files = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray();
        Application.Run(new MainForm(exePath, files));
        return 0;
    }
}
