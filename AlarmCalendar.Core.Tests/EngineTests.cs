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
    public void ASnoozePresetRemindsAfterItsTimeForThisOnce()
    {
        var t = EveryHourFromNow(RestartMode.OnSchedule, reminderMinutes: 0);   // "no further reminders"
        _clock.UtcNow = NY(2026, 6, 1, 10, 0, 0);
        _store.Dismiss(t.Id, TimeSpan.FromMinutes(5));
        var st = Status(t.Id);
        Assert.True(st.Snoozed);
        Assert.Equal(NY(2026, 6, 1, 10, 5), st.NextReminder);
        Assert.Equal(0, _store.Get(t.Id)!.Def.ReminderMinutes);   // the task's own interval is untouched
        Assert.Equal(["5 min", "10 min", "30 min", "1 h"], AlarmEngine.SnoozePresets.Select(TextFormat.Short));
    }

    [Fact]
    public void VolumeAndFadeInFollowTheTaskOrTheDefault()
    {
        var s = new AppSettings { DefaultVolume = 80, DefaultFadeInSeconds = 30 };
        var d = new TaskDefinition();
        Assert.Equal(0.8, s.VolumeFor(d));
        Assert.Equal(TimeSpan.FromSeconds(30), s.FadeInFor(d));
        Assert.Equal(0.5, s.VolumeFor(d with { Volume = 50 }));
        Assert.Equal(TimeSpan.Zero, s.FadeInFor(d with { FadeInSeconds = 0 }));

        var fade = TimeSpan.FromSeconds(60);
        Assert.Equal(0.5 * SoundLevels.FadeFloor, SoundLevels.FadeLevel(0.5, TimeSpan.Zero, fade));   // quiet, not silent
        Assert.Equal(0.125, SoundLevels.FadeLevel(0.5, TimeSpan.FromSeconds(30), fade), 6);           // a quarter of the way at half time
        Assert.Equal(0.5, SoundLevels.FadeLevel(0.5, TimeSpan.FromSeconds(90), fade));
        Assert.Equal(0.5, SoundLevels.FadeLevel(0.5, TimeSpan.FromSeconds(5), TimeSpan.Zero));
    }

    [Fact]
    public void APausedTaskStopsRingingAndPassesOverWhatCameUpMeanwhile()
    {
        var t = EveryHourFromNow(RestartMode.OnSchedule);
        _clock.UtcNow = NY(2026, 6, 1, 10, 0, 5);
        Assert.True(Status(t.Id).ShouldAlert);

        _store.Pause(t.Id);
        var st = Status(t.Id);
        Assert.True(st.Paused);
        Assert.False(st.ShouldAlert);
        Assert.Equal("Paused", TextFormat.Status(st, _clock.UtcNow, NewYork));
        _clock.UtcNow = NY(2026, 6, 1, 13, 30);
        Assert.False(Status(t.Id).Triggered);
        Assert.Null(AlarmEngine.NextWakeUp(_store.Live(), _clock.UtcNow, NewYork));

        _store.Resume(t.Id);
        st = Status(t.Id);
        Assert.False(st.Triggered);   // 10:00 to 13:00 passed over
        Assert.Equal(NY(2026, 6, 1, 14), st.NextOccurrence);
    }

    [Fact]
    public void APausedCountdownKeepsTheTimeItHadLeft()
    {
        var t = _store.Save(null, new TaskDefinition { Kind = ScheduleKind.Countdown, Duration = TimeSpan.FromMinutes(25) });
        _clock.UtcNow = NY(2026, 6, 1, 9, 10);
        _store.Pause(t.Id);
        var st = Status(t.Id);
        Assert.Equal(TimeSpan.FromMinutes(15), st.CountdownLeft);
        Assert.Equal("Paused, 15:00 left", TextFormat.Status(st, _clock.UtcNow, NewYork));

        _clock.UtcNow = NY(2026, 6, 1, 11, 0);   // paused for 1 h 50 min
        _store.Resume(t.Id);
        Assert.Equal(NY(2026, 6, 1, 11, 15), Status(t.Id).NextOccurrence);
        _clock.UtcNow = NY(2026, 6, 1, 11, 15, 1);
        Assert.True(Status(t.Id).ShouldAlert);
    }

    [Fact]
    public void EditingAPausedTaskKeepsItPausedButRestartingACountdownRunsIt()
    {
        var t = _store.Save(null, new TaskDefinition { Kind = ScheduleKind.Countdown, Duration = TimeSpan.FromMinutes(25) });
        _store.Pause(t.Id);
        _store.Save(t.Id, t.Def with { Duration = TimeSpan.FromMinutes(5) });
        var st = Status(t.Id);
        Assert.True(st.Paused);
        Assert.Equal(TimeSpan.FromMinutes(5), st.CountdownLeft);

        _store.Save(t.Id, _store.Get(t.Id)!.Def, restartCountdown: true);
        st = Status(t.Id);
        Assert.False(st.Paused);
        Assert.Equal(NY(2026, 6, 1, 9, 5), st.NextOccurrence);
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
    public void ADoneThroughLeftInTheFutureByAWrongClockIsClearedBySavingTheSchedule()
    {
        _clock.UtcNow = NY(2026, 7, 1, 9);   // the clock is a month ahead
        var t = EveryHourFromNow(RestartMode.OnSchedule);
        _clock.UtcNow = NY(2026, 6, 1, 9);   // fixed
        _store.Save(t.Id, _store.Get(t.Id)!.Def with { Start = Wall(_clock.UtcNow) });
        Assert.Equal(NY(2026, 6, 1, 10), Status(t.Id).NextOccurrence);

        // A peer still holding the old revision's future DoneThrough doesn't bring it back.
        var stale = t;
        var merged = MergeRules.Merge(_store.Get(t.Id)!, stale);
        Assert.Equal(_store.Get(t.Id)!.State, merged.State);
    }

    [Fact]
    public void SwitchingToTheOriginalScheduleCountsFromTheStartAgain()
    {
        var t = _store.Save(null, new TaskDefinition
        {
            Kind = ScheduleKind.Interval, Unit = IntervalUnit.Days, Start = new DateTime(2026, 6, 1, 9, 0, 0),
            Restart = RestartMode.FromDone,
        });
        _clock.UtcNow = NY(2026, 6, 2, 14, 37);
        _store.MarkDone(t.Id);
        Assert.Equal(NY(2026, 6, 3, 14, 37), Status(t.Id).NextOccurrence);
        _store.Edit(t.Id, d => d with { Restart = RestartMode.OnSchedule });
        Assert.Equal(NY(2026, 6, 3, 9), Status(t.Id).NextOccurrence);
    }

    [Fact]
    public void MarkingDoneInTheRepeatedHourCountsFromTheRightInstant()
    {
        // 2026-11-01 01:30 happens twice in New York; mark done during the second (EST).
        var secondPass = new DateTimeOffset(2026, 11, 1, 1, 30, 0, TimeSpan.FromHours(-5));
        _clock.UtcNow = secondPass - TimeSpan.FromHours(3);
        var t = _store.Save(null, new TaskDefinition
        {
            Kind = ScheduleKind.Interval, IntervalCount = 2, Unit = IntervalUnit.Hours,
            Start = Wall(_clock.UtcNow), Restart = RestartMode.FromDone,
        });
        _clock.UtcNow = secondPass;
        _store.MarkDone(t.Id);
        Assert.Equal(secondPass + TimeSpan.FromHours(2), Status(t.Id).NextOccurrence);
    }

    [Fact]
    public void ANewScheduleOutranksAStateFromAHigherRevisionThanItsDefinition()
    {
        var t = EveryHourFromNow(RestartMode.FromDone);
        // As after a merge that kept an older definition but a newer state.
        var skewed = t with { State = t.State with { Revision = 5 } };
        var store = new TaskStore(null, "b", _clock);
        store.Merge([skewed]);
        var saved = store.Save(t.Id, t.Def with { IntervalCount = 3 });
        Assert.Equal(6, saved.Def.ScheduleRevision);
        Assert.Equal(6, saved.State.Revision);
    }

    [Fact]
    public void AnAlarmThatStartedTheProcessIsNotMissed()
    {
        var t = EveryHourFromNow(RestartMode.FromDone);
        _clock.UtcNow = NY(2026, 6, 1, 10, 0, 1);
        var st = Status(t.Id);
        // On Android the alarm starts the process a moment after it is due: on time.
        Assert.False(AlarmEngine.MissedWhileNotRunning(st, NY(2026, 6, 1, 10, 0, 0) + TimeSpan.FromMilliseconds(400)));
        // Due well before the process started: missed while it wasn't running.
        Assert.True(AlarmEngine.MissedWhileNotRunning(st, NY(2026, 6, 1, 10, 5)));
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

    [Theory]
    [InlineData("en-US", "9:30 PM", 21, 30)]
    [InlineData("en-US", "9", 9, 0)]
    [InlineData("en-US", "21.15", 21, 15)]
    [InlineData("en-CA", "9:00 a.m.", 9, 0)]
    [InlineData("de-DE", "07:45", 7, 45)]
    [InlineData("ja-JP", "９:３０", 9, 30)]
    public void TimesReadInEachCulture(string culture, string text, int h, int m)
    {
        Assert.True(FieldParsing.TryTime(text, out var t, System.Globalization.CultureInfo.GetCultureInfo(culture)));
        Assert.Equal(new TimeOnly(h, m), t);
    }

    [Theory]
    [InlineData("en-CA")]
    [InlineData("ko-KR")]
    [InlineData("zh-TW")]
    [InlineData("ar-SA")]
    public void AFormattedTimeReadsBack(string culture)
    {
        var c = System.Globalization.CultureInfo.GetCultureInfo(culture);
        var t = new TimeOnly(21, 5);
        Assert.True(FieldParsing.TryTime(FieldParsing.FormatTime(t, c), out var back, c));
        Assert.Equal(t, back);
    }

    [Fact]
    public void BadTimesAreRefused()
    {
        var c = System.Globalization.CultureInfo.GetCultureInfo("en-US");
        Assert.False(FieldParsing.TryTime("25:00", out _, c));
        Assert.False(FieldParsing.TryTime("13 pm", out _, c));
        Assert.False(FieldParsing.TryTime("1/2/2026", out _, c));
        Assert.False(FieldParsing.TryTime("soon", out _, c));
    }

    [Fact]
    public void SnippetTakesTheFirstLine()
    {
        Assert.Equal("Buy milk …", TextFormat.Snippet("Buy milk\nand eggs"));
        Assert.Equal("abc…", TextFormat.Snippet("abcdefgh", 4));
    }
}
