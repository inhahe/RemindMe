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
    public static Notification Alarm(Context context, TaskStatus st)
    {
        var d = st.Task.Def;
        var open = AlarmActivity.IntentFor(context, st.Task.Id);
        var openPi = PendingIntent.GetActivity(context, AlarmId(st.Task.Id), open,
            PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
        string title = d.Kind == ScheduleKind.Countdown ? "Countdown finished" : st.IsReminder ? "Reminder" : "Alarm";
        var text = TextFormat.Snippet(d.Description, 80);
        // The builder returns itself from every call; statements rather than a chain,
        // because the bindings annotate each return as nullable.
        var b = new NotificationCompat.Builder(context, AlarmChannel);
        b.SetSmallIcon(Resource.Drawable.ic_stat_alarm);
        b.SetContentTitle(text.Length > 0 ? text : title);
        b.SetContentText($"{title} · {TextFormat.Schedule(d, st.Task.State, AppState.Clock.UtcNow, AppState.Clock.Zone)}");
        b.SetCategory(NotificationCompat.CategoryAlarm);
        b.SetPriority(NotificationCompat.PriorityMax);
        b.SetOngoing(true);
        b.SetAutoCancel(false);
        b.SetContentIntent(openPi);
        b.SetFullScreenIntent(openPi, true);
        b.SetVisibility(NotificationCompat.VisibilityPublic);
        b.AddAction(0, "Mark done", AlarmActionReceiver.IntentFor(context, st.Task.Id, AlarmActionReceiver.Done));
        b.AddAction(0, "Remind me later", AlarmActionReceiver.IntentFor(context, st.Task.Id, AlarmActionReceiver.Later));
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
        b.SetContentTitle($"\"{p.PeerName}\" wants to link");
        b.SetContentText($"Tap to answer. Its code should be {p.Code[..3]} {p.Code[3..]}.");
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

/// <summary>The Mark done and Remind me later buttons of an alarm notification.</summary>
[BroadcastReceiver(Enabled = true, Exported = false)]
public sealed class AlarmActionReceiver : BroadcastReceiver
{
    public const string Done = "done";
    public const string Later = "later";

    public static PendingIntent IntentFor(Context context, Guid task, string action)
    {
        var i = new Intent(context, typeof(AlarmActionReceiver)).SetAction(action).PutExtra("task", task.ToString());
        return PendingIntent.GetBroadcast(context, Notifications.AlarmId(task) * 2 + (action == Done ? 0 : 1), i,
            PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent)!;
    }

    public override void OnReceive(Context? context, Intent? intent)
    {
        if (!Guid.TryParse(intent?.GetStringExtra("task"), out var id)) return;
        if (intent!.Action == Done) AppState.Store.MarkDone(id);
        else AppState.Store.Dismiss(id);
    }
}
