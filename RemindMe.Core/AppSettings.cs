using System.Security.Cryptography;
using System.Text.Json;

namespace RemindMe;

/// <param name="LastSeenUtc">When a sync session with it was last open.</param>
/// <param name="Address">Its listener's address on the LAN when last in sync ("ip:port"),
/// which the sync engine dials to reach it again; null if never seen on the LAN.</param>
public sealed record KnownPeer(string NodeId, string Name, DateTimeOffset LastSeenUtc, string? Address = null);

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
    /// <summary>Percent of the device's alarm volume for tasks left on the default (S10).</summary>
    public int DefaultVolume { get; set; } = 100;
    /// <summary>Seconds of fade-in for tasks left on the default; 0 is none (S10).</summary>
    public int DefaultFadeInSeconds { get; set; }

    // ---- Backups ----

    /// <summary>Write a backup after every change to the tasks (<see cref="Backups"/>).</summary>
    public bool AutoBackup { get; set; } = true;
    /// <summary>Desktop: the folder backups go to; null means the app's default.</summary>
    public string? BackupFolder { get; set; }

    // ---- Desktop ----

    public bool StartWithWindows { get; set; } = true;
    /// <summary>The window's close button hides it to the tray rather than exiting, so
    /// alarms keep ringing.</summary>
    public bool CloseToTray { get; set; } = true;
    /// <summary>Wake the computer from sleep for alarms (S3).</summary>
    public bool WakeFromSleep { get; set; } = true;
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
    /// <summary>The name the user gave this device, which its linked devices show. Empty
    /// means it goes by its own name (the computer's, the phone's), following that as it
    /// changes: see <see cref="NameOr"/>. <see cref="SetDeviceName"/> keeps it so.</summary>
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
    /// <summary>Devices of this group this device has synced with, for the Devices screen
    /// to list while they are away.</summary>
    public List<KnownPeer> KnownPeers { get; set; } = [];

    /// <summary>The settings file's format: 0 (no field) for one from 0.1.1 or earlier, which
    /// stored a default name as <see cref="DeviceName"/>; <see cref="CurrentFormat"/> since.</summary>
    public int Format { get; set; }
    public const int CurrentFormat = 1;

    /// <summary>Loads the settings, creating the node ID and a one-device sync group on
    /// first run, and bringing a file of an earlier format up to date.</summary>
    /// <param name="ownName">This device's own name now (the computer's, the phone's).</param>
    /// <param name="earlierDefaults">The names earlier versions stored on first run (the
    /// computer's NetBIOS name, the phone's maker and model), which may differ from
    /// <paramref name="ownName"/>.</param>
    public static AppSettings Load(string path, string ownName, IReadOnlyCollection<string> earlierDefaults,
        Action<string>? log = null)
    {
        var s = Json.ReadWithFallback(path, Json.Files.AppSettings, log) ?? new AppSettings();
        bool dirty = false;
        if (string.IsNullOrEmpty(s.NodeId)) { s.NodeId = Guid.NewGuid().ToString("N"); dirty = true; }
        if (s.Format < 1)
        {
            // A name an earlier version stored on first run follows the device's name from
            // now on. In any case: Windows' NetBIOS name is in capitals. Another name can't
            // be told from one the user gave, and stays.
            var stored = (s.DeviceName ?? "").Trim();
            if (earlierDefaults.Append(ownName).Any(n => string.Equals(n.Trim(), stored, StringComparison.OrdinalIgnoreCase)))
                stored = "";
            s.DeviceName = stored;
            s.Format = CurrentFormat;
            dirty = true;
        }
        if (string.IsNullOrEmpty(s.GroupId) || string.IsNullOrEmpty(s.GroupKey)) { s.NewGroup(); dirty = true; }
        // A damaged or hand-edited file may hold remembered devices nothing can use (a null,
        // one without a node ID): dropped here, once, for every reader.
        var peers = s.KnownPeers ?? [];
        var usable = peers.Where(k => k is not null && Sync.Protocol.IsValidNodeId(k.NodeId))
            .Select(k => k.Name is null ? k with { Name = k.NodeId[..8] } : k).ToList();
        if (s.KnownPeers is null || !usable.SequenceEqual(peers)) { s.KnownPeers = usable; dirty = true; }
        if (dirty) s.Save(path, log);
        return s;
    }

    /// <summary>The name this device goes by: the one the user gave it, or else
    /// <paramref name="ownName"/>, its own name now (the computer's, the phone's).</summary>
    public string NameOr(string ownName) => DeviceName.Length > 0 ? DeviceName : ownName;

    /// <summary>Gives this device the name <paramref name="name"/>, as typed. Blank, or the
    /// device's own name <paramref name="ownName"/> exactly, means it goes by its own name,
    /// whatever that becomes; the same name in other capitals is a name of its own.</summary>
    public void SetDeviceName(string name, string ownName)
    {
        name = name.Trim();
        DeviceName = name.Length == 0 || name == ownName ? "" : name;
    }

    /// <summary>Starts a sync group of its own, linked to nothing.</summary>
    public void NewGroup()
    {
        GroupId = Guid.NewGuid().ToString("N");
        GroupKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        KnownPeers = [];
    }

    /// <summary>Records that the linked device <paramref name="node"/> was in sync at
    /// <paramref name="now"/> (when its session proved the key, and again when it closed).</summary>
    /// <param name="address">Its listener's LAN address then ("ip:port"); null keeps the one
    /// remembered (it was reached over the internet, say).</param>
    /// <remarks>Copy-on-write: the sync engine reads <see cref="KnownPeers"/> from its own
    /// threads, so the list is replaced, never changed in place.</remarks>
    public void RememberPeer(string node, string name, DateTimeOffset now, string? address = null)
    {
        var list = new List<KnownPeer>(KnownPeers);
        int i = list.FindIndex(p => p.NodeId == node);
        var peer = new KnownPeer(node, name, now, address ?? (i >= 0 ? list[i].Address : null));
        if (i >= 0) list[i] = peer;
        else list.Add(peer);
        KnownPeers = list;
    }

    public void Save(string path, Action<string>? log = null)
    {
        try
        {
            Json.WriteAtomically(path, JsonSerializer.Serialize(this, Json.Files.AppSettings));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log?.Invoke($"Could not save settings: {e.Message}");
        }
    }

    /// <summary>True when a setting the sync engine starts with differs, so it must restart.</summary>
    public bool SyncSettingsDiffer(AppSettings o) =>
        SyncEnabled != o.SyncEnabled || DeviceName != o.DeviceName || Port != o.Port
        || AcceptExternalConnections != o.AcceptExternalConnections || !RemoteAddresses.SequenceEqual(o.RemoteAddresses);

    public AppSettings Clone() =>
        JsonSerializer.Deserialize(JsonSerializer.Serialize(this, Json.Files.AppSettings), Json.Files.AppSettings)!;

    /// <summary>The sound mode <paramref name="d"/> plays with on this device.</summary>
    public SoundMode SoundModeFor(TaskDefinition d) => d.SoundMode ?? DefaultSoundMode;

    /// <summary>The volume <paramref name="d"/> plays at on this device, from 0.1 to 1.</summary>
    public double VolumeFor(TaskDefinition d) => Math.Clamp(d.Volume ?? DefaultVolume, 10, 100) / 100.0;

    /// <summary>How long <paramref name="d"/>'s sound takes to reach its volume on this device.</summary>
    public TimeSpan FadeInFor(TaskDefinition d) => TimeSpan.FromSeconds(Math.Clamp(d.FadeInSeconds ?? DefaultFadeInSeconds, 0, 600));

    /// <summary>The sound <paramref name="d"/> plays on <paramref name="platform"/>: its own
    /// choice, otherwise this device's default; null means the built-in alarm sound.</summary>
    public SoundRef? SoundFor(TaskDefinition d, string platform) => d.SoundFor(platform) ?? DefaultSound;
}
