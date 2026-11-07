using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Media;
using Android.OS;

namespace AlarmCalendar.Droid;

/// <summary>
/// Runs while any task is ringing: posts each one's alarm notification (whose full-screen
/// intent opens <see cref="AlarmActivity"/> over the lock screen), plays the sound, and
/// re-evaluates every second, so an alarm marked done or dismissed anywhere (another
/// screen, a notification button, another device) stops here too. A foreground service,
/// so the sound keeps playing with the app in the background (D4).
/// </summary>
[Service(Exported = false, ForegroundServiceType = ForegroundService.TypeMediaPlayback)]
public sealed class RingingService : Service
{
    /// <summary>What the alarm screen needs to know about a ringing task's current alert.</summary>
    public sealed record AlertInfo(DateTimeOffset? Due, string? LateText, string? SoundProblem);

    private static RingingService? _instance;
    private static readonly Dictionary<Guid, AlertInfo> Alerts = new();
    private readonly Handler _handler = new(Looper.MainLooper!);
    private AlarmSound? _sound;
    private Vibrator? _vibrator;
    private bool _foreground;

    public static bool IsRunning => _instance is not null;
    public static bool IsShowing(Guid task) => Alerts.ContainsKey(task);
    public static AlertInfo? InfoFor(Guid task) => Alerts.GetValueOrDefault(task);

    public static void Start(Context context)
    {
        try { context.StartForegroundService(new Intent(context, typeof(RingingService))); }
        catch (Exception e) when (e is Java.Lang.IllegalStateException or ForegroundServiceStartNotAllowedException)
        {
            // Not allowed from here (the app in the background without an exemption); the
            // scheduler's next alarm, a second away, starts it instead.
            AppState.Log($"Couldn't start the alarm service now: {e.Message}");
        }
    }

    /// <summary>Re-evaluates at once (after a task changed).</summary>
    public static void Poke() => _instance?.Evaluate();

    /// <summary>Stops the sound of one task without dismissing it (the Silence button).</summary>
    public static void Silence(Guid task) => _instance?._sound?.Stop(task);

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
        // A foreground service must call StartForeground promptly whatever happens next.
        if (!_foreground)
        {
            var b = new AndroidX.Core.App.NotificationCompat.Builder(this, Notifications.AlarmChannel);
            b.SetSmallIcon(Resource.Drawable.ic_stat_alarm);
            b.SetContentTitle("Alarm");
            Foreground(1, b.Build()!);
            _foreground = true;
        }
        Evaluate();
        return StartCommandResult.NotSticky;
    }

    private void Evaluate()
    {
        _handler.RemoveCallbacksAndMessages(null);
        var now = AppState.Clock.UtcNow;
        var ringing = AlarmScheduler.RingingNow(now, out _);
        var ids = ringing.Select(s => s.Task.Id).ToHashSet();

        foreach (var gone in Alerts.Keys.Where(k => !ids.Contains(k)).ToList())
        {
            Alerts.Remove(gone);
            _sound!.Stop(gone);
            Notifications.Cancel(this, Notifications.AlarmId(gone));
        }

        foreach (var st in ringing)
        {
            var id = st.Task.Id;
            if (Alerts.TryGetValue(id, out var info) && info.Due == st.AlertDue) continue;
            // A new alert: a task just came due, or its reminder did, or another occurrence.
            string? late = AlarmEngine.Lateness(st, now) is not null && st.AlertDue is { } due
                ? TextFormat.LateWarning(due, now, AppState.StartedUtc, AppState.Clock.Zone) : null;
            var mode = AppState.Settings.SoundModeFor(st.Task.Def);
            var problem = _sound!.Play(id, AppState.Settings.SoundFor(st.Task.Def, Platforms.Android), mode == SoundMode.Repeat);
            Alerts[id] = new AlertInfo(st.AlertDue, late, problem);
            Vibrate(mode == SoundMode.Repeat);
            var n = Notifications.Alarm(this, st);
            Notifications.Post(this, Notifications.AlarmId(id), n);
            Foreground(Notifications.AlarmId(id), n);
            AppState.Log($"Ringing: {TextFormat.Snippet(st.Task.Def.Description, 40)}");
        }

        if (ringing.Count == 0)
        {
            _vibrator?.Cancel();
            StopForeground(StopForegroundFlags.Remove);
            _foreground = false;
            StopSelf();
            return;
        }
        _handler.PostDelayed(Evaluate, 1000);
    }

    private void Foreground(int id, Notification n)
    {
        StartForeground(id, n, ForegroundService.TypeMediaPlayback);
    }

    private void Vibrate(bool repeat)
    {
        if (_vibrator is not { HasVibrator: true }) return;
        long[] pattern = [0, 600, 400, 600, 1400];
        var effect = VibrationEffect.CreateWaveform(pattern, repeat ? 0 : -1)!;
        if (OperatingSystem.IsAndroidVersionAtLeast(33))
            _vibrator.Vibrate(effect, VibrationAttributes.CreateForUsage((int)VibrationAttributesUsageType.Alarm));
        else
#pragma warning disable CA1422 // the old overload, for Android 12 and below only
            _vibrator.Vibrate(effect, new AudioAttributes.Builder().SetUsage(AudioUsageKind.Alarm)!.Build()!);
#pragma warning restore CA1422
    }

    public override void OnDestroy()
    {
        _handler.RemoveCallbacksAndMessages(null);
        _sound?.StopAll();
        _vibrator?.Cancel();
        foreach (var id in Alerts.Keys) Notifications.Cancel(this, Notifications.AlarmId(id));
        Alerts.Clear();
        _instance = null;
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
    private sealed record Entry(Guid Owner, global::Android.Net.Uri Uri, bool Loop);

    private readonly List<Entry> _active = [];
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

    /// <summary>Plays the task's sound (R10, R11: <paramref name="sound"/> is the task's
    /// choice or the default in Settings; null means the phone's alarm sound), falling back
    /// through the phone's own sounds if it can't be played.</summary>
    /// <returns>A description of the problem when the chosen sound couldn't play and another
    /// played instead; otherwise null.</returns>
    public string? Play(Guid owner, SoundRef? sound, bool loop)
    {
        _active.RemoveAll(e => e.Owner == owner);
        var candidates = new List<global::Android.Net.Uri>();
        if (sound is not null) candidates.Add(global::Android.Net.Uri.Parse(sound.Value)!);
        candidates.AddRange(SystemSounds());
        foreach (var uri in candidates)
        {
            var e = new Entry(owner, uri, loop);
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
        if (_playing?.Owner == owner) StartNewest();
    }

    public void StopAll()
    {
        _active.Clear();
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
            p.Completion += (_, _) => { if (_playing == e) { _active.Remove(e); StartNewest(); } };
            p.Prepare();
            p.Start();
            _player = p;
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

    private void Release()
    {
        try { _player?.Stop(); } catch (Java.Lang.IllegalStateException) { }
        _player?.Release();
        _player = null;
        _playing = null;
    }
}
