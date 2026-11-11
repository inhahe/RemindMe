using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace RemindMe.Desktop;

public partial class App : Application
{
    private Mutex? _instanceMutex;
    private EventWaitHandle? _showSignal, _exitSignal;
    private RegisteredWaitHandle? _showWait, _exitWait;
    private bool _showingError;

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        // Windows is logging off or shutting down: ringing alarms must survive, not be
        // dismissed by their windows closing.
        AppHost.BeginExit();
        base.OnSessionEnding(e);
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        bool minimized = e.Args.Contains("--minimized");
        bool exit = e.Args.Contains("--exit");
        string dataDir = Path.GetFullPath(ArgValue(e.Args, "--data-dir") ?? AppHost.DefaultDataDir);

        // One instance per data directory: a second launch brings the first one forward.
        // (--data-dir gives tests and experiments an instance of their own.)
        string tag = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(dataDir.ToLowerInvariant())))[..16];
        string name = $"{AppHost.Name}-{tag}";
        bool first;
        try { _instanceMutex = new Mutex(true, name, out first); }
        catch (UnauthorizedAccessException)
        {
            // A copy running as administrator holds it (started from an elevated prompt, say),
            // and this one can't reach it, to bring it forward or ask it to exit.
            if (!exit)
                MessageBox.Show("RemindMe is already running as administrator, so it can't be brought forward from here.\n\n"
                    + "Quit it from its icon in the notification area, then start RemindMe again.",
                    "RemindMe", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown(1);
            return;
        }
        if (first)
        {
            // Made at once, so that a launch during this copy's startup finds them. A signal
            // that comes before the window exists waits in its event until the waits below.
            _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, $"{name}-show");
            _exitSignal = new EventWaitHandle(false, EventResetMode.AutoReset, $"{name}-exit");
        }
        if (exit)
        {
            // --exit (the installer, before replacing the exe): the running copy, if any, is
            // told to exit, and this waits until it has, so its files are free.
            if (!first && OpenSignal($"{name}-exit") is { } quit)
            {
                quit.Set();
                try { _instanceMutex.WaitOne(TimeSpan.FromSeconds(15)); }
                catch (AbandonedMutexException) { }   // it exited holding it: as good as released
            }
            Shutdown();
            return;
        }
        if (!first)
        {
            if (!minimized) OpenSignal($"{name}-show")?.Set();
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, ev) =>
        {
            AppHost.Log($"Unhandled: {ev.Exception}");
            ev.Handled = true;
            // One box at a time: an error repeating on the 1-second tick would otherwise
            // stack a new nested message box every second.
            if (_showingError) return;
            _showingError = true;
            try { MessageBox.Show(ev.Exception.Message, "RemindMe", MessageBoxButton.OK, MessageBoxImage.Error); }
            finally { _showingError = false; }
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ev) => AppHost.Log($"Fatal: {ev.ExceptionObject}");

        MainWindow main;
        try
        {
            AppHost.Initialize(dataDir);
            Autostart.ApplyOnFirstRun();
            main = new MainWindow();
        }
        catch (Exception ex)
        {
            // Without a window or tray icon, a half-started copy would hold the
            // single-instance mutex invisibly and turn every later launch away.
            AppHost.Log($"Startup failed: {ex}");
            MessageBox.Show($"RemindMe couldn't start:\n\n{ex.Message}", "RemindMe",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }
        MainWindow = main;
        if (!minimized) main.ShowFromTray();

        _showWait = ThreadPool.RegisterWaitForSingleObject(_showSignal!,
            (_, _) => Dispatcher.BeginInvoke(main.ShowFromTray), null, Timeout.Infinite, executeOnlyOnce: false);
        _exitWait = ThreadPool.RegisterWaitForSingleObject(_exitSignal!,
            (_, _) => Dispatcher.BeginInvoke(main.ExitApp), null, Timeout.Infinite, executeOnlyOnce: true);
    }

    /// <summary>The running copy's named event <paramref name="name"/>, waiting up to 5 s for
    /// it: that copy makes it right after taking the mutex, and a launch can come in between.</summary>
    /// <returns>Null if it never appeared, or can't be opened (a copy running as administrator).</returns>
    private static EventWaitHandle? OpenSignal(string name)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (true)
        {
            try { if (EventWaitHandle.TryOpenExisting(name, out var signal)) return signal; }
            catch (UnauthorizedAccessException) { return null; }
            if (DateTime.UtcNow > deadline) return null;
            Thread.Sleep(100);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _showWait?.Unregister(null);
        _showSignal?.Dispose();
        _exitWait?.Unregister(null);
        _exitSignal?.Dispose();
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
