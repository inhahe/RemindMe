namespace AlarmCalendar;

/// <summary>
/// The instants at which a task fires. An Interval task's occurrence k is computed from
/// its anchor directly, never by stepping from occurrence k−1, so calendar units don't
/// drift (monthly from the 31st gives Feb 28, then Mar 31) and long gaps cost nothing.
/// </summary>
public static class Occurrences
{
    /// <summary>The wall-clock anchor an Interval task counts from: the state's anchor
    /// after a FromDone mark-done, otherwise the definition's start.</summary>
    public static DateTime AnchorOf(TaskDefinition d, TaskState s) => s.Anchor ?? d.Start;

    /// <summary>The latest occurrence at or before <paramref name="t"/>, or null if none.</summary>
    public static DateTimeOffset? AtOrBefore(TaskDefinition d, TaskState s, DateTimeOffset t, TimeZoneInfo z)
    {
        switch (d.Kind)
        {
            case ScheduleKind.Interval:
                {
                    var i = new IntervalGrid(d, s, z);
                    long k = i.IndexAtOrBefore(t);
                    return k < 0 ? null : i.Occurrence(k);
                }
            case ScheduleKind.Weekly:
                return WeeklyAtOrBefore(d, t, z);
            case ScheduleKind.Once:
                {
                    var o = TimeMath.ToInstant(d.At, z);
                    return o <= t ? o : null;
                }
            case ScheduleKind.Countdown:
                return s.CountdownEndsUtc is { } e && e <= t ? e : null;
            default:
                return null;
        }
    }

    /// <summary>The first occurrence strictly after <paramref name="t"/>, or null if there is none.</summary>
    public static DateTimeOffset? After(TaskDefinition d, TaskState s, DateTimeOffset t, TimeZoneInfo z)
    {
        switch (d.Kind)
        {
            case ScheduleKind.Interval:
                {
                    var i = new IntervalGrid(d, s, z);
                    long k = i.IndexAtOrBefore(t);
                    return k == long.MaxValue ? null : i.Occurrence(k + 1);
                }
            case ScheduleKind.Weekly:
                return WeeklyAfter(d, t, z);
            case ScheduleKind.Once:
                {
                    var o = TimeMath.ToInstant(d.At, z);
                    return o > t ? o : null;
                }
            case ScheduleKind.Countdown:
                return s.CountdownEndsUtc is { } e && e > t ? e : null;
            default:
                return null;
        }
    }

    /// <summary>How many occurrences fall in (<paramref name="after"/>, <paramref name="upTo"/>].
    /// Weekly schedules are counted one by one and stop at <paramref name="cap"/>.</summary>
    public static long Count(TaskDefinition d, TaskState s, DateTimeOffset after, DateTimeOffset upTo,
        TimeZoneInfo z, long cap = 1_000_000)
    {
        if (upTo <= after) return 0;
        if (d.Kind == ScheduleKind.Interval)
        {
            var i = new IntervalGrid(d, s, z);
            // Arithmetic, so no cap is needed.
            return Math.Max(0, i.IndexAtOrBefore(upTo) - i.IndexAtOrBefore(after));
        }
        long count = 0;
        var cursor = after;
        while (count < cap && After(d, s, cursor, z) is { } next && next <= upTo)
        {
            count++;
            cursor = next;
        }
        return count;
    }

    // ---- Weekly --------------------------------------------------------------------

    private static DateTimeOffset? WeeklyAtOrBefore(TaskDefinition d, DateTimeOffset t, TimeZoneInfo z)
    {
        if ((d.Days & 0x7F) == 0) return null;
        var today = TimeMath.ToWall(t, z).Date;
        // Eight days back always reaches the same weekday of the previous week.
        for (int back = 0; back <= 8; back++)
        {
            var day = today.AddDays(-back);
            if (!d.HasDay(day.DayOfWeek)) continue;
            var o = TimeMath.ToInstant(day + d.TimeOfDay.ToTimeSpan(), z);
            if (o <= t) return o;
        }
        return null;
    }

