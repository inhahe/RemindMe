namespace AlarmCalendar;

/// <summary>How a task decides when it fires.</summary>
public enum ScheduleKind
{
    /// <summary>Every N units from a start time.</summary>
    Interval,
    /// <summary>On chosen days of the week at a time of day.</summary>
    Weekly,
    /// <summary>A single date and time.</summary>
    Once,
    /// <summary>A one-shot timer of a given length, started when saved.</summary>
    Countdown,
}

public enum IntervalUnit { Seconds, Minutes, Hours, Days, Weeks, Months, Years }

/// <summary>What an Interval task counts its next occurrence from.</summary>
public enum RestartMode
{
    /// <summary>The next occurrence is N units after the task is marked done; the timer
    /// stops while a triggered task waits.</summary>
    FromDone,
    /// <summary>Occurrences keep to the grid set by the start time, whether or not the
    /// previous one was marked done.</summary>
    OnSchedule,
}

public enum SoundMode
{
    /// <summary>Play the sound once each time the alarm window appears.</summary>
    Once,
    /// <summary>Loop the sound until the alarm window is dismissed or the task is marked done.</summary>
    Repeat,
}

public enum SoundSource
{
    /// <summary>One of the platform's built-in sounds. <see cref="SoundRef.Value"/> is a
    /// file name under <c>C:\Windows\Media</c> on Windows, a ringtone URI on Android.</summary>
    System,
    /// <summary>A file the user picked. <see cref="SoundRef.Value"/> is a full path on
    /// Windows, a content URI on Android.</summary>
    File,
}

/// <param name="Source">Where <paramref name="Value"/> points.</param>
/// <param name="Value">The platform-specific locator; see <see cref="SoundSource"/>.</param>
/// <param name="Name">What to show the user, e.g. "Alarm01" or "rooster.mp3".</param>
public sealed record SoundRef(SoundSource Source, string Value, string Name);

/// <summary>Platform keys for <see cref="TaskDefinition.SoundFor"/>. A sound picked on one
/// platform means nothing on the other, so each keeps its own choice.</summary>
public static class Platforms
{
    public const string Windows = "windows";
    public const string Android = "android";
}

/// <summary>The part of a task the user edits. Synced last-writer-wins as a whole by
/// <see cref="AlarmTask.DefVersion"/>.</summary>
public sealed record TaskDefinition
{
    public string Description { get; init; } = "";
    public ScheduleKind Kind { get; init; } = ScheduleKind.Interval;

    // Interval
    public int IntervalCount { get; init; } = 1;
    public IntervalUnit Unit { get; init; } = IntervalUnit.Days;
    /// <summary>Wall-clock start of an Interval schedule (Kind Unspecified, read in the
    /// device's zone): occurrence k is Start + k·N·unit.</summary>
    public DateTime Start { get; init; }
    public RestartMode Restart { get; init; } = RestartMode.FromDone;

    // Weekly
    /// <summary>Bit (int)DayOfWeek set for each day the task fires; bit 0 is Sunday.</summary>
    public int Days { get; init; }
    public TimeOnly TimeOfDay { get; init; } = new(9, 0);

    // Once
    /// <summary>Wall-clock date and time of a Once task.</summary>
    public DateTime At { get; init; }

    // Countdown
    public TimeSpan Duration { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Minutes from a dismissal to the next reminder while the task is not done;
    /// 0 means it does not remind again until the next occurrence.</summary>
    public int ReminderMinutes { get; init; } = 10;

    /// <summary>The sound picked on Windows; null means the device's default sound.</summary>
    public SoundRef? WindowsSound { get; init; }

    /// <summary>The sound picked on Android; null means the device's default sound.</summary>
    public SoundRef? AndroidSound { get; init; }

    /// <summary>Null means the device's default sound mode.</summary>
    public SoundMode? SoundMode { get; init; }

    /// <summary>Raised on every edit that changes when the task fires; the state written
    /// with such an edit carries the new value in <see cref="TaskState.Revision"/>.</summary>
    public int ScheduleRevision { get; init; }

    /// <summary>Tombstone: the task was deleted. Kept so the deletion syncs.</summary>
    public bool Deleted { get; init; }

    public bool HasDay(DayOfWeek d) => (Days & (1 << (int)d)) != 0;

    public TaskDefinition WithDay(DayOfWeek d, bool on) =>
        this with { Days = on ? Days | (1 << (int)d) : Days & ~(1 << (int)d) };

    /// <summary>True when the two definitions would fire at different times.</summary>
    public bool ScheduleDiffers(TaskDefinition other) =>
        Kind != other.Kind || Kind switch
        {
            ScheduleKind.Interval => IntervalCount != other.IntervalCount || Unit != other.Unit || Start != other.Start,
            ScheduleKind.Weekly => Days != other.Days || TimeOfDay != other.TimeOfDay,
            ScheduleKind.Once => At != other.At,
            ScheduleKind.Countdown => Duration != other.Duration,
            _ => true,
        };

    /// <summary>The sound picked for <paramref name="platform"/> (a <see cref="Platforms"/>
    /// key); null means the device's default.</summary>
    public SoundRef? SoundFor(string platform) => platform switch
    {
        Platforms.Windows => WindowsSound,
        Platforms.Android => AndroidSound,
        _ => null,
    };

    public TaskDefinition WithSound(string platform, SoundRef? sound) => platform switch
    {
        Platforms.Windows => this with { WindowsSound = sound },
        Platforms.Android => this with { AndroidSound = sound },
        _ => throw new ArgumentException($"Unknown platform '{platform}'", nameof(platform)),
    };
}

/// <summary>What has happened to a task over time. Synced last-writer-wins by
/// (<see cref="Revision"/>, <see cref="AlarmTask.StateVersion"/>), except
/// <see cref="DoneThroughUtc"/>, which merges by maximum.</summary>
public sealed record TaskState
{
    /// <summary>The <see cref="TaskDefinition.ScheduleRevision"/> this state was written against.</summary>
    public int Revision { get; init; }

    /// <summary>Wall-clock time replacing <see cref="TaskDefinition.Start"/> after a
    /// FromDone task is marked done.</summary>
    public DateTime? Anchor { get; init; }

    /// <summary>When a running countdown ends; null when not started.</summary>
    public DateTimeOffset? CountdownEndsUtc { get; init; }

    /// <summary>Every occurrence at or before this instant has been dealt with. Never decreases.</summary>
    public DateTimeOffset DoneThroughUtc { get; init; }

    /// <summary>After a dismissal: when to remind again. <see cref="DateTimeOffset.MaxValue"/>
    /// means not until the next occurrence.</summary>
    public DateTimeOffset? SnoozeUntilUtc { get; init; }

    /// <summary>The latest occurrence at the time of the dismissal. A later occurrence
    /// ends the snooze.</summary>
    public DateTimeOffset? SnoozeOccurrenceUtc { get; init; }
}

public sealed record AlarmTask
{
    public Guid Id { get; init; }
    public TaskDefinition Def { get; init; } = new();
    public Hlc DefVersion { get; init; }
    public TaskState State { get; init; } = new();
    public Hlc StateVersion { get; init; }
}
