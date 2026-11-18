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
public sealed record DigestEntry(Guid Id, int Incarnation, bool Deleted, int ScheduleRevision, Hlc DefVersion,
    int StateIncarnation, int Revision, Hlc StateVersion, DateTimeOffset DoneThroughUtc);

/// <summary>
/// What decides which of two copies of a task's definition wins: the later
/// <see cref="TaskDefinition.Incarnation"/>, then, within one, a deletion over any edit, then
/// the later schedule, then the newer version. So a deletion stands against an edit made on
/// another device before the deletion reached it: that edit was made without knowing of it,
/// however much later. Only bringing the task back on purpose, which starts a new incarnation,
/// outranks a deletion. And a schedule change outranks an edit of the schedule before it (a
/// new description, a reminder interval), as its state outranks that schedule's state
/// (<see cref="StateRank"/>): the definition and the state that win are always of the same
/// schedule. A countdown's definition with a one-time task's state would never ring.
/// </summary>
public readonly record struct DefRank(int Incarnation, bool Deleted, int Revision, Hlc Version) : IComparable<DefRank>
{
    public static DefRank Of(AlarmTask t) => new(t.Def.Incarnation, t.Def.Deleted, t.Def.ScheduleRevision, t.DefVersion);

    public static DefRank Of(DigestEntry e) => new(e.Incarnation, e.Deleted, e.ScheduleRevision, e.DefVersion);

    public int CompareTo(DefRank other) =>
        Incarnation != other.Incarnation ? Incarnation.CompareTo(other.Incarnation)
        : Deleted != other.Deleted ? Deleted.CompareTo(other.Deleted)
        : Revision != other.Revision ? Revision.CompareTo(other.Revision)
        : Version.CompareTo(other.Version);
}

