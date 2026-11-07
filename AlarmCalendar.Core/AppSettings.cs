using System.Security.Cryptography;
using System.Text.Json;

namespace AlarmCalendar;

/// <summary>Per-device settings. Not synced: each device keeps its own defaults, and
/// its own sync identity.</summary>
public sealed class AppSettings
{
    // ---- Defaults for tasks ----

    /// <summary>The sound of every task left on "default" (R11); null means the
    /// platform's built-in alarm sound.</summary>
    public SoundRef? DefaultSound { get; set; }
    public SoundMode DefaultSoundMode { get; set; } = SoundMode.Repeat;
    public int DefaultReminderMinutes { get; set; } = 10;
    /// <summary>The time a new Weekly task starts with (R6).</summary>
    public TimeOnly DefaultTimeOfDay { get; set; } = new(9, 0);
    public RestartMode DefaultRestartMode { get; set; } = RestartMode.FromDone;

    // ---- Desktop ----

    public bool StartWithWindows { get; set; } = true;
    /// <summary>The window's close button hides it to the tray rather than exiting, so
    /// alarms keep ringing.</summary>
    public bool CloseToTray { get; set; } = true;
    /// <summary>Set once the first-run autostart choice has been applied, so a later
    /// manual change sticks.</summary>
    public bool AutostartInitialized { get; set; }
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }

    // ---- Sync ----

    /// <summary>This device's node ID, created on first run.</summary>
    public string NodeId { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public bool SyncEnabled { get; set; } = true;
    public int Port { get; set; } = 37660;
    /// <summary>Accept inbound sync connections from outside the local network.</summary>
    public bool AcceptExternalConnections { get; set; }
    /// <summary>host or host:port entries to keep a link to over the internet.</summary>
    public List<string> RemoteAddresses { get; set; } = [];
    /// <summary>Public ID of this device's sync group; peers with the same ID try to sync.</summary>
    public string GroupId { get; set; } = "";
    /// <summary>The group's secret, base64. Never sent in the clear.</summary>
    public string GroupKey { get; set; } = "";

    /// <summary>Loads the settings, creating the node ID and a one-device sync group on
    /// first run.</summary>
    public static AppSettings Load(string path, string defaultDeviceName, Action<string>? log = null)
    {
        var s = Json.ReadWithFallback<AppSettings>(path, log) ?? new AppSettings();
        bool dirty = false;
        if (string.IsNullOrEmpty(s.NodeId)) { s.NodeId = Guid.NewGuid().ToString("N"); dirty = true; }
        if (string.IsNullOrWhiteSpace(s.DeviceName)) { s.DeviceName = defaultDeviceName; dirty = true; }
        if (string.IsNullOrEmpty(s.GroupId) || string.IsNullOrEmpty(s.GroupKey)) { s.NewGroup(); dirty = true; }
        if (dirty) s.Save(path, log);
        return s;
    }

    /// <summary>Starts a sync group of its own, linked to nothing.</summary>
    public void NewGroup()
    {
        GroupId = Guid.NewGuid().ToString("N");
        GroupKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    }

    public void Save(string path, Action<string>? log = null)
    {
        try
        {
            Json.WriteAtomically(path, JsonSerializer.Serialize(this, Json.Options));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log?.Invoke($"Could not save settings: {e.Message}");
        }
    }

    public AppSettings Clone() =>
        JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(this, Json.Options), Json.Options)!;

    /// <summary>The sound mode <paramref name="d"/> plays with on this device.</summary>
    public SoundMode SoundModeFor(TaskDefinition d) => d.SoundMode ?? DefaultSoundMode;

    /// <summary>The sound <paramref name="d"/> plays on <paramref name="platform"/>: its own
    /// choice, otherwise this device's default; null means the built-in alarm sound.</summary>
    public SoundRef? SoundFor(TaskDefinition d, string platform) => d.SoundFor(platform) ?? DefaultSound;
}
