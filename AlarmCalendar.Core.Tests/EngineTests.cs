using static AlarmCalendar.Tests.Zones;

namespace AlarmCalendar.Tests;

public class EngineTests
{
    private readonly FakeClock _clock = new(NY(2026, 6, 1, 9, 0, 0), NewYork);
    private readonly TaskStore _store;

    public EngineTests() => _store = new TaskStore(null, "node-a", _clock);

    private TaskStatus Status(Guid id) => _store.Status(id)!;

    private AlarmTask EveryHourFromNow(RestartMode mode, int reminderMinutes = 10) =>
        _store.Save(null, new TaskDefinition
        {
            Description = "stretch",
            Kind = ScheduleKind.Interval,
            IntervalCount = 1,
            Unit = IntervalUnit.Hours,
            Start = Wall(_clock.UtcNow),
            Restart = mode,
            ReminderMinutes = reminderMinutes,
        });

    [Fact]
    public void StartingNowMeansTheFirstAlarmIsOneIntervalAway()
    {
        var t = EveryHourFromNow(RestartMode.FromDone);
        var st = Status(t.Id);
        Assert.False(st.Triggered);
        Assert.Equal(NY(2026, 6, 1, 10), st.NextOccurrence);
    }

    [Fact]
    public void TriggersThenDismissSnoozesForTheReminderInterval()
    {
        var t = EveryHourFromNow(RestartMode.FromDone, reminderMinutes: 15);
        _clock.UtcNow = NY(2026, 6, 1, 10, 0, 1);
        var st = Status(t.Id);
        Assert.True(st.ShouldAlert);
        Assert.Equal(NY(2026, 6, 1, 10), st.Pending);
        Assert.Null(st.NextOccurrence); // FromDone: counted from when it is marked done

        _store.Dismiss(t.Id);
        st = Status(t.Id);
        Assert.True(st.Triggered);
        Assert.True(st.Snoozed);
        Assert.Equal(NY(2026, 6, 1, 10, 15, 1), st.NextReminder);

        _clock.UtcNow = NY(2026, 6, 1, 10, 15, 1);
        st = Status(t.Id);
        Assert.True(st.ShouldAlert);
        Assert.True(st.IsReminder);
        Assert.Equal(NY(2026, 6, 1, 10, 15, 1), st.AlertDue);
    }

    [Fact]
    public void FromDoneRestartsTheTimerWhenMarkedDone()
    {
        var t = EveryHourFromNow(RestartMode.FromDone);
        _clock.UtcNow = NY(2026, 6, 1, 10, 40);
        Assert.True(Status(t.Id).Triggered);
        _store.MarkDone(t.Id);
        var st = Status(t.Id);
        Assert.False(st.Triggered);
        Assert.Equal(NY(2026, 6, 1, 11, 40), st.NextOccurrence);
    }

    [Fact]
    public void OnScheduleKeepsTheGridAndCountsMissedOccurrences()
    {
        var t = EveryHourFromNow(RestartMode.OnSchedule);
        _clock.UtcNow = NY(2026, 6, 1, 12, 30);
        var st = Status(t.Id);
        Assert.Equal(NY(2026, 6, 1, 10), st.Pending);
        Assert.Equal(NY(2026, 6, 1, 12), st.Latest);
        Assert.Equal(3, st.PendingCount);
        Assert.Equal(NY(2026, 6, 1, 13), st.NextOccurrence);

        _store.MarkDone(t.Id);
        st = Status(t.Id);
        Assert.False(st.Triggered);
        Assert.Equal(NY(2026, 6, 1, 13), st.NextOccurrence);
    }

    [Fact]
    public void ANewOccurrenceEndsTheSnooze()
    {
        var t = EveryHourFromNow(RestartMode.OnSchedule, reminderMinutes: 120);
        _clock.UtcNow = NY(2026, 6, 1, 10, 5);
        _store.Dismiss(t.Id);
        _clock.UtcNow = NY(2026, 6, 1, 10, 30);
        var st = Status(t.Id);
        Assert.True(st.Snoozed);
        Assert.Equal(NY(2026, 6, 1, 11), st.NextReminder); // the next occurrence comes before 12:05

        _clock.UtcNow = NY(2026, 6, 1, 11, 0, 1);
        st = Status(t.Id);
        Assert.True(st.ShouldAlert);
        Assert.False(st.IsReminder);
    }

    [Fact]
    public void ZeroReminderMinutesMeansNoFurtherReminders()
    {
        var t = EveryHourFromNow(RestartMode.FromDone, reminderMinutes: 0);
        _clock.UtcNow = NY(2026, 6, 1, 10, 5);
        _store.Dismiss(t.Id);
        _clock.UtcNow = NY(2026, 6, 5);
        var st = Status(t.Id);
        Assert.True(st.Snoozed);
        Assert.Null(st.NextReminder);
        Assert.Contains("no further reminders", TextFormat.Status(st, _clock.UtcNow, NewYork));
    }

