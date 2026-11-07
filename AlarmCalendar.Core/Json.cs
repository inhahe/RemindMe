using System.Text.Json;
using System.Text.Json.Serialization;

namespace AlarmCalendar;

public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>Compact form for the wire.</summary>
    public static readonly JsonSerializerOptions Wire = new(Options) { WriteIndented = false };

    /// <summary>
    /// Writes <paramref name="contents"/> to <paramref name="path"/> so that a crash
    /// leaves either the old file or the new one, never half of one, and keeps the
    /// previous version as <c>.bak</c>.
    /// </summary>
    public static void WriteAtomically(string path, string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var w = new StreamWriter(fs, new System.Text.UTF8Encoding(false)))
        {
            w.Write(contents);
            w.Flush();
            fs.Flush(flushToDisk: true);
        }
        if (File.Exists(path)) File.Replace(tmp, path, path + ".bak", ignoreMetadataErrors: true);
        else File.Move(tmp, path);
    }

    /// <summary>
    /// Reads and deserializes <paramref name="path"/>, falling back to its <c>.bak</c>.
    /// A file that won't parse is renamed aside (<c>.corrupt-&lt;time&gt;</c>), never
    /// overwritten, so the user's data can still be recovered by hand.
    /// </summary>
    /// <returns>The value, or null when neither file exists or parses.</returns>
    /// <exception cref="IOException">A file exists but stays unreadable (locked by another
    /// program, or access denied). Starting empty instead would let the next save overwrite
    /// the user's data, so the caller must not carry on as if there were none.</exception>
    public static T? ReadWithFallback<T>(string path, Action<string>? log = null) where T : class
    {
        foreach (var candidate in new[] { path, path + ".bak" })
        {
            if (!File.Exists(candidate)) continue;
            try
            {
                var value = JsonSerializer.Deserialize<T>(ReadShared(candidate), Options);
                if (value is not null)
                {
                    if (candidate != path) log?.Invoke($"{Path.GetFileName(path)} was unreadable; restored from its backup.");
                    return value;
                }
            }
            catch (Exception e) when (e is JsonException or NotSupportedException)
            {
                var aside = $"{candidate}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}";
                try { File.Move(candidate, aside); } catch (IOException) { }
                log?.Invoke($"{Path.GetFileName(candidate)} could not be read ({e.Message}); kept as {Path.GetFileName(aside)}.");
            }
        }
        return null;
    }

    /// <summary>Reads a file, retrying for a couple of seconds while another program (an
    /// antivirus scan, a backup) holds it open.</summary>
    private static string ReadShared(string path)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var r = new StreamReader(fs);
                return r.ReadToEnd();
            }
            catch (Exception e) when ((e is IOException or UnauthorizedAccessException) && e is not FileNotFoundException && attempt < 10)
            {
                Thread.Sleep(250);
            }
            catch (UnauthorizedAccessException e)
            {
                throw new IOException($"{path} can't be read: {e.Message}", e);
            }
        }
    }
}
