using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace AlarmCalendar;

/// <summary>
/// Automatic backups (S11): a file in the format of an export (S8), written a few seconds
/// after the tasks change, so the import restores from any of them. Each one is kept beside
/// the earlier ones and thinned out with age, so a mistake (a task deleted, here or on a
/// linked device) can't overwrite the backup that still has it. This says what the files
/// are called, what calls for a new one, and which to keep; <see cref="BackupRunner"/> acts
/// on it.
/// </summary>
public static class Backups
{
    private const string Prefix = "alarm-calendar-backup-";
    private const string Extension = ".json";
    private const string TimeFormat = "yyyy-MM-dd-HHmmss";

    /// <summary>How long a backup waits after a change for further changes, so an import, a
    /// sync or a quick series of edits makes one file.</summary>
    public static readonly TimeSpan Settle = TimeSpan.FromSeconds(5);

    /// <summary>How many of a device's newest backups are kept whatever their age. Ages come
    /// from the clock, and a clock set days ahead (to try an alarm, say) would otherwise
    /// make the day's backups look old enough to thin out.</summary>
    public const int AlwaysKept = 10;

    /// <summary>The device part of this device's backup names: the start of its sync node ID.
    /// Two devices can share a backup folder (a Documents folder in the same cloud drive),
    /// and each thins out only its own backups.</summary>
    public static string DeviceTag(string nodeId) =>
        new string(nodeId.Where(char.IsAsciiLetterOrDigit).Take(8).ToArray()).ToLowerInvariant() is { Length: > 0 } tag ? tag : "device";

    /// <summary>"alarm-calendar-backup-2026-11-07-075300-0500-de193e1e.json": the name of a
    /// backup taken at <paramref name="when"/> by the device <paramref name="device"/>. The
    /// time is the local one, which people recognise, with its offset from UTC, so backups
    /// taken across a change of clock time or time zone still order correctly.</summary>
    /// <exception cref="ArgumentException"><paramref name="device"/> isn't a
    /// <see cref="DeviceTag"/>: <see cref="Parse"/> would never recognise the name.</exception>
    public static string FileName(DateTimeOffset when, string device)
    {
        if (device.Length == 0 || !device.All(char.IsAsciiLetterOrDigit))
            throw new ArgumentException($"\"{device}\" isn't a device tag.", nameof(device));
        var offset = when.Offset;
        char sign = offset < TimeSpan.Zero ? '-' : '+';
        offset = offset.Duration();
        return $"{Prefix}{when.ToString(TimeFormat, CultureInfo.InvariantCulture)}{sign}{offset.Hours:00}{offset.Minutes:00}-{device}{Extension}";
    }

