namespace RemindMe;

/// <summary>What changed in a <see cref="TaskStore"/>.</summary>
/// <param name="Tasks">The new versions of every task that changed (tombstones included).</param>
/// <param name="FromSync">True when the change arrived from another device.</param>
public sealed record StoreChange(IReadOnlyList<AlarmTask> Tasks, bool FromSync);

/// <summary>The contents of <c>tasks.json</c>.</summary>
internal sealed class StoreFile
{
    public int Format { get; set; } = 1;
    /// <summary>The latest version this node issued or saw, so versions keep rising across restarts.</summary>
    public Hlc Clock { get; set; }
    public List<AlarmTask> Tasks { get; set; } = [];
}

/// <summary>A file of exported tasks (S8).</summary>
internal sealed class ExportFile
{
    public string App { get; set; } = "";
    public int Format { get; set; } = 1;
    public DateTimeOffset ExportedUtc { get; set; }
    public List<AlarmTask> Tasks { get; set; } = [];
}

/// <summary>What an import did.</summary>
/// <param name="Added">Tasks this device didn't have, or had deleted.</param>
/// <param name="Updated">Tasks the file held a newer version of.</param>
/// <param name="Unchanged">Tasks this device already had as they are, or newer.</param>
public sealed record ImportResult(int Added, int Updated, int Unchanged);

/// <summary>One line of a sync digest: enough to tell which side holds the newer record.</summary>
public sealed record DigestEntry(Guid Id, int Incarnation, bool Deleted, Hlc DefVersion, Hlc ScheduleStamp, Hlc RestartVersion,
    int StateIncarnation, Hlc StateScheduleStamp, Hlc StateVersion, DateTimeOffset DoneThroughUtc);

/// <summary>
/// What decides which of two copies of a task's definition wins, all but its schedule and its
/// restart mode (the description, the reminder interval, the sounds, and whether it is
/// deleted): the later
/// <see cref="TaskDefinition.Incarnation"/>, then, within one, a deletion over any edit, then
/// the newer version. So a deletion stands against an edit made on another device before the
/// deletion reached it: that edit was made without knowing of it, however much later. Only
/// bringing the task back on purpose, which starts a new incarnation, outranks a deletion.
/// </summary>
public readonly record struct DefRank(int Incarnation, bool Deleted, Hlc Version) : IComparable<DefRank>
{
    public static DefRank Of(AlarmTask t) => new(t.Def.Incarnation, t.Def.Deleted, t.DefVersion);

    public static DefRank Of(DigestEntry e) => new(e.Incarnation, e.Deleted, e.DefVersion);

    public int CompareTo(DefRank other) =>
        Incarnation != other.Incarnation ? Incarnation.CompareTo(other.Incarnation)
        : Deleted != other.Deleted ? Deleted.CompareTo(other.Deleted)
        : Version.CompareTo(other.Version);
}

/// <summary>
/// Which of two schedules wins, a definition's (the fields <see cref="TaskDefinition.WithScheduleOf"/>
/// copies) or the one a state was written for: the later incarnation, then the later stamp,
/// that is the schedule set last (a schedule set after another was seen has a later stamp, as
/// every version this device issues is later than all it has seen). A definition's schedule
/// and a state rank alike, so the two that win a merge are always of one schedule: a
/// countdown's definition with a one-time task's state would have no end, and never ring.
/// </summary>
public readonly record struct ScheduleRank(int Incarnation, Hlc Stamp) : IComparable<ScheduleRank>
{
    public static ScheduleRank Of(TaskDefinition d) => new(d.Incarnation, d.ScheduleStamp);

    public static ScheduleRank Of(TaskState s) => new(s.Incarnation, s.ScheduleStamp);

    public static ScheduleRank OfDefinition(DigestEntry e) => new(e.Incarnation, e.ScheduleStamp);

    public int CompareTo(ScheduleRank other) =>
        Incarnation != other.Incarnation ? Incarnation.CompareTo(other.Incarnation) : Stamp.CompareTo(other.Stamp);
}

