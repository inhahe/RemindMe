using Android.Content;
using Android.Provider;
using System.Text;

namespace AlarmCalendar.Droid;

/// <summary>
/// Automatic backups on Android (see Core's <see cref="Backups"/> and
/// <see cref="BackupRunner"/>): an attempt once the tasks have changed and nothing more has
/// changed for a few seconds, and one each time the data is loaded, in
/// Download/Alarm Calendar backups.
/// <para>Written through MediaStore, which needs no permission, puts the files where the
/// Files app and the import's file picker show them, and keeps them when the app is
/// uninstalled. An app sees only the files it wrote there itself, so after a reinstall the
/// older backups stay as they are, for the user to import or delete; the new installation
/// starts a series of its own.</para>
/// <para>Shared storage opens only once the phone has been unlocked since it restarted.
/// The tasks can change before that (they live in device storage, and sync runs), so an
/// attempt while locked waits for the unlock.</para>
/// </summary>
public static class AutoBackup
{
    /// <summary>The folder, as MediaStore's relative path.</summary>
    public const string Folder = "Download/Alarm Calendar backups/";

    private static readonly Java.Lang.Runnable Pending = new(() => Task.Run(Attempt));
    private static BackupRunner? _runner;

    /// <summary>When this device's newest backup was taken, when known.</summary>
    public static DateTimeOffset? NewestAt => _runner?.NewestAt;

    /// <summary>Why the last attempt failed; null when it worked.</summary>
    public static string? Problem => _runner?.Problem;

    /// <summary>Raised on the main thread after every attempt.</summary>
    public static event Action? Changed;

    /// <summary>Backs up <paramref name="store"/> from now on: the data was (re)loaded.</summary>
    public static void Attach(TaskStore store, string nodeId) =>
        _runner = new BackupRunner(store, nodeId, AppState.Log);

    /// <summary>Attempts a backup once nothing has changed for <see cref="Backups.Settle"/>.</summary>
    public static void Schedule()
    {
        AppState.Main.RemoveCallbacks(Pending);
        AppState.Main.PostDelayed(Pending, (long)Backups.Settle.TotalMilliseconds);
    }

    /// <summary>Off the main thread: MediaStore calls are calls to another process. While
    /// the phone is locked, it attempts again once unlocked; if the process dies first, the
    /// next one attempts when it loads.</summary>
    private static void Attempt()
    {
        if (_runner is not { } runner || AppState.Provisional || !AppState.Settings.AutoBackup) return;
        var context = AppState.Context;
        if (!AppState.IsUnlocked(context))
        {
            AppState.WhenUnlocked(Schedule);
            return;
        }
        runner.Run(new MediaStoreFolder(context.ContentResolver!));
        AppState.OnMain(() => Changed?.Invoke());
    }

    /// <summary><see cref="Folder"/> through MediaStore, holding the files this installation
    /// wrote there. Java exceptions become <see cref="IOException"/>s, as
    /// <see cref="IBackupFolder"/> asks. One per attempt: reads and deletes use the listing
    /// the attempt began with, rather than querying the media provider again for each.</summary>
    private sealed class MediaStoreFolder(ContentResolver resolver) : IBackupFolder
    {
        private static Android.Net.Uri Collection => MediaStore.Downloads.ExternalContentUri!;
        private Dictionary<string, Android.Net.Uri>? _listed;

        public string Name => Folder;

        public IEnumerable<string> List() => Io(() => (_listed = Files()).Keys.ToList());

        private Android.Net.Uri? UriOf(string fileName) => (_listed ?? (_listed = Files())).GetValueOrDefault(fileName);

        public string Read(string fileName) => Io(() =>
        {
            var uri = UriOf(fileName) ?? throw new IOException($"{fileName} is gone.");
            using var stream = resolver.OpenInputStream(uri) ?? throw new IOException($"{fileName} couldn't be opened.");
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadToEnd();
        });

        /// <summary>Kept hidden from other apps (IS_PENDING) until it is complete, and
        /// removed if it can't be completed.</summary>
        public void Write(string fileName, string contents) => Io(() =>
        {
            var values = new ContentValues();
            values.Put("_display_name", fileName);
            values.Put("mime_type", "application/json");
            values.Put("relative_path", Folder);
            values.Put("is_pending", 1);
            var uri = resolver.Insert(Collection, values) ?? throw new IOException("MediaStore made no file.");
            try
            {
                using (var stream = resolver.OpenOutputStream(uri, "w") ?? throw new IOException("The new backup couldn't be opened."))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                    writer.Write(contents);
                var done = new ContentValues();
                done.Put("is_pending", 0);
                resolver.Update(uri, done, null, null);
            }
            catch
            {
                resolver.Delete(uri, null, null);
                throw;
            }
            return 0;
        });

        public void Delete(string fileName) => Io(() =>
        {
            if (UriOf(fileName) is { } uri) resolver.Delete(uri, null, null);
            return 0;
        });

        /// <summary>The files this installation wrote to <see cref="Folder"/>, by name.</summary>
        private Dictionary<string, Android.Net.Uri> Files()
        {
            var files = new Dictionary<string, Android.Net.Uri>(StringComparer.OrdinalIgnoreCase);
            using var cursor = resolver.Query(Collection, ["_id", "_display_name"], "relative_path = ?", [Folder], null);
            while (cursor?.MoveToNext() == true)
                if (cursor.GetString(1) is { } name)
                    files[name] = ContentUris.WithAppendedId(Collection, cursor.GetLong(0));
            return files;
        }

        private static T Io<T>(Func<T> action)
        {
            try { return action(); }
            catch (Java.Lang.Exception e) { throw new IOException(e.Message, e); }
        }
    }
}
