using System.Globalization;

namespace AlarmCalendar;

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
        // Nothing at or before DoneThrough: dealt with, or from before the schedule was saved.
        var from = TimeMath.Max(TimeMath.ToInstant(gridStart, zone) - TimeSpan.FromTicks(1), s.DoneThroughUtc);
        var to = TimeMath.ToInstant(gridStart.AddDays(42), zone);

        if (d.Kind == ScheduleKind.Interval && d.Unit is IntervalUnit.Seconds or IntervalUnit.Minutes or IntervalUnit.Hours
            && Step(d) < TimeSpan.FromDays(1))
        {
            // Several times a day: one line a day, at its first time.
            string every = d.Unit == IntervalUnit.Seconds ? $"every {d.IntervalCount} s" : $"every {TextFormat.Short(Step(d))}";
            for (int i = 0; i < 42; i++)
            {
                var dayStart = TimeMath.Max(TimeMath.ToInstant(gridStart.AddDays(i), zone) - TimeSpan.FromTicks(1), from);
                var dayEnd = TimeMath.ToInstant(gridStart.AddDays(i + 1), zone);
                if (Occurrences.After(d, s, dayStart, zone) is { } o && o < dayEnd)
                    yield return new CalendarEntry(t.Id, o, every, title, Estimated: d.Restart == RestartMode.FromDone, Repeating: true,
                        Waiting: o <= now);
            }
            yield break;
        }

        // A task that restarts when marked done only knows its next time; the rest assume
        // it is marked done on time.
        bool fromDone = d.Kind == ScheduleKind.Interval && d.Restart == RestartMode.FromDone;
        var next = AlarmEngine.Evaluate(t, now, zone).NextOccurrence;
        var cursor = from;
        for (int n = 0; n < MaxPerTask && Occurrences.After(d, s, cursor, zone) is { } o && o < to; n++)
        {
            bool estimated = fromDone && o > now && o != next;
            string time = TextFormat.WallTime(TimeMath.ToWall(o, zone), culture);
            yield return new CalendarEntry(t.Id, o, estimated ? "~" + time : time, title, estimated, Repeating: false, Waiting: o <= now);
            cursor = o;
        }
    }

    private static TimeSpan Step(TaskDefinition d) => d.Unit switch
    {
        IntervalUnit.Seconds => TimeSpan.FromSeconds(d.IntervalCount),
        IntervalUnit.Minutes => TimeSpan.FromMinutes(d.IntervalCount),
        _ => TimeSpan.FromHours(d.IntervalCount),
    };
}
