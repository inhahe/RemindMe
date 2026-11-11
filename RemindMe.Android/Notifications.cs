using Android.App;
using Android.Content;
using Android.OS;
using AndroidX.Core.App;
using RemindMe.Sync;

namespace RemindMe.Droid;

/// <summary>The app's notification channels and the notifications built on them.</summary>
public static class Notifications
{
    public const string AlarmChannel = "alarms";
    /// <summary>Alarms posted when the ringing service couldn't start: this channel carries
    /// the phone's alarm sound itself.</summary>
    public const string FallbackChannel = "alarms_fallback";
    /// <summary>The ringing service's own notification's channel (see <see cref="CreateChannels"/>).
    /// It must not alert: Android groups an app's alerting notifications, and a grouped
    /// alarm's full-screen intent doesn't open the alarm screen.</summary>
    public static string ServiceChannel { get; private set; } = "ringing_status";
    /// <summary>The sync service's notification's channel (see <see cref="CreateChannels"/>).</summary>
    public static string SyncChannel { get; private set; } = "sync_status";
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
        ServiceChannel = StatusChannel(nm, "ringing_status", "ringing_service", "Alarm in progress",
            "Shown while an alarm rings, so Android lets the sound keep playing");
        SyncChannel = StatusChannel(nm, "sync_status", "sync", "Sync", "Shown while the app keeps your devices in sync");
        nm.CreateNotificationChannel(new NotificationChannel(PairChannel, "Linking devices", NotificationImportance.High)
        {
            Description = "Another device asks to link with this one",
        });
    }

    /// <summary>
    /// A status channel: news of nothing, so no badge on the app's icon (the alarms and link
    /// requests have one). A channel's badge can't be turned off once it exists, so these
    /// replace earlier versions' channels, which are deleted; unless the user has the old one
    /// off. That one stays in use while it is, so it stays off: Android raises a foreground
    /// service's channel that the app makes off (or minimized), though not one the user did.
    /// Off, it neither alerts nor shows anything to badge. Once it is on again, the next start
    /// moves to the new channel. Other choices (silent, minimized) aren't carried over, for
    /// that same reason: kept, a raised importance would make the ringing service's alert.
    /// </summary>
    /// <param name="id">The channel's ID.</param>
    /// <param name="oldId">Its predecessor's, from an earlier version.</param>
    /// <returns>The ID to post on: <paramref name="id"/>, or <paramref name="oldId"/> while the
    /// user has that one off.</returns>
    private static string StatusChannel(NotificationManager nm, string id, string oldId, string name, string description)
    {
        if (nm.GetNotificationChannel(oldId) is { Importance: NotificationImportance.None }) return oldId;
        var channel = new NotificationChannel(id, name, NotificationImportance.Low) { Description = description };
        channel.SetShowBadge(false);
        nm.CreateNotificationChannel(channel);
        // Android refuses to delete a channel a foreground service's notification is on;
        // none is at start, when this runs, but a refusal mustn't stop the app.
        try { nm.DeleteNotificationChannel(oldId); }
        catch (Java.Lang.SecurityException e) { AppState.Log($"Could not delete the old notification channel {oldId}: {e.Message}"); }
        return id;
    }

    /// <summary>A stable notification ID for a task's alarm.</summary>
    public static int AlarmId(Guid task) => 1000 + (task.GetHashCode() & 0x3FFFFFFF);

    /// <summary>The ringing notification of one task: it opens the alarm screen full-screen
    /// when the phone is locked, and offers Mark done and Remind me later. It also carries
    /// <paramref name="alert"/>, for a process that starts while it shows (<see cref="ShownAlarms"/>).</summary>
    /// <param name="fallback">Posted without the ringing service, on the channel that sounds.</param>
    /// <param name="silenced">The safety net's alarm, silenced: posted on the silent channel.</param>
    public static Notification Alarm(Context context, TaskStatus st, RingingService.AlertInfo alert, bool fallback, bool silenced = false)
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
        b.AddAction(0, "Mark done", AlarmActionReceiver.IntentFor(context, st.Task.Id, AlarmActionReceiver.Done));
        b.AddAction(0, "Remind me later", AlarmActionReceiver.IntentFor(context, st.Task.Id, AlarmActionReceiver.Later));
        var extras = new Bundle();
        extras.PutString(ExtraTask, st.Task.Id.ToString());
        extras.PutLong(ExtraDue, alert.Due?.UtcTicks ?? 0);
        extras.PutString(ExtraLate, alert.LateText);
        extras.PutString(ExtraProblem, alert.SoundProblem);
        extras.PutBoolean(ExtraRepeat, alert.Repeat);
        extras.PutBoolean(ExtraSilenced, silenced);
        b.AddExtras(extras);
        return b.Build()!;
    }

    private const string ExtraTask = "remindme.task", ExtraDue = "remindme.due", ExtraLate = "remindme.late",
        ExtraProblem = "remindme.problem", ExtraRepeat = "remindme.repeat", ExtraSilenced = "remindme.silenced";

    /// <summary>A task's alarm notification showing now, whoever posted it.</summary>
    /// <param name="Task">Null for one whose alert can't be read (an earlier version posted it).</param>
    public sealed record ShownAlarm(int Id, Guid? Task, RingingService.AlertInfo? Alert, bool Silenced);

    public static IEnumerable<ShownAlarm> ShownAlarms(Context context)
    {
        var nm = (NotificationManager)context.GetSystemService(Context.NotificationService)!;
        foreach (var n in nm.GetActiveNotifications() ?? [])
        {
            if (n.Notification?.ChannelId is not (AlarmChannel or FallbackChannel)) continue;
            var x = n.Notification.Extras;
            if (x is null || !Guid.TryParse(x.GetString(ExtraTask), out var task))
            {
                yield return new ShownAlarm(n.Id, null, null, false);
                continue;
            }
            long due = x.GetLong(ExtraDue);
            var alert = new RingingService.AlertInfo(due == 0 ? null : new DateTimeOffset(due, TimeSpan.Zero),
                x.GetString(ExtraLate), x.GetString(ExtraProblem), x.GetBoolean(ExtraRepeat));
            yield return new ShownAlarm(n.Id, task, alert, x.GetBoolean(ExtraSilenced));
        }
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

    /// <summary>A pairing request while no screen of the app is in front to ask (or only the
    /// alarm's): tapping it brings the app forward as it was, and the screen in front asks.</summary>
    /// <param name="quiet">Without a sound or pop-up: over the alarm screen, or once seen.</param>
    public static void ShowPairRequest(Context context, PairPrompt p, bool quiet)
    {
        // As the launcher opens the app: its task as it was, or a new one.
        var launch = context.PackageManager!.GetLaunchIntentForPackage(context.PackageName!)!;
        launch.AddFlags(ActivityFlags.NewTask | ActivityFlags.ResetTaskIfNeeded);
        var open = PendingIntent.GetActivity(context, PairId, launch,
            PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
        var b = new NotificationCompat.Builder(context, PairChannel);
        b.SetSmallIcon(Resource.Drawable.ic_stat_alarm);
        b.SetContentTitle(p.IsRequester ? $"Linking with \"{p.PeerName}\"" : $"\"{p.PeerName}\" wants to link");
        b.SetContentText($"Tap to compare the codes and answer ({p.Code[..3]} {p.Code[3..]}).");
        b.SetAutoCancel(true);
        b.SetContentIntent(open);
        b.SetPriority(NotificationCompat.PriorityHigh);
        b.SetSilent(quiet);
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
/// only while its task rings: a notification left over once the task was answered must not
/// mark it done again (which would move an interval task that restarts when done) or snooze it.</summary>
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
        if (!Guid.TryParse(intent?.GetStringExtra("task"), out var id) || AppState.Store.Status(id) is not { } st) return;
        if (!st.ShouldAlert)
        {
            AppState.Log($"Ignored a notification button for an alarm already answered: {TextFormat.Snippet(st.Task.Def.Description, 40)}");
            Notifications.Cancel(context!, Notifications.AlarmId(id));
            RingingService.Poke();
            return;
        }
        // Answering changes the store, whose handler pokes the ringing service, which
        // withdraws the notification.
        if (intent!.Action == Done) AppState.Store.MarkDone(id);
        else AppState.Store.Dismiss(id);
    }
}
