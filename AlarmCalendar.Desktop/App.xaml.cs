using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace AlarmCalendar.Desktop;

public partial class App : Application
{
    private Mutex? _instanceMutex;
    private EventWaitHandle? _showSignal;
    private RegisteredWaitHandle? _showWait;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        bool minimized = e.Args.Contains("--minimized");
        string dataDir = ArgValue(e.Args, "--data-dir")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AlarmCalendar");
        dataDir = Path.GetFullPath(dataDir);

        // One instance per data directory: a second launch brings the first one forward.
        // (--data-dir gives tests and experiments an instance of their own.)
        string tag = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(dataDir.ToLowerInvariant())))[..16];
        _instanceMutex = new Mutex(true, $"AlarmCalendar-{tag}", out bool first);
        if (!first)
        {
            if (!minimized && EventWaitHandle.TryOpenExisting($"AlarmCalendar-{tag}-show", out var show))
                show.Set();
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, ev) =>
        {
            AppHost.Log($"Unhandled: {ev.Exception}");
            MessageBox.Show(ev.Exception.Message, "Alarm & Calendar", MessageBoxButton.OK, MessageBoxImage.Error);
            ev.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ev) => AppHost.Log($"Fatal: {ev.ExceptionObject}");

        AppHost.Initialize(dataDir);
        Autostart.ApplyOnFirstRun();

        var main = new MainWindow();
        MainWindow = main;
        if (!minimized) main.ShowFromTray();

        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, $"AlarmCalendar-{tag}-show");
        _showWait = ThreadPool.RegisterWaitForSingleObject(_showSignal,
            (_, _) => Dispatcher.BeginInvoke(main.ShowFromTray), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _showWait?.Unregister(null);
        _showSignal?.Dispose();
        AppHost.Shutdown();
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }

    private static string? ArgValue(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
