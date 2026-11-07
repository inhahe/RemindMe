using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Media;
using Android.OS;

namespace AlarmCalendar.Droid;

/// <summary>
/// Runs while any task is ringing: posts each one's alarm notification (whose full-screen
/// intent opens <see cref="AlarmActivity"/> over the lock screen), plays the sound,
/// vibrates, and re-evaluates every second, so an alarm marked done or dismissed anywhere
/// (another screen, a notification button, another device) stops here too. A foreground
/// service, so the sound keeps playing with the app in the background (D4).
/// <para>The service's own foreground notification has a fixed ID that is never a task's:
/// Android won't cancel a running foreground service's notification, and removes the old
/// one when the service moves to another ID, so no task's alarm may serve as it.</para>
/// </summary>
[Service(Exported = false, ForegroundServiceType = ForegroundService.TypeMediaPlayback)]
public sealed class RingingService : Service
{
    /// <summary>What the alarm screen needs to know about a ringing task's current alert.</summary>
    /// <param name="Repeat">Its sound (and vibration) repeats until it is answered.</param>
    public sealed record AlertInfo(DateTimeOffset? Due, string? LateText, string? SoundProblem, bool Repeat);

    private const int ServiceNotificationId = 1;
    private static RingingService? _instance;
    /// <summary>The alerts posted and still showing, by task, on the main thread only. Shared
    /// with the safety net (<see cref="NotifyWithoutService"/>), so an alert is posted once,
    /// and rebuilt from the notifications themselves when a process starts
    /// (<see cref="RestoreFromShown"/>), so it stays true across a process that died.</summary>
    private static readonly Dictionary<Guid, AlertInfo> Alerts = new();
    private static readonly HashSet<Guid> Silenced = new();
    private readonly Handler _handler = new(Looper.MainLooper!);
    private AlarmSound? _sound;
    private Vibrator? _vibrator;
    private bool _vibrating;
    /// <summary>The count the service's notification shows; it is re-posted only when this changes.</summary>
    private int _shownCount = -1;

    public static bool IsRunning => _instance is not null;
    public static AlertInfo? InfoFor(Guid task) => Alerts.GetValueOrDefault(task);

    /// <summary>True when the current alert of <paramref name="st"/> has been posted.</summary>
    public static bool HasAlerted(TaskStatus st) => Alerts.TryGetValue(st.Task.Id, out var a) && a.Due == st.AlertDue;

    /// <returns>False when Android refused to start it from here.</returns>
    public static bool TryStart(Context context)
    {
        try
        {
            context.StartForegroundService(new Intent(context, typeof(RingingService)));
            return true;
        }
        // Refused in the background (Android 12+), or by an app op or a missing permission.
        catch (Exception e) when (e is Java.Lang.IllegalStateException or ForegroundServiceStartNotAllowedException
            or Java.Lang.SecurityException)
        {
            AppState.Log($"Couldn't start the ringing service: {e.Message}");
            return false;
        }
    }

    /// <summary>
    /// The safety net when the service can't start (a phone maker's battery manager, or an
    /// inexact alarm on Android 12 without exact-alarm access): each alert goes out as a
    /// notification on a channel with the phone's alarm sound, which Android itself plays,
    /// over and over (insistent) for a task whose sound repeats, until it is answered or
    /// silenced. The task's own sound isn't used, but it rings even if the process dies.
    /// <see cref="Poke"/> withdraws it once the task is answered anywhere.
    /// </summary>
    public static void NotifyWithoutService(Context context, IEnumerable<TaskStatus> ringing)
    {
        var now = AppState.Clock.UtcNow;
        foreach (var st in ringing)
        {
            if (HasAlerted(st)) continue;
            var alert = NewAlert(st, now, soundProblem: "The alarm couldn't start its own sound, so the phone's alarm sound plays.");
            Alerts[st.Task.Id] = alert;
            var n = Notifications.Alarm(context, st, alert, fallback: true);
            if (alert.Repeat) n.Flags |= NotificationFlags.Insistent;
            // Withdrawn first: an update of a notification still showing wouldn't sound again.
            Notifications.Cancel(context, Notifications.AlarmId(st.Task.Id));
            Notifications.Post(context, Notifications.AlarmId(st.Task.Id), n);
            AppState.Log($"Ringing without the service: {TextFormat.Snippet(st.Task.Def.Description, 40)}");
        }
    }

    private static AlertInfo NewAlert(TaskStatus st, DateTimeOffset now, string? soundProblem)
    {
        string? late = AlarmEngine.Lateness(st, now) is not null && st.AlertDue is { } due
            ? TextFormat.LateWarning(due, now, AppState.StartedUtc, AppState.Clock.Zone) : null;
        return new AlertInfo(st.AlertDue, late, soundProblem, AppState.Settings.SoundModeFor(st.Task.Def) == SoundMode.Repeat);
    }