    [Fact]
    public void CountdownRunsOnceAndSurvivesAReload()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "tasks.json");
            var store = new TaskStore(path, "node-a", _clock);
            var t = store.Save(null, new TaskDefinition { Kind = ScheduleKind.Countdown, Duration = TimeSpan.FromMinutes(25) });

            var reloaded = new TaskStore(path, "node-a", _clock);   // "after a reboot"
            Assert.Equal(NY(2026, 6, 1, 9, 25), reloaded.Status(t.Id)!.NextOccurrence);
            _clock.UtcNow = NY(2026, 6, 1, 9, 26);
            Assert.True(reloaded.Status(t.Id)!.ShouldAlert);
            reloaded.MarkDone(t.Id);
            var st = reloaded.Status(t.Id)!;
            Assert.True(st.Finished);
            Assert.Equal("Done", TextFormat.Status(st, _clock.UtcNow, NewYork));
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public void EditingOnlyTheDescriptionKeepsTheTriggeredState()
    {
        var t = EveryHourFromNow(RestartMode.FromDone);
        _clock.UtcNow = NY(2026, 6, 1, 10, 1);
        _store.Save(t.Id, t.Def with { Description = "stretch more" });
        Assert.True(Status(t.Id).Triggered);

        _store.Save(t.Id, t.Def with { IntervalCount = 2 });
        var st = Status(t.Id);
        Assert.False(st.Triggered);
        Assert.Equal(2, _store.Get(t.Id)!.Def.ScheduleRevision);
    }

    [Fact]
    public void LateWarningNamesTheDelayAndTheReason()
    {
        var t = EveryHourFromNow(RestartMode.FromDone);
        var appStarted = NY(2026, 6, 3, 12, 0);
        _clock.UtcNow = NY(2026, 6, 3, 12, 0, 5);
        var st = Status(t.Id);
        Assert.True(AlarmEngine.Lateness(st, _clock.UtcNow) > TimeSpan.FromDays(2));
        var text = TextFormat.LateWarning(st.AlertDue!.Value, _clock.UtcNow, appStarted, NewYork);
        Assert.Equal("This reminder is late by 2 days, 2 hours, 5 seconds: the app wasn't running at the time.", text);
    }

    [Fact]
    public void NextWakeUpIsTheEarliestThingDue()
    {
        var a = EveryHourFromNow(RestartMode.FromDone);
        _store.Save(null, new TaskDefinition { Kind = ScheduleKind.Countdown, Duration = TimeSpan.FromMinutes(20) });
        Assert.Equal(NY(2026, 6, 1, 9, 20), AlarmEngine.NextWakeUp(_store.Live(), _clock.UtcNow, NewYork));
        _store.Delete(a.Id);
        Assert.Single(_store.Live());
    }
}

public class TextFormatTests
{
    [Theory]
    [InlineData("2026-01-31 09:00", "2026-03-02 10:30", "1 month, 2 days, 1 hour")]
    [InlineData("2024-02-29 00:00", "2026-03-01 00:00", "2 years, 1 day")]
    [InlineData("2026-06-01 09:00", "2026-06-16 09:00:42", "2 weeks, 1 day, 42 seconds")]
    [InlineData("2026-06-01 09:00", "2026-06-01 09:00", "less than a second")]
    public void SpanUsesCalendarUnits(string from, string to, string expected) =>
        Assert.Equal(expected, TextFormat.Span(DateTime.Parse(from), DateTime.Parse(to)));

    [Fact]
    public void HoursMinutesRoundsUp()
    {
        Assert.Equal("1:25", TextFormat.HoursMinutes(TimeSpan.FromMinutes(84.2)));
        Assert.Equal("26:00", TextFormat.HoursMinutes(TimeSpan.FromHours(26)));
        Assert.Equal("0:01", TextFormat.HoursMinutes(TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public void WeekdayNames()
    {
        var c = System.Globalization.CultureInfo.GetCultureInfo("en-US");
        Assert.Equal("Weekdays", TextFormat.Weekdays(0b0111110, c));
        Assert.Equal("Mon, Wed", TextFormat.Weekdays((1 << 1) | (1 << 3), c));
        Assert.Equal("Weekends", TextFormat.Weekdays(1 | (1 << 6), c));
        Assert.Equal("Sun, Tue", TextFormat.Weekdays(1 | (1 << 2), c));
    }

    [Fact]
    public void SnippetTakesTheFirstLine()
    {
        Assert.Equal("Buy milk …", TextFormat.Snippet("Buy milk\nand eggs"));
        Assert.Equal("abc…", TextFormat.Snippet("abcdefgh", 4));
    }
}