/// <summary>
/// Which of two restart modes wins: the later incarnation's, then the later set
/// (<see cref="TaskDefinition.RestartVersion"/>). It merges on its own: any state serves
/// either mode.
/// </summary>
public readonly record struct RestartRank(int Incarnation, Hlc Version) : IComparable<RestartRank>
{
    public static RestartRank Of(TaskDefinition d) => new(d.Incarnation, d.RestartVersion);

    public static RestartRank Of(DigestEntry e) => new(e.Incarnation, e.RestartVersion);

    public int CompareTo(RestartRank other) =>
        Incarnation != other.Incarnation ? Incarnation.CompareTo(other.Incarnation) : Version.CompareTo(other.Version);
}

/// <summary>
/// What decides which of two copies of a task's state wins: the one of the later schedule
/// (<see cref="ScheduleRank"/>), then the newer version. Within one schedule, "done" merges by
/// maximum, whichever wins.
/// </summary>
public readonly record struct StateRank(ScheduleRank Schedule, Hlc Version) : IComparable<StateRank>
{
    public static StateRank Of(AlarmTask t) => new(ScheduleRank.Of(t.State), t.StateVersion);

    public static StateRank Of(DigestEntry e) =>
        new(new ScheduleRank(e.StateIncarnation, e.StateScheduleStamp), e.StateVersion);

    public bool SameSchedule(StateRank other) => Schedule.CompareTo(other.Schedule) == 0;

    public int CompareTo(StateRank other) => Schedule.CompareTo(other.Schedule) is var c and not 0 ? c : Version.CompareTo(other.Version);
}

/// <summary>The merge rules of design.md → <i>Sync merge</i>.</summary>
public static class MergeRules
{
    /// <summary>The most times a task is taken to have been brought back, and the latest
    /// schedule revision. A record claiming more (or fewer than none) is malformed, and this
    /// device never writes more: a restore or a schedule change that would stops there
    /// (<see cref="Next"/>), so arithmetic on them can't overflow.</summary>
    public const int MaxIncarnation = 1_000_000, MaxRevision = 1_000_000_000;

    /// <summary>One more than <paramref name="n"/>, but no more than <paramref name="max"/>.
    /// At the ceiling, a restore no longer outranks its tombstone, and a revision stops
    /// counting (schedules rank by their stamps): corrupt input, not use, gets there.</summary>
    public static int Next(int n, int max) => n < max ? n + 1 : max;

    /// <summary>The record both sides converge on, whichever order they see the two in. The
    /// definition's schedule merges apart from the rest of it, with the state, and its restart
    /// mode on its own: an edit of one part doesn't undo one of another made elsewhere, and
    /// the schedule and the state that win are of one schedule.</summary>
    public static AlarmTask Merge(AlarmTask local, AlarmTask remote)
    {
        var rest = DefRank.Of(remote).CompareTo(DefRank.Of(local)) > 0 ? remote : local;
        var schedule = ScheduleRank.Of(remote.Def).CompareTo(ScheduleRank.Of(local.Def)) > 0 ? remote.Def : local.Def;
        var restart = RestartRank.Of(remote.Def).CompareTo(RestartRank.Of(local.Def)) > 0 ? remote.Def : local.Def;
        StateRank localRank = StateRank.Of(local), remoteRank = StateRank.Of(remote);
        var winner = remoteRank.CompareTo(localRank) > 0 ? remote : local;
        // Within one schedule, "done" only ever moves forward, whichever write wins. A newer
        // schedule (a schedule edit, or a restore) replaces it outright, which is also how a
        // DoneThrough left in the future by a wrong clock gets cleared.
        var state = localRank.SameSchedule(remoteRank)
            ? winner.State with { DoneThroughUtc = TimeMath.Max(local.State.DoneThroughUtc, remote.State.DoneThroughUtc) }
            : winner.State;
        return local with
        {
            Def = rest.Def.WithScheduleOf(schedule).WithRestartOf(restart), DefVersion = rest.DefVersion,
            State = state, StateVersion = winner.StateVersion,
        };
    }

    /// <summary>True when <paramref name="local"/> holds something <paramref name="peer"/>'s
    /// copy lacks, so it should be sent.</summary>
    public static bool IsNewerThan(AlarmTask local, DigestEntry peer)
    {
        StateRank mine = StateRank.Of(local), theirs = StateRank.Of(peer);
        return DefRank.Of(local).CompareTo(DefRank.Of(peer)) > 0
            || ScheduleRank.Of(local.Def).CompareTo(ScheduleRank.OfDefinition(peer)) > 0
            || RestartRank.Of(local.Def).CompareTo(RestartRank.Of(peer)) > 0
            || mine.CompareTo(theirs) > 0
            || (mine.SameSchedule(theirs) && local.State.DoneThroughUtc > peer.DoneThroughUtc);
    }