    /// <summary>Re-evaluates at once, after a task changed. Without the service (the safety
    /// net), withdraws the notification of every alert that no longer rings, which nothing
    /// else would: answered on the alarm screen, by a notification button, or on another device.</summary>
    public static void Poke()
    {
        if (_instance is { } s) s.Evaluate();
        else WithdrawAnswered(AppState.Context, sound: null);
    }

    /// <summary>
    /// Rebuilds <see cref="Alerts"/> and <see cref="Silenced"/> from the alarm notifications
    /// showing; called as the process starts. They outlive a process that dies (the safety
    /// net's keep sounding, since Android plays them), and a process that didn't know of them
    /// would post each again, sounding one that was silenced and reopening the alarm screen,
    /// or leave one up once answered. One whose alert can't be read (an earlier version
    /// posted it) is withdrawn, and posted again as a new alert if its task still rings.
    /// </summary>
    public static void RestoreFromShown(Context context)
    {
        foreach (var n in Notifications.ShownAlarms(context))
        {
            if (n is not { Task: { } task, Alert: { } alert })
            {
                Notifications.Cancel(context, n.Id);
                continue;
            }
            Alerts[task] = alert;
            if (n.Silenced) Silenced.Add(task);
        }
    }

    /// <summary>Withdraws every alert whose task should no longer alert: its notification,
    /// its silencing, and <paramref name="sound"/>'s playing of it. One merely held back while
    /// sync settles after a start still should, and is kept.</summary>
    private static void WithdrawAnswered(Context context, AlarmSound? sound)
    {
        foreach (var id in Alerts.Keys.ToList())
        {
            if (AppState.Store.Status(id) is { ShouldAlert: true, Task.Def.Deleted: false }) continue;
            Alerts.Remove(id);
            Silenced.Remove(id);
            sound?.Stop(id);
            Notifications.Cancel(context, Notifications.AlarmId(id));
        }
    }

    /// <summary>Stops the sound, and the vibration, of one task without dismissing it.</summary>
    public static void Silence(Guid task)
    {
        Silenced.Add(task);
        if (_instance is { } s)
        {
            s._sound?.Stop(task);
            s.UpdateVibration(newAlert: false);
            return;
        }
        // The safety net, where the notification itself sounds: posted again on the silent channel.
        if (Alerts.TryGetValue(task, out var alert) && AppState.Store.Status(task) is { } st)
            Notifications.Post(AppState.Context, Notifications.AlarmId(task),
                Notifications.Alarm(AppState.Context, st, alert, fallback: false, silenced: true));
    }

    public override IBinder? OnBind(Intent? intent) => null;

