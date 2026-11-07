using System.Globalization;
using static AlarmCalendar.Tests.Zones;

namespace AlarmCalendar.Tests;

public class CalendarTests
{
    private static readonly CultureInfo Us = CultureInfo.GetCultureInfo("en-US");   // weeks start on Sunday
    private readonly FakeClock _clock = new(NY(2026, 6, 10, 12, 0, 0), NewYork);
    private readonly TaskStore _store;

    public CalendarTests() => _store = new TaskStore(null, "node-a", _clock);

    private IReadOnlyList<CalendarDay> June() => CalendarMonth.Build(_store.Live(), 2026, 6, _clock.UtcNow, NewYork, Us);

    private static CalendarDay Day(IReadOnlyList<CalendarDay> days, int month, int day) =>
        days.Single(x => x.Date == new DateOnly(2026, month, day));

    [Fact]
    public void TheGridIsSixWeeksFromTheCulturesFirstDay()
    {
        var days = June();
        Assert.Equal(42, days.Count);
        Assert.Equal(new DateOnly(2026, 5, 31), days[0].Date);   // June 1 2026 is a Monday
        Assert.False(days[0].InMonth);
        Assert.True(Day(days, 6, 10).IsToday);
        Assert.True(Day(days, 6, 9).Past);
        var monday = CalendarMonth.Build([], 2026, 6, _clock.UtcNow, NewYork, CultureInfo.GetCultureInfo("en-GB"));
        Assert.Equal(new DateOnly(2026, 6, 1), monday[0].Date);
    }

    [Fact]
    public void EachTaskShowsOnItsDays()
    {
        var weekly = _store.Save(null, new TaskDefinition { Description = "gym", Kind = ScheduleKind.Weekly, TimeOfDay = new TimeOnly(18, 0) }
            .WithDay(DayOfWeek.Tuesday, true));
        _store.Save(null, new TaskDefinition { Description = "dentist", Kind = ScheduleKind.Once, At = new DateTime(2026, 6, 20, 9, 30, 0) });
        var days = June();
        var tue = Day(days, 6, 16).Entries.Single();
        Assert.Equal(("6:00 PM", "gym", weekly.Id), (tue.Text, tue.Title, tue.TaskId));
        // Every Tuesday of the grid from now on, not the ones before the task existed.
        Assert.Equal(4, days.Count(x => x.Entries.Any(e => e.Title == "gym")));
        Assert.Empty(Day(days, 6, 9).Entries);
        Assert.Equal("9:30 AM", Day(days, 6, 20).Entries.Single().Text);
    }

    [Fact]
    public void WhatCameUpAndWaitsShowsUntilMarkedDone()
    {
        var t = _store.Save(null, new TaskDefinition
        {
            Description = "pills", Kind = ScheduleKind.Interval, Unit = IntervalUnit.Days, Start = new DateTime(2026, 6, 10, 20, 0, 0),
            Restart = RestartMode.OnSchedule,
        });
        _clock.UtcNow = NY(2026, 6, 12, 8);   // Jun 10 and Jun 11 at 20:00 have come up
        var days = June();
        Assert.True(Day(days, 6, 10).Entries.Single().Waiting);
        Assert.True(Day(days, 6, 11).Entries.Single().Waiting);
        Assert.False(Day(days, 6, 12).Entries.Single().Waiting);
        _store.MarkDone(t.Id);
        days = June();
        Assert.Empty(Day(days, 6, 10).Entries);
        Assert.Empty(Day(days, 6, 11).Entries);
        Assert.Single(Day(days, 6, 12).Entries);
    }

    [Fact]
    public void SeveralTimesADayIsOneLineADay()
    {
        _store.Save(null, new TaskDefinition
        {
            Description = "drink water", Kind = ScheduleKind.Interval, IntervalCount = 15, Unit = IntervalUnit.Minutes,
            Start = new DateTime(2026, 6, 1, 8, 0, 0), Restart = RestartMode.OnSchedule,
        });
        var days = June();
        var line = Day(days, 6, 12).Entries.Single();
        Assert.True(line.Repeating);
        Assert.Equal("every 15 min", line.Text);
        Assert.Empty(Day(days, 5, 31).Entries);   // before it started
    }

    [Fact]
    public void ARestartWhenDoneTaskShowsItsLaterTimesAsEstimates()
    {
        var t = _store.Save(null, new TaskDefinition
        {
            Description = "water plants", Kind = ScheduleKind.Interval, IntervalCount = 3, Unit = IntervalUnit.Days,
            Start = new DateTime(2026, 6, 10, 12, 0, 0), Restart = RestartMode.FromDone,
        });
        var days = June();
        var next = Day(days, 6, 13).Entries.Single();
        Assert.False(next.Estimated);
        Assert.Equal("12:00 PM", next.Text);
        Assert.True(Day(days, 6, 16).Entries.Single().Estimated);
        Assert.StartsWith("~", Day(days, 6, 16).Entries.Single().Text);

        _store.Pause(t.Id);
        Assert.All(June(), d => Assert.Empty(d.Entries));   // paused: it won't ring
    }

