using System.IO;
using System.Windows.Media;

namespace AlarmCalendar.Desktop;

/// <summary>
/// Resolves sound choices to files and plays them. Only one sound plays at a time (D5):
/// every caller registers under an owner (an alarm window, a preview), the newest owner's
/// sound plays, and when it stops, the newest remaining looping sound resumes.
/// </summary>
public sealed class SoundService
{
    private sealed record Entry(object Owner, string Path, bool Loop);

    public static readonly string MediaDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Media");

    /// <summary>Plays when nothing else is chosen, or a chosen file is missing.</summary>
    public static readonly SoundRef BuiltIn = new(SoundSource.System, "Alarm01.wav", "Alarm01");

    private readonly MediaPlayer _player = new() { Volume = 1.0 };
    private readonly List<Entry> _active = [];
    private Entry? _playing;

    public SoundService()
    {
        _player.MediaEnded += (_, _) => OnEnded();
        // Logged once per start (not per loop) so "no sound" reports can be checked.
        _player.MediaOpened += (_, _) => AppHost.Log($"Playing {_playing?.Path} ({(_playing?.Loop == true ? "looping" : "once")})");
        _player.MediaFailed += (_, e) =>
        {
            AppHost.Log($"Could not play {_playing?.Path}: {e.ErrorException?.Message}");
            OnEnded();
        };
    }

    /// <summary>The Windows sounds in <see cref="MediaDir"/>, alarms and rings first.</summary>
    public static IReadOnlyList<SoundRef> SystemSounds()
    {
        try
        {
            return Directory.GetFiles(MediaDir, "*.wav")
                .Select(f => new SoundRef(SoundSource.System, Path.GetFileName(f), Path.GetFileNameWithoutExtension(f)))
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
        var path = s.Source == SoundSource.System ? Path.Combine(MediaDir, s.Value) : s.Value;
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
        if (path is null) { if (_playing?.Owner == owner) StartNewest(); return; }
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

    public bool IsPlaying(object owner) => _playing?.Owner == owner;

    public void StopAll()
    {
        _active.Clear();
        _playing = null;
        _player.Stop();
        _player.Close();
    }

    private void Start(Entry e)
    {
        _playing = e;
        _player.Open(new Uri(e.Path));
        _player.Play();
    }

    private void OnEnded()
    {
        var e = _playing;
        if (e is null) return;
        if (e.Loop && _active.Contains(e))
        {
            _player.Position = TimeSpan.Zero;
            _player.Play();
            return;
        }
        _active.Remove(e);
        StartNewest();
    }

    /// <summary>Resumes the newest remaining sound that loops. A play-once sound that was
    /// interrupted doesn't come back: it has had its turn.</summary>
    private void StartNewest()
    {
        _active.RemoveAll(e => !e.Loop);
        if (_active.Count > 0) Start(_active[^1]);
        else
        {
            _playing = null;
            _player.Stop();
            _player.Close();
        }
    }
}
