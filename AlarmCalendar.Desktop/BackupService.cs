using System.IO;
using System.Windows.Threading;

namespace AlarmCalendar.Desktop;

/// <summary>
/// Automatic backups on Windows (see Core's <see cref="Backups"/> and
/// <see cref="BackupRunner"/>): an attempt once the tasks have changed and nothing more has
/// changed for a few seconds, and one at startup. The file work runs on the thread pool: the
/// folder may be a network share or a cloud drive, and the UI thread runs the alarms.
/// </summary>
public sealed class BackupService
{
    private readonly BackupRunner _runner;
    private readonly DispatcherTimer _settle = new() { Interval = Backups.Settle };
    private Task _last = Task.CompletedTask;

    /// <summary>Raised on the UI thread after every attempt.</summary>
    public event Action? Changed;

    public BackupService(TaskStore store, string nodeId)
    {
        _runner = new BackupRunner(store, nodeId, AppHost.Log);
        _settle.Tick += (_, _) => { _settle.Stop(); Start(); };
        store.Changed += _ => AppHost.OnUi(Schedule);
        AppHost.SettingsChanged += Schedule;   // turned on, or another folder
    }

    /// <summary>This device's newest backup's time, when known.</summary>
    public DateTimeOffset? NewestAt => _runner.NewestAt;

    /// <summary>Why the last attempt failed; null when it worked.</summary>
    public string? Problem => _runner.Problem;

    /// <summary>Attempts a backup once nothing has changed for <see cref="Backups.Settle"/>.
    /// UI thread.</summary>
    public void Schedule()
    {
        _settle.Stop();
        _settle.Start();
    }

    /// <summary>Before exiting: an attempt still waiting to settle runs now, and the app
    /// waits for it a few seconds at most, so a folder that hangs can't hold up the exit
    /// (or Windows shutting down).</summary>
    public void Flush()
    {
        if (_settle.IsEnabled)
        {
            _settle.Stop();
            Start();
        }
        try { _last.Wait(TimeSpan.FromSeconds(5)); }
        catch (AggregateException) { }
    }

    private void Start()
    {
        if (!AppHost.Settings.AutoBackup)
        {
            Changed?.Invoke();
            return;
        }
        var folder = new DiskFolder(AppHost.BackupFolderOf(AppHost.Settings.BackupFolder));
        _last = Task.Run(() =>
        {
            _runner.Run(folder);
            AppHost.OnUi(() => Changed?.Invoke());
        });
    }

    /// <summary>A folder on disk, as <see cref="IBackupFolder"/> wants it: a path Windows
    /// rejects is a storage failure like any other.</summary>
    private sealed class DiskFolder(string path) : IBackupFolder
    {
        public string Name => path;

        public IEnumerable<string> List() => Io(() =>
            Directory.Exists(path) ? Directory.EnumerateFiles(path).Select(p => Path.GetFileName(p)).ToList() : []);

        public string Read(string fileName) => Io(() => File.ReadAllText(Path.Combine(path, fileName)));

        public void Write(string fileName, string contents) => Io(() =>
        {
            Json.WriteAtomically(Path.Combine(path, fileName), contents);
            return 0;
        });

        public void Delete(string fileName) => Io(() =>
        {
            File.Delete(Path.Combine(path, fileName));
            return 0;
        });

        private T Io<T>(Func<T> action)
        {
            try { return action(); }
            catch (Exception e) when (e is ArgumentException or NotSupportedException)
            {
                throw new IOException($"\"{path}\" isn't a usable folder: {e.Message}", e);
            }
        }
    }
}
