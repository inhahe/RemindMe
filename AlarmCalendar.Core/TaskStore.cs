namespace AlarmCalendar;

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

/// <summary>One line of a sync digest: enough to tell which side holds the newer record.</summary>
public sealed record DigestEntry(Guid Id, Hlc DefVersion, int Revision, Hlc StateVersion, DateTimeOffset DoneThroughUtc);

/// <summary>The merge rules of design.md → <i>Sync merge</i>.</summary>
public static class MergeRules
{
    /// <summary>The record both sides converge on, whichever order they see the two in.</summary>
    public static AlarmTask Merge(AlarmTask local, AlarmTask remote)
    {
        var (def, defVersion) = remote.DefVersion > local.DefVersion
            ? (remote.Def, remote.DefVersion) : (local.Def, local.DefVersion);
        var winner = StateOrder(remote.State.Revision, remote.StateVersion, local.State.Revision, local.StateVersion) > 0
            ? remote : local;
        // Within one schedule revision, "done" only ever moves forward, whichever write wins.
        // A newer revision (a schedule edit) replaces it outright, which is also how a
        // DoneThrough left in the future by a wrong clock gets cleared.
        var state = local.State.Revision == remote.State.Revision
            ? winner.State with { DoneThroughUtc = TimeMath.Max(local.State.DoneThroughUtc, remote.State.DoneThroughUtc) }
            : winner.State;
        return local with { Def = def, DefVersion = defVersion, State = state, StateVersion = winner.StateVersion };
    }

    /// <summary>Positive when (rA, vA) orders after (rB, vB).</summary>
    public static int StateOrder(int rA, Hlc vA, int rB, Hlc vB) =>
        rA != rB ? rA.CompareTo(rB) : vA.CompareTo(vB);

    /// <summary>True when <paramref name="local"/> holds something <paramref name="peer"/>'s
    /// copy lacks, so it should be sent.</summary>
    public static bool IsNewerThan(AlarmTask local, DigestEntry peer) =>
        local.DefVersion > peer.DefVersion
        || StateOrder(local.State.Revision, local.StateVersion, peer.Revision, peer.StateVersion) > 0
        || (local.State.Revision == peer.Revision && local.State.DoneThroughUtc > peer.DoneThroughUtc);

    public static DigestEntry DigestOf(AlarmTask t) =>
        new(t.Id, t.DefVersion, t.State.Revision, t.StateVersion, t.State.DoneThroughUtc);
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

    /// <summary>Saves the editor's result: a new task when <paramref name="id"/> is null.</summary>
    /// <param name="def">The definition as edited; its ScheduleRevision and Deleted are set here.</param>
    /// <param name="restartCountdown">Start a countdown again even though its length didn't change.</param>
    /// <returns>The stored task.</returns>
    public AlarmTask Save(Guid? id, TaskDefinition def, bool restartCountdown = false)
    {
        var now = _clock.UtcNow;
        AlarmTask result;
        lock (_lock)
        {
            if (id is not { } existingId || !_tasks.TryGetValue(existingId, out var old))
            {
                def = def with { ScheduleRevision = 1, Deleted = false };
                result = new AlarmTask
                {
                    Id = id ?? Guid.NewGuid(),
                    Def = def,
                    DefVersion = Versions.Tick(),
                    State = AlarmEngine.FreshState(null, def, now),
                    StateVersion = Versions.Tick(),
                };
            }
            else
            {
                bool reschedule = old.Def.ScheduleDiffers(def) || (restartCountdown && def.Kind == ScheduleKind.Countdown);
                // Definition and state merge separately, so the state can already carry a
                // higher revision than the definition; a new schedule must outrank both.
                def = def with
                {
                    ScheduleRevision = reschedule
                        ? Math.Max(old.Def.ScheduleRevision, old.State.Revision) + 1
                        : old.Def.ScheduleRevision,
                    Deleted = false,
                };
                result = old;
                if (old.Def != def) result = result with { Def = def, DefVersion = Versions.Tick() };
                if (reschedule)
                    result = result with
                    {
                        State = AlarmEngine.FreshState(old.State, def, now, keepPaused: !restartCountdown),
                        StateVersion = Versions.Tick(),
                    };
                if (result == old) return old;
            }
            _tasks[result.Id] = result;
            SaveLocked();
        }
        Raise([result], false);
        return result;
    }

    /// <summary>Changes the definition without touching the schedule (the alarm window's
    /// reminder interval and restart mode, a sound choice).</summary>
    public AlarmTask? Edit(Guid id, Func<TaskDefinition, TaskDefinition> change)
    {
        AlarmTask result;
        lock (_lock)
        {
            if (!_tasks.TryGetValue(id, out var old)) return null;
            var def = change(old.Def) with { ScheduleRevision = old.Def.ScheduleRevision, Deleted = old.Def.Deleted };
            if (old.Def == def) return old;
            result = old with { Def = def, DefVersion = Versions.Tick() };
            _tasks[id] = result;
            SaveLocked();
        }
        Raise([result], false);
        return result;
    }

    public AlarmTask? MarkDone(Guid id) => ChangeState(id, t => AlarmEngine.MarkDone(t, _clock.UtcNow, _clock.Zone));

    public AlarmTask? Dismiss(Guid id) => ChangeState(id, t => AlarmEngine.Dismiss(t, _clock.UtcNow, _clock.Zone));

    public AlarmTask? Pause(Guid id) => ChangeState(id, t => AlarmEngine.Pause(t, _clock.UtcNow));

    public AlarmTask? Resume(Guid id) => ChangeState(id, t => AlarmEngine.Resume(t, _clock.UtcNow));

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
                if (r is null || r.Id == Guid.Empty || r.Def is null || r.State is null) continue;
                Versions.Observe(r.DefVersion);
                Versions.Observe(r.StateVersion);
                if (!_tasks.TryGetValue(r.Id, out var local))
                {
                    _tasks[r.Id] = r;
                    changed.Add(r);
                    continue;
                }
                var merged = MergeRules.Merge(local, r);
                if (merged.DefVersion != local.DefVersion || merged.StateVersion != local.StateVersion || merged.State != local.State)
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
