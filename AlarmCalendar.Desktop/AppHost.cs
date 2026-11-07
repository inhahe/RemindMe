using System.IO;
using System.Windows;
using AlarmCalendar.Sync;

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

    /// <summary>The running sync engine; null while sync is off or couldn't start.</summary>
    public static SyncEngine? Sync { get; private set; }
    /// <summary>One line on the state of sync, for the Devices window.</summary>
    public static string SyncStatus { get; private set; } = "";
    /// <summary>Raised on the UI thread when sync starts or stops, or its peers change.</summary>
    public static event Action? SyncChanged;
    /// <summary>Raised on the UI thread as a pairing this device asked for progresses.</summary>
    public static event Action<PairProgress>? PairProgressed;

    /// <summary>(Re)starts sync from the current settings.</summary>
    public static void RestartSync()
    {
        Sync?.Dispose();
        Sync = null;
        var s = Settings;
        if (!s.SyncEnabled)
        {
            SyncStatus = "Sync is off.";
            SyncChanged?.Invoke();
            return;
        }
        // Developer knob: ALARMCAL_BIND=127.0.0.1 keeps a test instance on loopback, with no
        // LAN discovery, so testing never raises a Windows Firewall prompt (design.md → Testing).
        var bind = Environment.GetEnvironmentVariable("ALARMCAL_BIND");
        var bindAddress = bind is not null && System.Net.IPAddress.TryParse(bind, out var ip) ? ip : null;
        var engine = new SyncEngine(Store, new SyncIdentity(s.NodeId, s.DeviceName, s.GroupId, s.GroupKey), new SyncOptions
        {
            BindAddress = bindAddress ?? System.Net.IPAddress.Any,
            EnableDiscovery = bindAddress is null,
            Port = s.Port,
            AcceptExternalConnections = s.AcceptExternalConnections,
            RemoteAddresses = s.RemoteAddresses.ToList(),
        }, Log);
        engine.IdentityChanged += id => OnUi(() =>
        {
            if (Settings.GroupId == id.GroupId) return;
            Settings.GroupId = id.GroupId;
            Settings.GroupKey = id.GroupKeyBase64;
            Settings.KnownPeers = [];   // the old group's devices are no longer linked
            SaveSettings();
        });
        engine.PairRequested += p => OnUi(() => new PairPromptWindow(p).Show());
        engine.PairProgressed += p => OnUi(() => PairProgressed?.Invoke(p));
        engine.PeersChanged += () => OnUi(() =>
        {
            if (Sync != engine) return;
            var linked = engine.Peers().Where(p => p.Linked && p.Connected).Select(p => (p.NodeId, p.Name));
            if (Settings.RememberPeers(linked, Clock.UtcNow)) SaveSettings();
            SyncChanged?.Invoke();
        });
        try
        {
            engine.Start();
            Sync = engine;
            SyncStatus = $"Sync is on, listening on port {engine.ListenPort}.";
        }
        catch (System.Net.Sockets.SocketException e)
        {
            engine.Dispose();
            SyncStatus = $"Sync couldn't start: {e.Message} (port {s.Port}; is another copy running?)";
            Log(SyncStatus);
        }
        SyncChanged?.Invoke();
    }

    /// <summary>Leaves the sync group: this device keeps its tasks but stops syncing with the others.</summary>
    public static void LeaveSyncGroup()
    {
        Settings.NewGroup();
        SaveSettings();
        Sync?.LeaveGroup(Settings.GroupId, Settings.GroupKey);
        SyncChanged?.Invoke();
    }

    /// <summary>While true, alarms missed while the app wasn't running wait a moment, so a
    /// peer that already marked one done can say so before it rings here.</summary>
    public static bool SettlingAfterStart(DateTimeOffset now) =>
        Sync is not null && now - StartedUtc < TimeSpan.FromSeconds(6);

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
        RestartSync();
    }

    /// <summary>Replaces the settings with <paramref name="updated"/>, saves them, and
    /// tells the windows.</summary>
    public static void ApplySettings(AppSettings updated)
    {
        var old = Settings;
        // The settings windows edit a copy taken when they opened; the sync identity may
        // have changed since (a pairing), and they never edit it, so the current one stands.
        updated.NodeId = old.NodeId;
        updated.GroupId = old.GroupId;
        updated.GroupKey = old.GroupKey;
        updated.KnownPeers = old.KnownPeers;
        updated.AutostartInitialized = old.AutostartInitialized;
        Settings = updated;
        Settings.Save(SettingsPath, Log);
        Autostart.Apply(Settings.StartWithWindows);
        bool syncChanged = old.SyncEnabled != updated.SyncEnabled || old.DeviceName != updated.DeviceName
            || old.Port != updated.Port || old.AcceptExternalConnections != updated.AcceptExternalConnections
            || !old.RemoteAddresses.SequenceEqual(updated.RemoteAddresses);
        if (syncChanged) RestartSync();
        SettingsChanged?.Invoke();
    }

    public static void SaveSettings() => Settings.Save(SettingsPath, Log);

    public static void Shutdown()
    {
        Sync?.Dispose();
        Sync = null;
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