    public static DigestEntry DigestOf(AlarmTask t) =>
        new(t.Id, t.Def.Incarnation, t.Def.Deleted, t.DefVersion, t.Def.ScheduleStamp, t.Def.RestartVersion,
            t.State.Incarnation, t.State.ScheduleStamp, t.StateVersion, t.State.DoneThroughUtc);

    /// <summary>True when <paramref name="r"/>, a record from another device or a file, has
    /// everything the app relies on. JSON can leave a member null whatever its declared type
    /// says (an explicit null, or a missing constructor parameter such as a sound's name),
    /// and a record stored with one would break every screen that shows it. Incarnations and
    /// revisions must lie in 0 to <see cref="MaxIncarnation"/> and <see cref="MaxRevision"/>,
    /// and a state can't be of a later incarnation than its definition: merging keeps that,
    /// and such a state would outrank any this device can write.</summary>
    public static bool IsWellFormed(AlarmTask? r) =>
        r is { Def: { Description: not null } d, State: { } s } && r.Id != Guid.Empty
        && d.Incarnation is >= 0 and <= MaxIncarnation && s.Incarnation >= 0 && s.Incarnation <= d.Incarnation
        && d.ScheduleRevision is >= 0 and <= MaxRevision && s.Revision is >= 0 and <= MaxRevision
        && IsWellFormed(d.WindowsSound) && IsWellFormed(d.AndroidSound);

    private static bool IsWellFormed(SoundRef? s) => s is null || (s.Value is not null && s.Name is not null);

    /// <summary>True when <paramref name="merged"/>, the result of merging into
    /// <paramref name="local"/>, differs from it: it must be stored, and passed on.</summary>
    public static bool Changed(AlarmTask local, AlarmTask merged) => merged != local;

    /// <summary><paramref name="r"/>, written before schedules had stamps (it has none),
    /// with one: <see cref="TaskDefinition.LegacyScheduleStamp"/>, which every device works
    /// out alike, on its definition, and on its state if that is of the same schedule (its
    /// incarnation and revision). Its restart mode, then, merged with the definition: its
    /// version is the definition's. Stamped records are returned as they are.</summary>
    public static AlarmTask WithLegacyStamps(AlarmTask r)
    {
        if (IsUnstamped(r.Def.RestartVersion)) r = r with { Def = r.Def with { RestartVersion = r.DefVersion } };
        if (!IsUnstamped(r.Def.ScheduleStamp)) return r;
        var stamp = r.Def.LegacyScheduleStamp();
        var s = r.State;
        bool same = IsUnstamped(s.ScheduleStamp) && s.Incarnation == r.Def.Incarnation && s.Revision == r.Def.ScheduleRevision;
        return r with { Def = r.Def with { ScheduleStamp = stamp }, State = same ? s with { ScheduleStamp = stamp } : s };
    }

    private static bool IsUnstamped(Hlc h) => h.CompareTo(Hlc.Zero) == 0;

    /// <summary>Whether a live task's definition and state are of different schedules, or its
    /// state can't serve its schedule: merging never makes one of records that aren't, but
    /// older builds did (<see cref="StateFits"/> tells which can still ring).</summary>
    public static bool Mismatched(AlarmTask t) =>
        !t.Def.Deleted && (ScheduleRank.Of(t.Def).CompareTo(ScheduleRank.Of(t.State)) != 0 || !StateFits(t));

    /// <summary>Whether <paramref name="t"/>'s state can serve its definition's schedule, of
    /// whichever schedule it was written for: all can but a countdown's without an end
    /// (another kind's state), which never rings. One that can keeps its "done", its anchor and
    /// its snooze.</summary>
    public static bool StateFits(AlarmTask t) => t.Def.Kind != ScheduleKind.Countdown || t.State.CountdownEndsUtc is not null;
}

