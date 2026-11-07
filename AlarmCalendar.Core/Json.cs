using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using AlarmCalendar.Sync;

namespace AlarmCalendar;

/// <summary>Enum values as camel-case strings ("fromDone"), the format of every file and message.</summary>
internal sealed class CamelCaseEnum<T>() : JsonStringEnumConverter<T>(JsonNamingPolicy.CamelCase) where T : struct, Enum;

/// <summary>
/// Source-generated serialization for everything Core reads and writes. No reflection, so
/// the Android app can trim safely (reflection-based System.Text.Json is unsafe to trim).
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    Converters = [typeof(CamelCaseEnum<ScheduleKind>), typeof(CamelCaseEnum<IntervalUnit>), typeof(CamelCaseEnum<RestartMode>),
        typeof(CamelCaseEnum<SoundMode>), typeof(CamelCaseEnum<SoundSource>), typeof(CamelCaseEnum<DayOfWeek>)])]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(StoreFile))]
[JsonSerializable(typeof(SyncMessage))]
[JsonSerializable(typeof(string[]))]
internal sealed partial class CoreJson : JsonSerializerContext;

public static class Json
{
    /// <summary>Indented, for the files people may open.</summary>
    internal static CoreJson Files => CoreJson.Default;

    /// <summary>Compact, for the wire.</summary>
    internal static readonly CoreJson Wire = new(new JsonSerializerOptions
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters =
        {
            new CamelCaseEnum<ScheduleKind>(), new CamelCaseEnum<IntervalUnit>(), new CamelCaseEnum<RestartMode>(),
            new CamelCaseEnum<SoundMode>(), new CamelCaseEnum<SoundSource>(), new CamelCaseEnum<DayOfWeek>(),
        },
    });

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
    internal static T? ReadWithFallback<T>(string path, JsonTypeInfo<T> type, Action<string>? log = null) where T : class
    {
        foreach (var candidate in new[] { path, path + ".bak" })
        {
            if (!File.Exists(candidate)) continue;
            try
            {
                var value = JsonSerializer.Deserialize(ReadShared(candidate), type);
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
