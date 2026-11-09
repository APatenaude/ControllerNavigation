namespace ControllerNavigation;

static class Program
{
    /// <summary>Held while the tray app runs: one per user session.</summary>
    const string InstanceMutex = @"Local\ControllerNavigation";

    /// <summary>Set by a second start: the running copy opens its settings window.</summary>
    public const string ShowSettingsEvent = @"Local\ControllerNavigation-ShowSettings";

    /// <summary>Set by the update job: the running copy exits so its exe can be replaced.</summary>
    public const string ExitEvent = @"Local\ControllerNavigation-Exit";

    /// <summary>
    /// Modes: no argument, the tray app; --install / --uninstall (Installer); --input-helper, the
    /// elevated helper for admin windows (InputHelper); --apply-update, the elevated update job
    /// (UpdateJob).
    /// </summary>
    [STAThread]
    static int Main(string[] args)
    {
        switch (args.FirstOrDefault())
        {
            case "--install" or "--uninstall":
                return Installer.Run(args);
            case "--input-helper":
                return InputHelper.Run();
            case "--apply-update":
                return UpdateJob.Run(args);
        }

        // A download or a copy outside Program Files: install it (or update the installed one) first.
        if (!AppInfo.RunningInstalled && Installer.OfferInstall())
            return 0;

        using var instance = new Mutex(true, InstanceMutex, out var first);
        if (!first)
        {
            try
            {
                using var show = EventWaitHandle.OpenExisting(ShowSettingsEvent);
                show.Set();
            }
            catch (Exception) { }
            return 0;
        }

        Log.Info($"{AppInfo.Name} {AppInfo.Version} starting ({Environment.ProcessPath})");
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.ThreadException += (_, e) => Log.Error("UI thread", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Log.Error("Unhandled", e.ExceptionObject as Exception);
            Log.Flush();
        };
        using var app = new TrayApp();
        Application.Run(app);
        Log.Info("Exiting");
        Log.Flush();
        return 0;
    }
}