/// <summary>
/// The device's tasks: persistence, every user action, and merging records from other
/// devices. Thread-safe; <see cref="Changed"/> is raised on whichever thread made the
/// change, after the store's lock is released.
/// </summary>
public sealed class TaskStore
{
    private readonly object _lock = new();
    private readonly Dictionary<Guid, AlarmTask> _tasks = new();
    private readonly string? _path;
    private readonly IClock _clock;
    private readonly Action<string>? _log;

    public HlcClock Versions { get; }
    public string NodeId { get; }
    public IClock Clock => _clock;

    public event Action<StoreChange>? Changed;

    /// <param name="path">The <c>tasks.json</c> to load and save; null keeps the store in memory.</param>
    /// <param name="nodeId">This device's sync node ID, stamped on its versions.</param>
    public TaskStore(string? path, string nodeId, IClock clock, Action<string>? log = null)
    {
        _path = path;
        _clock = clock;
        _log = log;
        NodeId = nodeId;
        var file = path is null ? null : Json.ReadWithFallback(path, Json.Files.StoreFile, log);
        foreach (var t in file?.Tasks ?? []) _tasks[t.Id] = t;
        var last = file?.Clock ?? Hlc.Zero;
        foreach (var t in _tasks.Values)
        {
            if (t.DefVersion > last) last = t.DefVersion;
            if (t.StateVersion > last) last = t.StateVersion;
            if (t.Def.ScheduleStamp > last) last = t.Def.ScheduleStamp;
            if (t.State.ScheduleStamp > last) last = t.State.ScheduleStamp;
            if (t.Def.RestartVersion > last) last = t.Def.RestartVersion;
        }
        Versions = new HlcClock(nodeId, () => _clock.UtcNow.ToUnixTimeMilliseconds(), last);
        bool changed = false;
        foreach (var t in _tasks.Values.ToList())
        {
            var fixedUp = Repaired(MergeRules.WithLegacyStamps(t), _clock.UtcNow);
            if (fixedUp == t) continue;
            _tasks[t.Id] = fixedUp;
            changed = true;
        }
        if (changed) SaveLocked();
    }

    /// <summary><paramref name="t"/>, if its definition and state are of different schedules
    /// (<see cref="MergeRules.Mismatched"/>: only older builds made such tasks), mended, and
    /// logged. A state that can serve the definition's schedule (<see cref="MergeRules.StateFits"/>)
    /// is taken as that schedule's, keeping its "done", anchor and snooze: every device does the
    /// same with the same record, so nothing new is written. One that can't (it would never
    /// ring) gives way to a new schedule, the one the definition describes, starting now.
    /// Otherwise <paramref name="t"/> itself.</summary>
    private AlarmTask Repaired(AlarmTask t, DateTimeOffset now)
    {
        if (!MergeRules.Mismatched(t)) return t;
        string name = TextFormat.Snippet(t.Def.Description, 40);
        if (MergeRules.StateFits(t))
        {
            _log?.Invoke($"\"{name}\": its state was of another of its schedules (an older version's merge); "
                + "it is kept, for the schedule it has.");
            return t with
            {
                State = t.State with { Incarnation = t.Def.Incarnation, Revision = t.Def.ScheduleRevision, ScheduleStamp = t.Def.ScheduleStamp },
            };
        }
        // Its stamp is worked out from the schedule, as a legacy one is (milliseconds 0), and
        // not taken from the clock: every device that repairs this task (upgrading on different
        // days, say) gives it the same one, and it ranks below any change made since, on a
        // device that repaired it before.
        // Its state too is the one every device writes for this record: started from when the
        // record was last written, not from now, and versioned below any real state (milliseconds
        // 0). So repairs made on different days are one, and what was marked done, snoozed or
        // paused since on a device that repaired first outranks it. A countdown whose end has
        // passed rings late, as any missed alarm does.
        var def = t.Def with { ScheduleRevision = NextRevision(t) };
        def = def with { ScheduleStamp = def.LegacyScheduleStamp() };
        var written = DateTimeOffset.FromUnixTimeMilliseconds(Math.Max(t.DefVersion.Ms, t.StateVersion.Ms));
        _log?.Invoke($"\"{name}\": its state was another kind of schedule's (an older version's merge), so it "
            + "couldn't ring; it starts afresh on its schedule, from when it was last changed.");
        return t with { Def = def, State = AlarmEngine.FreshState(t.State, def, written), StateVersion = RepairedVersion };
    }

