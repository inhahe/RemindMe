using Android.App;
using Android.Content;
using Android.OS;

namespace AlarmCalendar.Droid;

/// <summary>
/// Hands the engine's next wake-up to AlarmManager (D4). One alarm-clock alarm covers every
/// task: when it fires, <see cref="AlarmReceiver"/> evaluates them all and sets the next.
/// <c>SetAlarmClock</c> is exact, fires in Doze, and shows in the status bar as the next
/// alarm, which suits an alarm app.
/// </summary>
public static class AlarmScheduler
{
    private const int RequestCode = 1;

    /// <summary>The tasks that should be ringing now, leaving out any missed alarm that is
    /// still waiting for sync to settle after startup.</summary>
    public static List<TaskStatus> RingingNow(DateTimeOffset now, out DateTimeOffset? settleUntil)
    {
        settleUntil = null;
        var zone = AppState.Clock.Zone;
        var result = new List<TaskStatus>();
        foreach (var t in AppState.Store.Live())
        {
            var st = AlarmEngine.Evaluate(t, now, zone);
            if (!st.ShouldAlert) continue;
            if (st.AlertDue < AppState.StartedUtc && AppState.SettlingAfterStart(now) && !RingingService.IsShowing(t.Id))
            {
                settleUntil = AppState.StartedUtc + AppState.SettleLimit;
                continue;
            }
            result.Add(st);
        }
        return result;
    }

    /// <summary>Sets (or clears) the single alarm for the next moment anything is due. If
    /// something is due already and the ringing service isn't handling it, the alarm is set
    /// a second from now: starting the foreground service from an exact alarm is always
    /// allowed, whereas a boot receiver may not start a media-playback service at all
    /// (Android 15).</summary>
    public static void Reschedule(Context context)
    {
        var now = AppState.Clock.UtcNow;
        var ringing = RingingNow(now, out var settleUntil);
        DateTimeOffset? when = AlarmEngine.NextWakeUp(AppState.Store.Live(), now, AppState.Clock.Zone);
        if (ringing.Count > 0 && !RingingService.IsRunning) when = now + TimeSpan.FromSeconds(1);
        if (settleUntil is { } s && (when is null || s < when)) when = s;

        var am = (AlarmManager)context.GetSystemService(Context.AlarmService)!;
        var fire = FireIntent(context);
        if (when is not { } w)
        {
            am.Cancel(fire);
            return;
        }
        if (w < now + TimeSpan.FromSeconds(1)) w = now + TimeSpan.FromSeconds(1);
        if (OperatingSystem.IsAndroidVersionAtLeast(31) && !am.CanScheduleExactAlarms())
        {
            // Android 12 without the permission granted: inexact, but better than nothing.
            am.SetAndAllowWhileIdle(AlarmType.RtcWakeup, w.ToUnixTimeMilliseconds(), fire);
            return;
        }
        var show = PendingIntent.GetActivity(context, 0, new Intent(context, typeof(MainActivity)),
            PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
        am.SetAlarmClock(new AlarmManager.AlarmClockInfo(w.ToUnixTimeMilliseconds(), show), fire);
    }

    private static PendingIntent FireIntent(Context context) =>
        PendingIntent.GetBroadcast(context, RequestCode, new Intent(context, typeof(AlarmReceiver)),
            PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent)!;
}

/// <summary>The alarm fired: start ringing whatever is due, and set the next alarm.</summary>
[BroadcastReceiver(Enabled = true, Exported = false)]
public sealed class AlarmReceiver : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent)
    {
        if (context is null) return;
        var ringing = AlarmScheduler.RingingNow(AppState.Clock.UtcNow, out _);
        if (ringing.Count > 0) RingingService.Start(context);
        AlarmScheduler.Reschedule(context);
    }
}

/// <summary>After a reboot, an app update, or a change of clock or time zone, alarms must
/// be set again: AlarmManager forgets them on reboot, and the next wake-up moves with the
/// clock. Missed alarms then ring, marked late (R13).</summary>
[BroadcastReceiver(Enabled = true, Exported = true)]
[IntentFilter([Intent.ActionBootCompleted, Intent.ActionTimeChanged, Intent.ActionTimezoneChanged,
    Intent.ActionMyPackageReplaced, "android.intent.action.QUICKBOOT_POWERON"])]
public sealed class BootReceiver : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent)
    {
        if (context is null) return;
        TimeZoneInfo.ClearCachedData();
        AlarmScheduler.Reschedule(context);
        if (intent?.Action is Intent.ActionBootCompleted or Intent.ActionMyPackageReplaced && AppState.Settings.SyncEnabled)
            SyncService.Start(context);
    }
}
