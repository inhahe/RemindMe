using System.IO;
using System.Media;
using System.Windows;
using System.Windows.Media;

namespace AlarmCalendar.Desktop;

/// <summary>
/// Resolves sound choices to files and plays them. Only one sound plays at a time (D5):
/// every caller registers under an owner (an alarm window, a preview), the newest owner's
/// sound plays, and when it stops, the newest remaining looping sound resumes.
/// <para>WAV files, which include every Windows sound, play through <see cref="SoundPlayer"/>
/// (the waveOut API), which needs no Media Foundation and so also works on Windows "N"
/// editions. Other formats play through WPF's <see cref="MediaPlayer"/>. A sound that fails
/// to play falls back to <see cref="BuiltIn"/> and raises <see cref="Problem"/>, so an
/// alarm never rings silently.</para>
/// </summary>
public sealed class SoundService
{
    private sealed record Entry(object Owner, string Path, bool Loop);

    public static readonly string MediaDir =
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Media");

    /// <summary>Plays when nothing else is chosen, or a chosen sound can't be played.</summary>
    public static readonly SoundRef BuiltIn = new(SoundSource.System, "Alarm01.wav", "Alarm01");

    private readonly MediaPlayer _media = new() { Volume = 1.0 };
    private readonly List<Entry> _active = [];
    private SoundPlayer? _wav;
    private Entry? _playing;
    /// <summary>Bumped whenever playback stops or changes, so a stale "finished" callback
    /// from an earlier sound is ignored.</summary>
    private int _generation;

    /// <summary>(owner, message): the owner's sound couldn't play, and another plays instead.</summary>
    public event Action<object, string>? Problem;

    public SoundService()
    {
        _media.MediaEnded += (_, _) => OnEnded(_generation);
        _media.MediaFailed += (_, e) => OnFailed(_generation, e.ErrorException);
        _media.MediaOpened += (_, _) => AppHost.Log($"Playing {_playing?.Path} ({(_playing?.Loop == true ? "looping" : "once")})");
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
    public void Play(object owner, string? path, bool loop)
    {
        _active.RemoveAll(e => e.Owner == owner);
        if (path is null)
        {
            if (_playing?.Owner == owner) StartNewest();
            Beep();
            return;
        }
        var entry = new Entry(owner, path, loop);
        _active.Add(entry);
        Start(entry);
    }

    /// <summary>Stops <paramref name="owner"/>'s sound; the newest remaining looping sound resumes.</summary>
    public void Stop(object owner)
    {
        _active.RemoveAll(e => e.Owner == owner);
        if (_playing?.Owner == owner) StartNewest();
    }

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
        if (e.Path.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
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
        _media.Open(new Uri(e.Path));
        _media.Play();
    }

    private void StopCurrent()
    {
        _generation++;
        _playing = null;
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
        var builtIn = PathOf(BuiltIn);
        if (builtIn is not null && !string.Equals(e.Path, builtIn, StringComparison.OrdinalIgnoreCase))
        {
            Problem?.Invoke(e.Owner, $"\"{System.IO.Path.GetFileName(e.Path)}\" couldn't be played, so the built-in alarm sound plays instead.");
            Play(e.Owner, builtIn, e.Loop);
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
