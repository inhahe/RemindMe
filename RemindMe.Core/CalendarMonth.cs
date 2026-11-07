using System.Globalization;

namespace RemindMe;

/// <summary>One task's showing on a calendar day.</summary>
/// <param name="At">When it comes up (for a repeating line, its first time that day).</param>
/// <param name="Text">"9:00 AM", "~9:00 AM" (an estimate) or "every 15 min".</param>
/// <param name="Title">The start of the task's description.</param>
/// <param name="Estimated">For a task that restarts when marked done: assumes it is marked
/// done on time, which is all that can be known in advance.</param>
/// <param name="Repeating">A task that comes up several times a day, shown once.</param>
/// <param name="Waiting">It has come up and isn't marked done yet.</param>
public sealed record CalendarEntry(Guid TaskId, DateTimeOffset At, string Text, string Title, bool Estimated, bool Repeating, bool Waiting = false);

/// <param name="InMonth">False for the days of the previous and next months that fill the grid.</param>
/// <param name="Past">Before today.</param>
public sealed record CalendarDay(DateOnly Date, bool InMonth, bool IsToday, bool Past, IReadOnlyList<CalendarEntry> Entries);

/// <summary>
/// A month as a calendar shows it (S2): six weeks of days, starting on the culture's first
/// day of the week, each with the times its tasks come up. Built from the schedules alone,
/// like everything else, and nothing records a history: so it shows what is still to come
/// or still waiting to be marked done, not times already dealt with (nor, for that matter,
/// times before a task existed, which its schedule would also give).
/// </summary>
public static class CalendarMonth
{
    /// <summary>Occurrences per task per grid, at most: a schedule that comes up more often
    /// than that within six weeks is shown as one repeating line a day.</summary>
    private const int MaxPerTask = 200;

    public static IReadOnlyList<CalendarDay> Build(IEnumerable<AlarmTask> tasks, int year, int month, DateTimeOffset now,
        TimeZoneInfo zone, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        var first = new DateTime(year, month, 1);
        int back = ((int)first.DayOfWeek - (int)culture.DateTimeFormat.FirstDayOfWeek + 7) % 7;
        var gridStart = first.AddDays(-back);
        var today = TimeMath.ToWall(now, zone).Date;
        var byDay = new Dictionary<DateTime, List<CalendarEntry>>();
        for (int i = 0; i < 42; i++) byDay[gridStart.AddDays(i)] = [];

        foreach (var t in tasks)
            foreach (var e in EntriesOf(t, gridStart, now, zone, culture))
                if (byDay.TryGetValue(TimeMath.ToWall(e.At, zone).Date, out var list)) list.Add(e);

        return byDay.OrderBy(kv => kv.Key).Select(kv => new CalendarDay(
            DateOnly.FromDateTime(kv.Key), kv.Key.Month == month, kv.Key == today, kv.Key < today,
            kv.Value.OrderBy(e => TimeMath.ToWall(e.At, zone).TimeOfDay).ThenBy(e => e.Title, StringComparer.CurrentCultureIgnoreCase).ToList())).ToList();
    }

