namespace RemindMe.Tests;

public sealed class FakeClock(DateTimeOffset now, TimeZoneInfo zone) : IClock
{
    public DateTimeOffset UtcNow { get; set; } = now;
    public TimeZoneInfo Zone { get; set; } = zone;
    public void Advance(TimeSpan by) => UtcNow += by;
}

public static class Zones
{
    /// <summary>US Eastern: DST starts 2026-03-08 02:00, ends 2026-11-01 02:00.</summary>
    public static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    /// <summary>The instant of a New York wall-clock time (asserting it is unambiguous).</summary>
    public static DateTimeOffset NY(int y, int mo, int d, int h = 0, int mi = 0, int s = 0)
    {
        var wall = new DateTime(y, mo, d, h, mi, s);
        Assert.False(NewYork.IsInvalidTime(wall) || NewYork.IsAmbiguousTime(wall), "test time must be unambiguous");
        return new DateTimeOffset(wall, NewYork.GetUtcOffset(wall));
    }

    public static DateTime Wall(DateTimeOffset t) => TimeMath.ToWall(t, NewYork);
}
