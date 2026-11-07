using System.Windows.Threading;

namespace AlarmCalendar.Desktop;

/// <summary>
/// The once-a-second heartbeat. It evaluates every task, opens an <see cref="AlarmWindow"/>
/// for each that should be alerting, refreshes it when a new alert begins, and closes it
/// when the task stops alerting for any other reason (marked done or dismissed on
/// another device, edited, deleted). Polling rather than timers makes sleep, resume and
/// clock changes need no special handling: the first tick after them sees the truth.
/// </summary>
public sealed class AlarmCoordinator : IDisposable
{
    private readonly TaskStore _store;
    private readonly SoundService _sounds;
    private readonly DispatcherTimer _timer;
    private readonly Dictionary<Guid, AlarmWindow> _windows = new();
    /// <summary>Snoozed tasks the user asked to see again from the list. Local to this
    /// device: re-opening a window here shouldn't make the others ring.</summary>
    private readonly HashSet<Guid> _forced = new();

    /// <summary>Raised after every evaluation with the status of each live task.</summary>
    public event Action<IReadOnlyList<TaskStatus>>? Evaluated;

    public AlarmCoordinator(TaskStore store, SoundService sounds)
    {
        _store = store;
        _sounds = sounds;
        _timer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Evaluate();
        _store.Changed += _ => AppHost.OnUi(Evaluate);
        _timer.Start();
    }

    public IReadOnlyList<TaskStatus> Current { get; private set; } = [];

    public void Evaluate()
    {
        var now = AppHost.Clock.UtcNow;
        var zone = AppHost.Clock.Zone;
        var statuses = _store.Live().Select(t => AlarmEngine.Evaluate(t, now, zone)).ToList();
        var live = new HashSet<Guid>();
        foreach (var st in statuses)
        {
            var id = st.Task.Id;
            live.Add(id);
            if (!st.Triggered) _forced.Remove(id);
            bool want = st.ShouldAlert || (st.Triggered && _forced.Contains(id));
            // An alarm missed while the app was closed waits a few seconds after startup, so a
            // linked device that already dealt with it can say so before it rings here.
            if (want && !_windows.ContainsKey(id) && st.AlertDue < AppHost.StartedUtc && AppHost.SettlingAfterStart(now))
                continue;
            if (want)
            {
                if (_windows.TryGetValue(id, out var w))
                {
                    // A new alert (another occurrence, or the reminder coming due while a
                    // forced window is open) refreshes the window and sounds again.
                    if (w.AlertDue != st.AlertDue && st.ShouldAlert) w.Alert(st, now);
                    else w.Refresh(st);
                }
                else
                {
                    w = new AlarmWindow(this, st);
                    _windows[id] = w;
                    w.Alert(st, now);
                    w.Show();
                    w.Activate();
                }
            }
            else if (_windows.TryGetValue(id, out var w))
            {
                CloseWindow(id, w);
            }
        }
        foreach (var (id, w) in _windows.Where(kv => !live.Contains(kv.Key)).ToList())
            CloseWindow(id, w);

        Current = statuses;
        Evaluated?.Invoke(statuses);
    }

    /// <summary>Opens the alarm window of a triggered task that was dismissed, without
    /// ending its snooze on other devices.</summary>
    public void ShowNow(Guid id)
    {
        if (_windows.TryGetValue(id, out var w)) { w.Activate(); return; }
        _forced.Add(id);
        Evaluate();
    }

    /// <summary>Called by an alarm window the user closed without marking the task done.</summary>
    internal void UserDismissed(Guid id)
    {
        _forced.Remove(id);
        _windows.Remove(id);
        _store.Dismiss(id);
    }

    /// <summary>Called by an alarm window whose task the user marked done.</summary>
    internal void UserMarkedDone(Guid id)
    {
        _forced.Remove(id);
        _windows.Remove(id);
        _store.MarkDone(id);
    }

    private void CloseWindow(Guid id, AlarmWindow w)
    {
        _windows.Remove(id);
        w.CloseQuietly();
    }

    public void Dispose()
    {
        _timer.Stop();
        foreach (var w in _windows.Values.ToList()) w.CloseQuietly();
        _windows.Clear();
    }

    internal SoundService Sounds => _sounds;
}
