using static AlarmCalendar.Tests.Zones;

namespace AlarmCalendar.Tests;

public class TimeMathTests
{
    [Fact]
    public void SkippedTimeMovesForwardByTheGap()
    {
        // 2026-03-08 02:30 doesn't exist in New York; the clocks jump 02:00 → 03:00.
        var t = TimeMath.ToInstant(new DateTime(2026, 3, 8, 2, 30, 0), NewYork);
        Assert.Equal(new DateTime(2026, 3, 8, 3, 30, 0), Wall(t));
        Assert.Equal(TimeSpan.FromHours(-4), t.Offset);
    }

    [Fact]
    public void RepeatedTimeResolvesToItsFirstOccurrence()
    {
        // 2026-11-01 01:30 happens twice: first at -04:00 (EDT), then at -05:00 (EST).
        var t = TimeMath.ToInstant(new DateTime(2026, 11, 1, 1, 30, 0), NewYork);
        Assert.Equal(TimeSpan.FromHours(-4), t.Offset);
    }
}

public class OccurrenceTests
{
    private static readonly TaskState NoState = new();

    private static TaskDefinition Every(int n, IntervalUnit unit, DateTime start) =>
        new() { Kind = ScheduleKind.Interval, IntervalCount = n, Unit = unit, Start = start };

    [Fact]
    public void FixedUnitsCountAbsoluteTime()
    {
        var d = Every(2, IntervalUnit.Hours, new DateTime(2026, 6, 1, 9, 0, 0));
        var t = NY(2026, 6, 1, 14, 30);
        Assert.Equal(NY(2026, 6, 1, 13), Occurrences.AtOrBefore(d, NoState, t, NewYork));
        Assert.Equal(NY(2026, 6, 1, 15), Occurrences.After(d, NoState, t, NewYork));
        Assert.Equal(3, Occurrences.Count(d, NoState, NY(2026, 6, 1, 8), t, NewYork)); // 9, 11, 13
    }

    [Fact]
    public void NothingBeforeTheStart()
    {
        var d = Every(1, IntervalUnit.Days, new DateTime(2026, 6, 10, 9, 0, 0));
        Assert.Null(Occurrences.AtOrBefore(d, NoState, NY(2026, 6, 1), NewYork));
        Assert.Equal(NY(2026, 6, 10, 9), Occurrences.After(d, NoState, NY(2026, 6, 1), NewYork));
    }

    [Fact]
    public void EightHoursStaysEightHoursAcrossDst()
    {
        var d = Every(8, IntervalUnit.Hours, new DateTime(2026, 3, 7, 20, 0, 0));
        var next = Occurrences.After(d, NoState, NY(2026, 3, 7, 21), NewYork)!.Value;
        // 20:00 EST + 8h = 05:00 EDT on the 8th (wall-clock 05:00, not 04:00).
        Assert.Equal(new DateTime(2026, 3, 8, 5, 0, 0), Wall(next));
        Assert.Equal(TimeSpan.FromHours(8), next - NY(2026, 3, 7, 20));
    }

    [Fact]
    public void DailyKeepsTheWallClockTimeAcrossDst()
    {
        var d = Every(1, IntervalUnit.Days, new DateTime(2026, 3, 6, 9, 0, 0));
        var after = Occurrences.After(d, NoState, NY(2026, 3, 8, 1), NewYork)!.Value;
        Assert.Equal(new DateTime(2026, 3, 8, 9, 0, 0), Wall(after));
        var autumn = Every(1, IntervalUnit.Days, new DateTime(2026, 10, 30, 9, 0, 0));
        Assert.Equal(new DateTime(2026, 11, 2, 9, 0, 0), Wall(Occurrences.After(autumn, NoState, NY(2026, 11, 1, 12), NewYork)!.Value));
    }

    [Fact]
    public void MonthlyFromThe31stDoesNotDrift()
    {
        var d = Every(1, IntervalUnit.Months, new DateTime(2026, 1, 31, 9, 0, 0));
        var feb = Occurrences.After(d, NoState, NY(2026, 2, 1), NewYork)!.Value;
        Assert.Equal(new DateTime(2026, 2, 28, 9, 0, 0), Wall(feb));
        var mar = Occurrences.After(d, NoState, feb, NewYork)!.Value;
        Assert.Equal(new DateTime(2026, 3, 31, 9, 0, 0), Wall(mar));
        Assert.Equal(feb, Occurrences.AtOrBefore(d, NoState, NY(2026, 3, 30), NewYork));
    }

