using Android.Content;
using Android.OS;
using RemindMe.Sync;
using Application = Android.App.Application;

namespace RemindMe.Droid;

/// <summary>
/// The app's long-lived state, shared by every activity, receiver and service in the
/// process. The Android counterpart of the desktop's AppHost. Created by
/// <see cref="App.OnCreate"/>, which runs before any of them.
/// </summary>
public static class AppState
{
    private static readonly object LogLock = new();
    /// <summary>The tag of the app's lines in logcat.</summary>
    private const string LogTag = "RemindMe";
    /// <summary>The main thread's handler, for work to post there or to delay.</summary>
    internal static readonly Handler Main = new(Looper.MainLooper!);

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

    /// <summary>The data isn't loaded yet: the phone hasn't been unlocked since it restarted,
    /// and the files may still be in the credential-protected storage of an earlier version.
    /// Until <see cref="LoadOnceUnlocked"/>, nothing is read or written, and sync stays off.</summary>
    public static bool Provisional { get; private set; }

    /// <summary>Raised on the main thread when sync starts or stops, or its peers change.</summary>
    public static event Action? SyncChanged;
    /// <summary>Raised on the main thread as a pairing this device asked for progresses.</summary>
    public static event Action<PairProgress>? PairProgressed;
    /// <summary>Raised on the main thread after any task changes.</summary>
    public static event Action? TasksChanged;

    private static string SettingsPath => Path.Combine(DataDir, "settings.json");

    public static void Initialize(Context context)
    {
        Context = context;
        Load();
        // The boot-completed broadcast and the first screen opened cover the unlock too.
        if (Provisional) WhenUnlocked(LoadOnceUnlocked);
    }

    private static readonly List<Action> OnUnlock = [];
    private static UnlockReceiver? _unlockReceiver;

    /// <summary>Runs <paramref name="action"/> on the main thread when the phone is next
    /// unlocked (once, however often it is asked for). Only a receiver registered at run time
    /// hears of the unlock (no manifest receiver does), so if the process dies first the
    /// action is lost: callers also act when the next process starts. Any thread.</summary>
    internal static void WhenUnlocked(Action action)
    {
        lock (OnUnlock)
        {
            if (!OnUnlock.Contains(action)) OnUnlock.Add(action);
            if (_unlockReceiver is not null) return;
            _unlockReceiver = new UnlockReceiver();
            AndroidX.Core.Content.ContextCompat.RegisterReceiver(Context, _unlockReceiver, new IntentFilter(Intent.ActionUserUnlocked),
                AndroidX.Core.Content.ContextCompat.ReceiverNotExported);
        }
    }

    private sealed class UnlockReceiver : BroadcastReceiver
    {
        public override void OnReceive(Context? context, Intent? intent)
        {
            try { Context.UnregisterReceiver(this); }
            catch (Java.Lang.IllegalArgumentException) { }
            List<Action> actions;
            lock (OnUnlock)
            {
                actions = [.. OnUnlock];
                OnUnlock.Clear();
                _unlockReceiver = null;
            }
            foreach (var action in actions) action();
        }
    }

    private static void Load()
    {
        var dir = DataDirectory(Context);
        Provisional = dir is null;
        DataDir = dir ?? "";
        Settings = dir is null ? new AppSettings { SyncEnabled = false } : AppSettings.Load(SettingsPath, DefaultDeviceName(), Log);
        Store = new TaskStore(dir is null ? null : Path.Combine(dir, "tasks.json"), Settings.NodeId, Clock, Log);
        AutoBackup.Attach(Store, Settings.NodeId);
        Store.Changed += _ => OnMain(() =>
        {
            AlarmScheduler.Reschedule(Context);
            RingingService.Poke();
            AutoBackup.Schedule();
            TasksChanged?.Invoke();
        });
        if (!Provisional) OnMain(AutoBackup.Schedule);   // brings an out-of-date or missing backup up to date
    }

    /// <summary>Loads the data if the phone was locked when the process started
    /// (<see cref="Provisional"/>) and is unlocked now; then sets the alarms and starts sync.</summary>
    public static void LoadOnceUnlocked()
    {
        if (!Provisional || !IsUnlocked(Context)) return;
        Load();
        Log("Loaded the data after the first unlock.");
        AlarmScheduler.Reschedule(Context);
        TasksChanged?.Invoke();
        if (Settings.SyncEnabled) SyncService.Start(Context);
    }