/// <summary>
/// What decides which of two copies of a task's state wins: the later
/// <see cref="TaskState.Incarnation"/> (as for the definition, so a restored task's state is
/// never one written for the task before its deletion), then the later schedule revision,
/// then the newer version.
/// </summary>
public readonly record struct StateRank(int Incarnation, int Revision, Hlc Version) : IComparable<StateRank>
{
    public static StateRank Of(AlarmTask t) => new(t.State.Incarnation, t.State.Revision, t.StateVersion);

    public static StateRank Of(DigestEntry e) => new(e.StateIncarnation, e.Revision, e.StateVersion);

    /// <summary>Positive when this state is of a later schedule than <paramref name="other"/>
    /// (a later incarnation, or a later revision within one); zero for the same schedule,
    /// where "done" merges by maximum, whichever state wins.</summary>
    public int CompareSchedule(StateRank other) =>
        Incarnation != other.Incarnation ? Incarnation.CompareTo(other.Incarnation) : Revision.CompareTo(other.Revision);

    public bool SameSchedule(StateRank other) => CompareSchedule(other) == 0;

    public int CompareTo(StateRank other) => CompareSchedule(other) is var c and not 0 ? c : Version.CompareTo(other.Version);
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
    /// At the ceiling, a restore no longer outranks its tombstone, and a schedule change
    /// ranks with the schedule before it by version alone: corrupt input, not use, gets there.</summary>
    public static int Next(int n, int max) => n < max ? n + 1 : max;

    /// <summary>The record both sides converge on, whichever order they see the two in.</summary>
    public static AlarmTask Merge(AlarmTask local, AlarmTask remote)
    {
        var (def, defVersion) = DefRank.Of(remote).CompareTo(DefRank.Of(local)) > 0
            ? (remote.Def, remote.DefVersion) : (local.Def, local.DefVersion);
        StateRank localRank = StateRank.Of(local), remoteRank = StateRank.Of(remote);
        var winner = remoteRank.CompareTo(localRank) > 0 ? remote : local;
        // Within one schedule, "done" only ever moves forward, whichever write wins. A newer
        // schedule (a schedule edit, or a restore) replaces it outright, which is also how a
        // DoneThrough left in the future by a wrong clock gets cleared.
        var state = localRank.SameSchedule(remoteRank)
            ? winner.State with { DoneThroughUtc = TimeMath.Max(local.State.DoneThroughUtc, remote.State.DoneThroughUtc) }
            : winner.State;
        return local with { Def = def, DefVersion = defVersion, State = state, StateVersion = winner.StateVersion };
    }

    /// <summary>True when <paramref name="local"/> holds something <paramref name="peer"/>'s
    /// copy lacks, so it should be sent.</summary>
    public static bool IsNewerThan(AlarmTask local, DigestEntry peer)
    {
        StateRank mine = StateRank.Of(local), theirs = StateRank.Of(peer);
        return DefRank.Of(local).CompareTo(DefRank.Of(peer)) > 0
            || mine.CompareTo(theirs) > 0
            || (mine.SameSchedule(theirs) && local.State.DoneThroughUtc > peer.DoneThroughUtc);
    }

    public static DigestEntry DigestOf(AlarmTask t) =>
        new(t.Id, t.Def.Incarnation, t.Def.Deleted, t.Def.ScheduleRevision, t.DefVersion,
            t.State.Incarnation, t.State.Revision, t.StateVersion, t.State.DoneThroughUtc);

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
    public static bool Changed(AlarmTask local, AlarmTask merged) =>
        merged.DefVersion != local.DefVersion || merged.StateVersion != local.StateVersion || merged.State != local.State;
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
        }
        Versions = new HlcClock(nodeId, () => _clock.UtcNow.ToUnixTimeMilliseconds(), last);
    }

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
    /// <param name="def">The definition; its ScheduleRevision, Deleted and Incarnation are set here.</param>
    /// <returns>The stored task.</returns>
    public AlarmTask Add(TaskDefinition def)
    {
        def = def with { ScheduleRevision = 1, Deleted = false, Incarnation = 0 };
        AlarmTask result;
        lock (_lock)
        {
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
    /// <param name="def">The definition as edited; its ScheduleRevision, Deleted and Incarnation are set here.</param>
    /// <param name="restartCountdown">Start a countdown again from now, running even if it was
    /// paused, though its length didn't change. Ignored unless <paramref name="def"/> is a
    /// countdown: a paused task saved as another kind stays paused.</param>
    /// <returns>The stored task; null, with nothing saved, when there is no such task or it
    /// has been deleted (here or on a linked device): an editor left open across a deletion
    /// mustn't undo it. Keeping the edits means adding them as a new task.</returns>
    public AlarmTask? Save(Guid id, TaskDefinition def, bool restartCountdown = false)
    {
        var now = _clock.UtcNow;
        AlarmTask result;
        lock (_lock)
        {
            if (!_tasks.TryGetValue(id, out var old) || old.Def.Deleted) return null;
            bool reschedule = old.Def.ScheduleDiffers(def) || (restartCountdown && def.Kind == ScheduleKind.Countdown);
            // Definition and state merge separately, so the state can already carry a
            // higher revision than the definition; a new schedule must outrank both.
            def = def with
            {
                ScheduleRevision = reschedule
                    ? MergeRules.Next(Math.Max(old.Def.ScheduleRevision, old.State.Revision), MergeRules.MaxRevision)
                    : old.Def.ScheduleRevision,
                Deleted = false,
                Incarnation = old.Def.Incarnation,
            };
            result = old;
            if (old.Def != def) result = result with { Def = def, DefVersion = Versions.Tick() };
            if (reschedule)
                result = result with
                {
                    State = AlarmEngine.FreshState(old.State, def, now,
                        keepPaused: !(restartCountdown && def.Kind == ScheduleKind.Countdown)),
                    StateVersion = Versions.Tick(),
                };
            if (result == old) return old;
            _tasks[result.Id] = result;
            SaveLocked();
        }
        Raise([result], false);
        return result;
    }

    /// <summary>Changes the definition without touching the schedule (the alarm window's
    /// reminder interval and restart mode, a sound choice).</summary>
    /// <returns>The stored task; null, with nothing changed, when there is no such task or
    /// it has been deleted.</returns>
    public AlarmTask? Edit(Guid id, Func<TaskDefinition, TaskDefinition> change)
    {
        AlarmTask result;
        lock (_lock)
        {
            if (!_tasks.TryGetValue(id, out var old) || old.Def.Deleted) return null;
            var def = change(old.Def) with { ScheduleRevision = old.Def.ScheduleRevision, Deleted = false, Incarnation = old.Def.Incarnation };
            if (old.Def == def) return old;
            result = old with { Def = def, DefVersion = Versions.Tick() };
            _tasks[id] = result;
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
            foreach (var r in file.Tasks)
            {
                if (!MergeRules.IsWellFormed(r) || r.Def.Deleted) continue;
                Versions.Observe(r.DefVersion);
                Versions.Observe(r.StateVersion);
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
                    bool laterSchedule = StateRank.Of(r).CompareSchedule(StateRank.Of(local)) > 0;
                    result = MergeRules.Merge(local, laterSchedule ? PassOverPast(r, now) : r);
                    if (!MergeRules.Changed(local, result))
                    {
                        unchanged++;
                        continue;
                    }
                    if (local.Def.Deleted && !result.Def.Deleted) added++;
                    else updated++;
                }
                _tasks[r.Id] = result;
                changed.Add(result);
            }
            if (changed.Count > 0) SaveLocked();
        }
        if (changed.Count > 0) Raise(changed, false);
        return new ImportResult(added, updated, unchanged);
    }

    /// <summary><paramref name="r"/> with whatever came up before <paramref name="now"/>
    /// passed over: DoneThrough raised to now, which within one revision only ever rises, so
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
        int incarnation = MergeRules.Next(Math.Max(r.Def.Incarnation, tombstone.Def.Incarnation), MergeRules.MaxIncarnation);
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
            foreach (var r in incoming)
            {
                // Records come from other devices, possibly running another version: one
                // that is malformed is skipped, rather than stored and gossiped onwards.
                if (!MergeRules.IsWellFormed(r)) continue;
                Versions.Observe(r.DefVersion);
                Versions.Observe(r.StateVersion);
                if (!_tasks.TryGetValue(r.Id, out var local))
                {
                    _tasks[r.Id] = r;
                    changed.Add(r);
                    continue;
                }
                var merged = MergeRules.Merge(local, r);
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