    [Fact]
    public void YearlyAndWeeklyIntervals()
    {
        var y = Every(1, IntervalUnit.Years, new DateTime(2024, 2, 29, 8, 0, 0));
        Assert.Equal(new DateTime(2027, 2, 28, 8, 0, 0), Wall(Occurrences.After(y, NoState, NY(2026, 6, 1), NewYork)!.Value));
        var w = Every(2, IntervalUnit.Weeks, new DateTime(2026, 6, 1, 9, 0, 0)); // a Monday
        Assert.Equal(new DateTime(2026, 6, 15, 9, 0, 0), Wall(Occurrences.After(w, NoState, NY(2026, 6, 2), NewYork)!.Value));
        Assert.Equal(new DateTime(2026, 6, 15, 9, 0, 0), Wall(Occurrences.AtOrBefore(w, NoState, NY(2026, 6, 28), NewYork)!.Value));
    }

    [Fact]
    public void EverySecondForAYearIsCountedWithoutIterating()
    {
        var d = Every(1, IntervalUnit.Seconds, new DateTime(2025, 1, 1, 0, 0, 0));
        long n = Occurrences.Count(d, NoState, NY(2025, 1, 1), NY(2026, 1, 1), NewYork);
        Assert.Equal(365L * 24 * 3600, n);
    }

    [Fact]
    public void AnchorReplacesStart()
    {
        var d = Every(3, IntervalUnit.Days, new DateTime(2026, 6, 1, 9, 0, 0));
        var s = new TaskState { Anchor = new DateTime(2026, 6, 5, 14, 12, 0) };
        Assert.Equal(new DateTime(2026, 6, 8, 14, 12, 0), Wall(Occurrences.After(d, s, NY(2026, 6, 6), NewYork)!.Value));
    }

    [Fact]
    public void WeeklyPicksTheChosenDays()
    {
        var d = new TaskDefinition { Kind = ScheduleKind.Weekly, TimeOfDay = new TimeOnly(9, 0) }
            .WithDay(DayOfWeek.Monday, true).WithDay(DayOfWeek.Friday, true);
        // 2026-06-03 is a Wednesday.
        Assert.Equal(NY(2026, 6, 5, 9), Occurrences.After(d, NoState, NY(2026, 6, 3, 12), NewYork));
        Assert.Equal(NY(2026, 6, 1, 9), Occurrences.AtOrBefore(d, NoState, NY(2026, 6, 3, 12), NewYork));
        Assert.Equal(NY(2026, 6, 8, 9), Occurrences.After(d, NoState, NY(2026, 6, 5, 9), NewYork));
        Assert.Equal(4, Occurrences.Count(d, NoState, NY(2026, 6, 1), NY(2026, 6, 13), NewYork));
        Assert.Null(Occurrences.After(d with { Days = 0 }, NoState, NY(2026, 6, 3), NewYork));
    }

    [Fact]
    public void OnceAndCountdown()
    {
        var once = new TaskDefinition { Kind = ScheduleKind.Once, At = new DateTime(2026, 6, 3, 15, 0, 0) };
        Assert.Equal(NY(2026, 6, 3, 15), Occurrences.After(once, NoState, NY(2026, 6, 3), NewYork));
        Assert.Null(Occurrences.After(once, NoState, NY(2026, 6, 3, 15), NewYork));
        var cd = new TaskDefinition { Kind = ScheduleKind.Countdown, Duration = TimeSpan.FromMinutes(5) };
        Assert.Null(Occurrences.After(cd, NoState, NY(2026, 6, 3), NewYork));
        var running = new TaskState { CountdownEndsUtc = NY(2026, 6, 3, 10, 5) };
        Assert.Equal(NY(2026, 6, 3, 10, 5), Occurrences.After(cd, running, NY(2026, 6, 3, 10), NewYork));
    }
}