    [Fact]
    public void ARestartWhenDoneTaskThatCameUpWaitsOnceAndEstimatesFromNow()
    {
        _store.Save(null, new TaskDefinition
        {
            Description = "water plants", Kind = ScheduleKind.Interval, IntervalCount = 3, Unit = IntervalUnit.Days,
            Start = new DateTime(2026, 6, 10, 12, 0, 0), Restart = RestartMode.FromDone,
        });
        _clock.UtcNow = NY(2026, 6, 18, 8);   // came up on Jun 13 and has waited since: its timer stopped
        var days = June();
        Assert.True(Day(days, 6, 13).Entries.Single().Waiting);
        Assert.Empty(Day(days, 6, 16).Entries);   // no second time came up
        Assert.Empty(Day(days, 6, 19).Entries);
        var estimate = Day(days, 6, 21).Entries.Single();   // three days after now, if marked done now
        Assert.Equal(("~8:00 AM", true, false), (estimate.Text, estimate.Estimated, estimate.Waiting));
    }

    [Fact]
    public void ARestartWhenDoneTaskSeveralTimesADayWaitsOnItsOwnDayOnly()
    {
        _store.Save(null, new TaskDefinition
        {
            Description = "stretch", Kind = ScheduleKind.Interval, IntervalCount = 15, Unit = IntervalUnit.Minutes,
            Start = new DateTime(2026, 6, 10, 12, 0, 0), Restart = RestartMode.FromDone,
        });
        _clock.UtcNow = NY(2026, 6, 12, 8);   // came up at 12:15 on Jun 10
        var days = June();
        var waiting = Day(days, 6, 10).Entries.Single();
        Assert.True(waiting.Waiting);
        Assert.Equal(NY(2026, 6, 10, 12, 15), waiting.At);
        Assert.Empty(Day(days, 6, 11).Entries);
        var today = Day(days, 6, 12).Entries.Single();
        Assert.False(today.Waiting);
        Assert.True(today.Estimated);
        Assert.Equal(NY(2026, 6, 12, 8, 15), today.At);
        // Estimates reckoned from now don't count as a change to wait for, or an open
        // calendar would rebuild every 15 minutes (or every second, for a task of seconds).
        Assert.Null(CalendarMonth.NextChange(days, _clock.UtcNow));
    }

    [Fact]
    public void OnlyTheNextTimeOfARestartWhenDoneTaskIsExact()
    {
        _store.Save(null, new TaskDefinition
        {
            Description = "stretch", Kind = ScheduleKind.Interval, IntervalCount = 15, Unit = IntervalUnit.Minutes,
            Start = new DateTime(2026, 6, 10, 12, 0, 0), Restart = RestartMode.FromDone,
        });
        var days = June();
        var today = Day(days, 6, 10).Entries.Single();
        Assert.Equal(NY(2026, 6, 10, 12, 15), today.At);
        Assert.False(today.Estimated);   // its next time, known exactly
        Assert.True(Day(days, 6, 11).Entries.Single().Estimated);
        Assert.Equal(NY(2026, 6, 10, 12, 15), CalendarMonth.NextChange(days, _clock.UtcNow));
    }

    [Fact]
    public void AnOpenCalendarChecksJustAfterTheNextChangeAndAtLeastEveryMinute()
    {
        var now = _clock.UtcNow;
        Assert.Equal(TimeSpan.FromSeconds(10.2), CalendarMonth.TickDelay(now.AddSeconds(10), now));
        Assert.Equal(TimeSpan.FromMinutes(1), CalendarMonth.TickDelay(now.AddHours(3), now));
        Assert.Equal(TimeSpan.FromMinutes(1), CalendarMonth.TickDelay(null, now));
        Assert.Equal(TimeSpan.FromMilliseconds(200), CalendarMonth.TickDelay(now.AddSeconds(-5), now));

        var today = new DateTime(2026, 6, 10);
        Assert.False(CalendarMonth.Stale(today, now.AddMinutes(5), now, NewYork));
        Assert.True(CalendarMonth.Stale(today, now.AddMinutes(5), now.AddMinutes(5), NewYork));
        Assert.True(CalendarMonth.Stale(today, null, NY(2026, 6, 11, 0, 0, 1), NewYork));   // the date changed
        Assert.False(CalendarMonth.Stale(today, null, now, NewYork));
    }

    [Fact]
    public void TheNextChangeIsTheFirstTimeStillToCome()
    {
        _store.Save(null, new TaskDefinition { Description = "dentist", Kind = ScheduleKind.Once, At = new DateTime(2026, 6, 20, 9, 30, 0) });
        Assert.Equal(NY(2026, 6, 20, 9, 30), CalendarMonth.NextChange(June(), _clock.UtcNow));
        _clock.UtcNow = NY(2026, 6, 20, 9, 31);
        var days = June();
        Assert.True(Day(days, 6, 20).Entries.Single().Waiting);
        Assert.Null(CalendarMonth.NextChange(days, _clock.UtcNow));
    }
}
