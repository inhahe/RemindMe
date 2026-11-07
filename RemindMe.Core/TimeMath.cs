namespace RemindMe;

/// <summary>The clock and time zone everything reads, so tests can control both.</summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
    TimeZoneInfo Zone { get; }
}

public sealed class SystemClock : IClock
{
    public static readonly SystemClock Instance = new();
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    // TimeZoneInfo.Local is cached by the runtime; ClearCachedData() (called on a
    // time-zone change notification) refreshes it.
    public TimeZoneInfo Zone => TimeZoneInfo.Local;
}

public static class TimeMath
{
    /// <summary>
    /// The instant at which wall-clock time <paramref name="wall"/> occurs in <paramref name="zone"/>.
    /// A time skipped by a spring-forward transition moves forward by the gap (02:30 → 03:30);
    /// a time that occurs twice in a fall-back transition resolves to its first occurrence.
    /// </summary>
    public static DateTimeOffset ToInstant(DateTime wall, TimeZoneInfo zone)
    {
        wall = DateTime.SpecifyKind(wall, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(wall))
        {
            // The offset just before the gap applies to the skipped wall-clock range, so
            // reading the time with it lands the same distance past the transition.
            var before = zone.GetUtcOffset(wall.AddHours(-3));
            return new DateTimeOffset(wall, before).ToOffset(zone.GetUtcOffset(new DateTimeOffset(wall, before)));
        }
        if (zone.IsAmbiguousTime(wall))
        {
            // The larger offset is the one in force first (daylight time, before falling back).
            var offsets = zone.GetAmbiguousTimeOffsets(wall);
            return new DateTimeOffset(wall, offsets.Max());
        }
        return new DateTimeOffset(wall, zone.GetUtcOffset(wall));
    }

    /// <summary>The wall-clock time of <paramref name="instant"/> in <paramref name="zone"/>.</summary>
    public static DateTime ToWall(DateTimeOffset instant, TimeZoneInfo zone) =>
        DateTime.SpecifyKind(TimeZoneInfo.ConvertTime(instant, zone).DateTime, DateTimeKind.Unspecified);

    /// <summary>Drops the fractional seconds, so times written into a schedule stay readable.</summary>
    public static DateTime TruncateToSecond(DateTime t) => new(t.Ticks - t.Ticks % TimeSpan.TicksPerSecond, t.Kind);
    public static DateTimeOffset TruncateToSecond(DateTimeOffset t) =>
        new(t.Ticks - t.Ticks % TimeSpan.TicksPerSecond, t.Offset);

    public static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;
    public static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;
}
