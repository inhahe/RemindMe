using Android.Content;
using Android.OS;
using Android.Provider;
using System.Text;

namespace AlarmCalendar.Droid;

/// <summary>
/// Automatic backups on Android (see Core's <see cref="Backups"/>): a file in
/// Download/Alarm Calendar backups once the tasks have changed and things have been quiet
/// for a few seconds, and at startup if this device's newest backup is out of date.
/// <para>Written through MediaStore, which needs no permission, puts the files where the
/// Files app and the import's file picker show them, and keeps them when the app is
/// uninstalled. An app sees only the files it wrote there itself, so after a reinstall the
/// older backups stay as they are, for the user to import or delete; the new installation
/// starts a series of its own.</para>
/// <para>Shared storage opens only once the phone has been unlocked since it restarted, so
/// until then there are no backups (and nothing new to back up: sync waits for the unlock
/// too).</para>
/// </summary>
public static class AutoBackup
{
    /// <summary>The folder, as MediaStore's relative path.</summary>
    public const string Folder = "Download/Alarm Calendar backups/";

    private static readonly Handler Main = new(Looper.MainLooper!);
    private static readonly Java.Lang.Runnable Pending = new(() => Task.Run(BackUpNow));
    private static readonly object Lock = new();
    /// <summary>Whether <see cref="_newestFingerprint"/> has been read from the folder.</summary>
    private static bool _read;
    private static string? _newestFingerprint;
    private static string? _loggedProblem;

    /// <summary>When this device's newest backup was taken; null if there is none.</summary>
    public static DateTime? NewestAt { get; private set; }

    /// <summary>Why the last backup failed; null when it worked.</summary>
    public static string? Problem { get; private set; }

    /// <summary>Raised on the main thread after every backup attempt.</summary>
    public static event Action? Changed;

    /// <summary>Backs up once nothing has changed for <see cref="Backups.Settle"/>. Main thread.</summary>
    public static void Schedule()
    {
        Main.RemoveCallbacks(Pending);
        Main.PostDelayed(Pending, (long)Backups.Settle.TotalMilliseconds);
    }

    /// <summary>Writes a backup unless this device's newest one already has these tasks,
    /// then deletes the backups that <see cref="Backups.ToDelete"/> names. Off the main
    /// thread: MediaStore calls are calls to another process.</summary>
    private static void BackUpNow()
    {
        lock (Lock)
        {
            var settings = AppState.Settings;
            if (AppState.Provisional || !settings.AutoBackup || !IsUnlocked()) return;
            var resolver = AppState.Context.ContentResolver!;
            var device = Backups.DeviceTag(settings.NodeId);
            try
            {
                var files = List(resolver);
                if (!_read) ReadNewest(resolver, files, device);
                var json = AppState.Store.Export(out int count);
                var fingerprint = Backups.FingerprintOfFile(json);
                if (fingerprint != _newestFingerprint)
                {
                    var now = DateTime.Now;
                    var name = Backups.FileName(now, device);
                    Write(resolver, name, json);
                    _newestFingerprint = fingerprint;
                    NewestAt = now;
                    AppState.Log($"Backed up {count} task{(count == 1 ? "" : "s")} to {Folder}{name}.");
                    files = List(resolver);
                }
                foreach (var old in Backups.ToDelete(files.Keys, device, DateTime.Now))
                    resolver.Delete(files[old], null, null);
                Problem = null;
                _loggedProblem = null;
            }
            catch (Exception e) when (e is Java.Lang.Exception or IOException or UnauthorizedAccessException)
            {
                Problem = $"The last backup failed: {e.Message}";
                if (Problem != _loggedProblem) AppState.Log($"Couldn't back up to {Folder}: {e.Message}");
                _loggedProblem = Problem;
                _read = false;   // read the folder afresh next time
            }
        }
        Main.Post(() => Changed?.Invoke());
    }

    private static bool IsUnlocked() =>
        ((UserManager?)AppState.Context.GetSystemService(Context.UserService))?.IsUserUnlocked == true;

    /// <summary>The files this installation wrote to <see cref="Folder"/>, by name.</summary>
    private static Dictionary<string, Android.Net.Uri> List(ContentResolver resolver)
    {
        var collection = MediaStore.Downloads.ExternalContentUri!;
        var files = new Dictionary<string, Android.Net.Uri>(StringComparer.OrdinalIgnoreCase);
        using var cursor = resolver.Query(collection, ["_id", "_display_name"], "relative_path = ?", [Folder], null);
        while (cursor?.MoveToNext() == true)
            if (cursor.GetString(1) is { } name)
                files[name] = ContentUris.WithAppendedId(collection, cursor.GetLong(0));
        return files;
    }

    private static void ReadNewest(ContentResolver resolver, Dictionary<string, Android.Net.Uri> files, string device)
    {
        NewestAt = null;
        _newestFingerprint = null;
        if (Backups.Newest(files.Keys, device) is { } newest)
        {
            NewestAt = newest.At;
            using var stream = resolver.OpenInputStream(files[newest.Name]) ?? throw new IOException("The newest backup couldn't be opened.");
            using var reader = new StreamReader(stream, Encoding.UTF8);
            _newestFingerprint = Backups.FingerprintOfFile(reader.ReadToEnd());
        }
        _read = true;
    }

    /// <summary>Writes a file to <see cref="Folder"/>, kept hidden from other apps
    /// (IS_PENDING) until it is complete.</summary>
    private static void Write(ContentResolver resolver, string name, string json)
    {
        var values = new ContentValues();
        values.Put("_display_name", name);
        values.Put("mime_type", "application/json");
        values.Put("relative_path", Folder);
        values.Put("is_pending", 1);
        var uri = resolver.Insert(MediaStore.Downloads.ExternalContentUri!, values) ?? throw new IOException("MediaStore made no file.");
        try
        {
            using (var stream = resolver.OpenOutputStream(uri, "w") ?? throw new IOException("The new backup couldn't be opened."))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                writer.Write(json);
            var done = new ContentValues();
            done.Put("is_pending", 0);
            resolver.Update(uri, done, null, null);
        }
        catch
        {
            resolver.Delete(uri, null, null);
            throw;
        }
    }
}