    /// <summary>The version of a repaired state (<see cref="Repaired"/>): the same on every
    /// device, and below any a device issues.</summary>
    private static readonly Hlc RepairedVersion = new(0, 0, "~repaired");

    /// <summary>The revision of a new schedule of <paramref name="t"/>: past both its
    /// definition's and its state's (an older version's records can have the state's ahead).
    /// A count, for reading: schedules rank by their stamps.</summary>
    private static int NextRevision(AlarmTask t) =>
        MergeRules.Next(Math.Max(t.Def.ScheduleRevision, t.State.Revision), MergeRules.MaxRevision);

    /// <summary>Every task, deleted ones (tombstones) included.</summary>
    public IReadOnlyList<AlarmTask> All() { lock (_lock) return _tasks.Values.ToList(); }

    /// <summary>The tasks that haven't been deleted.</summary>
    public IReadOnlyList<AlarmTask> Live() { lock (_lock) return _tasks.Values.Where(t => !t.Def.Deleted).ToList(); }

    public AlarmTask? Get(Guid id) { lock (_lock) return _tasks.GetValueOrDefault(id); }

    public TaskStatus? Status(Guid id) =>
        Get(id) is { } t ? AlarmEngine.Evaluate(t, _clock.UtcNow, _clock.Zone) : null;

    public IReadOnlyList<DigestEntry> Digest() { lock (_lock) return _tasks.Values.Select(MergeRules.DigestOf).ToList(); }

    // ---- User actions --------------------------------------------------------------

    /// <summary>Adds a new task, as the editor made it.</summary>
    /// <param name="def">The definition; its ScheduleRevision and ScheduleStamp, Deleted and
    /// Incarnation are set here.</param>
    /// <returns>The stored task.</returns>
    public AlarmTask Add(TaskDefinition def)
    {
        AlarmTask result;
        lock (_lock)
        {
            def = def with
            {
                ScheduleRevision = 1, ScheduleStamp = Versions.Tick(), RestartVersion = Versions.Tick(), Deleted = false, Incarnation = 0,
            };
            result = new AlarmTask
            {
                Id = Guid.NewGuid(),
                Def = def,
                DefVersion = Versions.Tick(),
                State = AlarmEngine.FreshState(null, def, _clock.UtcNow),
                StateVersion = Versions.Tick(),
            };
            _tasks[result.Id] = result;
            SaveLocked();
        }
        Raise([result], false);
        return result;
    }

    /// <summary>Saves the editor's result for the existing task <paramref name="id"/>.</summary>
    /// <param name="def">The definition as edited; its ScheduleRevision and ScheduleStamp,
    /// Deleted and Incarnation are set here.</param>
    /// <param name="restartCountdown">Start the countdown again from now, running even if it
    /// was paused, though its length didn't change: the countdown the task has, or the one the
    /// editor makes it. Ignored for any other kind: a paused task saved as another kind stays
    /// paused.</param>
    /// <param name="editedFrom">The definition the editor opened with. Only what the editor
    /// changed from it is saved, laid on the task as it is now: the schedule's times as a
    /// whole, and each other field on its own. So what changed elsewhere
    /// meanwhile (synced in while the editor was open), and not in the editor, stays. Null:
    /// the task as it is now.</param>
    /// <returns>The stored task; null, with nothing saved, when there is no such task or it
    /// has been deleted (here or on a linked device): an editor left open across a deletion
    /// mustn't undo it. Keeping the edits means adding them as a new task.</returns>
    public AlarmTask? Save(Guid id, TaskDefinition def, bool restartCountdown = false, TaskDefinition? editedFrom = null) =>
        Write(id, _ => def, editedFrom, restartCountdown);

