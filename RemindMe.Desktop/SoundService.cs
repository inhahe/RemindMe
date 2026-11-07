using System.IO;
using System.Media;
using System.Windows;
using System.Windows.Media;

namespace RemindMe.Desktop;

/// <summary>
/// Resolves sound choices to files and plays them. Only one sound plays at a time (D5):
/// every caller registers under an owner (an alarm window, a preview), the newest owner's
/// sound plays, and when it stops, the newest remaining looping sound resumes.
/// <para>WAV files, which include every Windows sound, play through <see cref="SoundPlayer"/>
/// (the waveOut API), which needs no Media Foundation and so also works on Windows "N"
/// editions, unless they are to play quieter or fade in (S10), which only WPF's
/// <see cref="MediaPlayer"/> can do; other formats always play through it. Where it can't
/// play a WAV, the WAV plays through <see cref="SoundPlayer"/> at full volume instead. A
/// sound that fails to play falls back to <see cref="BuiltIn"/> and raises
/// <see cref="Problem"/>, so an alarm never rings silently.</para>
/// </summary>
public sealed class SoundService
{
    /// <param name="Volume">0.1 to 1.</param>
    /// <param name="Since">When the alarm began, as <see cref="Environment.TickCount64"/>,
    /// which no clock change moves: a fade-in counts from it, so a sound resumed after another
    /// alarm interrupted it carries on where its fade had got to.</param>
    /// <param name="PlainWav">Play through <see cref="SoundPlayer"/> whatever the volume.</param>
    private sealed record Entry(object Owner, string Path, bool Loop, double Volume, TimeSpan FadeIn, long Since, bool PlainWav = false)
    {
        public TimeSpan Elapsed => TimeSpan.FromMilliseconds(Environment.TickCount64 - Since);

