namespace AlarmCalendar;

/// <summary>Where a task stands at one moment. Everything here is derived from the task
/// and the time; nothing in it is stored.</summary>
public sealed record TaskStatus
{
    public required AlarmTask Task { get; init; }

    /// <summary>An occurrence has come up and not been marked done.</summary>
    public bool Triggered { get; init; }

    /// <summary>The earliest occurrence not yet dealt with, when triggered.</summary>
    public DateTimeOffset? Pending { get; init; }

    /// <summary>The most recent occurrence that has come up, when triggered. Differs from
    /// <see cref="Pending"/> only for an OnSchedule task that came up several times.</summary>
    public DateTimeOffset? Latest { get; init; }

    /// <summary>How many occurrences have come up without being marked done (≥ 1 when triggered).</summary>
    public long PendingCount { get; init; }

    /// <summary>Triggered, but the alarm window was dismissed and the reminder isn't due yet.</summary>
    public bool Snoozed { get; init; }

    /// <summary>When a snoozed task reminds again: the end of the snooze, or the next
    /// occurrence if that comes first. Null when it won't remind before the next occurrence
    /// and there is none.</summary>
    public DateTimeOffset? NextReminder { get; init; }

    /// <summary>The next scheduled occurrence after now. Null for a finished task, and for a
    /// triggered FromDone task, whose next occurrence is counted from when it is marked done.</summary>
    public DateTimeOffset? NextOccurrence { get; init; }

    /// <summary>The alarm window should be showing.</summary>
    public bool ShouldAlert => Triggered && !Snoozed;

    /// <summary>Nothing will ever fire again: a Once or Countdown task that is done, or a
    /// Weekly task with no days.</summary>
    public bool Finished { get; init; }

    /// <summary>The moment the current alert was due: the latest occurrence, or for a
    /// reminder, the end of the snooze. Used for the late warning.</summary>
    public DateTimeOffset? AlertDue { get; init; }

    /// <summary>The current alert is a reminder after a dismissal rather than a new occurrence.</summary>
    public bool IsReminder { get; init; }
}

/// <summary>Evaluation of tasks against the clock, and the state changes for each user action.</summary>
public static class AlarmEngine
{
    /// <summary>Lateness beyond this earns the "late by" warning (R13). The desktop polls
    /// every second and Android alarms fire within a few, so anything longer means the app
    /// wasn't running or the device was asleep.</summary>
    public static readonly TimeSpan LateThreshold = TimeSpan.FromSeconds(15);

    public static TaskStatus Evaluate(AlarmTask task, DateTimeOffset now, TimeZoneInfo zone)
    {
        var d = task.Def;
        var s = task.State;
        if (d.Deleted) return new TaskStatus { Task = task, Finished = true };

        bool fromDone = d.Kind == ScheduleKind.Interval && d.Restart == RestartMode.FromDone;
        var pending = Occurrences.After(d, s, s.DoneThroughUtc, zone);

        if (pending is not { } p || p > now)
        {
            return new TaskStatus
            {
                Task = task,
                NextOccurrence = pending,
                Finished = pending is null,
            };
        }

        var latest = fromDone ? p : Occurrences.AtOrBefore(d, s, now, zone) ?? p;
        long count = fromDone ? 1 : Math.Max(1, Occurrences.Count(d, s, s.DoneThroughUtc, now, zone));
        var next = fromDone ? null : Occurrences.After(d, s, now, zone);

        bool dismissedThis = s.SnoozeOccurrenceUtc is { } so && so >= latest && s.SnoozeUntilUtc is not null;
        bool snoozed = dismissedThis && now < s.SnoozeUntilUtc!.Value;

        DateTimeOffset? nextReminder = null;
        if (snoozed)
        {
            var until = s.SnoozeUntilUtc!.Value;
            nextReminder = until == DateTimeOffset.MaxValue ? next
                : next is { } n && n < until ? n : until;
        }

        return new TaskStatus
        {
            Task = task,
            Triggered = true,
            Pending = p,
            Latest = latest,
            PendingCount = count,
            Snoozed = snoozed,
            NextReminder = nextReminder,
            NextOccurrence = next,
            AlertDue = dismissedThis ? s.SnoozeUntilUtc : latest,
            IsReminder = dismissedThis,
        };
    }

