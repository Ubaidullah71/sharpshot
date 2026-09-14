namespace Sharpshot;

internal static class Program
{
    private const string InstanceMutexName = @"Local\Sharpshot.Instance";
    private const string ShowSettingsEventName = @"Local\Sharpshot.ShowSettings";

    [STAThread]
    private static void Main(string[] args)
    {
        using var instance = new Mutex(initiallyOwned: true, InstanceMutexName, out var isFirstInstance);
        using var showSettings = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSettingsEventName);
        if (!isFirstInstance)
        {
            // Already running: opening Sharpshot again brings up its settings. Windows gave this process the right to take
            // focus, so pass it on to the running one.
            Native.NativeMethods.AllowSetForegroundWindow(Native.NativeMethods.ASFW_ANY);
            showSettings.Set();
            return;
        }

        var paths = AppPaths.Default;
        Log.Initialize(paths.LogFile);
        Log.Info($"{AppInfo.Name} {AppInfo.Version} starting");

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Log.Error("Unhandled exception on the UI thread", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("Fatal unhandled exception", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error("Unobserved task exception", e.Exception);
            e.SetObserved();
        };

        ApplicationConfiguration.Initialize();
        Application.SetColorMode(SystemColorMode.System);

        TrayApp app;
        try
        {
            app = new TrayApp(paths, args.Contains(AutoStart.Argument), showSettings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("Couldn't start", ex);
            MessageBox.Show($"Sharpshot couldn't start: {ex.Message}\n\nThe log is in {Path.GetDirectoryName(paths.LogFile)}.", AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        using (app)
        {
            Application.Run(app);
        }

        Log.Info($"{AppInfo.Name} exited");
        GC.KeepAlive(instance);
    }
}