    /// <summary>The phone has been unlocked since it restarted: credential-protected and
    /// shared storage are open.</summary>
    internal static bool IsUnlocked(Context context) =>
        ((Android.OS.UserManager?)context.GetSystemService(Context.UserService))?.IsUserUnlocked == true;

    /// <summary>
    /// Device-protected storage, readable before the first unlock after a restart, so the
    /// locked-boot receiver can set the alarms of a phone that restarted overnight (as
    /// Android's own clock app does). The cost: those files are protected by the device key
    /// only, not the user's PIN.
    /// <para>Earlier versions kept their files in credential-protected storage, readable only
    /// once unlocked. On the first unlocked start they are copied over, overwriting, then a
    /// marker file records that device storage holds the data, and only then are the old
    /// files deleted. Until the marker exists the old files are the data: a start while
    /// locked has none to use, and a start whose copy failed runs from the old files and
    /// copies again next time, so nothing saved since is ever overwritten.</para>
    /// </summary>
    /// <returns>The directory, or null while the data can't be known yet.</returns>
    private static string? DataDirectory(Context context)
    {
        var dir = context.CreateDeviceProtectedStorageContext()!.FilesDir!.AbsolutePath;
        var marker = Path.Combine(dir, "moved-to-device-storage");
        if (File.Exists(marker)) return dir;
        if (!IsUnlocked(context)) return null;
        var old = context.FilesDir?.AbsolutePath;
        string[] names = ["tasks.json", "tasks.json.bak", "settings.json", "settings.json.bak", "app.log"];
        try
        {
            if (old is not null && old != dir)
                foreach (var name in names)
                    if (File.Exists(Path.Combine(old, name))) File.Copy(Path.Combine(old, name), Path.Combine(dir, name), overwrite: true);
            File.WriteAllText(marker, "");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            global::Android.Util.Log.Warn(LogTag, $"Couldn't copy the data to device storage: {e.Message}");
            return old ?? dir;
        }
        if (old is not null && old != dir)
            foreach (var name in names)
            {
                try { File.Delete(Path.Combine(old, name)); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        return dir;
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
        if (old.SyncSettingsDiffer(updated))
        {
            if (Settings.SyncEnabled) { StopSync(); SyncService.Start(Context); }
            else SyncService.Stop(Context);
        }
        AlarmScheduler.Reschedule(Context);
        if (updated.AutoBackup && !old.AutoBackup) AutoBackup.Schedule();
    }

    // ---- Sync --------------------------------------------------------------------------

    /// <summary>Starts sync if it is on and not running. Called by <see cref="SyncService"/>,
    /// which keeps the process alive around it.</summary>
    public static void EnsureSync()
    {
        if (Sync is not null || !Settings.SyncEnabled || Provisional) return;
        var s = Settings;
        var engine = new SyncEngine(Store, new SyncIdentity(s.NodeId, s.DeviceName, s.GroupId, s.GroupKey), new SyncOptions
        {
            Port = s.Port,
            AcceptExternalConnections = s.AcceptExternalConnections,
            RemoteAddresses = s.RemoteAddresses.ToList(),
            AcquireMulticastLock = AcquireMulticastLock,
            LinkedDeviceCount = () => Settings.KnownPeers.Count,
            IsLinkedPeer = node => Settings.IsKnownPeer(node),
        }, Log);
        // Missed alarms wait for the first exchange; ring them as soon as it is done.
        engine.FirstSyncCompleted += () => OnMain(() => AlarmScheduler.Reschedule(Context));
        engine.PeerSeen += (node, name) => OnMain(() =>
        {
            if (Sync != engine) return;
            Settings.RememberPeer(node, name, Clock.UtcNow);
            SaveSettings();
        });
        engine.IdentityChanged += id => OnMain(() =>
        {
            if (Settings.GroupId == id.GroupId) return;
            Settings.GroupId = id.GroupId;
            Settings.GroupKey = id.GroupKeyBase64;
            Settings.KnownPeers = [];
            SaveSettings();
        });
        engine.PairRequested += p => OnMain(() => AddPrompt(p));
        engine.PairProgressed += p => OnMain(() => PairProgressed?.Invoke(p));
        engine.PeersChanged += () => OnMain(() =>
        {
            if (Sync != engine) return;
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

    // ---- Pairing questions (main thread only) ------------------------------------------

    private static readonly List<PairPrompt> Prompts = [];

    /// <summary>The pairing question to answer next, if any. The Devices screen asks it while
    /// in front; otherwise a notification stands for it until it is answered or withdrawn.</summary>
    public static PairPrompt? PendingPrompt => Prompts.Count > 0 ? Prompts[0] : null;

    /// <summary>Set by the Devices screen while its dialog asks <see cref="PendingPrompt"/>:
    /// then no notification is needed.</summary>
    public static bool PromptOnScreen { get; set; }

    /// <summary>A question has become <see cref="PendingPrompt"/>: a Devices screen in front asks it.</summary>
    public static event Action<PairPrompt>? PairRequested;
    /// <summary>A question was answered or withdrawn: a screen asking it closes its dialog.</summary>
    public static event Action<PairPrompt>? PromptEnded;

    private static void AddPrompt(PairPrompt p)
    {
        // Subscribed before the check, so that a withdrawal at any moment is seen.
        p.Withdrawn += () => OnMain(() => EndPrompt(p));
        if (p.IsWithdrawn) return;
        Prompts.Add(p);
        if (PendingPrompt == p) ShowPrompt(p);
    }

    private static void ShowPrompt(PairPrompt p)
    {
        PairRequested?.Invoke(p);
        if (!PromptOnScreen) Notifications.ShowPairRequest(Context, p);
    }

    private static void EndPrompt(PairPrompt p)
    {
        bool wasNext = PendingPrompt == p;
        if (!Prompts.Remove(p)) return;
        PromptEnded?.Invoke(p);
        if (!wasNext) return;
        Notifications.Cancel(Context, Notifications.PairId);
        if (PendingPrompt is { } next) ShowPrompt(next);
    }

    /// <summary>The user answered <paramref name="p"/>, on the Devices screen.</summary>
    public static void AnswerPrompt(PairPrompt p, bool yes)
    {
        EndPrompt(p);
        _ = yes ? p.AcceptAsync() : p.RejectAsync();
    }

    /// <summary>The Devices screen stopped asking the question unanswered (it went to the
    /// background): the notification stands for it again.</summary>
    public static void PromptHidden()
    {
        PromptOnScreen = false;
        if (PendingPrompt is { } p) Notifications.ShowPairRequest(Context, p);
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
        var l = wifi?.CreateMulticastLock("RemindMe.Discovery");
        if (l is null) return null;
        l.SetReferenceCounted(false);
        l.Acquire();
        return new Releaser(() => { if (l.IsHeld) l.Release(); });
    }

    private sealed class Releaser(Action release) : IDisposable
    {
        public void Dispose() => release();
    }

    /// <summary>How long missed alarms wait at most for the first exchange with a linked device.</summary>
    public static readonly TimeSpan SettleLimit = TimeSpan.FromSeconds(15);

    /// <summary>True while alarms missed before this process started should wait, so a
    /// linked device that already marked them done can say so first: until the first
    /// exchange with a linked device completes, at most <see cref="SettleLimit"/>, and not
    /// at all for a phone linked with nothing.</summary>
    public static bool SettlingAfterStart(DateTimeOffset now) =>
        Settings.SyncEnabled && Settings.KnownPeers.Count > 0 && Sync?.FirstSyncDone != true
        && now - StartedUtc < SettleLimit;

    // ---- Utilities ---------------------------------------------------------------------

    public static void OnMain(Action action)
    {
        if (Looper.MyLooper() == Looper.MainLooper) action();
        else Main.Post(action);
    }

    public static void PostDelayed(Action action, long ms) => Main.PostDelayed(action, ms);

    /// <summary>Logs to logcat, and to <c>app.log</c> once the data directory is known.</summary>
    public static void Log(string message)
    {
        global::Android.Util.Log.Info(LogTag, message);
        if (DataDir.Length == 0) return;   // Provisional: the files can't be written yet
        try
        {
            lock (LogLock)
            {
                var path = Path.Combine(DataDir, "app.log");
                var info = new FileInfo(path);
                if (info.Exists && info.Length > 500_000) File.Move(path, path + ".old", overwrite: true);
                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{System.Environment.NewLine}");
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
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
        RingingService.RestoreFromShown(this);
        // Sync is started by whatever brought the process up and is allowed to start a
        // foreground service: MainActivity, the boot receiver, or Android restarting the
        // sticky service. Starting it from here is refused when a broadcast woke the app.
        AlarmScheduler.Reschedule(this);
    }
}