    private static IEnumerable<CalendarEntry> EntriesOf(AlarmTask t, DateTime gridStart, DateTimeOffset now, TimeZoneInfo zone, CultureInfo culture)
    {
        var d = t.Def;
        var s = t.State;
        if (d.Deleted || s.PausedAtUtc is not null) yield break;
        var title = TextFormat.Snippet(d.Description, 40) is { Length: > 0 } x ? x : "(no description)";
        var gridFrom = TimeMath.ToInstant(gridStart, zone) - TimeSpan.FromTicks(1);
        var to = TimeMath.ToInstant(gridStart.AddDays(42), zone);
        // Nothing at or before DoneThrough: dealt with, or from before the schedule was saved.
        var from = TimeMath.Max(gridFrom, s.DoneThroughUtc);
        bool fromDone = d.Kind == ScheduleKind.Interval && d.Restart == RestartMode.FromDone;
        bool severalADay = d.Kind == ScheduleKind.Interval && d.Unit is IntervalUnit.Seconds or IntervalUnit.Minutes or IntervalUnit.Hours
            && Step(d) < TimeSpan.FromDays(1);
        string every = d.Unit == IntervalUnit.Seconds ? $"every {d.IntervalCount} s" : $"every {TextFormat.Short(Step(d))}";
        var st = AlarmEngine.Evaluate(t, now, zone);

        // A task that restarts when marked done stops while it waits: only the time it came
        // up is pending, and the next ones count from when it is marked done, now at the
        // earliest.
        DateTime? pendingDay = null;
        if (fromDone && st.Triggered && st.Pending is { } p)
        {
            if (p > gridFrom && p < to)
            {
                pendingDay = TimeMath.ToWall(p, zone).Date;
                yield return new CalendarEntry(t.Id, p, severalADay ? every : TextFormat.WallTime(TimeMath.ToWall(p, zone), culture),
                    title, Estimated: false, Repeating: severalADay, Waiting: true);
            }
            s = AlarmEngine.MarkDone(t, now, zone);
            from = TimeMath.Max(gridFrom, now);
        }

        if (severalADay)
        {
            // Several times a day: one line a day, at its first time.
            for (int i = 0; i < 42; i++)
            {
                if (gridStart.AddDays(i) == pendingDay) continue;
                var dayStart = TimeMath.Max(TimeMath.ToInstant(gridStart.AddDays(i), zone) - TimeSpan.FromTicks(1), from);
                var dayEnd = TimeMath.ToInstant(gridStart.AddDays(i + 1), zone);
                if (Occurrences.After(d, s, dayStart, zone) is { } o && o < dayEnd)
                    yield return new CalendarEntry(t.Id, o, every, title, Estimated: fromDone && o != st.NextOccurrence,
                        Repeating: true, Waiting: o <= now);
            }
            yield break;
        }

        // A task that restarts when marked done only knows its next time; the rest assume
        // it is marked done on time.
        var next = st.NextOccurrence;
        var cursor = from;
        for (int n = 0; n < MaxPerTask && Occurrences.After(d, s, cursor, zone) is { } o && o < to; n++)
        {
            bool estimated = fromDone && o > now && o != next;
            string time = TextFormat.WallTime(TimeMath.ToWall(o, zone), culture);
            yield return new CalendarEntry(t.Id, o, estimated ? "~" + time : time, title, estimated, Repeating: false, Waiting: o <= now);
            cursor = o;
        }
    }

    /// <summary>The next moment <paramref name="days"/> would look different without any
    /// change to the tasks: when the first time still to come that it shows comes up, and
    /// starts waiting (or moves a restart-when-done task's estimates). Null when it shows
    /// nothing still to come. Estimates don't count: a waiting task's are reckoned from now,
    /// so they would always be about to change. The date changing is the caller's to watch.</summary>
    public static DateTimeOffset? NextChange(IEnumerable<CalendarDay> days, DateTimeOffset now) =>
        days.SelectMany(d => d.Entries).Where(e => !e.Waiting && !e.Estimated && e.At > now)
            .Select(e => (DateTimeOffset?)e.At).Min();

    /// <summary>How long a calendar on screen waits before checking again whether to
    /// rebuild: until just after <paramref name="nextChange"/>, but at least once a minute,
    /// for the date.</summary>
    public static TimeSpan TickDelay(DateTimeOffset? nextChange, DateTimeOffset now)
    {
        var floor = TimeSpan.FromMilliseconds(200);
        var cap = TimeSpan.FromMinutes(1);
        if (nextChange is not { } at || at - now > cap) return cap;
        var wait = at - now + floor;
        return wait < floor ? floor : wait;
    }

    /// <summary>Whether a calendar built on <paramref name="builtOn"/> (its date), with
    /// <paramref name="nextChange"/> from <see cref="NextChange"/>, is out of date.</summary>
    public static bool Stale(DateTime builtOn, DateTimeOffset? nextChange, DateTimeOffset now, TimeZoneInfo zone) =>
        TimeMath.ToWall(now, zone).Date != builtOn || now >= nextChange;

    private static TimeSpan Step(TaskDefinition d) => d.Unit switch
    {
        IntervalUnit.Seconds => TimeSpan.FromSeconds(d.IntervalCount),
        IntervalUnit.Minutes => TimeSpan.FromMinutes(d.IntervalCount),
        _ => TimeSpan.FromHours(d.IntervalCount),
    };
}
