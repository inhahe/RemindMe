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
        var t = a.Save(null, Hourly(ca));
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
        var t = a.Save(null, Hourly(ca));
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
        var t = a.Save(null, Hourly(ca));
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
        var t = a.Save(null, Hourly(ca));
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
        var t = a.Save(null, Hourly(ca));
        Sync(a, b);
        b.Delete(t.Id);
        Sync(a, b);
        Assert.Empty(a.Live());
        Assert.True(a.Get(t.Id)!.Def.Deleted);
    }

    [Fact]
    public void MergeIsOrderIndependent()
    {
        var (a, b, ca, cb) = TwoDevices();
        var t = a.Save(null, Hourly(ca));
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
        var stores = clocks.Select((c, i) => new TaskStore(null, $"n{i}", c)).ToArray();
        var rng = new Random(42);
        var ids = new List<Guid>();
        for (int step = 0; step < 200; step++)
        {
            int i = rng.Next(3);
            foreach (var c in clocks) c.Advance(TimeSpan.FromMinutes(rng.Next(1, 20)));
            var s = stores[i];
            var live = s.Live();
            switch (rng.Next(6))
            {
                case 0: ids.Add(s.Save(null, Hourly(clocks[i]) with { Description = $"t{step}" }).Id); break;
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
    }
}

public class PersistenceTests
{
    [Fact]
    public void ACorruptFileIsKeptAsideAndTheBackupUsed()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "tasks.json");
            var clock = new FakeClock(NY(2026, 6, 1, 9), NewYork);
            var s = new TaskStore(path, "a", clock);
            var t1 = s.Save(null, new TaskDefinition { Description = "one", Kind = ScheduleKind.Once, At = new DateTime(2026, 7, 1, 9, 0, 0) });
            s.Save(null, new TaskDefinition { Description = "two", Kind = ScheduleKind.Once, At = new DateTime(2026, 7, 2, 9, 0, 0) });
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
        var daily = a.Save(null, new TaskDefinition
        {
            Description = "daily", Kind = ScheduleKind.Interval, Unit = IntervalUnit.Days, Start = new DateTime(2026, 5, 1, 8, 0, 0),
            Restart = RestartMode.OnSchedule,
        });
        var countdown = a.Save(null, new TaskDefinition { Description = "cd", Kind = ScheduleKind.Countdown, Duration = TimeSpan.FromHours(2) });
        var gone = a.Save(null, new TaskDefinition { Description = "deleted", Kind = ScheduleKind.Once, At = new DateTime(2026, 7, 1, 9, 0, 0) });
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
        var t = a.Save(null, new TaskDefinition { Description = "v1", Kind = ScheduleKind.Weekly, Days = 0b0000010 });
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

        // The restored task outranks the tombstone wherever they meet.
        var tombstone = t with { Def = t.Def with { Deleted = true }, DefVersion = new Hlc(back.DefVersion.Ms - 1, 0, "node-z") };
        Assert.False(MergeRules.Merge(back, tombstone).Def.Deleted);
    }

    [Fact]
    public void ATaskRescheduledThenDeletedComesBackAsTheFileHasIt()
    {
        var a = NewStore("node-a");
        var t = a.Save(null, new TaskDefinition
        {
            Description = "daily", Kind = ScheduleKind.Interval, Unit = IntervalUnit.Days, Start = new DateTime(2026, 6, 1, 8, 0, 0),
            Restart = RestartMode.OnSchedule,
        });
        var backup = a.Export();
        _clock.Advance(TimeSpan.FromMinutes(1));
        a.Save(t.Id, t.Def with { Start = new DateTime(2026, 6, 1, 20, 0, 0) });   // revision 2
        a.Delete(t.Id);
        _clock.UtcNow = NY(2026, 6, 5, 10);
        Assert.Equal(new ImportResult(1, 0, 0), a.Import(backup));
        var back = a.Get(t.Id)!;
        Assert.Equal(new DateTime(2026, 6, 1, 8, 0, 0), back.Def.Start);
        Assert.Equal(3, back.State.Revision);   // outranks the state written for 20:00 everywhere
        Assert.False(a.Status(t.Id)!.Triggered);
        Assert.Equal(NY(2026, 6, 6, 8), a.Status(t.Id)!.NextOccurrence);
    }

    /// <summary>The definition syncs apart from the state: this device has another's new
    /// schedule (revision 2) but not yet its state, deletes the task, and imports a file of
    /// revision 1. The restored task must outrank that revision-2 state when it arrives.</summary>
    [Fact]
    public void ATombstoneWhoseScheduleIsAheadOfItsStateIsOutrankedToo()
    {
        var a = NewStore("node-a");
        var t = a.Save(null, new TaskDefinition
        {
            Description = "daily", Kind = ScheduleKind.Interval, Unit = IntervalUnit.Days, Start = new DateTime(2026, 6, 1, 8, 0, 0),
            Restart = RestartMode.OnSchedule,
        });
        var file = a.Export();
        var b = NewStore("node-b");
        b.Merge(a.All());
        _clock.Advance(TimeSpan.FromMinutes(1));
        var rescheduled = b.Save(t.Id, t.Def with { Start = new DateTime(2026, 6, 1, 20, 0, 0) });   // revision 2
        a.Merge([rescheduled with { State = a.Get(t.Id)!.State, StateVersion = a.Get(t.Id)!.StateVersion }]);   // its definition only
        Assert.Equal((2, 1), (a.Get(t.Id)!.Def.ScheduleRevision, a.Get(t.Id)!.State.Revision));
        a.Delete(t.Id);
        _clock.UtcNow = NY(2026, 6, 5, 10);

        a.Import(file);
        Assert.Equal(3, a.Get(t.Id)!.State.Revision);
        a.Merge(b.All());   // b's revision-2 state arrives
        var back = a.Get(t.Id)!;
        Assert.Equal(new DateTime(2026, 6, 1, 8, 0, 0), back.Def.Start);
        Assert.Equal(3, back.State.Revision);   // the state still belongs to the schedule shown
        Assert.Equal(NY(2026, 6, 6, 8), a.Status(t.Id)!.NextOccurrence);
    }

    [Fact]
    public void AnExportSaysHowManyTasksItHolds()
    {
        var a = NewStore("node-a");
        a.Save(null, new TaskDefinition { Description = "x", Kind = ScheduleKind.Once, At = new DateTime(2026, 7, 1, 9, 0, 0) });
        var gone = a.Save(null, new TaskDefinition { Description = "y", Kind = ScheduleKind.Once, At = new DateTime(2026, 7, 1, 9, 0, 0) });
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
        var t = pc.Save(null, new TaskDefinition
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
        var t = a.Save(null, new TaskDefinition
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
        a.Save(null, new TaskDefinition { Description = "x", Kind = ScheduleKind.Once, At = new DateTime(2026, 7, 1, 9, 0, 0) });
        var withSound = a.Save(null, new TaskDefinition { Description = "y", Kind = ScheduleKind.Once, At = new DateTime(2026, 7, 1, 9, 0, 0) }
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
        var t = a.Save(null, new TaskDefinition { Kind = ScheduleKind.Countdown, Duration = TimeSpan.FromMinutes(30) });
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
        var t = a.Save(null, new TaskDefinition { Description = "x", Kind = ScheduleKind.Once, At = new DateTime(2026, 7, 1, 9, 0, 0) });
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