    public override void OnCreate()
    {
        base.OnCreate();
        _instance = this;
        _sound = new AlarmSound(this);
#pragma warning disable CA1422 // the old service, for Android 10 and 11 only
        _vibrator = OperatingSystem.IsAndroidVersionAtLeast(31)
            ? ((VibratorManager)GetSystemService(VibratorManagerService)!).DefaultVibrator
            : (Vibrator?)GetSystemService(VibratorService);
#pragma warning restore CA1422
    }

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        // A foreground service must call StartForeground promptly, whatever happens next.
        try
        {
            StartForeground(ServiceNotificationId, Notifications.Ringing(this, Alerts.Count), ForegroundService.TypeMediaPlayback);
        }
        catch (Exception e) when (e is Java.Lang.IllegalStateException or ForegroundServiceStartNotAllowedException
            or Java.Lang.SecurityException)
        {
            // Restarted in the background (sticky, after Android killed the process), where
            // Android 12 and later may refuse a foreground service: the safety net rings instead.
            // No longer the running service from here on, so that nothing (a Poke before the
            // destroy) has it play over the safety net's notification, only to stop.
            AppState.Log($"The ringing service couldn't go to the foreground: {e.Message}");
            if (_instance == this) _instance = null;
            NotifyWithoutService(this, AlarmScheduler.RingingNow(AppState.Clock.UtcNow, out _));
            StopSelf();
            return StartCommandResult.NotSticky;
        }
        Evaluate();
        // Sticky: if Android kills the process mid-alarm, it restarts the service, which
        // finds the alarm still due and rings it again.
        return StartCommandResult.Sticky;
    }

    private void Evaluate()
    {
        _handler.RemoveCallbacksAndMessages(null);
        var now = AppState.Clock.UtcNow;
        var ringing = AlarmScheduler.RingingNow(now, out _);
        WithdrawAnswered(this, _sound);

        bool newAlert = false;
        foreach (var st in ringing)
        {
            var id = st.Task.Id;
            bool posted = HasAlerted(st);
            // Posted already, by this service with its sound running: nothing new.
            if (posted && (_sound!.IsPlayingOrPlayed(id) || Silenced.Contains(id))) continue;
            Silenced.Remove(id);
            var settings = AppState.Settings;
            var problem = _sound!.Play(id, settings.SoundFor(st.Task.Def, Platforms.Android),
                loop: settings.SoundModeFor(st.Task.Def) == SoundMode.Repeat,
                volume: settings.VolumeFor(st.Task.Def), fadeIn: settings.FadeInFor(st.Task.Def));
            Alerts[id] = posted ? Alerts[id] with { SoundProblem = problem } : NewAlert(st, now, problem);
            // A new alert (the first, or a later occurrence) is posted afresh, so it alerts and
            // opens the alarm screen; alarm notifications alert only once. One taken over (from
            // the safety net, or from a process that died) is updated quietly.
            if (!posted) Notifications.Cancel(this, Notifications.AlarmId(id));
            Notifications.Post(this, Notifications.AlarmId(id), Notifications.Alarm(this, st, Alerts[id], fallback: false));
            newAlert = true;
            AppState.Log($"Ringing: {TextFormat.Snippet(st.Task.Def.Description, 40)}");
        }

        if (ringing.Count == 0)
        {
            StopVibration();
            StopForeground(StopForegroundFlags.Remove);
            StopSelf();
            return;
        }
        if (ringing.Count != _shownCount)
        {
            _shownCount = ringing.Count;
            Notifications.Post(this, ServiceNotificationId, Notifications.Ringing(this, ringing.Count));
        }
        UpdateVibration(newAlert);
        _handler.PostDelayed(Evaluate, 1000);
    }

    /// <summary>Vibrates for a new alert, keeps vibrating while some unsilenced alert
    /// repeats, and stops once none does.</summary>
    private void UpdateVibration(bool newAlert)
    {
        if (_vibrator is not { HasVibrator: true }) return;
        bool repeat = Alerts.Any(kv => kv.Value.Repeat && !Silenced.Contains(kv.Key));
        if (newAlert)
        {
            Vibrate(repeat);
            _vibrating = repeat;
        }
        else if (_vibrating && !repeat) StopVibration();
    }

    private void StopVibration()
    {
        _vibrator?.Cancel();
        _vibrating = false;
    }

    private void Vibrate(bool repeat)
    {
        long[] pattern = [0, 600, 400, 600, 1400];
        var effect = VibrationEffect.CreateWaveform(pattern, repeat ? 0 : -1)!;
        if (OperatingSystem.IsAndroidVersionAtLeast(33))
            _vibrator!.Vibrate(effect, VibrationAttributes.CreateForUsage((int)VibrationAttributesUsageType.Alarm));
        else
#pragma warning disable CA1422 // the old overload, for Android 12 and below only
            _vibrator!.Vibrate(effect, new AudioAttributes.Builder().SetUsage(AudioUsageKind.Alarm)!.Build()!);
#pragma warning restore CA1422
    }

    public override void OnDestroy()
    {
        _handler.RemoveCallbacksAndMessages(null);
        _sound?.StopAll();
        StopVibration();
        if (_instance == this) _instance = null;
        base.OnDestroy();
        AlarmScheduler.Reschedule(this);
    }
}

/// <summary>
/// Plays alarm sounds on the alarm audio stream. One at a time (D5): the newest alert's
/// sound plays, and when it stops, the newest remaining looping one resumes.
/// </summary>
public sealed class AlarmSound(Context context)
{
    /// <param name="Volume">0.1 to 1, of the alarm stream's volume (S10).</param>
    /// <param name="Since">When the alarm began: a fade-in counts from it, so a sound resumed
    /// after another alarm interrupted it carries on where its fade had got to.</param>
    private sealed record Entry(Guid Owner, global::Android.Net.Uri Uri, bool Loop, float Volume, TimeSpan FadeIn, DateTime Since);

    private readonly Handler _fade = new(Looper.MainLooper!);

    private readonly List<Entry> _active = [];
    private readonly HashSet<Guid> _started = [];
    private MediaPlayer? _player;
    private Entry? _playing;