    /// <summary>Changes the definition without touching when the task comes up (the alarm
    /// window's reminder interval and restart mode, a sound choice). <paramref name="change"/>
    /// is applied to the task as it is now.</summary>
    /// <returns>The stored task; null, with nothing changed, when there is no such task or
    /// it has been deleted.</returns>
    /// <exception cref="ArgumentException"><paramref name="change"/> changes when the task
    /// comes up: that is <see cref="Save"/>'s, which starts a new schedule.</exception>
    public AlarmTask? Edit(Guid id, Func<TaskDefinition, TaskDefinition> change) => Write(id, d =>
    {
        var changed = change(d);
        if (d.ScheduleDiffers(changed)) throw new ArgumentException("Edit doesn't change when a task comes up: Save does.", nameof(change));
        return changed;
    }, null, false);

    /// <summary>Starts the countdown <paramref name="id"/> is again, from now (the list's
    /// "Restart countdown"). Nothing for a task of another kind.</summary>
    /// <returns>The stored task; null when there is no such task or it has been deleted.</returns>
    public AlarmTask? RestartCountdown(Guid id) => Write(id, d => d, null, restartCountdown: true);

    /// <summary>Saves what <paramref name="edit"/> makes of the task as it is now, changed from
    /// <paramref name="editedFrom"/> (or from the task as it is now): see <see cref="Save"/>.</summary>
    private AlarmTask? Write(Guid id, Func<TaskDefinition, TaskDefinition> edit, TaskDefinition? editedFrom, bool restartCountdown)
    {
        var now = _clock.UtcNow;
        AlarmTask result;
        lock (_lock)
        {
            if (!_tasks.TryGetValue(id, out var old) || old.Def.Deleted) return null;
            var def = edit(old.Def);
            var from = editedFrom ?? old.Def;
            // The parts merge apart (MergeRules.Merge), so each gets a new version only when it
            // changes. A new schedule outranks every one this device has seen of the task (its
            // stamp is later than all of them); unchanged, it is kept exactly, fields that don't
            // count included, so that one schedule is always the same everywhere.
            bool newTimes = from.ScheduleDiffers(def);
            var times = newTimes ? def : old.Def;   // the times: the editor's, or the task's as it is now
            bool restart = restartCountdown && times.Kind == ScheduleKind.Countdown;
            var rest = old.Def.WithRestEditsOf(from, def);
            bool restChanged = rest != old.Def;
            if (def.Restart != from.Restart) rest = rest with { Restart = def.Restart, RestartVersion = Versions.Tick() };
            result = old;
            if (newTimes || restart)
            {
                rest = rest.WithScheduleOf(times) with { ScheduleRevision = NextRevision(old), ScheduleStamp = Versions.Tick() };
                result = result with { State = AlarmEngine.FreshState(old.State, rest, now, keepPaused: !restart), StateVersion = Versions.Tick() };
            }
            result = result with { Def = rest, DefVersion = restChanged ? Versions.Tick() : old.DefVersion };
            if (result == old) return old;
            _tasks[result.Id] = result;
            SaveLocked();
        }
        Raise([result], false);
        return result;
    }

    public AlarmTask? MarkDone(Guid id) => ChangeState(id, t => AlarmEngine.MarkDone(t, _clock.UtcNow, _clock.Zone));

    /// <param name="remindIn">Remind after this, for this once, instead of the task's reminder interval.</param>
    public AlarmTask? Dismiss(Guid id, TimeSpan? remindIn = null) =>
        ChangeState(id, t => AlarmEngine.Dismiss(t, _clock.UtcNow, _clock.Zone, remindIn));

    public AlarmTask? Pause(Guid id) => ChangeState(id, t => AlarmEngine.Pause(t, _clock.UtcNow));

    public AlarmTask? Resume(Guid id) => ChangeState(id, t => AlarmEngine.Resume(t, _clock.UtcNow, _clock.Zone));

    public void Delete(Guid id)
    {
        AlarmTask result;
        lock (_lock)
        {
            if (!_tasks.TryGetValue(id, out var old) || old.Def.Deleted) return;
            result = old with { Def = old.Def with { Deleted = true }, DefVersion = Versions.Tick() };
            _tasks[id] = result;
            SaveLocked();
        }
        Raise([result], false);
    }

    private AlarmTask? ChangeState(Guid id, Func<AlarmTask, TaskState> change)
    {
        AlarmTask result;
        lock (_lock)
        {
            if (!_tasks.TryGetValue(id, out var old) || old.Def.Deleted) return null;
            var state = change(old);
            if (state == old.State) return old;
            result = old with { State = state, StateVersion = Versions.Tick() };
            _tasks[id] = result;
            SaveLocked();
        }
        Raise([result], false);
        return result;
    }