    private static DateTimeOffset? WeeklyAfter(TaskDefinition d, DateTimeOffset t, TimeZoneInfo z)
    {
        if ((d.Days & 0x7F) == 0) return null;
        var today = TimeMath.ToWall(t, z).Date;
        for (int fwd = -1; fwd <= 8; fwd++)
        {
            var day = today.AddDays(fwd);
            if (!d.HasDay(day.DayOfWeek)) continue;
            var o = TimeMath.ToInstant(day + d.TimeOfDay.ToTimeSpan(), z);
            if (o > t) return o;
        }
        return null;
    }

    // ---- Interval ------------------------------------------------------------------

    /// <summary>The occurrences anchor + k·N·unit for k ≥ 0, addressed by index k.</summary>
    private readonly struct IntervalGrid
    {
        private readonly DateTime _anchor;
        private readonly DateTimeOffset _anchorInstant;
        private readonly int _n;
        private readonly IntervalUnit _unit;
        private readonly TimeZoneInfo _z;

        public IntervalGrid(TaskDefinition d, TaskState s, TimeZoneInfo z)
        {
            _anchor = AnchorOf(d, s);
            _anchorInstant = TimeMath.ToInstant(_anchor, z);
            _n = Math.Max(1, d.IntervalCount);
            _unit = d.Unit;
            _z = z;
        }

        private bool IsFixedLength => _unit is IntervalUnit.Seconds or IntervalUnit.Minutes or IntervalUnit.Hours;

        private long StepTicks => _unit switch
        {
            IntervalUnit.Seconds => TimeSpan.TicksPerSecond,
            IntervalUnit.Minutes => TimeSpan.TicksPerMinute,
            _ => TimeSpan.TicksPerHour,
        } * _n;

        /// <summary>Occurrence k, or null when it lies beyond the representable range.</summary>
        public DateTimeOffset? Occurrence(long k)
        {
            if (k < 0) return null;
            if (IsFixedLength)
            {
                Int128 ticks = (Int128)_anchorInstant.UtcTicks + (Int128)k * StepTicks;
                if (ticks > DateTimeOffset.MaxValue.UtcTicks) return null;
                return new DateTimeOffset((long)ticks, TimeSpan.Zero);
            }
            var wall = AddUnits(_anchor, k);
            return wall is { } w ? TimeMath.ToInstant(w, _z) : null;
        }

        private DateTime? AddUnits(DateTime from, long k)
        {
            try
            {
                long amount = checked(k * _n);
                return _unit switch
                {
                    IntervalUnit.Days => amount > 4_000_000 ? null : from.AddDays(amount),
                    IntervalUnit.Weeks => amount > 600_000 ? null : from.AddDays(amount * 7),
                    IntervalUnit.Months => amount > 120_000 ? null : from.AddMonths((int)amount),
                    IntervalUnit.Years => amount > 10_000 ? null : from.AddYears((int)amount),
                    _ => null,
                };
            }
            catch (Exception e) when (e is ArgumentOutOfRangeException or OverflowException)
            {
                return null;
            }
        }

        /// <summary>The largest k with occurrence k ≤ t; −1 if t precedes the anchor.</summary>
        public long IndexAtOrBefore(DateTimeOffset t)
        {
            if (t < _anchorInstant) return -1;
            if (IsFixedLength)
                return (long)(((Int128)t.UtcTicks - _anchorInstant.UtcTicks) / StepTicks);

            // Estimate from the wall-clock distance, then correct; the estimate is off by
            // at most one or two steps (month lengths, DST shifts).
            var wall = TimeMath.ToWall(t, _z);
            long est = _unit switch
            {
                IntervalUnit.Days => (long)Math.Floor((wall - _anchor).TotalDays / _n),
                IntervalUnit.Weeks => (long)Math.Floor((wall - _anchor).TotalDays / (7.0 * _n)),
                IntervalUnit.Months => MonthsBetween(_anchor, wall) / _n,
                _ => (wall.Year - _anchor.Year) / _n,
            };
            long k = Math.Max(0, est);
            while (k > 0 && (Occurrence(k) is not { } o || o > t)) k--;
            while (Occurrence(k + 1) is { } next && next <= t) k++;
            return k;
        }

        private static long MonthsBetween(DateTime a, DateTime b) =>
            (b.Year - a.Year) * 12L + (b.Month - a.Month);
    }
}
