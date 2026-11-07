using Android.App;
using Android.Content;
using Android.OS;
using AndroidX.Core.App;
using AlarmCalendar.Sync;

namespace AlarmCalendar.Droid;

/// <summary>The app's notification channels and the notifications built on them.</summary>
public static class Notifications
{
    public const string AlarmChannel = "alarms";
    /// <summary>Alarms posted when the ringing service couldn't start: this channel carries
    /// the phone's alarm sound itself.</summary>
    public const string FallbackChannel = "alarms_fallback";
    /// <summary>The ringing service's own notification. It must not alert: Android groups an
    /// app's alerting notifications, and a grouped alarm's full-screen intent doesn't open
    /// the alarm screen.</summary>
    public const string ServiceChannel = "ringing_service";
    public const string SyncChannel = "sync";
    public const string PairChannel = "pairing";
    public const int SyncId = 2;
    public const int PairId = 3;

    public static void CreateChannels(Context context)
    {
        var nm = (NotificationManager)context.GetSystemService(Context.NotificationService)!;
        // The ringing service plays the sound itself (per-task sounds, once or looping), so
        // the channel stays silent rather than adding a second sound.
        var alarms = new NotificationChannel(AlarmChannel, "Alarms", NotificationImportance.High)
        {
            Description = "Tasks that are due",
            LockscreenVisibility = NotificationVisibility.Public,
        };
        alarms.SetSound(null, null);
        alarms.EnableVibration(false);
        nm.CreateNotificationChannel(alarms);
        var fallback = new NotificationChannel(FallbackChannel, "Alarms (when the app can't play its own sound)", NotificationImportance.High)
        {
            Description = "Used only if Android or the phone's battery manager stops the app's alarm sound",
            LockscreenVisibility = NotificationVisibility.Public,
        };
        fallback.SetSound(Android.Media.RingtoneManager.GetDefaultUri(Android.Media.RingtoneType.Alarm),
            new Android.Media.AudioAttributes.Builder().SetUsage(Android.Media.AudioUsageKind.Alarm)!.Build());
        fallback.EnableVibration(true);
        nm.CreateNotificationChannel(fallback);
        nm.CreateNotificationChannel(new NotificationChannel(ServiceChannel, "Alarm in progress", NotificationImportance.Low)
        {
            Description = "Shown while an alarm rings, so Android lets the sound keep playing",
        });
        nm.CreateNotificationChannel(new NotificationChannel(SyncChannel, "Sync", NotificationImportance.Low)
        {
            Description = "Shown while the app keeps your devices in sync",
        });
        nm.CreateNotificationChannel(new NotificationChannel(PairChannel, "Linking devices", NotificationImportance.High)
        {
            Description = "Another device asks to link with this one",
        });
    }

    /// <summary>A stable notification ID for a task's alarm.</summary>
    public static int AlarmId(Guid task) => 1000 + (task.GetHashCode() & 0x3FFFFFFF);