    // ---- Export and import (S8) ----------------------------------------------------

    /// <summary>The app name in exports and backups written before the app was renamed
    /// RemindMe, which import still reads: the same format under its old name.</summary>
    private const string LegacyExportApp = "AlarmCalendar";

    /// <summary>Every task that isn't deleted, as the JSON of an export file.</summary>
    public string Export() => Export(out _);

    /// <param name="count">How many tasks the file holds.</param>
    public string Export(out int count)
    {
        var file = new ExportFile { App = Sync.Protocol.App, ExportedUtc = _clock.UtcNow, Tasks = Live().OrderBy(t => t.Id).ToList() };
        count = file.Tasks.Count;
        return System.Text.Json.JsonSerializer.Serialize(file, Json.Files.ExportFile);
    }

    /// <summary>
    /// Imports an export file. Each record merges by the sync rules, keeping the versions
    /// it was exported with, so an old file can't undo newer edits, here or, once linked,
    /// on the device it came from. Whatever came up before now is passed over (for a paused
    /// task, when it is resumed), so importing doesn't ring for the past: where the file's
    /// state replaces this device's, its DoneThrough is raised to now, which the sync rules
    /// carry as "done" moving forward. A task deleted here comes back as a new incarnation
    /// (<see cref="TaskDefinition.Incarnation"/>), to outrank the deletion, unless the file
    /// holds a later incarnation than the deletion's already: that merges as it is.
    /// The changes reach linked devices like any edit made here.
    /// </summary>
    /// <exception cref="InvalidDataException">The text isn't an export file of this app.</exception>
    public ImportResult Import(string json)
    {
        ExportFile? file;
        try { file = System.Text.Json.JsonSerializer.Deserialize(json, Json.Files.ExportFile); }
        catch (System.Text.Json.JsonException e) { throw new InvalidDataException("This isn't a file of exported tasks.", e); }
        if (file is null || file.App is not (Sync.Protocol.App or LegacyExportApp) || file.Tasks is null)
            throw new InvalidDataException("This isn't a file of exported tasks.");
        if (file.Format > 1)
            throw new InvalidDataException("This file was exported by a newer version of the app.");

        var now = _clock.UtcNow;
        int added = 0, updated = 0, unchanged = 0;
        var changed = new List<AlarmTask>();
        lock (_lock)
        {
            foreach (var record in file.Tasks)
            {
                if (!MergeRules.IsWellFormed(record) || record.Def.Deleted) continue;
                Observe(record);
                var r = MergeRules.WithLegacyStamps(record);
                _tasks.TryGetValue(r.Id, out var local);
                AlarmTask result;
                if (local is null)
                {
                    result = PassOverPast(r, now);
                    added++;
                }
                else if (local.Def.Deleted && DefRank.Of(local).CompareTo(DefRank.Of(r)) > 0)
                {
                    result = Restore(local, r, now);
                    added++;
                }
                else
                {
                    // A deleted task whose copy here the file's outranks (a later incarnation:
                    // it was restored elsewhere) merges like any other, and comes back as it is.
                    bool laterSchedule = ScheduleRank.Of(r.State).CompareTo(ScheduleRank.Of(local.State)) > 0;
                    result = MergeRules.Merge(local, laterSchedule ? PassOverPast(r, now) : r);
                    if (!MergeRules.Changed(local, result))
                    {
                        unchanged++;
                        continue;
                    }
                    if (local.Def.Deleted && !result.Def.Deleted) added++;
                    else updated++;
                }
                result = Repaired(result, now);   // a file of an older version's can hold such a task
                _tasks[r.Id] = result;
                changed.Add(result);
            }
            if (changed.Count > 0) SaveLocked();
        }
        if (changed.Count > 0) Raise(changed, false);
        return new ImportResult(added, updated, unchanged);
    }

    /// <summary><paramref name="r"/> with whatever came up before <paramref name="now"/>
    /// passed over: DoneThrough raised to now, which within one schedule only ever rises, so
    /// the record keeps its version. A paused task keeps its own: resuming passes over the
    /// past anyway, and a paused countdown's end may lie in the past until it is resumed.</summary>
    private static AlarmTask PassOverPast(AlarmTask r, DateTimeOffset now) =>
        r.State.PausedAtUtc is not null ? r
            : r with { State = r.State with { DoneThroughUtc = TimeMath.Max(r.State.DoneThroughUtc, now) } };

