using Android.Content;
using Android.OS;
using AlarmCalendar.Sync;
using Application = Android.App.Application;

namespace AlarmCalendar.Droid;

/// <summary>
/// The app's long-lived state, shared by every activity, receiver and service in the
/// process. The Android counterpart of the desktop's AppHost. Created by
/// <see cref="App.OnCreate"/>, which runs before any of them.
/// </summary>
public static class AppState
{
    private static readonly object LogLock = new();
    private static readonly Handler Main = new(Looper.MainLooper!);

    public static Context Context { get; private set; } = null!;
    public static string DataDir { get; private set; } = "";
    public static AppSettings Settings { get; private set; } = new();
    public static TaskStore Store { get; private set; } = null!;
    public static IClock Clock { get; } = SystemClock.Instance;

    /// <summary>When this process started: an alarm due before it was missed while the app
    /// (or the phone) wasn't running (R13).</summary>
    public static DateTimeOffset StartedUtc { get; } = DateTimeOffset.UtcNow;

    public static SyncEngine? Sync { get; private set; }
    public static string SyncStatus { get; private set; } = "";
    /// <summary>A pairing request no screen has answered yet.</summary>
    public static PairPrompt? PendingPrompt { get; set; }

    /// <summary>Raised on the main thread when sync starts or stops, or its peers change.</summary>
    public static event Action? SyncChanged;
    /// <summary>Raised on the main thread as a pairing this device asked for progresses.</summary>
    public static event Action<PairProgress>? PairProgressed;
    /// <summary>Raised on the main thread for a pairing request; a screen that handles it
    /// sets <see cref="PendingPrompt"/> to null.</summary>
    public static event Action<PairPrompt>? PairRequested;
    /// <summary>Raised on the main thread after any task changes.</summary>
    public static event Action? TasksChanged;

    private static string SettingsPath => Path.Combine(DataDir, "settings.json");

    public static void Initialize(Context context)
    {
        Context = context;
        DataDir = context.FilesDir!.AbsolutePath;
        Settings = AppSettings.Load(SettingsPath, DefaultDeviceName(), Log);
        Store = new TaskStore(Path.Combine(DataDir, "tasks.json"), Settings.NodeId, Clock, Log);
        Store.Changed += _ => OnMain(() =>
        {
            AlarmScheduler.Reschedule(Context);
            RingingService.Poke();
            TasksChanged?.Invoke();
        });
    }

    private static string DefaultDeviceName()
    {
        var model = Build.Model ?? "Android";
        var maker = Build.Manufacturer ?? "";
        return model.StartsWith(maker, StringComparison.OrdinalIgnoreCase) || maker.Length == 0 ? model : $"{maker} {model}";
    }

    public static void SaveSettings() => Settings.Save(SettingsPath, Log);

    /// <summary>Applies a screen's edits to the settings as they are now (so a pairing made
    /// while the screen was open isn't reverted), saves, and restarts sync if a sync
    /// setting changed.</summary>
    public static void UpdateSettings(Action<AppSettings> edit)
    {
        var old = Settings;
        var updated = old.Clone();
        edit(updated);
        Settings = updated;
        SaveSettings();
        bool syncChanged = old.SyncEnabled != updated.SyncEnabled || old.DeviceName != updated.DeviceName
            || old.Port != updated.Port || old.AcceptExternalConnections != updated.AcceptExternalConnections
            || !old.RemoteAddresses.SequenceEqual(updated.RemoteAddresses);
        if (syncChanged)
        {
            if (Settings.SyncEnabled) { StopSync(); SyncService.Start(Context); }
            else SyncService.Stop(Context);
        }
        AlarmScheduler.Reschedule(Context);
    }

    // ---- Sync --------------------------------------------------------------------------

