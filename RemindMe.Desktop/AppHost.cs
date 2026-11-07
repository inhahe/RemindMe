using System.IO;
using System.Windows;
using RemindMe.Sync;

namespace RemindMe.Desktop;

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
        // Developer knob: REMINDME_BIND=127.0.0.1 keeps a test instance on loopback, with no
        // LAN discovery, so testing never raises a Windows Firewall prompt (design.md → Testing).
        var bind = Environment.GetEnvironmentVariable("REMINDME_BIND");
        var bindAddress = bind is not null && System.Net.IPAddress.TryParse(bind, out var ip) ? ip : null;
        var engine = new SyncEngine(Store, new SyncIdentity(s.NodeId, s.DeviceName, s.GroupId, s.GroupKey), new SyncOptions
        {
            BindAddress = bindAddress ?? System.Net.IPAddress.Any,
            EnableDiscovery = bindAddress is null,
            Port = s.Port,
            AcceptExternalConnections = s.AcceptExternalConnections,
            RemoteAddresses = s.RemoteAddresses.ToList(),
            LinkedDeviceCount = () => Settings.KnownPeers.Count,
            IsLinkedPeer = node => Settings.IsKnownPeer(node),
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
        engine.PeerSeen += (node, name) => OnUi(() =>
        {
            if (Sync != engine) return;
            Settings.RememberPeer(node, name, Clock.UtcNow);
            SaveSettings();
        });
        engine.PeersChanged += () => OnUi(() => { if (Sync == engine) SyncChanged?.Invoke(); });
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

    /// <summary>While true, alarms missed while the app wasn't running wait, so a linked
    /// device that already marked one done can say so before it rings here: until the first
    /// exchange with a linked device completes, for at most 15 s, and not at all for a
    /// device linked with nothing.</summary>
    public static bool SettlingAfterStart(DateTimeOffset now) =>
        Sync is { FirstSyncDone: false } && Settings.KnownPeers.Count > 0 && now - StartedUtc < TimeSpan.FromSeconds(15);

    private static string SettingsPath => Path.Combine(DataDir, "settings.json");

    /// <summary>Where tasks and settings live unless <c>--data-dir</c> says otherwise.</summary>
    public static string DefaultDataDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RemindMe");

    /// <summary>Where backups go unless Settings names a folder: "RemindMe backups" in
    /// Documents, where people look for their files (and which a cloud drive often keeps).
    /// An instance with a data folder of its own (<c>--data-dir</c>) keeps its backups
    /// inside it instead, away from the real ones, and so does one whose Documents folder
    /// Windows can't find (redirected to a share that is gone, say).</summary>
    public static string DefaultBackupFolder { get; private set; } = "";

    /// <summary>The folder backups go to, given the setting (null or empty: the default).</summary>
    public static string BackupFolderOf(string? setting) => setting is { Length: > 0 } folder ? folder : DefaultBackupFolder;

    public static BackupService Backup { get; private set; } = null!;

    public static void Initialize(string dataDir)
    {
        DataDir = dataDir;
        Directory.CreateDirectory(dataDir);
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        DefaultBackupFolder = documents.Length > 0
            && string.Equals(Path.GetFullPath(dataDir).TrimEnd('\\'), DefaultDataDir, StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(documents, "RemindMe backups")
            : Path.Combine(dataDir, "backups");
        Settings = AppSettings.Load(SettingsPath, Environment.MachineName, Log);
        Store = new TaskStore(Path.Combine(dataDir, "tasks.json"), Settings.NodeId, Clock, Log);
        Sounds = new SoundService();
        Alarms = new AlarmCoordinator(Store, Sounds);
        Backup = new BackupService(Store, Settings.NodeId);
        Backup.Schedule();   // brings an out-of-date or missing backup up to date, once started
        Microsoft.Win32.SystemEvents.TimeChanged += (_, _) => TimeZoneInfo.ClearCachedData();
        RestartSync();
    }

    /// <summary>Applies <paramref name="edit"/> to the current settings, saves them, applies
    /// what changed (autostart, sync), and tells the windows. A window passes only the
    /// fields it shows, applied to the settings as they are now, so anything that changed
    /// while it was open (a pairing, the window placement) isn't reverted.</summary>
    public static void UpdateSettings(Action<AppSettings> edit)
    {
        var old = Settings;
        var updated = old.Clone();
        edit(updated);
        Settings = updated;
        Settings.Save(SettingsPath, Log);
        Autostart.Apply(Settings.StartWithWindows);
        if (old.SyncSettingsDiffer(updated)) RestartSync();
        SettingsChanged?.Invoke();
    }

    public static void SaveSettings() => Settings.Save(SettingsPath, Log);

    /// <summary>The app is exiting: windows closing now must not count as the user's answer.</summary>
    public static bool Exiting { get; private set; }

    /// <summary>Starts exiting: closes the alarm windows quietly, so a ringing alarm stays
    /// due and rings again (marked late) on the next start, rather than being dismissed.</summary>
    public static void BeginExit()
    {
        if (Exiting) return;
        Exiting = true;
        Alarms?.Dispose();
        Backup?.Flush();
    }

    public static void Shutdown()
    {
        BeginExit();
        Sync?.Dispose();
        Sync = null;
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
