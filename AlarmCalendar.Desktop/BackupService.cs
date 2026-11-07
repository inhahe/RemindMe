using System.IO;
using System.Text;
using System.Windows.Threading;

namespace AlarmCalendar.Desktop;

/// <summary>
/// Automatic backups on Windows (see Core's <see cref="Backups"/>): a file in the backup
/// folder once the tasks have changed and things have been quiet for a few seconds, and
/// at startup if this device's newest backup there is out of date. Used from the UI thread.
/// </summary>
public sealed class BackupService
{
    private readonly TaskStore _store;
    private readonly string _device;
    private readonly DispatcherTimer _settle = new() { Interval = Backups.Settle };
    /// <summary>The folder <see cref="_newestFingerprint"/> was read from; null when it is to
    /// be read again.</summary>
    private string? _readFolder;
    private string? _newestFingerprint;
    private string? _loggedProblem;

    /// <summary>This device's newest backup, and when it was taken; null if there is none.</summary>
    public (string Path, DateTime At)? Newest { get; private set; }

    /// <summary>Why the last backup failed; null when it worked.</summary>
    public string? Problem { get; private set; }

    /// <summary>Raised after every backup attempt.</summary>
    public event Action? Changed;

    public BackupService(TaskStore store, string nodeId)
    {
        _store = store;
        _device = Backups.DeviceTag(nodeId);
        _settle.Tick += (_, _) => { _settle.Stop(); BackUpNow(); };
        store.Changed += _ => AppHost.OnUi(Schedule);
        AppHost.SettingsChanged += Schedule;   // turned on, or another folder
    }

    /// <summary>The folder backups go to now.</summary>
    public static string Folder => AppHost.Settings.BackupFolder is { Length: > 0 } f ? f : AppHost.DefaultBackupFolder;

    /// <summary>Backs up once nothing has changed for <see cref="Backups.Settle"/>.</summary>
    public void Schedule()
    {
        _settle.Stop();
        _settle.Start();
    }

    /// <summary>Before exiting: a backup still waiting to settle is written now.</summary>
    public void Flush()
    {
        if (!_settle.IsEnabled) return;
        _settle.Stop();
        BackUpNow();
    }

    /// <summary>Writes a backup unless this device's newest one already has these tasks,
    /// then deletes the backups that <see cref="Backups.ToDelete"/> names.</summary>
    private void BackUpNow()
    {
        if (!AppHost.Settings.AutoBackup) return;
        var folder = Folder;
        try
        {
            Directory.CreateDirectory(folder);
            if (_readFolder != folder) ReadNewest(folder);
            var json = _store.Export(out int count);
            var fingerprint = Backups.FingerprintOfFile(json);
            if (fingerprint != _newestFingerprint)
            {
                var now = DateTime.Now;
                var path = Path.Combine(folder, Backups.FileName(now, _device));
                var tmp = path + ".tmp";
                File.WriteAllText(tmp, json, new UTF8Encoding(false));
                File.Move(tmp, path, overwrite: true);
                _newestFingerprint = fingerprint;
                Newest = (path, now);
                AppHost.Log($"Backed up {count} task{(count == 1 ? "" : "s")} to {path}.");
            }
            Prune(folder);
            Problem = null;
            _loggedProblem = null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Problem = $"The last backup failed: {e.Message}";
            if (Problem != _loggedProblem) AppHost.Log($"Couldn't back up to {folder}: {e.Message}");
            _loggedProblem = Problem;
            _readFolder = null;   // the folder may have changed under us: read it afresh
        }
        Changed?.Invoke();
    }

    private void ReadNewest(string folder)
    {
        Newest = null;
        _newestFingerprint = null;
        if (Backups.Newest(Directory.EnumerateFiles(folder).Select(p => Path.GetFileName(p)), _device) is { } newest)
        {
            var path = Path.Combine(folder, newest.Name);
            Newest = (path, newest.At);
            _newestFingerprint = Backups.FingerprintOfFile(File.ReadAllText(path));
        }
        _readFolder = folder;
    }

    private void Prune(string folder)
    {
        foreach (var name in Backups.ToDelete(Directory.EnumerateFiles(folder).Select(p => Path.GetFileName(p)), _device, DateTime.Now))
        {
            try { File.Delete(Path.Combine(folder, name)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                AppHost.Log($"Couldn't delete the old backup {name}: {e.Message}");
            }
        }
    }
}