    /// <summary>Starts sync if it is on and not running. Called by <see cref="SyncService"/>,
    /// which keeps the process alive around it.</summary>
    public static void EnsureSync()
    {
        if (Sync is not null || !Settings.SyncEnabled) return;
        var s = Settings;
        var engine = new SyncEngine(Store, new SyncIdentity(s.NodeId, s.DeviceName, s.GroupId, s.GroupKey), new SyncOptions
        {
            Port = s.Port,
            AcceptExternalConnections = s.AcceptExternalConnections,
            RemoteAddresses = s.RemoteAddresses.ToList(),
            AcquireMulticastLock = AcquireMulticastLock,
        }, Log);
        engine.IdentityChanged += id => OnMain(() =>
        {
            if (Settings.GroupId == id.GroupId) return;
            Settings.GroupId = id.GroupId;
            Settings.GroupKey = id.GroupKeyBase64;
            Settings.KnownPeers = [];
            SaveSettings();
        });
        engine.PairRequested += p => OnMain(() =>
        {
            PendingPrompt = p;
            PairRequested?.Invoke(p);
            if (PendingPrompt == p) Notifications.ShowPairRequest(Context, p);
        });
        engine.PairProgressed += p => OnMain(() => PairProgressed?.Invoke(p));
        engine.PeersChanged += () => OnMain(() =>
        {
            if (Sync != engine) return;
            var linked = engine.Peers().Where(p => p.Linked && p.Connected).Select(p => (p.NodeId, p.Name));
            if (Settings.RememberPeers(linked, Clock.UtcNow)) SaveSettings();
            SyncService.UpdateNotification(Context);
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
            SyncStatus = $"Sync couldn't start: {e.Message}";
            Log(SyncStatus);
        }
        SyncChanged?.Invoke();
    }

    public static void StopSync()
    {
        Sync?.Dispose();
        Sync = null;
        SyncStatus = Settings.SyncEnabled ? "Sync is stopped." : "Sync is off.";
        SyncChanged?.Invoke();
    }

    public static void LeaveSyncGroup()
    {
        Settings.NewGroup();
        SaveSettings();
        Sync?.LeaveGroup(Settings.GroupId, Settings.GroupKey);
        SyncChanged?.Invoke();
    }

    /// <summary>Android drops broadcast packets before they reach an app unless it holds a
    /// WiFi MulticastLock (as LanLink found).</summary>
    private static IDisposable? AcquireMulticastLock()
    {
        var wifi = (Android.Net.Wifi.WifiManager?)Context.GetSystemService(Context.WifiService);
        var l = wifi?.CreateMulticastLock("AlarmCalendar.Discovery");
        if (l is null) return null;
        l.SetReferenceCounted(false);
        l.Acquire();
        return new Releaser(() => { if (l.IsHeld) l.Release(); });
    }

    private sealed class Releaser(Action release) : IDisposable
    {
        public void Dispose() => release();
    }

    /// <summary>True while alarms missed before this process started should wait, so a
    /// linked device that already marked them done can say so first.</summary>
    public static bool SettlingAfterStart(DateTimeOffset now) =>
        Settings.SyncEnabled && now - StartedUtc < TimeSpan.FromSeconds(6);

    // ---- Utilities ---------------------------------------------------------------------

    public static void OnMain(Action action)
    {
        if (Looper.MyLooper() == Looper.MainLooper) action();
        else Main.Post(action);
    }

    public static void PostDelayed(Action action, long ms) => Main.PostDelayed(action, ms);

    public static void Log(string message)
    {
        try
        {
            lock (LogLock)
            {
                var path = Path.Combine(DataDir.Length > 0 ? DataDir : Path.GetTempPath(), "app.log");
                var info = new FileInfo(path);
                if (info.Exists && info.Length > 500_000) File.Move(path, path + ".old", overwrite: true);
                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{System.Environment.NewLine}");
            }
            global::Android.Util.Log.Info("AlarmCalendar", message);
        }
        catch (IOException) { }
    }
}

/// <summary>Initializes <see cref="AppState"/> before any activity, receiver or service runs.</summary>
[global::Android.App.Application]
public sealed class App : Application
{
    public App(IntPtr handle, global::Android.Runtime.JniHandleOwnership ownership) : base(handle, ownership) { }

    public override void OnCreate()
    {
        base.OnCreate();
        AppDomain.CurrentDomain.UnhandledException += (_, e) => AppState.Log($"Fatal: {e.ExceptionObject}");
        AppState.Initialize(this);
        Notifications.CreateChannels(this);
        // Sync is started by whatever brought the process up and is allowed to start a
        // foreground service: MainActivity, the boot receiver, or Android restarting the
        // sticky service. Starting it from here is refused when a broadcast woke the app.
        AlarmScheduler.Reschedule(this);
    }
}