    /// <summary>The earliest moment any of <paramref name="tasks"/> needs attention: an
    /// untriggered task's next occurrence or a snoozed task's next reminder. Android hands
    /// this to AlarmManager. Null when nothing is scheduled.</summary>
    public static DateTimeOffset? NextWakeUp(IEnumerable<AlarmTask> tasks, DateTimeOffset now, TimeZoneInfo zone)
    {
        DateTimeOffset? best = null;
        foreach (var t in tasks)
        {
            var st = Evaluate(t, now, zone);
            var when = st.Triggered ? (st.Snoozed ? st.NextReminder : null) : st.NextOccurrence;
            if (when is { } w && (best is null || w < best)) best = w;
        }
        return best;
    }

    /// <summary>How late the current alert of <paramref name="st"/> is if shown at
    /// <paramref name="shownAt"/>; null when within <see cref="LateThreshold"/>.</summary>
    public static TimeSpan? Lateness(TaskStatus st, DateTimeOffset shownAt)
    {
        if (st.AlertDue is not { } due) return null;
        var late = shownAt - due;
        return late > LateThreshold ? late : null;
    }

    // ---- Actions. Each returns the new state; the caller stores it. ----------------

    /// <summary>The state after the user marks the task done at <paramref name="now"/>.</summary>
    public static TaskState MarkDone(AlarmTask task, DateTimeOffset now, TimeZoneInfo zone)
    {
        var d = task.Def;
        var s = task.State;
        var nowS = TimeMath.TruncateToSecond(now);
        var done = TimeMath.Max(s.DoneThroughUtc, now);
        if (d.Kind == ScheduleKind.Interval && d.Restart == RestartMode.FromDone)
            return s with
            {
                Anchor = TimeMath.ToWall(nowS, zone),
                AnchorUtc = nowS.ToUniversalTime(),
                DoneThroughUtc = done, SnoozeUntilUtc = null, SnoozeOccurrenceUtc = null,
            };
        return s with { DoneThroughUtc = done, SnoozeUntilUtc = null, SnoozeOccurrenceUtc = null };
    }

    /// <summary>The state after the user closes the alarm window without marking the task
    /// done: it reminds again after the task's reminder interval.</summary>
    public static TaskState Dismiss(AlarmTask task, DateTimeOffset now, TimeZoneInfo zone)
    {
        var st = Evaluate(task, now, zone);
        if (!st.Triggered) return task.State;
        return task.State with
        {
            SnoozeOccurrenceUtc = st.Latest,
            SnoozeUntilUtc = task.Def.ReminderMinutes > 0
                ? now + TimeSpan.FromMinutes(task.Def.ReminderMinutes)
                : DateTimeOffset.MaxValue,
        };
    }

    /// <summary>The state to store with a newly saved schedule: only occurrences after
    /// <paramref name="now"/> fire, and a countdown starts.</summary>
    /// <param name="old">The task's previous state, or null for a new task.</param>
    /// <param name="def">The saved definition, carrying its new ScheduleRevision.</param>
    /// <remarks>DoneThrough is set to <paramref name="now"/>, not raised to it: this is the
    /// one way a DoneThrough left in the future by a wrong clock gets lowered again.</remarks>
    public static TaskState FreshState(TaskState? old, TaskDefinition def, DateTimeOffset now) => new()
    {
        Revision = def.ScheduleRevision,
        DoneThroughUtc = now,
        CountdownEndsUtc = def.Kind == ScheduleKind.Countdown ? TimeMath.TruncateToSecond(now) + def.Duration : null,
    };
}
