using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace AlarmCalendar;

/// <summary>
/// Automatic backups: a file in the format of an export (S8), written a few seconds after
/// the tasks change, so the import restores from any of them. Each one is kept beside the
/// earlier ones and thinned out with age, so a mistake (a task deleted, here or on a linked
/// device) can't overwrite the backup that still has it. The apps write, list and delete
/// the files; this says what they are called, when a new one is due, and which to keep.
/// </summary>
public static class Backups
{
    private const string Prefix = "alarm-calendar-backup-";
    private const string Extension = ".json";
    private const string TimeFormat = "yyyy-MM-dd-HHmmss";

    /// <summary>How long a backup waits after a change for further changes, so an import, a
    /// sync or a quick series of edits makes one file.</summary>
    public static readonly TimeSpan Settle = TimeSpan.FromSeconds(5);

    /// <summary>The device part of this device's backup names: the start of its sync node ID.
    /// Two devices can share a backup folder (a Documents folder in the same cloud drive),
    /// and each thins out only its own backups.</summary>
    public static string DeviceTag(string nodeId) =>
        new string(nodeId.Where(char.IsAsciiLetterOrDigit).Take(8).ToArray()).ToLowerInvariant() is { Length: > 0 } tag ? tag : "device";

    /// <summary>"alarm-calendar-backup-2026-11-07-075300-de193e1e.json": the name of a backup
    /// taken at local time <paramref name="when"/> by the device <paramref name="device"/>
    /// (a <see cref="DeviceTag"/>). Names sort by time.</summary>
    public static string FileName(DateTime when, string device) =>
        $"{Prefix}{when.ToString(TimeFormat, CultureInfo.InvariantCulture)}-{device}{Extension}";

    /// <summary>When the backup called <paramref name="fileName"/> was taken, in local time,
    /// and by which device; null for any other file.</summary>
    public static (DateTime At, string Device)? Parse(string fileName)
    {
        int timeEnd = Prefix.Length + TimeFormat.Length;
        if (fileName.Length <= timeEnd + 1 + Extension.Length
            || !fileName.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)
            || !fileName.EndsWith(Extension, StringComparison.OrdinalIgnoreCase)
            || fileName[timeEnd] != '-'
            || !DateTime.TryParseExact(fileName[Prefix.Length..timeEnd], TimeFormat, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var at))
            return null;
        var device = fileName[(timeEnd + 1)..^Extension.Length];
        return device.All(char.IsAsciiLetterOrDigit) ? (at, device) : null;
    }

    /// <summary>What a backup has to keep up with: which tasks there are, and each one's
    /// definition (as its version, which every edit, here or elsewhere, changes). Marking
    /// done, snoozing and pausing change neither, so they don't call for a new backup.</summary>
    public static string Fingerprint(IEnumerable<AlarmTask> tasks)
    {
        var text = new StringBuilder();
        foreach (var t in tasks.Where(t => !t.Def.Deleted).OrderBy(t => t.Id))
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
            return file?.Tasks is { } tasks ? Fingerprint(tasks.Where(MergeRules.IsWellFormed)) : null;
        }
        catch (System.Text.Json.JsonException) { return null; }
    }

    /// <summary>The newest of <paramref name="device"/>'s backups among
    /// <paramref name="fileNames"/>, and when it was taken; null if it has none.</summary>
    public static (string Name, DateTime At)? Newest(IEnumerable<string> fileNames, string device) =>
        Of(fileNames, device).FirstOrDefault() is { Name: not null } b ? b : null;

    /// <summary><paramref name="device"/>'s backups among <paramref name="fileNames"/>, newest first.</summary>
    private static List<(string Name, DateTime At)> Of(IEnumerable<string> fileNames, string device) =>
        fileNames
            .Select(name => (Name: name, Parsed: Parse(name)))
            .Where(b => b.Parsed is { } p && string.Equals(p.Device, device, StringComparison.OrdinalIgnoreCase))
            .Select(b => (b.Name, b.Parsed!.Value.At))
            .OrderByDescending(b => b.At)
            .ToList();

    /// <summary>
    /// <paramref name="device"/>'s backups among <paramref name="fileNames"/> to delete at
    /// local time <paramref name="now"/>. Kept: the newest, whatever its age (it is what
    /// the tasks are now); every one from the last day; the newest of each day for 60 days;
    /// and the newest of each month for two years. Other devices' backups, and names that
    /// aren't backups, are never returned.
    /// </summary>
    public static IReadOnlyList<string> ToDelete(IEnumerable<string> fileNames, string device, DateTime now)
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
            bool keep = i == 0
                || age < TimeSpan.FromDays(1)
                || (newestOfDay && age < TimeSpan.FromDays(61))
                || (newestOfMonth && at > now.AddYears(-2));
            if (!keep) delete.Add(backups[i].Name);
        }
        return delete;
    }
}
