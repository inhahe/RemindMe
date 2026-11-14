using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;

namespace RemindMe.Droid;

/// <summary>
/// Keeps the process, and so the sync engine in <see cref="AppState"/>, alive in the
/// background while sync is on (R17). LanLink measured why this is needed: a backgrounded
/// app's networking is throttled until inbound connections simply fail. Its type is
/// connectedDevice rather than dataSync because Android 15 limits dataSync services to six
/// hours a day; connectedDevice qualifies through CHANGE_WIFI_MULTICAST_STATE.
/// </summary>
[Service(Exported = false, ForegroundServiceType = ForegroundService.TypeConnectedDevice)]
public sealed class SyncService : Service
{
    private static SyncService? _instance;

    /// <summary>Whether the service runs in this process now.</summary>
    public static bool IsRunning => _instance is not null;

    public static void Start(Context context)
    {
        try { context.StartForegroundService(new Intent(context, typeof(SyncService))); }
        // Refused in the background (Android 12+), or by an app op or a missing permission.
        catch (Exception e) when (e is Java.Lang.IllegalStateException or ForegroundServiceStartNotAllowedException
            or Java.Lang.SecurityException)
        {
            AppState.Log($"Couldn't start sync now: {e.Message}");
            // From an exact alarm, the start is allowed: try again from one shortly (not from
            // the watchdog's own start, which would then repeat every few seconds).
            if (e is ForegroundServiceStartNotAllowedException && !SyncWatchdog.Firing) SyncWatchdog.Schedule(context, SyncWatchdog.Soon);
        }
    }

    public static void Stop(Context context)
    {
        SyncWatchdog.Cancel(context);
        context.StopService(new Intent(context, typeof(SyncService)));
        AppState.StopSync();
    }

    public static void UpdateNotification(Context context)
    {
        if (_instance is null) return;
        Notifications.Post(context, Notifications.SyncId, Notifications.Sync(context, Summary()));
    }

    private static string Summary()
    {
        var peers = AppState.Sync?.Peers().Where(p => p.Linked && p.Connected).Select(p => p.Name).ToList() ?? [];
        return peers.Count == 0 ? "No other device connected right now" : $"Connected: {string.Join(", ", peers)}";
    }

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        _instance = this;
        var n = Notifications.Sync(this, Summary());
        try { StartForeground(Notifications.SyncId, n, ForegroundService.TypeConnectedDevice); }
        catch (Exception e) when (e is Java.Lang.IllegalStateException or ForegroundServiceStartNotAllowedException
            or Java.Lang.SecurityException)
        {
            // Restarted in the background (sticky), where Android 12 and later may refuse a
            // foreground service: the watchdog starts it again from an exact alarm shortly.
            AppState.Log($"Sync couldn't run in the background now: {e.Message}");
            if (AppState.Settings.SyncEnabled) SyncWatchdog.Schedule(this, SyncWatchdog.Soon);
            StopSelf();
            return StartCommandResult.NotSticky;
        }
        if (!AppState.Settings.SyncEnabled)
        {
            StopSelf();
            return StartCommandResult.NotSticky;
        }
        AppState.EnsureSync();
        SyncWatchdog.Schedule(this);
        // Sticky: if Android kills the process, it restarts the service, and sync with it.
        return StartCommandResult.Sticky;
    }

    public override void OnDestroy()
    {
        _instance = null;
        AppState.StopSync();
        base.OnDestroy();
    }
}

/// <summary>
/// Keeps sync running where the process is ended and the service isn't brought back: a phone
/// maker's battery manager may close the app without Android restarting it, and Android 12
/// and later may refuse a service restarted in the background the foreground. While sync is
/// on, an exact alarm every <see cref="Interval"/> starts it again if it isn't running.
/// Starting a foreground service from an exact alarm is allowed in the background
/// (<see cref="AlarmScheduler"/>). The alarm outlives the process; a reboot or an update
/// clears it, and the boot receiver starts sync, which sets it again.
/// </summary>
public static class SyncWatchdog
{
    private const int RequestCode = 2;
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);
    /// <summary>The wait before trying again after a start was refused in the background.</summary>
    public static readonly TimeSpan Soon = TimeSpan.FromSeconds(5);

    /// <summary>True while the watchdog's alarm is being handled: a start refused then isn't
    /// retried <see cref="Soon"/>, which would repeat every few seconds.</summary>
    [ThreadStatic] public static bool Firing;

    /// <summary>Sets the next check, <paramref name="after"/> from now (<see cref="Interval"/>
    /// by default), replacing any set before.</summary>
    public static void Schedule(Context context, TimeSpan? after = null)
    {
        var am = (AlarmManager)context.GetSystemService(Context.AlarmService)!;
        long at = Android.OS.SystemClock.ElapsedRealtime() + (long)(after ?? Interval).TotalMilliseconds;
        var fire = FireIntent(context);
        // Without the exact-alarm permission (Android 12, if revoked): inexact, and a start
        // from it may be refused; the next one tries again.
        if (OperatingSystem.IsAndroidVersionAtLeast(31) && !am.CanScheduleExactAlarms())
            am.SetAndAllowWhileIdle(AlarmType.ElapsedRealtimeWakeup, at, fire);
        else
            am.SetExactAndAllowWhileIdle(AlarmType.ElapsedRealtimeWakeup, at, fire);
    }

    public static void Cancel(Context context) =>
        ((AlarmManager)context.GetSystemService(Context.AlarmService)!).Cancel(FireIntent(context));

    private static PendingIntent FireIntent(Context context) =>
        PendingIntent.GetBroadcast(context, RequestCode, new Intent(context, typeof(SyncWatchdogReceiver)),
            PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent)!;
}

/// <summary>The watchdog's alarm: sync started again if it should run and doesn't, and the
/// next check set.</summary>
[BroadcastReceiver(Enabled = true, Exported = false)]
public sealed class SyncWatchdogReceiver : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent)
    {
        if (context is null || !AppState.Settings.SyncEnabled) return;   // stopped meanwhile: no more checks
        if (!SyncService.IsRunning)
        {
            AppState.Log("Sync wasn't running; starting it again.");
            SyncWatchdog.Firing = true;
            try { SyncService.Start(context); }
            finally { SyncWatchdog.Firing = false; }
        }
        SyncWatchdog.Schedule(context);
    }
}
