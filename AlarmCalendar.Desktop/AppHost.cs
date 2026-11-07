using System.IO;
using System.Windows;

namespace AlarmCalendar.Desktop;

/// <summary>The app's long-lived services, created once at startup.</summary>
public static class AppHost
{
    private static readonly object LogLock = new();

    public static string DataDir { get; private set; } = "";
    public static AppSettings Settings { get; private set; } = new();
    public static TaskStore Store { get; private set; } = null!;
    public static IClock Clock { get; } = SystemClock.Instance;
    public static SoundService Sounds { get; private set; } = null!;
    public static AlarmCoordinator Alarms { get; private set; } = null!;

    /// <summary>When this process started: an alarm due before this was missed because
    /// the app wasn't running (R13).</summary>
    public static DateTimeOffset StartedUtc { get; } = DateTimeOffset.UtcNow;

    /// <summary>Raised on the UI thread after the settings change.</summary>
    public static event Action? SettingsChanged;

    private static string SettingsPath => Path.Combine(DataDir, "settings.json");

    public static void Initialize(string dataDir)
    {
        DataDir = dataDir;
        Directory.CreateDirectory(dataDir);
        Settings = AppSettings.Load(SettingsPath, Environment.MachineName, Log);
        Store = new TaskStore(Path.Combine(dataDir, "tasks.json"), Settings.NodeId, Clock, Log);
        Sounds = new SoundService();
        Alarms = new AlarmCoordinator(Store, Sounds);
        Microsoft.Win32.SystemEvents.TimeChanged += (_, _) => TimeZoneInfo.ClearCachedData();
    }

    /// <summary>Replaces the settings with <paramref name="updated"/>, saves them, and
    /// tells the windows.</summary>
    public static void ApplySettings(AppSettings updated)
    {
        Settings = updated;
        Settings.Save(SettingsPath, Log);
        Autostart.Apply(Settings.StartWithWindows);
        SettingsChanged?.Invoke();
    }

    public static void SaveSettings() => Settings.Save(SettingsPath, Log);

    public static void Shutdown()
    {
        Alarms?.Dispose();
        Sounds?.StopAll();
    }

    public static void Log(string message)
    {
        try
        {
            lock (LogLock)
            {
                var path = Path.Combine(DataDir.Length > 0 ? DataDir : Path.GetTempPath(), "app.log");
                var info = new FileInfo(path);
                if (info.Exists && info.Length > 1_000_000) File.Move(path, path + ".old", overwrite: true);
                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>Runs <paramref name="action"/> on the UI thread.</summary>
    public static void OnUi(Action action)
    {
        var d = Application.Current?.Dispatcher;
        if (d is null) return;
        if (d.CheckAccess()) action(); else d.BeginInvoke(action);
    }
}
