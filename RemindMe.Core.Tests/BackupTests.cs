using static RemindMe.Tests.Zones;

namespace RemindMe.Tests;

public class BackupTests
{
    private const string Me = "de193e1e";
    private const string NodeId = "de193e1e01884990bf4119c610a1b712";
    private readonly FakeClock _clock = new(NY(2026, 6, 1, 9, 0, 0), NewYork);

    private static DateTimeOffset At(int y, int mo, int d, int h, int offsetHours = -4) =>
        new(y, mo, d, h, 0, 0, TimeSpan.FromHours(offsetHours));

    [Fact]
    public void BackupNamesCarryTheirTimeOffsetAndDevice()
    {
        Assert.Equal(Me, Backups.DeviceTag("DE193E1E01884990bf4119c610a1b712"));
        var at = new DateTimeOffset(2026, 11, 7, 7, 53, 0, TimeSpan.FromHours(-5));
        Assert.Equal("remindme-backup-2026-11-07-075300-0500-de193e1e.json", Backups.FileName(at, Me));
        Assert.Equal("remindme-backup-2026-07-01-120000+0530-de193e1e.json",
            Backups.FileName(new DateTimeOffset(2026, 7, 1, 12, 0, 0, new TimeSpan(5, 30, 0)), Me));
        var parsed = Backups.Parse(Backups.FileName(at, Me))!.Value;
        Assert.Equal((at, at.Offset, Me), (parsed.At, parsed.At.Offset, parsed.Device));
        Assert.Null(Backups.Parse("remindme-tasks-2026-11-07.json"));                            // an export
        Assert.Null(Backups.Parse("remindme-backup-2026-11-07-075300-0500-de193e1e (1).json"));  // renamed by someone
        Assert.Null(Backups.Parse("remindme-backup-2026-11-07-075300-de193e1e.json"));           // no offset
        Assert.Null(Backups.Parse("remindme-backup-.json"));
        Assert.Throws<ArgumentException>(() => Backups.FileName(at, "de19-3e1e"));   // Parse would never find it
        Assert.Throws<ArgumentException>(() => Backups.FileName(at, ""));
    }

    [Fact]
    public void TheNewestIsTheLatestInstantNotTheLatestClockTime()
    {
        // 10:00 in New York, then two hours later in Los Angeles: 09:00 by the local clock.
        var now = At(2026, 11, 7, 13);
        var east = Backups.FileName(At(2026, 11, 7, 10, -5), Me);
        var west = Backups.FileName(At(2026, 11, 7, 9, -8), Me);
        Assert.Equal(west, Backups.Newest([east, west], Me, now)!.Value.Name);
        var names = new[] { Backups.FileName(At(2026, 11, 7, 11), Me), Backups.FileName(At(2026, 11, 7, 12), "0ther0ne") };
        Assert.Equal(names[0], Backups.Newest(names, Me, now)!.Value.Name);
        Assert.Null(Backups.Newest(names, "nobody", now));

        // One written while the clock was set days ahead isn't the newest once it is back.
        var ahead = Backups.FileName(At(2026, 11, 10, 9), Me);
        Assert.Equal(names[0], Backups.Newest([.. names, ahead], Me, now)!.Value.Name);
    }