    /// <summary>The file's copy <paramref name="r"/> of a task this device deleted, which the
    /// tombstone outranks: brought back as a new incarnation, of its definition and its state.
    /// That outranks the deletion, and everything written for the old incarnation, wherever
    /// they meet (a later schedule's state elsewhere included). Both parts get new versions.
    /// The state is the file's, merged with the tombstone's when that is of the same schedule
    /// (newer news of it: a later mark done, say), with the past passed over.</summary>
    private AlarmTask Restore(AlarmTask tombstone, AlarmTask r, DateTimeOffset now)
    {
        int incarnation = MergeRules.Next(tombstone.Def.Incarnation, MergeRules.MaxIncarnation);   // it outranks the file's
        var state = StateRank.Of(r).SameSchedule(StateRank.Of(tombstone)) ? MergeRules.Merge(tombstone, r).State : r.State;
        return PassOverPast(r with
        {
            Def = r.Def with { Deleted = false, Incarnation = incarnation },
            DefVersion = Versions.Tick(),
            State = state with { Incarnation = incarnation },
            StateVersion = Versions.Tick(),
        }, now);
    }

    // ---- Sync ----------------------------------------------------------------------

    /// <summary>Merges records from another device.</summary>
    /// <returns>The tasks whose local copy changed; these are what to forward to other peers.</returns>
    public IReadOnlyList<AlarmTask> Merge(IEnumerable<AlarmTask> incoming)
    {
        var changed = new List<AlarmTask>();
        lock (_lock)
        {
            foreach (var record in incoming)
            {
                // Records come from other devices, possibly running another version: one
                // that is malformed is skipped, rather than stored and gossiped onwards.
                if (!MergeRules.IsWellFormed(record)) continue;
                Observe(record);
                var r = MergeRules.WithLegacyStamps(record);
                if (!_tasks.TryGetValue(r.Id, out var local))
                {
                    var added = Repaired(r, _clock.UtcNow);
                    _tasks[r.Id] = added;
                    changed.Add(added);
                    continue;
                }
                var merged = Repaired(MergeRules.Merge(local, r), _clock.UtcNow);
                if (MergeRules.Changed(local, merged))
                {
                    _tasks[r.Id] = merged;
                    changed.Add(merged);
                }
            }
            if (changed.Count > 0) SaveLocked();
        }
        if (changed.Count > 0) Raise(changed, true);
        return changed;
    }

    /// <summary>The records <paramref name="peerDigest"/> shows the peer lacks or holds older.</summary>
    public IReadOnlyList<AlarmTask> NewerThan(IEnumerable<DigestEntry> peerDigest)
    {
        var peer = peerDigest.ToDictionary(e => e.Id);
        lock (_lock)
            return _tasks.Values.Where(t => !peer.TryGetValue(t.Id, out var e) || MergeRules.IsNewerThan(t, e)).ToList();
    }

    // ---- Persistence ---------------------------------------------------------------

    /// <summary>Advances this device's clock past every version <paramref name="r"/> carries,
    /// so that what it writes next is later than all it has seen.</summary>
    private void Observe(AlarmTask r)
    {
        Versions.Observe(r.DefVersion);
        Versions.Observe(r.StateVersion);
        Versions.Observe(r.Def.ScheduleStamp);
        Versions.Observe(r.Def.RestartVersion);
        Versions.Observe(r.State.ScheduleStamp);
    }

    private void SaveLocked()
    {
        if (_path is null) return;
        var file = new StoreFile { Clock = Versions.Last, Tasks = _tasks.Values.OrderBy(t => t.Id).ToList() };
        try
        {
            Json.WriteAtomically(_path, System.Text.Json.JsonSerializer.Serialize(file, Json.Files.StoreFile));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log?.Invoke($"Could not save tasks: {e.Message}");
        }
    }

    private void Raise(IReadOnlyList<AlarmTask> tasks, bool fromSync) => Changed?.Invoke(new StoreChange(tasks, fromSync));
}