        public bool ViaSoundPlayer => Path.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)
            && (PlainWav || (Volume >= 0.999 && FadeIn <= TimeSpan.Zero));
    }

    public static readonly string MediaDir =
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Media");

    /// <summary>Plays when nothing else is chosen, or a chosen sound can't be played.</summary>
    public static readonly SoundRef BuiltIn = new(SoundSource.System, "Alarm01.wav", "Alarm01");

    private readonly MediaPlayer _media = new() { Volume = 1.0 };
    private readonly List<Entry> _active = [];
    private SoundPlayer? _wav;
    private Entry? _playing;
    private readonly System.Windows.Threading.DispatcherTimer _fade = new() { Interval = TimeSpan.FromMilliseconds(200) };
    /// <summary>Bumped whenever playback stops or changes, so a stale "finished" callback
    /// from an earlier sound is ignored.</summary>
    private int _generation;

    /// <summary>(owner, message): the owner's sound couldn't play, and another plays instead.</summary>
    public event Action<object, string>? Problem;

    public SoundService()
    {
        _media.MediaEnded += (_, _) => OnEnded(_generation);
        _media.MediaFailed += (_, e) => OnFailed(_generation, e.ErrorException);
        _media.MediaOpened += (_, _) => AppHost.Log($"Playing {_playing?.Path} ({(_playing?.Loop == true ? "looping" : "once")}, "
            + $"volume {_playing?.Volume:P0}{(_playing?.FadeIn > TimeSpan.Zero ? $", fading in over {_playing.FadeIn.TotalSeconds:0} s" : "")})");
        _fade.Tick += (_, _) => Fade();
    }

    /// <summary>The Windows sounds in <see cref="MediaDir"/>, alarms and rings first.</summary>
    public static IReadOnlyList<SoundRef> SystemSounds()
    {
        try
        {
            return Directory.GetFiles(MediaDir, "*.wav")
                .Select(f => new SoundRef(SoundSource.System, System.IO.Path.GetFileName(f), System.IO.Path.GetFileNameWithoutExtension(f)))
                .OrderBy(s => s.Name.StartsWith("Alarm", StringComparison.OrdinalIgnoreCase) ? 0
                    : s.Name.StartsWith("Ring", StringComparison.OrdinalIgnoreCase) ? 1 : 2)
                .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (IOException) { return [BuiltIn]; }
        catch (UnauthorizedAccessException) { return [BuiltIn]; }
    }

    /// <summary>The file a choice plays, or null if it doesn't exist.</summary>
    public static string? PathOf(SoundRef? s)
    {
        if (s is null) return null;
        var path = s.Source == SoundSource.System ? System.IO.Path.Combine(MediaDir, s.Value) : s.Value;
        return File.Exists(path) ? path : null;
    }

    /// <summary>The file <paramref name="taskSound"/> plays: the task's own choice, else the
    /// default in Settings, else <see cref="BuiltIn"/> (R11).</summary>
    /// <param name="taskSound">The task's choice for Windows; null means "default".</param>
    /// <param name="problem">Set when a chosen sound is missing and another plays instead.</param>
    public static string? Resolve(SoundRef? taskSound, out string? problem)
    {
        problem = null;
        foreach (var candidate in new[] { taskSound, AppHost.Settings.DefaultSound, BuiltIn })
        {
            if (candidate is null) continue;
            if (PathOf(candidate) is { } p) return p;
            problem ??= $"The sound \"{candidate.Name}\" wasn't found, so another plays instead.";
        }
        return null;
    }

    /// <summary>"Default (Alarm01)" or the chosen sound's name, for a sound button.</summary>
    public static string Describe(SoundRef? taskSound) =>
        taskSound is null ? $"Default ({(AppHost.Settings.DefaultSound ?? BuiltIn).Name})" : taskSound.Name;

    /// <summary>Plays <paramref name="path"/> for <paramref name="owner"/>, replacing whatever
    /// that owner was playing and taking over from any other owner's sound.</summary>
    /// <param name="loop">Repeat until <see cref="Stop"/>; otherwise play once.</param>
    /// <param name="volume">0.1 to 1: the task's volume (S10).</param>
    /// <param name="fadeIn">How long the sound takes to grow to <paramref name="volume"/>.</param>
    public void Play(object owner, string? path, bool loop, double volume = 1.0, TimeSpan fadeIn = default)
    {
        _active.RemoveAll(e => e.Owner == owner);
        if (path is null)
        {
            if (_playing?.Owner == owner) StartNewest();
            Beep();
            return;
        }
        var entry = new Entry(owner, path, loop, Math.Clamp(volume, 0.1, 1.0), fadeIn, Environment.TickCount64);
        _active.Add(entry);
        Start(entry);
    }

    /// <summary>Stops <paramref name="owner"/>'s sound; the newest remaining looping sound resumes.</summary>
    public void Stop(object owner)
    {
        _active.RemoveAll(e => e.Owner == owner);
        if (_playing?.Owner == owner) StartNewest();
    }

    /// <summary>True while <paramref name="owner"/>'s sound is the one playing.</summary>
    public bool IsPlaying(object owner) => _playing?.Owner == owner;

    public void StopAll()
    {
        _active.Clear();
        StopCurrent();
    }

    private void Start(Entry e)
    {
        StopCurrent();
        _playing = e;
        int gen = _generation;
        if (e.ViaSoundPlayer)
        {
            try
            {
                var player = new SoundPlayer(e.Path);
                player.Load();
                _wav = player;
                AppHost.Log($"Playing {e.Path} ({(e.Loop ? "looping" : "once")})");
                if (e.Loop) player.PlayLooping();
                else
                {
                    // SoundPlayer has no "finished" event: wait for the end on a pool thread.
                    var dispatcher = Application.Current.Dispatcher;
                    Task.Run(() => { try { player.PlaySync(); } catch (Exception) { } })
                        .ContinueWith(_ => dispatcher.BeginInvoke(() => OnEnded(gen)));
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or TimeoutException or UriFormatException)
            {
                OnFailed(gen, ex);
            }
            return;
        }
        _media.Volume = SoundLevels.FadeLevel(e.Volume, e.Elapsed, e.FadeIn);
        _media.Open(new Uri(e.Path));
        _media.Play();
        if (_media.Volume < e.Volume) _fade.Start();
    }

    /// <summary>One step of a fade-in, until the sound reaches its volume.</summary>
    private void Fade()
    {
        if (_playing is not { } e || e.ViaSoundPlayer) { _fade.Stop(); return; }
        _media.Volume = SoundLevels.FadeLevel(e.Volume, e.Elapsed, e.FadeIn);
        if (_media.Volume >= e.Volume) _fade.Stop();
    }

    private void StopCurrent()
    {
        _generation++;
        _playing = null;
        _fade.Stop();
        if (_wav is not null)
        {
            _wav.Stop();
            _wav.Dispose();
            _wav = null;
        }
        _media.Stop();
        _media.Close();
    }

    private void OnEnded(int gen)
    {
        if (gen != _generation || _playing is not { } e) return;
        if (e.Loop && _active.Contains(e) && _wav is null)
        {
            _media.Position = TimeSpan.Zero;
            _media.Play();
            return;
        }
        _active.Remove(e);
        StartNewest();
    }

    private void OnFailed(int gen, Exception? error)
    {
        if (gen != _generation || _playing is not { } e) return;
        AppHost.Log($"Could not play {e.Path}: {error?.Message}");
        _active.Remove(e);
        if (e.Path.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) && !e.ViaSoundPlayer)
        {
            // Media Foundation can't play here (a Windows "N" edition, say); the waveOut player
            // can, though only at full volume.
            Problem?.Invoke(e.Owner, "The volume and fade-in couldn't be applied here, so the sound plays at full volume.");
            var plain = e with { PlainWav = true };
            _active.Add(plain);
            Start(plain);
            return;
        }
        var builtIn = PathOf(BuiltIn);
        if (builtIn is not null && !string.Equals(e.Path, builtIn, StringComparison.OrdinalIgnoreCase))
        {
            Problem?.Invoke(e.Owner, $"\"{System.IO.Path.GetFileName(e.Path)}\" couldn't be played, so the built-in alarm sound plays instead.");
            // At the task's volume, its fade carrying on.
            var fallback = e with { Path = builtIn, PlainWav = false };
            _active.Add(fallback);
            Start(fallback);
            return;
        }
        Problem?.Invoke(e.Owner, "No sound could be played; check the sound settings and the volume.");
        Beep();
        StartNewest();
    }

    /// <summary>The last resort: the Windows "exclamation" beep.</summary>
    private static void Beep() => System.Media.SystemSounds.Exclamation.Play();

    /// <summary>Resumes the newest remaining sound that loops. A play-once sound that was
    /// interrupted doesn't come back: it has had its turn.</summary>
    private void StartNewest()
    {
        _active.RemoveAll(e => !e.Loop);
        if (_active.Count > 0) Start(_active[^1]);
        else StopCurrent();
    }
}