    [Fact]
    public void AStoreAndItsBackupAgreeEvenOnARecordAnExportLeavesOut()
    {
        // A record the app wouldn't accept today (a sound with no name), from an older
        // version's file. An export of it reads back without the name too, so a file's
        // fingerprint leaves it out; the store's must as well.
        var dir = Directory.CreateTempSubdirectory("remindme-test-");
        try
        {
            var json = $$"""
                { "format": 1, "tasks": [
                  { "id": "{{Guid.NewGuid()}}", "def": { "description": "x", "kind": "once", "at": "2026-07-01T09:00:00", "scheduleRevision": 1 },
                    "defVersion": { "ms": 1, "counter": 0, "node": "a" }, "state": { "revision": 1, "doneThroughUtc": "2026-06-01T13:00:00+00:00" },
                    "stateVersion": { "ms": 1, "counter": 1, "node": "a" } },
                  { "id": "{{Guid.NewGuid()}}", "def": { "description": "y", "kind": "once", "at": "2026-07-01T09:00:00", "scheduleRevision": 1,
                      "windowsSound": { "source": "file", "value": "C:/a.wav" } },
                    "defVersion": { "ms": 1, "counter": 2, "node": "a" }, "state": { "revision": 1, "doneThroughUtc": "2026-06-01T13:00:00+00:00" },
                    "stateVersion": { "ms": 1, "counter": 3, "node": "a" } } ] }
                """;
            File.WriteAllText(Path.Combine(dir.FullName, "tasks.json"), json);
            var store = new TaskStore(Path.Combine(dir.FullName, "tasks.json"), NodeId, _clock);
            Assert.Equal(2, store.Live().Count);
            Assert.Equal(Backups.Fingerprint(store.Live()), Backups.FingerprintOfFile(store.Export()));

            var folder = new FakeFolder();
            var runner = new BackupRunner(store, NodeId, _ => { });
            runner.Run(folder);
            _clock.Advance(TimeSpan.FromMinutes(1));
            runner.Run(folder);
            Assert.Single(folder.Files);   // not a new, identical backup on every attempt
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public void OnlyAddingEditingOrDeletingCallsForANewBackup()
    {
        var store = new TaskStore(null, "node-a", _clock);
        var t = store.Save(null, new TaskDefinition
        {
            Description = "pills", Kind = ScheduleKind.Interval, Unit = IntervalUnit.Hours, Start = new DateTime(2026, 6, 1, 9, 0, 0),
        });
        var first = Backups.Fingerprint(store.Live());

        _clock.UtcNow = NY(2026, 6, 1, 10, 0, 5);
        store.Dismiss(t.Id);
        store.MarkDone(t.Id);
        store.Pause(t.Id);
        store.Resume(t.Id);
        Assert.Equal(first, Backups.Fingerprint(store.Live()));   // state only

        store.Save(t.Id, store.Get(t.Id)!.Def with { Description = "pills, with food" });
        var edited = Backups.Fingerprint(store.Live());
        Assert.NotEqual(first, edited);
        var other = store.Save(null, new TaskDefinition { Description = "x", Kind = ScheduleKind.Once, At = new DateTime(2026, 7, 1, 9, 0, 0) });
        Assert.NotEqual(edited, Backups.Fingerprint(store.Live()));
        store.Delete(other.Id);
        Assert.Equal(edited, Backups.Fingerprint(store.Live()));   // back to the same tasks

        // A file's fingerprint is its tasks', and survives an import, which keeps versions.
        var json = store.Export();
        Assert.Equal(edited, Backups.FingerprintOfFile(json));
        var elsewhere = new TaskStore(null, "node-b", _clock);
        elsewhere.Import(json);
        Assert.Equal(edited, Backups.Fingerprint(elsewhere.Live()));
        Assert.Null(Backups.FingerprintOfFile("not json"));
    }

    [Fact]
    public void OldBackupsThinOutWithAge()
    {
        var now = At(2026, 11, 7, 12);
        string B(int y, int mo, int d, int h, string device = Me) => Backups.FileName(At(y, mo, d, h), device);
        var names = Enumerable.Range(0, 10).Select(i => B(2026, 11, 7, 11 - i)).ToList();   // the last day, and the ten newest: kept
        names.AddRange(new[]
        {
            B(2026, 11, 4, 18), B(2026, 11, 4, 9),                         // the newest of the day kept
            B(2026, 8, 9, 12), B(2026, 8, 1, 12),                          // beyond 60 days: the newest of the month
            B(2023, 6, 1, 12),                                             // beyond two years
            B(2026, 11, 4, 8, "0ther0ne"), B(2023, 6, 1, 11, "0ther0ne"),  // another device's: its own to thin
            "notes.txt", "remindme-tasks-2026-01-01.json",           // not backups: never deleted
        });
        Assert.Equal([B(2026, 11, 4, 9), B(2026, 8, 1, 12), B(2023, 6, 1, 12)], Backups.ToDelete(names, Me, now));
    }

    [Fact]
    public void AClockSetAheadDoesNotThinOutTheNewestBackups()
    {
        // Twelve backups this morning; then the clock is set three days ahead to try an alarm.
        var names = Enumerable.Range(0, 12).Select(i => Backups.FileName(At(2026, 11, 7, 8).AddMinutes(10 * i), Me)).ToList();
        var deleted = Backups.ToDelete(names, Me, At(2026, 11, 10, 12));
        Assert.Equal(2, deleted.Count);                       // only beyond the ten newest
        Assert.Equal(names.Take(2), deleted.Order());
        // The newest stays whatever its age: it is what the tasks are now.
        Assert.Empty(Backups.ToDelete([Backups.FileName(At(2020, 1, 1, 12), Me)], Me, At(2026, 11, 7, 12)));
    }

    /// <summary>A folder in memory, which can be made to fail.</summary>
    private sealed class FakeFolder : IBackupFolder
    {
        public readonly Dictionary<string, string> Files = new();
        public bool FailReads, FailWrites, FailDeletes;
        public string Name { get; set; } = "fake";
        public IEnumerable<string> List() => Files.Keys.ToList();
        public string Read(string fileName) => FailReads ? throw new IOException("offline") : Files[fileName];
        public void Write(string fileName, string contents)
        {
            if (FailWrites) throw new IOException("disk full");
            Files[fileName] = contents;
        }
        public void Delete(string fileName)
        {
            if (FailDeletes) throw new UnauthorizedAccessException("read-only");
            Files.Remove(fileName);
        }
    }

    [Fact]
    public void TheRunnerWritesOnlyWhenTheTasksChanged()
    {
        var log = new List<string>();
        var store = new TaskStore(null, NodeId, _clock);
        var t = store.Save(null, new TaskDefinition { Description = "x", Kind = ScheduleKind.Once, At = new DateTime(2026, 7, 1, 9, 0, 0) });
        var folder = new FakeFolder();
        var runner = new BackupRunner(store, NodeId, log.Add);

        runner.Run(folder);
        var first = Assert.Single(folder.Files);
        Assert.EndsWith("-0400-de193e1e.json", first.Key);   // New York's summer time
        Assert.Equal(_clock.UtcNow, runner.NewestAt);
        Assert.Null(runner.Problem);

        _clock.Advance(TimeSpan.FromMinutes(1));
        store.Pause(t.Id);
        runner.Run(folder);
        Assert.Single(folder.Files);   // state only

        store.Save(t.Id, store.Get(t.Id)!.Def with { Description = "y" });
        runner.Run(folder);
        Assert.Equal(2, folder.Files.Count);

        // Started again: it reads the newest backup and finds nothing to write.
        _clock.Advance(TimeSpan.FromMinutes(1));
        var restarted = new BackupRunner(store, NodeId, log.Add);
        restarted.Run(folder);
        Assert.Equal(2, folder.Files.Count);
        Assert.Equal(_clock.UtcNow - TimeSpan.FromMinutes(1), restarted.NewestAt);
    }

    [Fact]
    public void ABackupDatedAheadDoesNotCallForANewOneOnEveryStart()
    {
        var store = new TaskStore(null, NodeId, _clock);
        store.Save(null, new TaskDefinition { Description = "x", Kind = ScheduleKind.Once, At = new DateTime(2026, 7, 1, 9, 0, 0) });
        var folder = new FakeFolder();
        // Written three days "ahead" of a different set of tasks, while the clock was wrong.
        folder.Files[Backups.FileName(At(2026, 6, 4, 9), Me)] = "{}";

        new BackupRunner(store, NodeId, _ => { }).Run(folder);
        Assert.Equal(2, folder.Files.Count);
        _clock.Advance(TimeSpan.FromMinutes(1));
        var restarted = new BackupRunner(store, NodeId, _ => { });
        restarted.Run(folder);
        Assert.Equal(2, folder.Files.Count);
        Assert.Equal(_clock.UtcNow - TimeSpan.FromMinutes(1), restarted.NewestAt);   // not three days hence
    }

    [Fact]
    public void AFolderEmptiedSinceTheLastBackupGetsANewOne()
    {
        var store = new TaskStore(null, NodeId, _clock);
        var t = store.Save(null, new TaskDefinition { Description = "x", Kind = ScheduleKind.Once, At = new DateTime(2026, 7, 1, 9, 0, 0) });
        var folder = new FakeFolder();
        var runner = new BackupRunner(store, NodeId, _ => { });
        runner.Run(folder);
        folder.Files.Clear();   // by hand, or a cloud drive freeing space
        _clock.Advance(TimeSpan.FromMinutes(1));
        store.Pause(t.Id);      // a change that calls for no backup of its own
        runner.Run(folder);
        Assert.Single(folder.Files);
    }

    [Fact]
    public void TwoBackupsInOneSecondGetTwoNames()
    {
        var store = new TaskStore(null, NodeId, _clock);
        var t = store.Save(null, new TaskDefinition { Description = "x", Kind = ScheduleKind.Once, At = new DateTime(2026, 7, 1, 9, 0, 0) });
        var folder = new FakeFolder();
        var runner = new BackupRunner(store, NodeId, _ => { });
        runner.Run(folder);
        store.Save(t.Id, store.Get(t.Id)!.Def with { Description = "y" });   // within the same second
        runner.Run(folder);
        Assert.Equal(2, folder.Files.Count);
        Assert.Contains(Backups.FileName(At(2026, 6, 1, 9).AddSeconds(1), Me), folder.Files.Keys);
        Assert.Equal(At(2026, 6, 1, 9).AddSeconds(1), runner.NewestAt);
    }

    [Fact]
    public void AnUnexpectedFailureIsReportedNotThrown()
    {
        var log = new List<string>();
        var store = new TaskStore(null, NodeId, _clock);
        var runner = new BackupRunner(store, NodeId, log.Add);
        runner.Run(new BrokenFolder());
        Assert.Equal("The last backup failed: broken", runner.Problem);
        Assert.Single(log, l => l.StartsWith("Backing up to broken failed: System.InvalidOperationException"));
    }

    private sealed class BrokenFolder : IBackupFolder
    {
        public string Name => "broken";
        public IEnumerable<string> List() => throw new InvalidOperationException("broken");
        public string Read(string fileName) => throw new InvalidOperationException("broken");
        public void Write(string fileName, string contents) => throw new InvalidOperationException("broken");
        public void Delete(string fileName) => throw new InvalidOperationException("broken");
    }

    [Fact]
    public void AnUnreadableNewestBackupDoesNotStopBackups()
    {
        var log = new List<string>();
        var store = new TaskStore(null, NodeId, _clock);
        store.Save(null, new TaskDefinition { Description = "x", Kind = ScheduleKind.Once, At = new DateTime(2026, 7, 1, 9, 0, 0) });
        var folder = new FakeFolder();
        new BackupRunner(store, NodeId, log.Add).Run(folder);
        _clock.Advance(TimeSpan.FromMinutes(1));

        folder.FailReads = true;   // left online-only in a cloud drive, say, and offline now
        var runner = new BackupRunner(store, NodeId, log.Add);
        runner.Run(folder);
        Assert.Equal(2, folder.Files.Count);
        Assert.Null(runner.Problem);
        Assert.Contains(log, l => l.StartsWith("Couldn't read the newest backup"));
    }

    [Fact]
    public void AFailedBackupIsReportedLoggedOnceAndTriedAgain()
    {
        var log = new List<string>();
        var store = new TaskStore(null, NodeId, _clock);
        store.Save(null, new TaskDefinition { Description = "x", Kind = ScheduleKind.Once, At = new DateTime(2026, 7, 1, 9, 0, 0) });
        var folder = new FakeFolder { FailWrites = true };
        var runner = new BackupRunner(store, NodeId, log.Add);

        runner.Run(folder);
        runner.Run(folder);
        Assert.Equal("The last backup failed: disk full", runner.Problem);
        Assert.Single(log, l => l.StartsWith("Couldn't back up"));
        Assert.Empty(folder.Files);

        folder.FailWrites = false;
        runner.Run(folder);
        Assert.Single(folder.Files);
        Assert.Null(runner.Problem);
    }

    [Fact]
    public void TheRunnerThinsOutAfterWritingAndAnOldBackupItCannotDeleteIsLoggedOnce()
    {
        var log = new List<string>();
        var store = new TaskStore(null, NodeId, _clock);
        var t = store.Save(null, new TaskDefinition { Description = "x", Kind = ScheduleKind.Once, At = new DateTime(2026, 7, 1, 9, 0, 0) });
        var folder = new FakeFolder();
        // Twelve backups from one day a week ago: older than a day, so beyond the ten newest
        // only the day's newest would stay, and it is among the ten.
        string Old(int minute) => Backups.FileName(At(2026, 5, 25, 8).AddMinutes(minute), Me);
        for (int i = 0; i < 12; i++) folder.Files[Old(i)] = "{}";
        var runner = new BackupRunner(store, NodeId, log.Add);
        folder.FailDeletes = true;
        runner.Run(folder);   // writes (the tasks differ), then fails to delete 08:02, 08:01, 08:00
        _clock.Advance(TimeSpan.FromMinutes(1));
        store.Save(t.Id, store.Get(t.Id)!.Def with { Description = "y" });
        runner.Run(folder);   // writes, then fails again, now from 08:03
        Assert.Equal(14, folder.Files.Count);
        Assert.Single(log, l => l.StartsWith("Couldn't delete the old backup " + Old(1)));   // two tries, one line
        Assert.Single(log, l => l.StartsWith("Couldn't delete the old backup " + Old(3)));

        folder.FailDeletes = false;
        _clock.Advance(TimeSpan.FromMinutes(1));
        store.Save(t.Id, store.Get(t.Id)!.Def with { Description = "z" });
        runner.Run(folder);
        Assert.Equal(10, folder.Files.Count);   // the three new ones and seven of a week ago
        Assert.Contains(Old(11), folder.Files.Keys);
        Assert.DoesNotContain(Old(4), folder.Files.Keys);
    }

    [Fact]
    public void TheSettingsLineSaysWhereBackupsStand()
    {
        var now = NY(2026, 6, 1, 15);
        Assert.Equal("Automatic backups are off.", TextFormat.BackupStatus(false, NY(2026, 6, 1, 9), "The last backup failed: x", now, NewYork));
        Assert.Equal("The last backup failed: x", TextFormat.BackupStatus(true, NY(2026, 6, 1, 9), "The last backup failed: x", now, NewYork));
        Assert.Equal("No backup yet.", TextFormat.BackupStatus(true, null, null, now, NewYork));
        Assert.StartsWith("Last backup: today ", TextFormat.BackupStatus(true, NY(2026, 6, 1, 9), null, now, NewYork));
    }
}