    /// <summary>The phone's own sounds, in the order to fall back on: its alarm, ringtone and
    /// notification defaults, then the first alarm sound installed (a phone, or an emulator,
    /// may have no default set at all).</summary>
    private IEnumerable<global::Android.Net.Uri> SystemSounds()
    {
        foreach (var type in new[] { RingtoneType.Alarm, RingtoneType.Ringtone, RingtoneType.Notification })
            if (RingtoneManager.GetActualDefaultRingtoneUri(context, type) is { } u) yield return u;
        global::Android.Net.Uri? first = null;
        try
        {
            var rm = new RingtoneManager(context);
            rm.SetType(RingtoneType.Alarm);
            using var cursor = rm.Cursor;
            if (cursor is not null && cursor.MoveToFirst()) first = rm.GetRingtoneUri(0);
        }
        catch (Java.Lang.Exception) { }
        if (first is not null) yield return first;
    }

    /// <summary>This owner's sound was started (it may have finished, if it plays once).</summary>
    public bool IsPlayingOrPlayed(Guid owner) => _started.Contains(owner);

    /// <summary>Plays the task's sound (R10, R11: <paramref name="sound"/> is the task's
    /// choice or the default in Settings; null means the phone's alarm sound), falling back
    /// through the phone's own sounds if it can't be played.</summary>
    /// <returns>A description of the problem when the chosen sound couldn't play and another
    /// played instead; otherwise null.</returns>
    /// <param name="volume">0.1 to 1: the task's volume (S10).</param>
    /// <param name="fadeIn">How long the sound takes to grow to <paramref name="volume"/>.</param>
    public string? Play(Guid owner, SoundRef? sound, bool loop, double volume = 1.0, TimeSpan fadeIn = default)
    {
        _active.RemoveAll(e => e.Owner == owner);
        _started.Add(owner);
        var candidates = new List<global::Android.Net.Uri>();
        if (sound is not null) candidates.Add(global::Android.Net.Uri.Parse(sound.Value)!);
        candidates.AddRange(SystemSounds());
        foreach (var uri in candidates)
        {
            var e = new Entry(owner, uri, loop, (float)Math.Clamp(volume, 0.1, 1.0), fadeIn, DateTime.UtcNow);
            if (!Start(e)) continue;
            _active.Add(e);
            return sound is not null && uri != candidates[0]
                ? $"The sound \"{sound.Name}\" couldn't be played, so the phone's alarm sound plays instead."
                : null;
        }
        return "No sound could be played; check the sound settings and the alarm volume.";
    }

    public void Stop(Guid owner)
    {
        _active.RemoveAll(e => e.Owner == owner);
        _started.Remove(owner);
        if (_playing?.Owner == owner) StartNewest();
    }

    public void StopAll()
    {
        _active.Clear();
        _started.Clear();
        Release();
    }

    /// <returns>False when <paramref name="e"/> can't be played.</returns>
    private bool Start(Entry e)
    {
        Release();
        _playing = e;
        var p = new MediaPlayer();
        try
        {
            p.SetAudioAttributes(new AudioAttributes.Builder()
                .SetUsage(AudioUsageKind.Alarm)!.SetContentType(AudioContentType.Sonification)!.Build()!);
            p.SetDataSource(context, e.Uri);
            p.Looping = e.Loop;
            float level = Level(e);
            p.SetVolume(level, level);
            p.Completion += (_, _) => { if (_playing == e) { _active.Remove(e); StartNewest(); } };
            p.Prepare();
            p.Start();
            _player = p;
            if (level < e.Volume) _fade.PostDelayed(Fade, 200);
            AppState.Log($"Playing {e.Uri} ({(e.Loop ? "looping" : "once")})");
            return true;
        }
        catch (Exception ex) when (ex is Java.Lang.Exception or IOException)
        {
            AppState.Log($"Couldn't play {e.Uri}: {ex.Message}");
            p.Release();
            _playing = null;
            return false;
        }
    }

    private void StartNewest()
    {
        _active.RemoveAll(e => !e.Loop);
        while (_active.Count > 0 && !Start(_active[^1])) _active.RemoveAt(_active.Count - 1);
        if (_active.Count == 0) Release();
    }

    private static float Level(Entry e) => (float)SoundLevels.FadeLevel(e.Volume, DateTime.UtcNow - e.Since, e.FadeIn);

    /// <summary>One step of a fade-in, every 200 ms until the sound reaches its volume.</summary>
    private void Fade()
    {
        if (_playing is not { } e || _player is not { } p) return;
        float level = Level(e);
        try { p.SetVolume(level, level); }
        catch (Java.Lang.IllegalStateException) { return; }
        if (level < e.Volume) _fade.PostDelayed(Fade, 200);
    }

    private void Release()
    {
        _fade.RemoveCallbacksAndMessages(null);
        try { _player?.Stop(); } catch (Java.Lang.IllegalStateException) { }
        _player?.Release();
        _player = null;
        _playing = null;
    }
}
