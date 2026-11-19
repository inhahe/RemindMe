namespace RemindMe;

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

/// <summary>The part of a task the user edits. Synced in three parts: its schedule (the
/// fields <see cref="WithScheduleOf"/> copies) by <see cref="ScheduleRank"/>, as the state is;
/// its restart mode by <see cref="RestartVersion"/>; and the rest by <see cref="DefRank"/>.</summary>
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

    /// <summary>The version of <see cref="Restart"/>, which merges on its own: it says how an
    /// interval task counts from being marked done, but no state is unable to serve either
    /// mode, so a change of it neither undoes a change of the times made elsewhere nor gives
    /// way to one, nor to an edit of the rest.</summary>
    public Hlc RestartVersion { get; init; }

    // Weekly
    /// <summary>Bit (int)DayOfWeek set for each day the task fires; bit 0 is Sunday.</summary>
    public int Days { get; init; }
    public TimeOnly TimeOfDay { get; init; } = new(9, 0);
    /// <summary>Rings every this many weeks; 1 is every week (S7).</summary>
    public int WeekInterval { get; init; } = 1;
    /// <summary>With <see cref="WeekInterval"/> above 1: a date in the first week that rings.
    /// From that week on, every <see cref="WeekInterval"/>th rings; none before it.</summary>
    public DateOnly WeeksFrom { get; init; }
    /// <summary>With <see cref="WeekInterval"/> above 1: the day weeks start on, taken from the
    /// culture of the device that saved the task, so that every device agrees which weeks ring.</summary>
    public DayOfWeek FirstDayOfWeek { get; init; } = DayOfWeek.Monday;

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

    /// <summary>How loud the alarm plays, in percent of the device's own alarm volume
    /// (one of <see cref="SoundLevels.Volumes"/>); null means the device's default (S10).</summary>
    public int? Volume { get; init; }

    /// <summary>Seconds over which the alarm grows from quiet to <see cref="Volume"/>; 0 is
    /// at full volume at once; null means the device's default (S10).</summary>
    public int? FadeInSeconds { get; init; }

    /// <summary>Raised on every edit that changes when the task fires; the state written
    /// with such an edit carries the new value in <see cref="TaskState.Revision"/>.</summary>
    public int ScheduleRevision { get; init; }

    /// <summary>The version of the schedule: when it was last set (the schedule's fields are
    /// those <see cref="WithScheduleOf"/> copies), which says which schedule this is. The
    /// state written for it carries it too (<see cref="TaskState.ScheduleStamp"/>), so the
    /// schedule and the state that win a merge are always of one schedule, the later set
    /// (<see cref="ScheduleRank"/>).</summary>
    public Hlc ScheduleStamp { get; init; }

    /// <summary>Tombstone: the task was deleted. Kept so the deletion syncs.</summary>
    public bool Deleted { get; init; }

    /// <summary>How many times the task has been brought back after a deletion (an import
    /// restoring it). A deletion outranks every edit of the same incarnation, wherever and
    /// whenever made, and only a later incarnation outranks the deletion: see
    /// <see cref="DefRank"/>.</summary>
    public int Incarnation { get; init; }

    public bool HasDay(DayOfWeek d) => (Days & (1 << (int)d)) != 0;

    public TaskDefinition WithDay(DayOfWeek d, bool on) =>
        this with { Days = on ? Days | (1 << (int)d) : Days & ~(1 << (int)d) };

    /// <summary>This definition as a days-of-the-week schedule, as both editors save it.</summary>
    /// <param name="days">The <see cref="Days"/> mask.</param>
    /// <param name="weeks">Every this many weeks; anything below 2 is every week.</param>
    /// <param name="weeksFrom">With <paramref name="weeks"/> above 1: a date in the first week that rings.</param>
    /// <param name="firstDayHere">The day weeks start on in this device's culture. A task that
    /// already rang every few weeks keeps the week start it was saved with instead, so that
    /// opening it on a device whose weeks start on another day doesn't move its schedule.</param>
    public TaskDefinition AsWeekly(int days, TimeOnly time, int weeks, DateOnly weeksFrom, DayOfWeek firstDayHere)
    {
        var d = this with { Kind = ScheduleKind.Weekly, Days = days, TimeOfDay = time, WeekInterval = Math.Max(1, weeks) };
        if (weeks <= 1) return d;
        return d with
        {
            WeeksFrom = weeksFrom,
            FirstDayOfWeek = this is { Kind: ScheduleKind.Weekly, WeekInterval: > 1 } ? FirstDayOfWeek : firstDayHere,
        };
    }

    /// <summary>This definition with <paramref name="other"/>'s schedule: every field that
    /// says when the task comes up, and the schedule's revision and stamp. The rest (the
    /// description, the reminder interval, the sounds, the restart mode, whether it is
    /// deleted, its incarnation) stays this one's. The parts merge apart: an edit of one
    /// doesn't undo one of another made elsewhere.</summary>
    public TaskDefinition WithScheduleOf(TaskDefinition other) => this with
    {
        Kind = other.Kind,
        IntervalCount = other.IntervalCount, Unit = other.Unit, Start = other.Start,
        Days = other.Days, TimeOfDay = other.TimeOfDay, WeekInterval = other.WeekInterval,
        WeeksFrom = other.WeeksFrom, FirstDayOfWeek = other.FirstDayOfWeek,
        At = other.At,
        Duration = other.Duration,
        ScheduleRevision = other.ScheduleRevision, ScheduleStamp = other.ScheduleStamp,
    };

    /// <summary>The stamp of a schedule saved before stamps existed (none, in the file): one
    /// every device works out alike from the schedule itself, so that two devices holding
    /// the same such schedule agree it is one, and two holding different ones (changed on
    /// each, out of touch, before updating) still pick the same. It orders after an older
    /// build's earlier revisions, as they ranked then, and before any stamp of a schedule set
    /// since (its milliseconds are 0).</summary>
    public Hlc LegacyScheduleStamp()
    {
        var text = FormattableString.Invariant($"{Kind}|{IntervalCount}|{Unit}|{Start.Ticks}|{Days}|{TimeOfDay.Ticks}|{WeekInterval}|{WeeksFrom.DayNumber}|{FirstDayOfWeek}|{At.Ticks}|{Duration.Ticks}");
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text));
        return new Hlc(0, ScheduleRevision, "~" + Convert.ToHexString(hash, 0, 16));
    }

    /// <summary>This definition with <paramref name="other"/>'s restart mode, and its version.</summary>
    public TaskDefinition WithRestartOf(TaskDefinition other) => this with { Restart = other.Restart, RestartVersion = other.RestartVersion };

    /// <summary>This definition, with the fields outside its schedule and restart mode that
    /// <paramref name="edited"/> changes from <paramref name="from"/>: an editor's save, laid
    /// on the task as it is now. A field changed elsewhere since the editor opened, and not
    /// here, keeps its newer value. Whether the task is deleted, and its incarnation, are the
    /// store's to set, not an editor's.</summary>
    /// <param name="from">The definition the editor opened with.</param>
    /// <param name="edited">What the editor saves.</param>
    public TaskDefinition WithRestEditsOf(TaskDefinition from, TaskDefinition edited) => this with
    {
        Description = edited.Description != from.Description ? edited.Description : Description,
        ReminderMinutes = edited.ReminderMinutes != from.ReminderMinutes ? edited.ReminderMinutes : ReminderMinutes,
        WindowsSound = edited.WindowsSound != from.WindowsSound ? edited.WindowsSound : WindowsSound,
        AndroidSound = edited.AndroidSound != from.AndroidSound ? edited.AndroidSound : AndroidSound,
        SoundMode = edited.SoundMode != from.SoundMode ? edited.SoundMode : SoundMode,
        Volume = edited.Volume != from.Volume ? edited.Volume : Volume,
        FadeInSeconds = edited.FadeInSeconds != from.FadeInSeconds ? edited.FadeInSeconds : FadeInSeconds,
    };


    /// <summary>True when the two definitions would fire at different times.</summary>
    public bool ScheduleDiffers(TaskDefinition other) =>
        Kind != other.Kind || Kind switch
        {
            ScheduleKind.Interval => IntervalCount != other.IntervalCount || Unit != other.Unit || Start != other.Start,
            ScheduleKind.Weekly => Days != other.Days || TimeOfDay != other.TimeOfDay || WeekInterval != other.WeekInterval
                || (WeekInterval > 1 && (WeeksFrom != other.WeeksFrom || FirstDayOfWeek != other.FirstDayOfWeek)),
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
/// <see cref="StateRank"/>: the schedule it was written for, then
/// <see cref="AlarmTask.StateVersion"/>; except <see cref="DoneThroughUtc"/>, which merges by
/// maximum within one schedule.</summary>
public sealed record TaskState
{
    /// <summary>The <see cref="TaskDefinition.ScheduleRevision"/> this state was written against.</summary>
    public int Revision { get; init; }

    /// <summary>The <see cref="TaskDefinition.Incarnation"/> this state was written for. A
    /// later one outranks any state of an earlier one, whatever its revision: a restored
    /// task's state is never one written for the task as it was before its deletion.</summary>
    public int Incarnation { get; init; }

    /// <summary>The <see cref="TaskDefinition.ScheduleStamp"/> of the schedule this state was
    /// written for: with <see cref="Incarnation"/>, which schedule.</summary>
    public Hlc ScheduleStamp { get; init; }

    /// <summary>Wall-clock time replacing <see cref="TaskDefinition.Start"/> after a
    /// FromDone task is marked done.</summary>
    public DateTime? Anchor { get; init; }

    /// <summary>The instant of <see cref="Anchor"/>, which fixed-length intervals (seconds,
    /// minutes, hours) count from: a wall-clock time in the repeated hour of a fall-back
    /// transition would otherwise resolve to the wrong one of its two instants.</summary>
    public DateTimeOffset? AnchorUtc { get; init; }

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

    /// <summary>When the task was paused; null while it isn't. A paused task never rings, and
    /// a paused countdown keeps the time it had left (<see cref="CountdownEndsUtc"/> minus this).</summary>
    public DateTimeOffset? PausedAtUtc { get; init; }
}

public sealed record AlarmTask
{
    public Guid Id { get; init; }
    public TaskDefinition Def { get; init; } = new();
    public Hlc DefVersion { get; init; }
    public TaskState State { get; init; } = new();
    public Hlc StateVersion { get; init; }
}
