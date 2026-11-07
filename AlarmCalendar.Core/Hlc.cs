namespace AlarmCalendar;

/// <summary>
/// A hybrid logical clock timestamp: wall-clock milliseconds, a counter that orders
/// events within one millisecond (or while the wall clock lags a version already
/// seen), and the issuing node, which breaks the remaining ties. Ordered in that order.
/// </summary>
public readonly record struct Hlc(long Ms, int Counter, string Node) : IComparable<Hlc>
{
    public static readonly Hlc Zero = new(0, 0, "");

    public int CompareTo(Hlc other)
    {
        int c = Ms.CompareTo(other.Ms);
        if (c != 0) return c;
        c = Counter.CompareTo(other.Counter);
        return c != 0 ? c : string.CompareOrdinal(Node ?? "", other.Node ?? "");
    }

    public static bool operator >(Hlc a, Hlc b) => a.CompareTo(b) > 0;
    public static bool operator <(Hlc a, Hlc b) => a.CompareTo(b) < 0;
    public static bool operator >=(Hlc a, Hlc b) => a.CompareTo(b) >= 0;
    public static bool operator <=(Hlc a, Hlc b) => a.CompareTo(b) <= 0;

    public override string ToString() => $"{Ms}.{Counter}.{Node}";
}

/// <summary>Issues <see cref="Hlc"/> versions for one node. Thread-safe.</summary>
public sealed class HlcClock
{
    private readonly object _lock = new();
    private readonly string _node;
    private readonly Func<long> _nowMs;
    private Hlc _last;

    /// <param name="node">This node's ID, stamped on every version it issues.</param>
    /// <param name="nowMs">Wall clock in Unix milliseconds.</param>
    /// <param name="last">The latest version this node has issued or seen, from storage.</param>
    public HlcClock(string node, Func<long> nowMs, Hlc last)
    {
        _node = node;
        _nowMs = nowMs;
        _last = last;
    }

    public Hlc Last { get { lock (_lock) return _last; } }

    /// <summary>A version later than every version issued or seen so far.</summary>
    public Hlc Tick()
    {
        lock (_lock)
        {
            long now = _nowMs();
            _last = now > _last.Ms ? new Hlc(now, 0, _node) : new Hlc(_last.Ms, _last.Counter + 1, _node);
            return _last;
        }
    }

    /// <summary>Records a version received from another node, so later local versions
    /// order after it even if this node's wall clock is behind.</summary>
    public void Observe(Hlc remote)
    {
        lock (_lock)
        {
            if (remote.Ms > _last.Ms || (remote.Ms == _last.Ms && remote.Counter > _last.Counter))
                _last = new Hlc(remote.Ms, remote.Counter, _node);
        }
    }
}
