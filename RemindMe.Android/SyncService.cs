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

    public static void Start(Context context)
    {
        try { context.StartForegroundService(new Intent(context, typeof(SyncService))); }
        // Refused in the background (Android 12+), or by an app op or a missing permission.
        catch (Exception e) when (e is Java.Lang.IllegalStateException or ForegroundServiceStartNotAllowedException
            or Java.Lang.SecurityException)
        {
            AppState.Log($"Couldn't start sync now: {e.Message}");
        }
    }

    public static void Stop(Context context)
    {
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
            // foreground service: sync waits for the app to be opened.
            AppState.Log($"Sync couldn't run in the background now: {e.Message}");
            StopSelf();
            return StartCommandResult.NotSticky;
        }
        if (!AppState.Settings.SyncEnabled)
        {
            StopSelf();
            return StartCommandResult.NotSticky;
        }
        AppState.EnsureSync();
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
