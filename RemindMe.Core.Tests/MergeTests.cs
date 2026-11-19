using static RemindMe.Tests.Zones;

namespace RemindMe.Tests;

public class HlcTests
{
    [Fact]
    public void TicksIncreaseEvenWhenTheWallClockGoesBack()
    {
        long now = 1000;
        var c = new HlcClock("n", () => now, Hlc.Zero);
        var a = c.Tick();
        now = 500;
        var b = c.Tick();
        Assert.True(b > a);
    }

    [Fact]
    public void ObservingARemoteVersionOrdersLaterTicksAfterIt()
    {
        long now = 1000;
        var c = new HlcClock("a", () => now, Hlc.Zero);
        var remote = new Hlc(5000, 3, "b");
        c.Observe(remote);
        Assert.True(c.Tick() > remote);
    }
}

public class MergeTests
{
    private static (TaskStore a, TaskStore b, FakeClock ca, FakeClock cb) TwoDevices()
    {
        var ca = new FakeClock(NY(2026, 6, 1, 9), NewYork);
        var cb = new FakeClock(NY(2026, 6, 1, 9), NewYork);
        return (new TaskStore(null, "a", ca), new TaskStore(null, "b", cb), ca, cb);
    }

    /// <summary>Full two-way sync, as a session start does it.</summary>
    private static void Sync(TaskStore x, TaskStore y)
    {
        var toY = x.NewerThan(y.Digest());
        var toX = y.NewerThan(x.Digest());
        y.Merge(toY);
        x.Merge(toX);
    }

    private static TaskDefinition Hourly(FakeClock c) => new()
    {
        Description = "pills",
        Kind = ScheduleKind.Interval,
        Unit = IntervalUnit.Hours,
        Start = Wall(c.UtcNow),
        Restart = RestartMode.OnSchedule,
    };

    [Fact]
    public void ATaskCreatedOnOneDeviceAppearsOnTheOther()
    {
        var (a, b, ca, _) = TwoDevices();
        var t = a.Add(Hourly(ca));
        Sync(a, b);
        Assert.Equal(t, b.Get(t.Id));
        Assert.Empty(a.NewerThan(b.Digest()));
        Assert.Empty(b.NewerThan(a.Digest()));
    }

    [Fact]
    public void DoneOnOneDeviceBeatsTheOtherTriggeringLater()
    {
        // The phone (b) was off at 10:00; the PC (a) marked the task done at 10:05.
        // The phone comes back at 10:30 and, before syncing, sees the task triggered
        // and the user dismisses it there — a state write with a *newer* version.
        var (a, b, ca, cb) = TwoDevices();
        var t = a.Add(Hourly(ca));
        Sync(a, b);
        ca.UtcNow = NY(2026, 6, 1, 10, 5);
        a.MarkDone(t.Id);
        cb.UtcNow = NY(2026, 6, 1, 10, 30);
        Assert.True(b.Status(t.Id)!.Triggered);
        b.Dismiss(t.Id);

        Sync(a, b);
        Assert.False(b.Status(t.Id)!.Triggered);
        Assert.False(a.Status(t.Id)!.Triggered);
        Assert.Equal(a.Get(t.Id)!.State, b.Get(t.Id)!.State);
    }

    [Fact]
    public void DismissOnOneDeviceSnoozesTheOther()
    {
        var (a, b, ca, cb) = TwoDevices();
        var t = a.Add(Hourly(ca));
        Sync(a, b);
        ca.UtcNow = cb.UtcNow = NY(2026, 6, 1, 10, 1);
        Assert.True(b.Status(t.Id)!.ShouldAlert);
        a.Dismiss(t.Id);
        Sync(a, b);
        Assert.False(b.Status(t.Id)!.ShouldAlert);
        Assert.True(b.Status(t.Id)!.Snoozed);
    }

    [Fact]
    public void ConcurrentEditsConvergeOnTheLaterOne()
    {
        var (a, b, ca, cb) = TwoDevices();
        var t = a.Add(Hourly(ca));
        Sync(a, b);
        ca.Advance(TimeSpan.FromSeconds(1));
        a.Save(t.Id, t.Def with { Description = "from a" });
        cb.Advance(TimeSpan.FromSeconds(2));
        b.Save(t.Id, t.Def with { Description = "from b" });
        Sync(a, b);
        Assert.Equal("from b", a.Get(t.Id)!.Def.Description);
        Assert.Equal(a.Get(t.Id), b.Get(t.Id));
    }

    [Fact]
    public void ADeletionSyncs()
    {
        var (a, b, ca, _) = TwoDevices();
        var t = a.Add(Hourly(ca));
        Sync(a, b);
        b.Delete(t.Id);
        Sync(a, b);
        Assert.Empty(a.Live());
        Assert.True(a.Get(t.Id)!.Def.Deleted);
    }

    /// <summary>What <paramref name="from"/> holds that <paramref name="to"/> lacks, sent one way.</summary>
    private static void Send(TaskStore from, TaskStore to) => to.Merge(from.NewerThan(to.Digest()));

