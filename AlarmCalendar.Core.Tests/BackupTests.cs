using static AlarmCalendar.Tests.Zones;

namespace AlarmCalendar.Tests;

public class BackupTests
{
    private readonly FakeClock _clock = new(NY(2026, 6, 1, 9, 0, 0), NewYork);

    private const string Me = "de193e1e";

    [Fact]
    public void BackupNamesCarryTheirTimeAndDevice()
    {
        Assert.Equal(Me, Backups.DeviceTag("DE193E1E01884990bf4119c610a1b712"));
        var at = new DateTime(2026, 11, 7, 7, 53, 0);
        Assert.Equal("alarm-calendar-backup-2026-11-07-075300-de193e1e.json", Backups.FileName(at, Me));
        Assert.Equal((at, Me), Backups.Parse(Backups.FileName(at, Me)));
        Assert.Null(Backups.Parse("alarm-calendar-tasks-2026-11-07.json"));                       // an export
        Assert.Null(Backups.Parse("alarm-calendar-backup-2026-11-07-075300-de193e1e (1).json"));  // renamed by someone
        Assert.Null(Backups.Parse("alarm-calendar-backup-2026-11-07-075300.json"));
        Assert.Null(Backups.Parse("alarm-calendar-backup-.json"));

        var names = new[] { Backups.FileName(at, Me), Backups.FileName(at.AddHours(1), "0ther0ne"), Backups.FileName(at.AddHours(-1), Me) };
        Assert.Equal((names[0], at), Backups.Newest(names, Me));
        Assert.Null(Backups.Newest(names, "nobody"));
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
        var added = Backups.Fingerprint(store.Live());
        Assert.NotEqual(edited, added);
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
        var now = new DateTime(2026, 11, 7, 12, 0, 0);
        string B(int y, int mo, int d, int h, string device = Me) => Backups.FileName(new DateTime(y, mo, d, h, 0, 0), device);
        var names = new[]
        {
            B(2026, 11, 7, 11), B(2026, 11, 7, 10), B(2026, 11, 6, 13),   // the last day: all kept
            B(2026, 11, 4, 18), B(2026, 11, 4, 9),                         // the newest of the day kept
            B(2026, 8, 9, 12), B(2026, 8, 1, 12),                          // beyond 60 days: the newest of the month
            B(2023, 6, 1, 12),                                             // beyond two years
            B(2026, 11, 4, 8, "0ther0ne"), B(2023, 6, 1, 11, "0ther0ne"),  // another device's: its own to thin
            "notes.txt", "alarm-calendar-tasks-2026-01-01.json",           // not backups: never deleted
        };
        Assert.Equal(
            [B(2026, 11, 4, 9), B(2026, 8, 1, 12), B(2023, 6, 1, 12)],
            Backups.ToDelete(names, Me, now));

        // The newest backup stays whatever its age: it is what the tasks are now.
        Assert.Empty(Backups.ToDelete([B(2020, 1, 1, 12)], Me, now));
    }
}