    /// <summary>When the backup called <paramref name="fileName"/> was taken, and by which
    /// device; null for any other file.</summary>
    public static (DateTimeOffset At, string Device)? Parse(string fileName)
    {
        int timeEnd = Prefix.Length + TimeFormat.Length, offsetEnd = timeEnd + 5;
        if (fileName.Length <= offsetEnd + 1 + Extension.Length
            || !fileName.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)
            || !fileName.EndsWith(Extension, StringComparison.OrdinalIgnoreCase)
            || fileName[timeEnd] is not ('+' or '-')
            || fileName[offsetEnd] != '-'
            || !DateTime.TryParseExact(fileName[Prefix.Length..timeEnd], TimeFormat, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var local)
            || !int.TryParse(fileName.AsSpan(timeEnd + 1, 2), NumberStyles.None, CultureInfo.InvariantCulture, out int hours)
            || !int.TryParse(fileName.AsSpan(timeEnd + 3, 2), NumberStyles.None, CultureInfo.InvariantCulture, out int minutes)
            || hours > 14 || minutes > 59)
            return null;
        var device = fileName[(offsetEnd + 1)..^Extension.Length];
        if (!device.All(char.IsAsciiLetterOrDigit)) return null;
        var offset = new TimeSpan(hours, minutes, 0) * (fileName[timeEnd] == '-' ? -1 : 1);
        return (new DateTimeOffset(local, offset), device);
    }

    /// <summary>What a backup has to keep up with: which tasks there are, and each one's
    /// definition (as its version, which every edit, here or elsewhere, changes). Marking
    /// done, snoozing and pausing change neither, so they don't call for a new backup.</summary>
    /// <remarks>Only well-formed records count, as in a file, where an export of them would
    /// leave the rest out: the store and its backup must agree whatever the store holds.</remarks>
    public static string Fingerprint(IEnumerable<AlarmTask> tasks)
    {
        var text = new StringBuilder();
        foreach (var t in tasks.Where(t => MergeRules.IsWellFormed(t) && !t.Def.Deleted).OrderBy(t => t.Id))
            text.Append(t.Id).Append(' ').Append(t.DefVersion.ToString()).Append('\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    /// <summary>The <see cref="Fingerprint"/> of the tasks in an export or backup file;
    /// null when <paramref name="json"/> isn't one.</summary>
    public static string? FingerprintOfFile(string json)
    {
        try
        {
            var file = System.Text.Json.JsonSerializer.Deserialize(json, Json.Files.ExportFile);
            return file?.Tasks is { } tasks ? Fingerprint(tasks) : null;
        }
        catch (System.Text.Json.JsonException) { return null; }
    }

    /// <summary>The newest of <paramref name="device"/>'s backups among
    /// <paramref name="fileNames"/>, and when it was taken; null if it has none. One dated
    /// more than a minute after <paramref name="now"/> was written while the clock was ahead,
    /// and doesn't count: it would stay "newest" until the clock caught up with it.</summary>
    public static (string Name, DateTimeOffset At)? Newest(IEnumerable<string> fileNames, string device, DateTimeOffset now) =>
        Of(fileNames, device).FirstOrDefault(b => b.At <= now.AddMinutes(1)) is { Name: not null } b ? b : null;

    /// <summary><paramref name="device"/>'s backups among <paramref name="fileNames"/>, newest first.</summary>
    private static List<(string Name, DateTimeOffset At)> Of(IEnumerable<string> fileNames, string device) =>
        fileNames
            .Select(name => (Name: name, Parsed: Parse(name)))
            .Where(b => b.Parsed is { } p && string.Equals(p.Device, device, StringComparison.OrdinalIgnoreCase))
            .Select(b => (b.Name, b.Parsed!.Value.At))
            .OrderByDescending(b => b.At)
            .ToList();

    /// <summary>
    /// <paramref name="device"/>'s backups among <paramref name="fileNames"/> to delete at
    /// <paramref name="now"/>. Kept: the <see cref="AlwaysKept"/> newest, whatever their age;
    /// every one from the last day; the newest of each day for 60 days; and the newest of
    /// each month for two years (days and months as the backup's own local time had them).
    /// Other devices' backups, and names that aren't backups, are never returned.
    /// </summary>
    public static IReadOnlyList<string> ToDelete(IEnumerable<string> fileNames, string device, DateTimeOffset now)
    {
        var backups = Of(fileNames, device);
        var days = new HashSet<DateTime>();
        var months = new HashSet<(int, int)>();
        var delete = new List<string>();
        for (int i = 0; i < backups.Count; i++)   // newest first, so each day's and month's first is its newest
        {
            var at = backups[i].At;
            bool newestOfDay = days.Add(at.Date), newestOfMonth = months.Add((at.Year, at.Month));
            var age = now - at;
            bool keep = i < AlwaysKept
                || age < TimeSpan.FromDays(1)
                || (newestOfDay && age < TimeSpan.FromDays(61))
                || (newestOfMonth && at > now.AddYears(-2));
            if (!keep) delete.Add(backups[i].Name);
        }
        return delete;
    }
}

/// <summary>Where backups are kept, as an app provides it: a folder on Windows, MediaStore's
/// Downloads on Android. Every member throws <see cref="IOException"/> or
/// <see cref="UnauthorizedAccessException"/> when the storage fails.</summary>
public interface IBackupFolder
{
    /// <summary>What to call the folder in messages; also tells one folder from another.</summary>
    string Name { get; }

    /// <summary>The names of the files in it (none if it doesn't exist yet).</summary>
    IEnumerable<string> List();

    string Read(string fileName);

    /// <summary>Creates the file, completely or not at all.</summary>
    void Write(string fileName, string contents);

    void Delete(string fileName);
}

/// <summary>
/// One backup attempt at a time, for either app, which only says when to attempt one and
/// where (<see cref="IBackupFolder"/>). An attempt lists the folder once, writes a backup
/// unless this device's newest one already has the tasks as they are, and thins out the
/// old ones after writing (and once per folder). Thread-safe; attempts may run on any thread.
/// </summary>
public sealed class BackupRunner
{
    private readonly object _lock = new();
    private readonly TaskStore _store;
    private readonly string _device;
    private readonly Action<string> _log;
    /// <summary>The folder the fields below describe; null when it is to be read afresh.</summary>
    private string? _folder;
    private string? _newestName;
    private string? _newestFingerprint;
    private bool _pruneDue;
    private string? _loggedProblem;
    private readonly HashSet<string> _loggedDeletes = [];

    /// <summary>When this device's newest backup was taken; null if there is none, or the
    /// folder hasn't been read yet.</summary>
    public DateTimeOffset? NewestAt { get; private set; }

    /// <summary>Why the last attempt failed; null when it worked.</summary>
    public string? Problem { get; private set; }

    /// <param name="nodeId">This device's sync node ID, which names its backups.</param>
    /// <param name="log">Writes a line to the app's log.</param>
    public BackupRunner(TaskStore store, string nodeId, Action<string> log)
    {
        _store = store;
        _device = Backups.DeviceTag(nodeId);
        _log = log;
    }

    /// <summary>One attempt, against <paramref name="folder"/>. Failures end up in
    /// <see cref="Problem"/> (and the log, once per message), never thrown: a backup must
    /// neither take the app down nor stop without a word.</summary>
    public void Run(IBackupFolder folder)
    {
        lock (_lock)
        {
            var clock = _store.Clock;
            var now = TimeZoneInfo.ConvertTime(clock.UtcNow, clock.Zone);
            try
            {
                var names = folder.List().ToList();
                // Read the folder when it is new here, and again when the newest backup has
                // gone (deleted by hand, or by a cloud drive freeing space): what is there now
                // decides whether to write.
                if (_folder != folder.Name || (_newestName is { } known && !names.Contains(known, StringComparer.OrdinalIgnoreCase)))
                    ReadNewest(folder, names, now);
                if (Backups.Fingerprint(_store.Live()) != _newestFingerprint)
                {
                    var json = _store.Export(out int count);
                    var (name, at) = FreeName(names, now);
                    folder.Write(name, json);
                    names.Add(name);
                    _newestName = name;
                    _newestFingerprint = Backups.FingerprintOfFile(json);
                    NewestAt = at;
                    _pruneDue = true;
                    _log($"Backed up {count} task{(count == 1 ? "" : "s")} to {folder.Name}: {name}.");
                }
                if (_pruneDue) Prune(folder, names, now);
                Problem = null;
                _loggedProblem = null;
            }
            catch (Exception e)
            {
                bool storage = e is IOException or UnauthorizedAccessException;
                Problem = $"The last backup failed: {e.Message}";
                if (Problem != _loggedProblem)
                    _log(storage ? $"Couldn't back up to {folder.Name}: {e.Message}" : $"Backing up to {folder.Name} failed: {e}");
                _loggedProblem = Problem;
                _folder = null;   // read it afresh next time
            }
        }
    }

    private void ReadNewest(IBackupFolder folder, IReadOnlyList<string> names, DateTimeOffset now)
    {
        _newestName = null;
        _newestFingerprint = null;
        NewestAt = null;
        if (Backups.Newest(names, _device, now) is { } newest)
        {
            _newestName = newest.Name;
            NewestAt = newest.At;
            try { _newestFingerprint = Backups.FingerprintOfFile(folder.Read(newest.Name)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Reading it only saves writing a duplicate: an unreadable one (a cloud file
                // left online-only, say) mustn't stop backups. A fresh one is written.
                _log($"Couldn't read the newest backup, {newest.Name} ({e.Message}); writing a new one.");
            }
        }
        _folder = folder.Name;
        _pruneDue = true;
    }

    /// <summary>The name for a backup taken at <paramref name="now"/>, and the time it
    /// carries. Names go to the second, so a second backup within one second (an edit
    /// just before exit, say) moves on a second: the same name would replace the first
    /// backup on Windows, and MediaStore would rename it beyond <see cref="Backups.Parse"/>.</summary>
    private (string Name, DateTimeOffset At) FreeName(List<string> names, DateTimeOffset now)
    {
        var at = now.AddTicks(-(now.Ticks % TimeSpan.TicksPerSecond));
        if (NewestAt is { } last && at <= last) at = last.AddSeconds(1).ToOffset(now.Offset);
        string name;
        while (names.Contains(name = Backups.FileName(at, _device), StringComparer.OrdinalIgnoreCase)) at = at.AddSeconds(1);
        return (name, at);
    }

    private void Prune(IBackupFolder folder, IReadOnlyList<string> names, DateTimeOffset now)
    {
        foreach (var name in Backups.ToDelete(names, _device, now))
        {
            try { folder.Delete(name); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                if (_loggedDeletes.Add(name)) _log($"Couldn't delete the old backup {name}: {e.Message}");
            }
        }
        _pruneDue = false;
    }
}