    /// <summary>The two halves of a sync, in either order.</summary>
    private static void Meet(TaskStore a, TaskStore b, bool aFirst)
    {
        if (aFirst) { Send(a, b); Send(b, a); }
        else { Send(b, a); Send(a, b); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ADeletionStandsWhenTheOtherDeviceMarksItDoneLater(bool deletionArrivesFirst)
    {
        // Deleted on one device while the other, out of touch, still rings it and has it
        // marked done afterwards: a state write newer than the deletion, which doesn't
        // bring the task back, whichever device hears of the other's change first.
        var (a, b, ca, cb) = TwoDevices();
        var t = a.Add(Hourly(ca));
        Sync(a, b);
        ca.UtcNow = NY(2026, 6, 1, 10, 1);
        a.Delete(t.Id);
        cb.UtcNow = NY(2026, 6, 1, 10, 30);
        Assert.True(b.Status(t.Id)!.ShouldAlert);
        var done = b.MarkDone(t.Id)!;
        Assert.True(done.StateVersion > a.Get(t.Id)!.DefVersion);
        Meet(a, b, deletionArrivesFirst);
        Assert.Empty(a.Live());
        Assert.Empty(b.Live());
        Assert.Equal(a.Get(t.Id), b.Get(t.Id));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ADeletionStandsAgainstALaterEditMadeBeforeItArrived(bool deletionArrivesFirst)
    {
        // As above, but on the other device's alarm screen the reminder interval is changed
        // too, and later the description in its editor: definitions newer than the
        // deletion, written without knowing of it.
        var (a, b, ca, cb) = TwoDevices();
        var t = a.Add(Hourly(ca));
        Sync(a, b);
        ca.UtcNow = NY(2026, 6, 1, 10, 1);
        a.Delete(t.Id);
        cb.UtcNow = NY(2026, 6, 1, 10, 30);
        Assert.NotNull(b.Edit(t.Id, d => d with { ReminderMinutes = 5 }));
        b.MarkDone(t.Id);
        cb.UtcNow = NY(2026, 6, 1, 10, 45);
        var edited = b.Save(t.Id, b.Get(t.Id)!.Def with { Description = "pills, with food" })!;
        Assert.True(edited.DefVersion > a.Get(t.Id)!.DefVersion);
        Meet(a, b, deletionArrivesFirst);
        Assert.Empty(a.Live());
        Assert.Empty(b.Live());
        Assert.Equal(a.Get(t.Id), b.Get(t.Id));
    }

    [Fact]
    public void SavingOrEditingADeletedTaskChangesNothing()
    {
        // An editor or alarm screen left open across a deletion, made here or arriving by
        // sync, mustn't undo it. Keeping the edits means adding them as a new task.
        var (a, _, ca, _) = TwoDevices();
        var t = a.Add(Hourly(ca));
        a.Delete(t.Id);
        var tombstone = a.Get(t.Id)!;
        ca.Advance(TimeSpan.FromMinutes(1));
        Assert.Null(a.Save(t.Id, t.Def with { Description = "pills, with food" }));
        Assert.Null(a.Edit(t.Id, d => d with { ReminderMinutes = 5 }));
        Assert.Equal(tombstone, a.Get(t.Id));
        var copy = a.Add(tombstone.Def with { Description = "pills, with food" });
        Assert.NotEqual(t.Id, copy.Id);
        Assert.Equal((false, 0, 1), (copy.Def.Deleted, copy.Def.Incarnation, copy.Def.ScheduleRevision));
    }

    [Fact]
    public void ARestoreOutranksTheDeletionAndEditsOfTheTaskAsItWas()
    {
        // Brought back by importing a backup: a new incarnation, which neither the deletion
        // nor an edit made elsewhere without knowing of either outranks. Deleting it again
        // does, and importing again brings it back again.
        var (a, b, ca, cb) = TwoDevices();
        var t = a.Add(Hourly(ca));
        Sync(a, b);
        var backup = a.Export();
        ca.UtcNow = NY(2026, 6, 1, 10, 1);
        a.Delete(t.Id);
        ca.UtcNow = NY(2026, 6, 1, 10, 5);
        Assert.Equal(new ImportResult(1, 0, 0), a.Import(backup));
        cb.UtcNow = NY(2026, 6, 1, 10, 30);
        Assert.NotNull(b.Save(t.Id, t.Def with { Description = "edited on b" }));
        Sync(a, b);
        var restored = a.Get(t.Id)!;
        Assert.Equal((false, 1, "pills"), (restored.Def.Deleted, restored.Def.Incarnation, restored.Def.Description));
        Assert.Equal(restored, b.Get(t.Id));

        cb.UtcNow = NY(2026, 6, 1, 10, 40);
        b.Delete(t.Id);
        Sync(a, b);
        Assert.Empty(a.Live());
        Assert.Empty(b.Live());

        ca.UtcNow = NY(2026, 6, 1, 10, 50);
        Assert.Equal(new ImportResult(1, 0, 0), a.Import(backup));
        Sync(a, b);
        Assert.Equal((false, 2), (b.Get(t.Id)!.Def.Deleted, b.Get(t.Id)!.Def.Incarnation));
        Assert.Equal(a.Get(t.Id), b.Get(t.Id));
    }

    [Fact]
    public void ARestoredTaskKeepsItsOwnStateAgainstALaterScheduleOfTheDeletedOne()
    {
        // The PC deletes a countdown. The phone, not yet told, makes it a one-time task (a
        // later schedule revision). Then the PC restores the countdown from a backup. The
        // countdown's own state must come back with it: paired with the one-time task's
        // state, a countdown has no end, and never rings.
        var (a, b, ca, cb) = TwoDevices();
        var t = a.Add(new TaskDefinition { Description = "tea", Kind = ScheduleKind.Countdown, Duration = TimeSpan.FromMinutes(30) });
        Sync(a, b);
        var backup = a.Export();
        ca.Advance(TimeSpan.FromMinutes(1));
        a.Delete(t.Id);
        cb.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(2, b.Save(t.Id, t.Def with { Kind = ScheduleKind.Once, At = Wall(cb.UtcNow).AddHours(1) })!.State.Revision);
        ca.Advance(TimeSpan.FromMinutes(3));
        Assert.Equal(new ImportResult(1, 0, 0), a.Import(backup));
        Sync(a, b);
        foreach (var s in new[] { a, b })
        {
            var x = s.Get(t.Id)!;
            Assert.Equal(ScheduleKind.Countdown, x.Def.Kind);
            Assert.NotNull(x.State.CountdownEndsUtc);
        }
        Assert.Equal(a.Get(t.Id), b.Get(t.Id));
    }

    [Fact]
    public void ImportingAFileOfALaterIncarnationMergesItAsItIs()
    {
        // Both devices deleted the task; A restored it, backed it up, and edited it since. C
        // imports A's backup before hearing of any of it. The file's copy already outranks C's
        // tombstone, so it merges as any record would, and A's later edit still wins.
        var (a, c, ca, cc) = TwoDevices();
        var t = a.Add(Hourly(ca));
        var early = a.Export();
        Sync(a, c);
        ca.Advance(TimeSpan.FromMinutes(1));
        a.Delete(t.Id);
        Sync(a, c);
        ca.Advance(TimeSpan.FromMinutes(1));
        a.Import(early);
        var restored = a.Export();
        ca.Advance(TimeSpan.FromMinutes(1));
        a.Save(t.Id, a.Get(t.Id)!.Def with { Description = "edited after" });
        cc.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(new ImportResult(1, 0, 0), c.Import(restored));
        Assert.Equal(1, c.Get(t.Id)!.Def.Incarnation);
        Sync(a, c);
        Assert.Equal("edited after", c.Get(t.Id)!.Def.Description);
        Assert.Equal(a.Get(t.Id), c.Get(t.Id));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AScheduleChangeAndAnEditOfTheRestMadeElsewhereBothStand(bool changeArrivesFirst)
    {
        // Out of touch, the PC turns a countdown into a one-time task (a new schedule), and
        // the phone, later, changes only the description and, on its alarm screen, the
        // reminder interval. The schedule merges with the state, the rest by version: both
        // edits stand, and the schedule and the state are of one schedule. (A countdown's
        // definition with the one-time task's state would have no end, and never ring.)
        var (a, b, ca, cb) = TwoDevices();
        var t = a.Add(new TaskDefinition { Description = "tea", Kind = ScheduleKind.Countdown, Duration = TimeSpan.FromHours(2) });
        Sync(a, b);
        ca.Advance(TimeSpan.FromMinutes(1));
        var once = a.Save(t.Id, t.Def with { Kind = ScheduleKind.Once, At = Wall(ca.UtcNow).AddHours(1) })!;
        cb.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(1, b.Save(t.Id, t.Def with { Description = "green tea" })!.Def.ScheduleRevision);
        Assert.NotNull(b.Edit(t.Id, d => d with { ReminderMinutes = 5 }));
        Meet(a, b, changeArrivesFirst);
        foreach (var s in new[] { a, b })
        {
            var x = s.Get(t.Id)!;
            Assert.Equal((ScheduleKind.Once, once.Def.At), (x.Def.Kind, x.Def.At));
            Assert.Equal(("green tea", 5), (x.Def.Description, x.Def.ReminderMinutes));
            Assert.False(MergeRules.Mismatched(x));
        }
        Assert.Equal(a.Get(t.Id), b.Get(t.Id));
        Assert.Empty(a.NewerThan(b.Digest()));   // and each knows the other has it: nothing to send again
        Assert.Empty(b.NewerThan(a.Digest()));
    }

    [Fact]
    public void AScheduleChangeMadeLaterLeavesAnEditOfTheRestMadeElsewhere()
    {
        // The other way round: the phone changes the description first, the PC moves the
        // task later. Saving only a schedule leaves the rest's version alone, so the PC's
        // unchanged description doesn't outrank the phone's new one.
        var (a, b, ca, cb) = TwoDevices();
        var t = a.Add(Hourly(ca));
        Sync(a, b);
        cb.Advance(TimeSpan.FromMinutes(1));
        b.Save(t.Id, t.Def with { Description = "pills, with food" });
        ca.Advance(TimeSpan.FromMinutes(5));
        var moved = a.Save(t.Id, t.Def with { Start = Wall(ca.UtcNow).AddHours(1) })!;
        Assert.Equal(t.DefVersion, moved.DefVersion);
        Sync(a, b);
        var x = a.Get(t.Id)!;
        Assert.Equal(("pills, with food", moved.Def.Start), (x.Def.Description, x.Def.Start));
        Assert.Equal(x, b.Get(t.Id));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TwoScheduleChangesOfOneRevisionComeTogetherAsOne(bool pcFirst)
    {
        // Out of touch, the PC makes a task a one-time task and the phone, later, a 3-hour
        // countdown: both revision 2. Then the PC's alarm rings and is dismissed, a state
        // write newer than the phone's. The stamps tell the two schedules apart, so the
        // schedule and the state that win are one: the phone's, the later.
        var (a, b, ca, cb) = TwoDevices();
        var t = a.Add(Hourly(ca));
        Sync(a, b);
        ca.Advance(TimeSpan.FromMinutes(1));
        var once = a.Save(t.Id, t.Def with { Kind = ScheduleKind.Once, At = Wall(ca.UtcNow).AddMinutes(30) })!;
        cb.Advance(TimeSpan.FromMinutes(2));
        var countdown = b.Save(t.Id, t.Def with { Kind = ScheduleKind.Countdown, Duration = TimeSpan.FromHours(3) })!;
        Assert.Equal((2, 2), (once.Def.ScheduleRevision, countdown.Def.ScheduleRevision));
        ca.Advance(TimeSpan.FromMinutes(40));
        Assert.True(a.Status(t.Id)!.ShouldAlert);
        Assert.True(a.Dismiss(t.Id)!.StateVersion > countdown.StateVersion);
        Meet(a, b, pcFirst);
        foreach (var s in new[] { a, b })
        {
            var x = s.Get(t.Id)!;
            Assert.Equal(ScheduleKind.Countdown, x.Def.Kind);
            Assert.False(MergeRules.Mismatched(x));
            Assert.NotNull(x.State.CountdownEndsUtc);
        }
        Assert.Equal(a.Get(t.Id), b.Get(t.Id));
        Assert.Empty(a.NewerThan(b.Digest()));
        Assert.Empty(b.NewerThan(a.Digest()));
    }

    [Fact]
    public void TheLaterScheduleChangeWinsNotTheMoreChanges()
    {
        // Out of touch, the PC sets 7:00 and then corrects it to 7:15 (two changes); three
        // hours later the phone sets 8:00 (one). The later change wins, as anywhere else.
        var (a, b, ca, cb) = TwoDevices();
        var t = a.Add(Hourly(ca));
        Sync(a, b);
        ca.Advance(TimeSpan.FromMinutes(1));
        a.Save(t.Id, t.Def with { Start = new DateTime(2026, 6, 2, 7, 0, 0) });
        ca.Advance(TimeSpan.FromMinutes(1));
        a.Save(t.Id, a.Get(t.Id)!.Def with { Start = new DateTime(2026, 6, 2, 7, 15, 0) });
        cb.Advance(TimeSpan.FromHours(3));
        b.Save(t.Id, t.Def with { Start = new DateTime(2026, 6, 2, 8, 0, 0) });
        Sync(a, b);
        Assert.Equal(new DateTime(2026, 6, 2, 8, 0, 0), a.Get(t.Id)!.Def.Start);
        Assert.Equal(a.Get(t.Id), b.Get(t.Id));
    }

    [Fact]
    public void AnEditorLeftOpenSavesOnlyWhatWasEditedInIt()
    {
        // The PC's editor opens on the task. Meanwhile the phone changes the description and,
        // later, the reminder interval, and both sync in. In the editor the user only moves the
        // start, and saves: the phone's changes stay. Then, the other way, an editor still open
        // from before the move, which changes only the description, leaves the new start.
        var (a, b, ca, cb) = TwoDevices();
        var t = a.Add(Hourly(ca));
        Sync(a, b);
        var opened = a.Get(t.Id)!.Def;
        cb.Advance(TimeSpan.FromMinutes(1));
        b.Save(t.Id, t.Def with { Description = "pills, with food" });
        b.Edit(t.Id, d => d with { ReminderMinutes = 5 });
        Sync(a, b);
        ca.Advance(TimeSpan.FromMinutes(5));
        var moved = a.Save(t.Id, opened with { Start = new DateTime(2026, 6, 2, 8, 0, 0) }, editedFrom: opened)!;
        Assert.Equal(("pills, with food", 5, new DateTime(2026, 6, 2, 8, 0, 0)), (moved.Def.Description, moved.Def.ReminderMinutes, moved.Def.Start));
        ca.Advance(TimeSpan.FromMinutes(1));
        var renamed = a.Save(t.Id, opened with { Description = "vitamins" }, editedFrom: opened)!;
        Assert.Equal(("vitamins", 5, new DateTime(2026, 6, 2, 8, 0, 0)), (renamed.Def.Description, renamed.Def.ReminderMinutes, renamed.Def.Start));
        Assert.Equal(moved.Def.ScheduleStamp, renamed.Def.ScheduleStamp);   // the schedule untouched
    }

    [Fact]
    public void ARestartModeChangeAndADescriptionEditElsewhereBothStand()
    {
        // The PC moves the task and makes it keep to its schedule; the phone, later, changes
        // only the description. All three stand: the times, the restart mode and the rest
        // merge apart.
        var (a, b, ca, cb) = TwoDevices();
        var t = a.Add(Hourly(ca) with { Restart = RestartMode.FromDone });
        Sync(a, b);
        ca.Advance(TimeSpan.FromMinutes(1));
        a.Save(t.Id, t.Def with { Start = Wall(ca.UtcNow).AddHours(1), Restart = RestartMode.OnSchedule }, editedFrom: t.Def);
        cb.Advance(TimeSpan.FromMinutes(5));
        b.Save(t.Id, t.Def with { Description = "pills, with food" }, editedFrom: t.Def);
        Sync(a, b);
        var x = a.Get(t.Id)!;
        Assert.Equal((RestartMode.OnSchedule, "pills, with food"), (x.Def.Restart, x.Def.Description));
        Assert.Equal(x, b.Get(t.Id));
    }

    [Fact]
    public void ARestartModeChangedOnTheAlarmScreenKeepsTheState()
    {
        // Changed with the alarm still waiting: the restart mode has a version of its own, and
        // neither the schedule nor the state changes: the waiting alarm carries on.
        var (a, _, ca, _) = TwoDevices();
        var t = a.Add(Hourly(ca) with { Restart = RestartMode.FromDone });
        ca.Advance(TimeSpan.FromMinutes(61));
        Assert.True(a.Status(t.Id)!.ShouldAlert);
        var before = a.Get(t.Id)!;
        var after = a.Edit(t.Id, d => d with { Restart = RestartMode.OnSchedule })!;
        Assert.True(after.Def.RestartVersion > before.Def.RestartVersion);
        Assert.Equal((before.Def.ScheduleStamp, before.DefVersion), (after.Def.ScheduleStamp, after.DefVersion));
        Assert.Equal((before.State, before.StateVersion), (after.State, after.StateVersion));
        Assert.True(a.Status(t.Id)!.ShouldAlert);
    }

    [Fact]
    public void ARestartModeChangeAndATimesChangeElsewhereBothStand()
    {
        // Out of touch, the phone moves the task to 7:00 and marks it done; later, the PC's
        // alarm screen switches it to keep to its schedule. Both stand: the move with what the
        // phone marked done, and the restart mode.
        var (a, b, ca, cb) = TwoDevices();
        var t = a.Add(Hourly(ca) with { Restart = RestartMode.FromDone });
        Sync(a, b);
        cb.Advance(TimeSpan.FromMinutes(1));
        var moved = b.Save(t.Id, t.Def with { Start = new DateTime(2026, 6, 1, 7, 0, 0) })!;
        cb.Advance(TimeSpan.FromMinutes(5));
        var done = b.MarkDone(t.Id)!;
        ca.Advance(TimeSpan.FromMinutes(30));
        a.Edit(t.Id, d => d with { Restart = RestartMode.OnSchedule });
        Sync(a, b);
        var x = a.Get(t.Id)!;
        Assert.Equal((new DateTime(2026, 6, 1, 7, 0, 0), RestartMode.OnSchedule), (x.Def.Start, x.Def.Restart));
        Assert.Equal(done.State, x.State);
        Assert.Equal(x, b.Get(t.Id));
        Assert.Equal(moved.Def.ScheduleStamp, x.Def.ScheduleStamp);
    }

    [Fact]
    public void EditDoesntChangeWhenATaskComesUp()
    {
        // That is Save's, which starts a new schedule: Edit says so out loud rather than do it.
        var (a, _, ca, _) = TwoDevices();
        var t = a.Add(Hourly(ca));
        Assert.Throws<ArgumentException>(() => a.Edit(t.Id, d => d with { Start = d.Start.AddHours(1) }));
        Assert.Equal(t, a.Get(t.Id));
    }

    [Fact]
    public void RestartingTheCountdownLeavesAScheduleChangedElsewhere()
    {
        // The PC's editor opens on a finished countdown, "start the countdown again" ticked.
        // Meanwhile the phone makes it a one-time task. In the editor the user changes only the
        // description: the one-time task stays, and there is no countdown to restart.
        var (a, b, ca, cb) = TwoDevices();
        var t = a.Add(new TaskDefinition { Description = "tea", Kind = ScheduleKind.Countdown, Duration = TimeSpan.FromMinutes(5) });
        Sync(a, b);
        var opened = t.Def;
        cb.Advance(TimeSpan.FromMinutes(1));
        var once = b.Save(t.Id, t.Def with { Kind = ScheduleKind.Once, At = Wall(cb.UtcNow).AddHours(2) })!;
        Sync(a, b);
        ca.Advance(TimeSpan.FromMinutes(10));
        var saved = a.Save(t.Id, opened with { Description = "green tea" }, restartCountdown: true, editedFrom: opened)!;
        Assert.Equal((ScheduleKind.Once, "green tea"), (saved.Def.Kind, saved.Def.Description));
        Assert.Equal(once.Def.ScheduleStamp, saved.Def.ScheduleStamp);
        Assert.Equal(once.State, saved.State);
    }

    [Fact]
    public void EveryFieldOfADefinitionIsInOnePart()
    {
        // The parts of a definition merge apart (WithScheduleOf, WithRestartOf, the rest): a new
        // field must be put in one of them, knowingly, here.
        string[] schedule =
        [
            nameof(TaskDefinition.Kind), nameof(TaskDefinition.IntervalCount), nameof(TaskDefinition.Unit),
            nameof(TaskDefinition.Start), nameof(TaskDefinition.Days),
            nameof(TaskDefinition.TimeOfDay), nameof(TaskDefinition.WeekInterval), nameof(TaskDefinition.WeeksFrom),
            nameof(TaskDefinition.FirstDayOfWeek), nameof(TaskDefinition.At), nameof(TaskDefinition.Duration),
            nameof(TaskDefinition.ScheduleRevision), nameof(TaskDefinition.ScheduleStamp),
        ];
        // An editor's save (WithRestEditsOf) takes each of these it changed.
        string[] edited =
        [
            nameof(TaskDefinition.Description), nameof(TaskDefinition.ReminderMinutes),
            nameof(TaskDefinition.WindowsSound), nameof(TaskDefinition.AndroidSound), nameof(TaskDefinition.SoundMode),
            nameof(TaskDefinition.Volume), nameof(TaskDefinition.FadeInSeconds),
        ];
        // The restart mode merges on its own (WithRestartOf).
        string[] restart = [nameof(TaskDefinition.Restart), nameof(TaskDefinition.RestartVersion)];
        // The store's to set.
        string[] store = [nameof(TaskDefinition.Deleted), nameof(TaskDefinition.Incarnation)];
        var all = typeof(TaskDefinition).GetProperties().Where(p => p.CanWrite).Select(p => p.Name).Order().ToList();
        Assert.Equal(all, schedule.Concat(edited).Concat(restart).Concat(store).Order().ToList());

        // And WithScheduleOf takes exactly the schedule's fields from the other.
        var one = new TaskDefinition();
        var other = new TaskDefinition
        {
            Description = "x", Kind = ScheduleKind.Weekly, IntervalCount = 7, Unit = IntervalUnit.Weeks, Start = new DateTime(2030, 1, 1),
            Restart = RestartMode.OnSchedule, Days = 3, TimeOfDay = new TimeOnly(7, 7), WeekInterval = 3, WeeksFrom = new DateOnly(2030, 1, 1),
            FirstDayOfWeek = DayOfWeek.Sunday, At = new DateTime(2030, 2, 2), Duration = TimeSpan.FromHours(7), ReminderMinutes = 77,
            WindowsSound = new SoundRef(SoundSource.File, "w", "w"), AndroidSound = new SoundRef(SoundSource.File, "a", "a"), SoundMode = SoundMode.Repeat,
            Volume = 70, FadeInSeconds = 7, ScheduleRevision = 7, ScheduleStamp = new Hlc(7, 7, "n"), Deleted = true, Incarnation = 7,
            RestartVersion = new Hlc(7, 7, "r"),
        };
        var merged = one.WithScheduleOf(other);
        var edits = one.WithRestEditsOf(one, other);
        var restarted = one.WithRestartOf(other);
        foreach (var p in typeof(TaskDefinition).GetProperties().Where(p => p.CanWrite))
        {
            var expected = schedule.Contains(p.Name) ? p.GetValue(other) : p.GetValue(one);
            Assert.True(Equals(expected, p.GetValue(merged)), p.Name);
            Assert.False(Equals(p.GetValue(one), p.GetValue(other)), $"{p.Name} isn't set apart in this test");
            expected = edited.Contains(p.Name) ? p.GetValue(other) : p.GetValue(one);
            Assert.True(Equals(expected, p.GetValue(edits)), $"{p.Name}, edited");
            expected = restart.Contains(p.Name) ? p.GetValue(other) : p.GetValue(one);
            Assert.True(Equals(expected, p.GetValue(restarted)), $"{p.Name}, restart mode");
        }
    }

    [Fact]
    public void TwoRestoresOfDifferentSchedulesComeBackAsOne()
    {
        // A countdown is backed up, made a one-time task, backed up again, and deleted. Out
        // of touch, the phone restores the one-time backup, and the PC, later, the countdown
        // one: one incarnation, two schedules. The later schedule wins, definition and state.
        var (a, b, ca, cb) = TwoDevices();
        var t = a.Add(new TaskDefinition { Description = "tea", Kind = ScheduleKind.Countdown, Duration = TimeSpan.FromHours(2) });
        var countdown = a.Export();
        ca.Advance(TimeSpan.FromMinutes(1));
        a.Save(t.Id, t.Def with { Kind = ScheduleKind.Once, At = Wall(ca.UtcNow).AddHours(3) });
        var once = a.Export();
        ca.Advance(TimeSpan.FromMinutes(1));
        a.Delete(t.Id);
        Sync(a, b);
        cb.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(new ImportResult(1, 0, 0), b.Import(once));
        ca.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(new ImportResult(1, 0, 0), a.Import(countdown));
        Sync(a, b);
        var x = a.Get(t.Id)!;
        Assert.Equal((1, 1), (x.Def.Incarnation, x.State.Incarnation));
        Assert.Equal(ScheduleKind.Once, x.Def.Kind);
        Assert.Equal(x.Def.ScheduleRevision, x.State.Revision);
        Assert.Equal(x, b.Get(t.Id));
        Assert.Empty(a.NewerThan(b.Digest()));
        Assert.Empty(b.NewerThan(a.Digest()));
    }

    [Fact]
    public void IncarnationsAndRevisionsOutOfRangeAreMalformed()
    {
        // A restore and a schedule change each add one: a record claiming more than there can
        // be would overflow. And a state can't be of a later incarnation than its definition.
        var (a, _, ca, _) = TwoDevices();
        var t = a.Add(Hourly(ca));
        Assert.True(MergeRules.IsWellFormed(t));
        Assert.False(MergeRules.IsWellFormed(t with { Def = t.Def with { Incarnation = int.MaxValue } }));
        Assert.False(MergeRules.IsWellFormed(t with { Def = t.Def with { Incarnation = -1 } }));
        Assert.False(MergeRules.IsWellFormed(t with { State = t.State with { Incarnation = 1 } }));
        Assert.False(MergeRules.IsWellFormed(t with { Def = t.Def with { ScheduleRevision = MergeRules.MaxRevision + 1 } }));
        Assert.False(MergeRules.IsWellFormed(t with { State = t.State with { Revision = -1 } }));
    }

    [Fact]
    public void AtTheCeilingsWhatThisDeviceWritesStaysWellFormed()
    {
        // Only corrupt input gets a task there; what this device then writes must still be
        // taken by every other device, or they would never agree again.
        var (a, b, ca, _) = TwoDevices();
        var t = a.Add(Hourly(ca));
        var top = t with
        {
            Def = t.Def with { Incarnation = MergeRules.MaxIncarnation, ScheduleRevision = MergeRules.MaxRevision },
            State = t.State with { Incarnation = MergeRules.MaxIncarnation, Revision = MergeRules.MaxRevision },
        };
        b.Merge([top]);
        var backup = b.Export();
        ca.Advance(TimeSpan.FromMinutes(1));
        var saved = b.Save(t.Id, top.Def with { IntervalCount = 2 })!;
        Assert.True(MergeRules.IsWellFormed(saved));
        b.Delete(t.Id);
        b.Import(backup);
        Assert.All(b.All(), r => Assert.True(MergeRules.IsWellFormed(r)));
    }

    [Fact]
    public void MergeIsOrderIndependent()
    {
        var (a, b, ca, cb) = TwoDevices();
        var t = a.Add(Hourly(ca));
        Sync(a, b);
        ca.UtcNow = NY(2026, 6, 1, 10, 5);
        var x = a.MarkDone(t.Id)!;
        cb.UtcNow = NY(2026, 6, 1, 10, 30);
        var y = b.Dismiss(t.Id)!;
        var m1 = MergeRules.Merge(x, y);
        var m2 = MergeRules.Merge(y, x);
        Assert.Equal(m1.State, m2.State);
        Assert.Equal(m1.StateVersion, m2.StateVersion);
        Assert.Equal(m1.DefVersion, m2.DefVersion);
    }

    [Fact]
    public void ThreeDevicesGossipingConverge()
    {
        var clocks = Enumerable.Range(0, 3).Select(_ => new FakeClock(NY(2026, 6, 1, 9), NewYork)).ToArray();
        var repairs = new List<string>();
        var stores = clocks.Select((c, i) => new TaskStore(null, $"n{i}", c, repairs.Add)).ToArray();
        var rng = new Random(42);
        var ids = new List<Guid>();
        var exports = new List<string>();
        var opened = new List<(int Store, Guid Task, TaskDefinition From)>();
        for (int step = 0; step < 300; step++)
        {
            int i = rng.Next(3);
            foreach (var c in clocks) c.Advance(TimeSpan.FromMinutes(rng.Next(1, 20)));
            var s = stores[i];
            var live = s.Live();
            switch (rng.Next(13))
            {
                case 9 when live.Count > 0:
                    s.Edit(live[rng.Next(live.Count)].Id, d => d with { Restart = d.Restart == RestartMode.FromDone ? RestartMode.OnSchedule : RestartMode.FromDone });
                    break;
                case 10 when live.Count > 0: s.RestartCountdown(live[rng.Next(live.Count)].Id); break;
                case 11 when live.Count > 0:
                    // An editor opened now, saved some steps later with only its own edits.
                    var e2 = live[rng.Next(live.Count)];
                    opened.Add((i, e2.Id, e2.Def));
                    break;
                case 12 when opened.Count > 0:
                    var (store, task, openedWith) = opened[rng.Next(opened.Count)];
                    stores[store].Save(task, rng.Next(2) == 0
                        ? openedWith with { Description = $"edited{step}" }
                        : openedWith with { Start = Wall(clocks[store].UtcNow).AddMinutes(rng.Next(1, 60)), Kind = ScheduleKind.Interval },
                        editedFrom: openedWith);
                    break;
                case 7 when live.Count > 0:
                    // A schedule change: another length, a later start, or another kind.
                    var r = live[rng.Next(live.Count)];
                    var start = Wall(clocks[i].UtcNow).AddMinutes(rng.Next(1, 120));
                    s.Save(r.Id, rng.Next(3) switch
                    {
                        0 => r.Def with { IntervalCount = rng.Next(1, 4) },
                        1 => r.Def with { Kind = ScheduleKind.Interval, Start = start },
                        _ => r.Def with { Kind = ScheduleKind.Once, At = start },
                    });
                    break;
                case 8 when live.Count > 0: s.Edit(live[rng.Next(live.Count)].Id, d => d with { ReminderMinutes = rng.Next(0, 30) }); break;
                case 5 when rng.Next(3) == 0: exports.Add(s.Export()); break;
                // An old export restores the tasks deleted since: new incarnations.
                case 6 when exports.Count > 0 && rng.Next(2) == 0: s.Import(exports[rng.Next(exports.Count)]); break;
                case 0: ids.Add(s.Add(Hourly(clocks[i]) with { Description = $"t{step}" }).Id); break;
                case 1 when live.Count > 0: s.MarkDone(live[rng.Next(live.Count)].Id); break;
                case 2 when live.Count > 0: s.Dismiss(live[rng.Next(live.Count)].Id); break;
                case 3 when live.Count > 0:
                    var e = live[rng.Next(live.Count)];
                    s.Save(e.Id, e.Def with { Description = $"edit{step}" }); break;
                case 4 when live.Count > 0 && rng.Next(4) == 0: s.Delete(live[rng.Next(live.Count)].Id); break;
                default:
                    // Only neighbours sync (0–1, 1–2): the 0–2 path exists only through 1, as across a bridge.
                    int j = rng.Next(2);
                    Sync(stores[j], stores[j + 1]);
                    break;
            }
        }
        for (int round = 0; round < 2; round++) { Sync(stores[0], stores[1]); Sync(stores[1], stores[2]); }
        var expected = stores[0].All().OrderBy(t => t.Id).ToList();
        foreach (var s in stores.Skip(1))
            Assert.Equal(expected, s.All().OrderBy(t => t.Id).ToList());
        Assert.Contains(expected, t => t.Def.Incarnation > 0);   // restores happened
        Assert.Contains(expected, t => t.Def.Deleted);
        Assert.Contains(expected, t => t.Def.ScheduleRevision > 2);   // and schedule changes
        Assert.Contains(expected, t => t.Def.Restart == RestartMode.FromDone);   // and restart modes
        Assert.All(expected, t => Assert.False(MergeRules.Mismatched(t), "a schedule with another's state"));
        Assert.Empty(repairs);   // nor did a merge ever make one, to be mended
    }
}

public class PersistenceTests
{
    /// <summary>The tasks of <paramref name="export"/> (an export file) as an older version's
    /// tasks.json holds them: no schedule stamps, no restart mode versions. <paramref name="stateFrom"/>, another export
    /// of the same tasks, gives each its state instead.</summary>
    private static string LegacyStoreFile(string export, string? stateFrom = null)
    {
        var tasks = System.Text.Json.Nodes.JsonNode.Parse(export)!["tasks"]!.AsArray();
        var states = stateFrom is null ? null : System.Text.Json.Nodes.JsonNode.Parse(stateFrom)!["tasks"]!.AsArray();
        for (int i = 0; i < tasks.Count; i++)
        {
            var t = tasks[i]!.AsObject();
            if (states is not null)
            {
                t["state"] = states[i]!["state"]!.DeepClone();
                t["stateVersion"] = states[i]!["stateVersion"]!.DeepClone();
            }
            t["def"]!.AsObject().Remove("scheduleStamp");
            t["def"]!.AsObject().Remove("restartVersion");
            t["state"]!.AsObject().Remove("scheduleStamp");
        }
        return new System.Text.Json.Nodes.JsonObject { ["format"] = 1, ["tasks"] = tasks.DeepClone() }.ToJsonString();
    }

    private static TaskStore Load(DirectoryInfo dir, string name, string json, FakeClock clock, List<string>? log = null)
    {
        var path = Path.Combine(dir.FullName, name);
        File.WriteAllText(path, json);
        return new TaskStore(path, name, clock, log is null ? null : log.Add);
    }

    [Fact]
    public void AnOlderVersionsTasksGetScheduleStampsEveryDeviceAgreesOn()
    {
        // The same task, saved by an older version on both devices: each works out the same
        // stamp, so the two agree it is one schedule, and have nothing to send each other.
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var clock = new FakeClock(NY(2026, 6, 1, 9), NewYork);
            var src = new TaskStore(null, "src", clock);
            src.Add(new TaskDefinition { Description = "pills", Kind = ScheduleKind.Weekly, Days = 0b0111110 });
            var file = LegacyStoreFile(src.Export());
            var a = Load(dir, "a", file, clock);
            var b = Load(dir, "b", file, clock);
            var t = a.All().Single();
            Assert.False(MergeRules.Mismatched(t));
            Assert.Equal(t.Def.LegacyScheduleStamp(), t.Def.ScheduleStamp);
            Assert.Equal(t, b.All().Single());
            Assert.Empty(a.NewerThan(b.Digest()));
            Assert.Empty(b.NewerThan(a.Digest()));
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public void TwoOlderSchedulesOfOneRevisionComeTogetherAsOne()
    {
        // Changed on each device out of touch, before updating: one revision, two schedules,
        // both unstamped. Each device's stamp is worked out from its schedule, so the two
        // pick the same one, definition and state.
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var clock = new FakeClock(NY(2026, 6, 1, 9), NewYork);
            var src = new TaskStore(null, "src", clock);
            var t = src.Add(new TaskDefinition { Description = "tea", Kind = ScheduleKind.Countdown, Duration = TimeSpan.FromHours(2) });
            var other = new TaskStore(null, "other", clock);
            other.Merge(src.All());
            clock.Advance(TimeSpan.FromMinutes(1));
            src.Save(t.Id, t.Def with { Kind = ScheduleKind.Once, At = new DateTime(2026, 6, 1, 12, 0, 0) });
            other.Save(t.Id, t.Def with { Duration = TimeSpan.FromHours(3) });
            var a = Load(dir, "a", LegacyStoreFile(src.Export()), clock);
            var b = Load(dir, "b", LegacyStoreFile(other.Export()), clock);
            var toB = a.NewerThan(b.Digest());
            var toA = b.NewerThan(a.Digest());
            b.Merge(toB);
            a.Merge(toA);
            var x = a.Get(t.Id)!;
            Assert.Equal(x, b.Get(t.Id));
            Assert.False(MergeRules.Mismatched(x));
            Assert.Equal(2, x.Def.ScheduleRevision);
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public void AnOlderVersionsSameRevisionPairThatCantRingIsRepaired()
    {
        // Under an older version, out of touch, one device made a countdown a one-time task and
        // the other changed its length: both revision 2. The countdown's definition won, with
        // the one-time task's state: one revision, and no countdown end. Loaded now, it starts
        // afresh, though its stamps would agree.
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var clock = new FakeClock(NY(2026, 6, 1, 9), NewYork);
            var src = new TaskStore(null, "src", clock);
            var t = src.Add(new TaskDefinition { Description = "tea", Kind = ScheduleKind.Countdown, Duration = TimeSpan.FromHours(2) });
            var other = new TaskStore(null, "other", clock);
            other.Merge(src.All());
            clock.Advance(TimeSpan.FromMinutes(1));
            src.Save(t.Id, t.Def with { Kind = ScheduleKind.Once, At = new DateTime(2026, 6, 1, 12, 0, 0) });
            other.Save(t.Id, t.Def with { Duration = TimeSpan.FromHours(3) });
            var log = new List<string>();
            var a = Load(dir, "a", LegacyStoreFile(other.Export(), stateFrom: src.Export()), clock, log);
            var x = a.Get(t.Id)!;
            Assert.False(MergeRules.Mismatched(x));
            Assert.Equal(clock.UtcNow + TimeSpan.FromHours(3), x.State.CountdownEndsUtc);   // written just now
            Assert.Contains(log, l => l.Contains("starts afresh"));
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public void ARepairRanksBelowAChangeMadeSince()
    {
        // Both devices held an older version's countdown that couldn't ring. The PC updates
        // first and repairs it, and the user then makes it a one-time task. The phone updates
        // the next day and repairs its own copy. Its repair is the PC's (worked out alike, not
        // stamped now), so it ranks below the PC's change, which stands.
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var clock = new FakeClock(NY(2026, 6, 1, 9), NewYork);
            var src = new TaskStore(null, "src", clock);
            var t = src.Add(new TaskDefinition { Description = "tea", Kind = ScheduleKind.Countdown, Duration = TimeSpan.FromHours(2) });
            var countdown = src.Export();
            clock.Advance(TimeSpan.FromMinutes(1));
            src.Save(t.Id, t.Def with { Kind = ScheduleKind.Once, At = new DateTime(2026, 6, 1, 12, 0, 0) });
            var file = LegacyStoreFile(countdown, stateFrom: src.Export());
            var pc = Load(dir, "pc", file, clock);
            clock.Advance(TimeSpan.FromHours(1));
            var changed = pc.Save(t.Id, pc.Get(t.Id)!.Def with { Kind = ScheduleKind.Once, At = new DateTime(2026, 6, 2, 9, 0, 0) })!;
            clock.Advance(TimeSpan.FromDays(1));
            var phone = Load(dir, "phone", file, clock);
            Assert.Equal(pc.Get(t.Id)!.Def.ScheduleRevision - 1, phone.Get(t.Id)!.Def.ScheduleRevision);
            phone.Merge(pc.NewerThan(phone.Digest()));
            pc.Merge(phone.NewerThan(pc.Digest()));
            Assert.Equal(changed.Def.At, phone.Get(t.Id)!.Def.At);
            Assert.Equal(pc.Get(t.Id), phone.Get(t.Id));
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public void ARepairedStateRanksBelowAStateWrittenSince()
    {
        // The PC updates first and repairs an older version's countdown that couldn't ring, and
        // the user pauses it. The phone updates the next day and repairs its own copy: the same
        // schedule, so the states rank by version, and a repair's is below any written since.
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var clock = new FakeClock(NY(2026, 6, 1, 9), NewYork);
            var src = new TaskStore(null, "src", clock);
            var t = src.Add(new TaskDefinition { Description = "tea", Kind = ScheduleKind.Countdown, Duration = TimeSpan.FromHours(2) });
            var countdown = src.Export();
            clock.Advance(TimeSpan.FromMinutes(1));
            src.Save(t.Id, t.Def with { Kind = ScheduleKind.Once, At = new DateTime(2026, 6, 1, 12, 0, 0) });
            var file = LegacyStoreFile(countdown, stateFrom: src.Export());
            var pc = Load(dir, "pc", file, clock);
            clock.Advance(TimeSpan.FromMinutes(10));
            var paused = pc.Pause(t.Id)!;
            clock.Advance(TimeSpan.FromDays(1));
            var phone = Load(dir, "phone", file, clock);
            phone.Merge(pc.NewerThan(phone.Digest()));
            pc.Merge(phone.NewerThan(pc.Digest()));
            Assert.NotNull(phone.Get(t.Id)!.State.PausedAtUtc);
            Assert.Equal(paused.State.CountdownEndsUtc, phone.Get(t.Id)!.State.CountdownEndsUtc);
            Assert.Equal(pc.Get(t.Id), phone.Get(t.Id));
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public void TwoOlderCopiesThatDifferInTheirRestartModeComeTogether()
    {
        // Under an older version the restart mode merged with the definition, by its version:
        // given that version, the later of two copies' restart modes wins, as it would have.
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var clock = new FakeClock(NY(2026, 6, 1, 9), NewYork);
            var src = new TaskStore(null, "src", clock);
            var t = src.Add(new TaskDefinition
            {
                Description = "pills", Kind = ScheduleKind.Interval, Unit = IntervalUnit.Hours, Start = Wall(clock.UtcNow),
                Restart = RestartMode.OnSchedule,
            });
            var other = new TaskStore(null, "other", clock);
            other.Merge(src.All());
            clock.Advance(TimeSpan.FromMinutes(1));
            // As an older version saved it: with a new version of the whole definition.
            other.Save(t.Id, other.Get(t.Id)!.Def with { Restart = RestartMode.FromDone, Description = "pills, from done" });
            var a = Load(dir, "a", LegacyStoreFile(src.Export()), clock);
            var b = Load(dir, "b", LegacyStoreFile(other.Export()), clock);
            b.Merge(a.NewerThan(b.Digest()));
            a.Merge(b.NewerThan(a.Digest()));
            Assert.Equal((RestartMode.FromDone, "pills, from done"), (a.Get(t.Id)!.Def.Restart, a.Get(t.Id)!.Def.Description));
            Assert.Equal(a.Get(t.Id), b.Get(t.Id));
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public void AnOlderVersionsPairThatCanRingKeepsItsState()
    {
        // The definition of revision 1 (a daily task at 8:00, done from when marked done) with
        // the state of revision 2, which an older version's merge left together. The pair
        // rang. Loaded now, the state is taken as the definition's, and keeps its anchor and
        // what it has dealt with: the alarm waiting since 8:00 still waits.
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var clock = new FakeClock(NY(2026, 6, 1, 7), NewYork);
            var src = new TaskStore(null, "src", clock);
            var t = src.Add(new TaskDefinition
            {
                Description = "walk", Kind = ScheduleKind.Interval, Unit = IntervalUnit.Days, Start = new DateTime(2026, 5, 1, 8, 0, 0),
                Restart = RestartMode.FromDone,
            });
            var eight = src.Export();
            clock.Advance(TimeSpan.FromMinutes(1));
            src.Save(t.Id, t.Def with { Start = new DateTime(2026, 5, 1, 9, 0, 0) });   // revision 2
            src.MarkDone(t.Id);   // an anchor in the state
            var nine = src.Export();
            clock.UtcNow = NY(2026, 6, 2, 9, 30);
            var log = new List<string>();
            var a = Load(dir, "a", LegacyStoreFile(eight, stateFrom: nine), clock, log);
            var x = a.Get(t.Id)!;
            Assert.False(MergeRules.Mismatched(x));
            Assert.Equal(src.Get(t.Id)!.State.Anchor, x.State.Anchor);
            Assert.Equal(src.Get(t.Id)!.State.DoneThroughUtc, x.State.DoneThroughUtc);
            Assert.True(a.Status(t.Id)!.Triggered);
            Assert.Contains(log, l => l.Contains("is kept"));
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public void AnOlderVersionsTaskWhoseDefinitionAndStateDifferIsRepaired()
    {
        // Under an older version, one device made a countdown a one-time task and the other,
        // later, edited only the countdown's description: every device kept the countdown's
        // definition with the one-time task's state, which has no countdown end, so it never
        // rang. Loaded now, it starts afresh on its schedule, and says so in the log.
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var clock = new FakeClock(NY(2026, 6, 1, 9), NewYork);
            var src = new TaskStore(null, "src", clock);
            var t = src.Add(new TaskDefinition { Description = "tea", Kind = ScheduleKind.Countdown, Duration = TimeSpan.FromHours(2) });
            var countdown = src.Export();
            clock.Advance(TimeSpan.FromMinutes(1));
            src.Save(t.Id, t.Def with { Kind = ScheduleKind.Once, At = new DateTime(2026, 6, 1, 12, 0, 0) });
            var once = src.Export();
            var written = clock.UtcNow;
            clock.Advance(TimeSpan.FromDays(1));
            var log = new List<string>();
            var file = LegacyStoreFile(countdown, stateFrom: once);
            var a = Load(dir, "a", file, clock, log);
            var x = a.Get(t.Id)!;
            Assert.False(MergeRules.Mismatched(x));
            Assert.Equal(ScheduleKind.Countdown, x.Def.Kind);
            // From when the task was last written, not from now: overdue, it rings late.
            Assert.Equal(written + TimeSpan.FromHours(2), x.State.CountdownEndsUtc);
            Assert.True(a.Status(t.Id)!.ShouldAlert);
            // Another device repairing it a day later makes the very same task: nothing to send.
            clock.Advance(TimeSpan.FromDays(1));
            var b2 = Load(dir, "b", file, clock);
            Assert.Equal(x, b2.Get(t.Id));
            Assert.Empty(a.NewerThan(b2.Digest()));
            Assert.Empty(b2.NewerThan(a.Digest()));
            Assert.Equal(3, x.Def.ScheduleRevision);   // past the one-time state's
            Assert.Contains(log, l => l.Contains("\"tea\"") && l.Contains("starts afresh"));
            // Saved so: loaded again, nothing more to repair.
            log.Clear();
            Assert.Equal(x, new TaskStore(Path.Combine(dir.FullName, "a"), "a", clock, log.Add).Get(t.Id));
            Assert.Empty(log);
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public void ACorruptFileIsKeptAsideAndTheBackupUsed()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "tasks.json");
            var clock = new FakeClock(NY(2026, 6, 1, 9), NewYork);
            var s = new TaskStore(path, "a", clock);
            var t1 = s.Add(new TaskDefinition { Description = "one", Kind = ScheduleKind.Once, At = new DateTime(2026, 7, 1, 9, 0, 0) });
            s.Add(new TaskDefinition { Description = "two", Kind = ScheduleKind.Once, At = new DateTime(2026, 7, 2, 9, 0, 0) });
            File.WriteAllText(path, "{ not json");
            var logs = new List<string>();
            var reloaded = new TaskStore(path, "a", clock, logs.Add);
            Assert.Equal(t1, reloaded.Get(t1.Id));      // the backup held one task
            Assert.Contains(dir.GetFiles(), f => f.Name.Contains(".corrupt-"));
            Assert.NotEmpty(logs);
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public void SettingsCreateAnIdentityOnFirstRun()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "settings.json");
            var s = AppSettings.Load(path, "PC", []);
            Assert.NotEmpty(s.NodeId);
            Assert.Equal(32, Convert.FromBase64String(s.GroupKey).Length);
            var again = AppSettings.Load(path, "other", []);
            Assert.Equal(s.NodeId, again.NodeId);
            Assert.Equal(new TimeOnly(9, 0), again.DefaultTimeOfDay);
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public void ADeviceGoesByItsOwnNameAsItChangesUnlessGivenAnother()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "settings.json");
            Assert.Equal("DESKTOP-1", AppSettings.Load(path, "DESKTOP-1", []).NameOr("DESKTOP-1"));
            // The computer renamed: the next start goes by the new name.
            var renamed = AppSettings.Load(path, "Logoplex4", []);
            Assert.Equal("Logoplex4", renamed.NameOr("Logoplex4"));

            // A name given sticks through a rename; blank, or the device's own, follows it again.
            renamed.SetDeviceName(" Study PC ", "Logoplex4");
            Assert.Equal("Study PC", renamed.NameOr("Logoplex4"));
            renamed.Save(path);
            Assert.Equal("Study PC", AppSettings.Load(path, "Elsewhere", []).NameOr("Elsewhere"));
            renamed.SetDeviceName("  ", "Logoplex4");
            Assert.Equal("Elsewhere", renamed.NameOr("Elsewhere"));
            renamed.SetDeviceName("Logoplex4", "Logoplex4");
            Assert.Equal("Elsewhere", renamed.NameOr("Elsewhere"));
            // The own name in other capitals is a name of its own.
            renamed.SetDeviceName("PIXEL 7", "Pixel 7");
            Assert.Equal("PIXEL 7", renamed.NameOr("Pixel 7"));
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public void RememberedDevicesNothingCanUseAreDroppedOnLoad()
    {
        // A damaged or hand-edited settings file.
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "settings.json");
            AppSettings.Load(path, "PC", []);
            string good = Guid.NewGuid().ToString("N"), nameless = Guid.NewGuid().ToString("N");
            var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
            json["knownPeers"] = System.Text.Json.Nodes.JsonNode.Parse($$"""
                [null,
                 {"name": "no ID", "lastSeenUtc": "2026-11-11T00:00:00+00:00"},
                 {"nodeId": "{{nameless}}", "lastSeenUtc": "2026-11-11T00:00:00+00:00"},
                 {"nodeId": "{{good}}", "name": "Phone", "lastSeenUtc": "2026-11-11T00:00:00+00:00"},
                 {"nodeId": "{{good}}", "name": "Phone, earlier", "lastSeenUtc": "2026-11-10T00:00:00+00:00", "address": "192.168.1.66:37660"}]
                """);
            json["deviceName"] = null;
            File.WriteAllText(path, json.ToJsonString());
            var loaded = AppSettings.Load(path, "PC", []);
            // One entry for the device listed twice: the latest, keeping the address the other had.
            Assert.Equal([nameless[..8], "Phone"], loaded.KnownPeers.Select(k => k.Name));
            Assert.Equal("192.168.1.66:37660", loaded.KnownPeers[1].Address);
            Assert.Equal("PC", loaded.NameOr("PC"));   // a null name: the device's own
            loaded.RememberPeer(good, "Phone", DateTimeOffset.UtcNow);   // nothing left to trip over
            Assert.Equal(2, AppSettings.Load(path, "PC", []).KnownPeers.Count);   // and saved so

            json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
            json["remoteAddresses"] = null;
            File.WriteAllText(path, json.ToJsonString());
            Assert.Empty(AppSettings.Load(path, "PC", []).RemoteAddresses);
            json["remoteAddresses"] = System.Text.Json.Nodes.JsonNode.Parse("""[" home.example:4000 ", null, "", "HOME.example:4000", "10.0.0.2"]""");
            File.WriteAllText(path, json.ToJsonString());
            Assert.Equal(["home.example:4000", "10.0.0.2"], AppSettings.Load(path, "PC", []).RemoteAddresses);
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public void ALinkedDevicesLastLanAddressIsKeptAndSaved()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "settings.json");
            var s = AppSettings.Load(path, "PC", []);
            var node = Guid.NewGuid().ToString("N");
            var t0 = DateTimeOffset.UtcNow;
            s.RememberPeer(node, "Phone", t0, "192.168.1.66:37660");
            // In sync over the internet: no LAN address, so the last one stays.
            s.RememberPeer(node, "Phone renamed", t0.AddMinutes(1), null);
            var k = Assert.Single(s.KnownPeers);
            Assert.Equal(("Phone renamed", "192.168.1.66:37660"), (k.Name, k.Address));
            s.RememberPeer(node, "Phone renamed", t0.AddMinutes(2), "192.168.1.70:37660");
            s.Save(path);
            Assert.Equal("192.168.1.70:37660", Assert.Single(AppSettings.Load(path, "PC", []).KnownPeers).Address);
        }
        finally { dir.Delete(true); }
    }

    /// <summary>The name stored once a settings file from 0.1.1 or earlier (no format), which
    /// holds <paramref name="stored"/>, is loaded on a device now named <paramref name="ownName"/>.</summary>
    private static string Upgraded(string stored, string ownName, params string[] earlierDefaults)
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "settings.json");
            File.WriteAllText(path, $$"""{ "nodeId": "n1", "deviceName": "{{stored}}" }""");
            Assert.Equal(AppSettings.CurrentFormat, AppSettings.Load(path, ownName, earlierDefaults).Format);
            return AppSettings.Load(path, ownName, earlierDefaults).DeviceName;   // as saved
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public void ANameAnEarlierVersionStoredFollowsTheDeviceFromNowOn()
    {
        Assert.Equal("", Upgraded("DESKTOP-1", "DESKTOP-1"));
        // Windows' NetBIOS name: in capitals, and at most 15 characters.
        Assert.Equal("", Upgraded("MYPC", "MyPc", "MYPC"));
        Assert.Equal("", Upgraded("LOGOPLEX-WORKST", "Logoplex-Workstation", "LOGOPLEX-WORKST"));
        // Android's maker and model; the phone's own name is the one set in About phone.
        Assert.Equal("", Upgraded("samsung SM-S908U", "Galaxy S22 Ultra", "samsung SM-S908U"));
        // Another name can't be told from one given.
        Assert.Equal("DESKTOP-1", Upgraded("DESKTOP-1", "Logoplex4", "LOGOPLEX4"));
    }

    [Fact]
    public void TheUpgradeRunsOnceOnly()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "settings.json");
            File.WriteAllText(path, """{ "nodeId": "n1", "deviceName": "Office" }""");
            Assert.Equal("Office", AppSettings.Load(path, "Logoplex4", ["LOGOPLEX4"]).DeviceName);
            // The computer later renamed to the name given, in capitals: the name given stays.
            Assert.Equal("Office", AppSettings.Load(path, "OFFICE", ["OFFICE"]).DeviceName);
        }
        finally { dir.Delete(true); }
    }
}

public class ExportImportTests
{
    private readonly FakeClock _clock = new(NY(2026, 6, 1, 9, 0, 0), NewYork);

    private TaskStore NewStore(string node) => new(null, node, _clock);

    [Fact]
    public void AnExportImportsIntoAnotherDeviceWithoutRingingForThePast()
    {
        var a = NewStore("node-a");
        var daily = a.Add(new TaskDefinition
        {
            Description = "daily", Kind = ScheduleKind.Interval, Unit = IntervalUnit.Days, Start = new DateTime(2026, 5, 1, 8, 0, 0),
            Restart = RestartMode.OnSchedule,
        });
        var countdown = a.Add(new TaskDefinition { Description = "cd", Kind = ScheduleKind.Countdown, Duration = TimeSpan.FromHours(2) });
        var gone = a.Add(new TaskDefinition { Description = "deleted", Kind = ScheduleKind.Once, At = new DateTime(2026, 7, 1, 9, 0, 0) });
        a.Delete(gone.Id);
        var file = a.Export();

        _clock.UtcNow = NY(2026, 6, 3, 10);   // two daily occurrences later
        var b = NewStore("node-b");
        Assert.Equal(new ImportResult(2, 0, 0), b.Import(file));   // the deleted task isn't exported
        Assert.False(b.Status(daily.Id)!.Triggered);                // the past is passed over
        Assert.Equal(NY(2026, 6, 4, 8), b.Status(daily.Id)!.NextOccurrence);
        Assert.True(b.Status(countdown.Id)!.Finished);              // it ended at 11:00 on Jun 1

        Assert.Equal(new ImportResult(0, 0, 2), b.Import(file));   // again: nothing to do
    }

    [Fact]
    public void ImportingBringsBackADeletedTaskButNotOverANewerEdit()
    {
        var a = NewStore("node-a");
        var t = a.Add(new TaskDefinition { Description = "v1", Kind = ScheduleKind.Weekly, Days = 0b0000010 });
        var backup = a.Export();
        _clock.Advance(TimeSpan.FromMinutes(1));
        a.Save(t.Id, a.Get(t.Id)!.Def with { Description = "v2" });
        Assert.Equal(new ImportResult(0, 0, 1), a.Import(backup));
        Assert.Equal("v2", a.Get(t.Id)!.Def.Description);

        a.Delete(t.Id);
        _clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(new ImportResult(1, 0, 0), a.Import(backup));
        var back = a.Get(t.Id)!;
        Assert.False(back.Def.Deleted);
        Assert.Equal("v1", back.Def.Description);
        // The schedule wasn't changed after the export, so the revision needn't be either.
        Assert.Equal(t.State.Revision, back.State.Revision);

        // The restored task outranks the tombstone wherever they meet, even one written later
        // elsewhere (by a device that hadn't heard of the restore): it is the old incarnation's.
        var tombstone = t with { Def = t.Def with { Deleted = true }, DefVersion = new Hlc(back.DefVersion.Ms + 60_000, 0, "node-z") };
        Assert.False(MergeRules.Merge(back, tombstone).Def.Deleted);
        Assert.False(MergeRules.Merge(tombstone, back).Def.Deleted);
    }

    [Fact]
    public void ATaskRescheduledThenDeletedComesBackAsTheFileHasIt()
    {
        var a = NewStore("node-a");
        var t = a.Add(new TaskDefinition
        {
            Description = "daily", Kind = ScheduleKind.Interval, Unit = IntervalUnit.Days, Start = new DateTime(2026, 6, 1, 8, 0, 0),
            Restart = RestartMode.OnSchedule,
        });
        var backup = a.Export();
        _clock.Advance(TimeSpan.FromMinutes(1));
        var evening = a.Save(t.Id, t.Def with { Start = new DateTime(2026, 6, 1, 20, 0, 0) })!;   // revision 2
        a.Delete(t.Id);
        _clock.UtcNow = NY(2026, 6, 5, 10);
        Assert.Equal(new ImportResult(1, 0, 0), a.Import(backup));
        var back = a.Get(t.Id)!;
        Assert.Equal(new DateTime(2026, 6, 1, 8, 0, 0), back.Def.Start);
        Assert.Equal((1, 1), (back.Def.Incarnation, back.State.Incarnation));
        // It outranks the state written for 20:00 wherever they meet (a device still holding it).
        Assert.Equal(back, MergeRules.Merge(evening, back));
        Assert.False(a.Status(t.Id)!.Triggered);
        Assert.Equal(NY(2026, 6, 6, 8), a.Status(t.Id)!.NextOccurrence);
    }

    [Fact]
    public void ARecordWhoseDefinitionAndStateDifferIsRepairedOnArrival()
    {
        // As an older version's merge could leave one: the 20:00 schedule's definition with
        // the 8:00 schedule's state. That state can serve the 20:00 schedule, so it is taken as
        // its, keeping what it holds, and the task rings by its definition.
        var a = NewStore("node-a");
        var t = a.Add(new TaskDefinition
        {
            Description = "daily", Kind = ScheduleKind.Interval, Unit = IntervalUnit.Days, Start = new DateTime(2026, 6, 1, 8, 0, 0),
            Restart = RestartMode.OnSchedule,
        });
        var b = NewStore("node-b");
        b.Merge(a.All());
        _clock.Advance(TimeSpan.FromMinutes(1));
        var rescheduled = b.Save(t.Id, t.Def with { Start = new DateTime(2026, 6, 1, 20, 0, 0) })!;   // revision 2
        var c = NewStore("node-c");
        c.Merge([rescheduled with { State = t.State, StateVersion = t.StateVersion }]);
        var x = c.Get(t.Id)!;
        Assert.False(MergeRules.Mismatched(x));
        Assert.Equal((2, 2), (x.Def.ScheduleRevision, x.State.Revision));
        Assert.Equal(t.State.DoneThroughUtc, x.State.DoneThroughUtc);   // kept, not started afresh
        Assert.Equal(new DateTime(2026, 6, 1, 20, 0, 0), x.Def.Start);
        Assert.Equal(NY(2026, 6, 1, 20), c.Status(t.Id)!.NextOccurrence);
    }

    [Fact]
    public void AnExportSaysHowManyTasksItHolds()
    {
        var a = NewStore("node-a");
        a.Add(new TaskDefinition { Description = "x", Kind = ScheduleKind.Once, At = new DateTime(2026, 7, 1, 9, 0, 0) });
        var gone = a.Add(new TaskDefinition { Description = "y", Kind = ScheduleKind.Once, At = new DateTime(2026, 7, 1, 9, 0, 0) });
        a.Delete(gone.Id);
        a.Export(out int count);
        Assert.Equal(1, count);
        Assert.Equal("Exported 1 task.", TextFormat.ExportSummary(count));
        Assert.Equal("Exported 2 tasks to C:/x.json.", TextFormat.ExportSummary(2, "C:/x.json"));
    }

    /// <summary>The device a file came from goes on editing and marking done; another
    /// imports the file, and the two are linked later. The import must not outrank any of it.</summary>
    [Fact]
    public void AnImportOutranksNothingTheDeviceItCameFromDidLater()
    {
        var pc = NewStore("node-pc");
        var t = pc.Add(new TaskDefinition
        {
            Description = "pills", Kind = ScheduleKind.Interval, Unit = IntervalUnit.Days, Start = new DateTime(2026, 6, 1, 8, 0, 0),
            Restart = RestartMode.OnSchedule,
        });
        var file = pc.Export();
        _clock.UtcNow = NY(2026, 6, 2, 9);
        pc.Save(t.Id, pc.Get(t.Id)!.Def with { Description = "pills, with food" });
        _clock.UtcNow = NY(2026, 6, 3, 9);
        var phone = NewStore("node-phone");
        phone.Import(file);
        _clock.UtcNow = NY(2026, 6, 20, 9);
        pc.MarkDone(t.Id);   // done through Jun 20

        pc.Merge(phone.All());
        phone.Merge(pc.All());
        foreach (var store in new[] { pc, phone })
        {
            Assert.Equal("pills, with food", store.Get(t.Id)!.Def.Description);
            Assert.False(store.Status(t.Id)!.Triggered);   // Jun 4 to 20 were marked done on the PC
            Assert.Equal(NY(2026, 6, 21, 8), store.Status(t.Id)!.NextOccurrence);
        }
    }

    [Fact]
    public void ImportingANewerScheduleDoesNotRingForThePast()
    {
        var a = NewStore("node-a");
        var t = a.Add(new TaskDefinition
        {
            Description = "daily", Kind = ScheduleKind.Interval, Unit = IntervalUnit.Days, Start = new DateTime(2026, 6, 1, 8, 0, 0),
            Restart = RestartMode.OnSchedule,
        });
        var b = NewStore("node-b");
        b.Merge(a.All());   // b has revision 1
        _clock.Advance(TimeSpan.FromMinutes(1));
        a.Save(t.Id, t.Def with { Start = new DateTime(2026, 6, 1, 20, 0, 0) });   // revision 2, from Jun 1
        var file = a.Export();
        _clock.UtcNow = NY(2026, 6, 10, 10);
        b.MarkDone(t.Id);

        Assert.Equal(new ImportResult(0, 1, 0), b.Import(file));
        Assert.Equal(2, b.Get(t.Id)!.State.Revision);
        Assert.False(b.Status(t.Id)!.Triggered);   // Jun 1 to 9 at 20:00 are passed over
        Assert.Equal(NY(2026, 6, 10, 20), b.Status(t.Id)!.NextOccurrence);
    }

    [Fact]
    public void RecordsMissingWhatTheAppReliesOnAreSkipped()
    {
        var a = NewStore("node-a");
        a.Add(new TaskDefinition { Description = "x", Kind = ScheduleKind.Once, At = new DateTime(2026, 7, 1, 9, 0, 0) });
        var withSound = a.Add(new TaskDefinition { Description = "y", Kind = ScheduleKind.Once, At = new DateTime(2026, 7, 1, 9, 0, 0) }
            .WithSound(Platforms.Windows, new SoundRef(SoundSource.File, "C:/a.wav", "a")));
        var file = a.Export();
        var nullDescription = file.Replace("\"description\": \"x\"", "\"description\": null");
        var noSoundName = file.Replace("\"name\": \"a\"", "\"other\": 1");
        Assert.NotEqual(file, nullDescription);
        Assert.NotEqual(file, noSoundName);

        var b = NewStore("node-b");
        Assert.Equal(new ImportResult(1, 0, 0), b.Import(nullDescription));   // only "y"
        var c = NewStore("node-c");
        Assert.Equal(new ImportResult(1, 0, 0), c.Import(noSoundName));       // only "x"
        Assert.Throws<InvalidDataException>(() => c.Import("{\"app\": \"RemindMe\", \"tasks\": null}"));

        // Sync applies the same check.
        var broken = withSound with { Def = withSound.Def with { Description = null! } };
        Assert.Empty(NewStore("node-d").Merge([broken]));
    }

    [Fact]
    public void APausedCountdownImportsPausedWithItsTimeLeft()
    {
        var a = NewStore("node-a");
        var t = a.Add(new TaskDefinition { Kind = ScheduleKind.Countdown, Duration = TimeSpan.FromMinutes(30) });
        _clock.Advance(TimeSpan.FromMinutes(10));
        a.Pause(t.Id);
        var file = a.Export();
        _clock.Advance(TimeSpan.FromHours(5));
        var b = NewStore("node-b");
        b.Import(file);
        var st = b.Status(t.Id)!;
        Assert.True(st.Paused);
        Assert.Equal(TimeSpan.FromMinutes(20), st.CountdownLeft);
    }

    [Fact]
    public void AFileExportedBeforeTheRenameStillImports()
    {
        var a = NewStore("node-a");
        var t = a.Add(new TaskDefinition { Description = "x", Kind = ScheduleKind.Once, At = new DateTime(2026, 7, 1, 9, 0, 0) });
        var old = a.Export().Replace("\"app\": \"RemindMe\"", "\"app\": \"AlarmCalendar\"");
        Assert.Contains("\"AlarmCalendar\"", old);
        var b = NewStore("node-b");
        Assert.Equal(new ImportResult(1, 0, 0), b.Import(old));
        Assert.Equal("x", b.Get(t.Id)!.Def.Description);
        Assert.Throws<InvalidDataException>(() => b.Import(old.Replace("\"AlarmCalendar\"", "\"SomethingElse\"")));
    }

    [Fact]
    public void ANonExportFileIsRefused()
    {
        var a = NewStore("node-a");
        Assert.Throws<InvalidDataException>(() => a.Import("{\"tasks\": []}"));
        Assert.Throws<InvalidDataException>(() => a.Import("not json"));
        Assert.Throws<InvalidDataException>(() => a.Import("{\"app\": \"RemindMe\", \"format\": 2, \"tasks\": []}"));
    }
}