    /// <summary>The ringing notification of one task: it opens the alarm screen full-screen
    /// when the phone is locked, and offers Mark done and Remind me later.</summary>
    /// <param name="fallback">Posted without the ringing service, on the channel that sounds.</param>
    public static Notification Alarm(Context context, TaskStatus st, bool fallback)
    {
        var d = st.Task.Def;
        var open = AlarmActivity.IntentFor(context, st.Task.Id);
        var openPi = PendingIntent.GetActivity(context, AlarmId(st.Task.Id), open,
            PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
        string title = AlarmEngine.AlertTitle(st);
        var text = TextFormat.Snippet(d.Description, 80);
        // The builder returns itself from every call; statements rather than a chain,
        // because the bindings annotate each return as nullable.
        var b = new NotificationCompat.Builder(context, fallback ? FallbackChannel : AlarmChannel);
        b.SetSmallIcon(Resource.Drawable.ic_stat_alarm);
        // A group of its own: Android auto-groups an app's ungrouped notifications, and an
        // auto-grouped alarm's full-screen intent doesn't open the alarm screen.
        b.SetGroup($"alarm-{st.Task.Id:N}");
        b.SetContentTitle(text.Length > 0 ? text : title);
        b.SetContentText($"{title} · {TextFormat.Schedule(d, st.Task.State, AppState.Clock.UtcNow, AppState.Clock.Zone)}");
        b.SetCategory(NotificationCompat.CategoryAlarm);
        b.SetPriority(NotificationCompat.PriorityMax);
        b.SetOngoing(true);
        b.SetAutoCancel(false);
        // Posted again over itself (the service taking over from the safety net, or a silenced
        // safety-net alarm), it must neither sound nor open the alarm screen a second time.
        b.SetOnlyAlertOnce(true);
        b.SetContentIntent(openPi);
        b.SetFullScreenIntent(openPi, true);
        b.SetVisibility(NotificationCompat.VisibilityPublic);
        b.AddAction(0, "Mark done", AlarmActionReceiver.IntentFor(context, st, AlarmActionReceiver.Done));
        b.AddAction(0, "Remind me later", AlarmActionReceiver.IntentFor(context, st, AlarmActionReceiver.Later));
        return b.Build()!;
    }

    /// <summary>The ringing service's own notification, which Android requires of a
    /// foreground service; the tasks' alarms are separate notifications.</summary>
    public static Notification Ringing(Context context, int count)
    {
        var open = PendingIntent.GetActivity(context, 0, new Intent(context, typeof(MainActivity)),
            PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
        var b = new NotificationCompat.Builder(context, ServiceChannel);
        b.SetSmallIcon(Resource.Drawable.ic_stat_alarm);
        b.SetGroup("ringing-service");
        b.SetContentTitle(count == 1 ? "An alarm is ringing" : $"{count} alarms are ringing");
        b.SetContentIntent(open);
        b.SetOngoing(true);
        b.SetSilent(true);
        b.SetPriority(NotificationCompat.PriorityLow);
        return b.Build()!;
    }

    public static Notification Sync(Context context, string text)
    {
        var open = PendingIntent.GetActivity(context, SyncId, new Intent(context, typeof(DevicesActivity)),
            PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
        var b = new NotificationCompat.Builder(context, SyncChannel);
        b.SetSmallIcon(Resource.Drawable.ic_stat_alarm);
        b.SetContentTitle("Keeping your devices in sync");
        b.SetContentText(text);
        b.SetOngoing(true);
        b.SetContentIntent(open);
        b.SetPriority(NotificationCompat.PriorityLow);
        return b.Build()!;
    }

    /// <summary>A pairing request while no screen is open to ask: tapping it opens the
    /// Devices screen, which asks.</summary>
    public static void ShowPairRequest(Context context, PairPrompt p)
    {
        var open = PendingIntent.GetActivity(context, PairId, new Intent(context, typeof(DevicesActivity)),
            PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
        var b = new NotificationCompat.Builder(context, PairChannel);
        b.SetSmallIcon(Resource.Drawable.ic_stat_alarm);
        b.SetContentTitle(p.IsRequester ? $"Linking with \"{p.PeerName}\"" : $"\"{p.PeerName}\" wants to link");
        b.SetContentText($"Tap to compare the codes and answer ({p.Code[..3]} {p.Code[3..]}).");
        b.SetAutoCancel(true);
        b.SetContentIntent(open);
        b.SetPriority(NotificationCompat.PriorityHigh);
        Post(context, PairId, b.Build()!);
    }

    public static void Post(Context context, int id, Notification n)
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(33)
            && context.CheckSelfPermission(global::Android.Manifest.Permission.PostNotifications) != global::Android.Content.PM.Permission.Granted)
            return;
        NotificationManagerCompat.From(context)!.Notify(id, n);
    }

    public static void Cancel(Context context, int id) => NotificationManagerCompat.From(context)!.Cancel(id);
}

/// <summary>The Mark done and Remind me later buttons of an alarm notification. Each acts
/// only on the alert it was posted for: a notification left over from one already answered
/// must not mark the task done again (which would move an interval task that restarts when
/// done) or snooze it.</summary>
[BroadcastReceiver(Enabled = true, Exported = false)]
public sealed class AlarmActionReceiver : BroadcastReceiver
{
    public const string Done = "done";
    public const string Later = "later";

    public static PendingIntent IntentFor(Context context, TaskStatus st, string action)
    {
        var i = new Intent(context, typeof(AlarmActionReceiver)).SetAction(action)
            .PutExtra("task", st.Task.Id.ToString()).PutExtra("due", st.AlertDue?.UtcTicks ?? 0);
        return PendingIntent.GetBroadcast(context, Notifications.AlarmId(st.Task.Id) * 2 + (action == Done ? 0 : 1), i,
            PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent)!;
    }

    public override void OnReceive(Context? context, Intent? intent)
    {
        if (!Guid.TryParse(intent?.GetStringExtra("task"), out var id) || AppState.Store.Get(id) is not { } t) return;
        var st = AlarmEngine.Evaluate(t, AppState.Clock.UtcNow, AppState.Clock.Zone);
        if (!st.ShouldAlert || st.AlertDue?.UtcTicks != intent!.GetLongExtra("due", -1))
        {
            // Every post of a task's alarm updates these buttons, so this notification is
            // stale: withdrawn. An alert due now is posted afresh by the next evaluation.
            AppState.Log($"Ignored a notification button for an alert already answered: {TextFormat.Snippet(t.Def.Description, 40)}");
            Notifications.Cancel(context!, Notifications.AlarmId(id));
            RingingService.Poke();
            return;
        }
        if (intent.Action == Done) AppState.Store.MarkDone(id);
        else AppState.Store.Dismiss(id);
    }
}
