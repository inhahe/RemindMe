using System.Globalization;
using System.Text;

namespace RemindMe;

/// <summary>What both apps say about linking devices.</summary>
public static class PairText
{
    private static string Devices(int n) => n == 1 ? "1 other device" : $"{n} other devices";

    /// <summary>The question on either device: both must accept after comparing the codes.</summary>
    public static string Question(Sync.PairPrompt p)
    {
        string who = p.IsRequester
            ? $"Linking with \"{p.PeerName}\": check that it shows this same code, then accept on both devices."
            : $"\"{p.PeerName}\" wants to link with this device and sync tasks with it.";
        if (!p.ThisDeviceJoins) return who;
        return who + " This device will join its sync group"
            + (p.MyLinkedDevices > 0 ? $" and stop syncing with the {Devices(p.MyLinkedDevices)} it is linked with now." : ".");
    }

    /// <summary>A device's line in the Devices list.</summary>
    public static string DeviceStatus(Sync.PeerInfo p) =>
        p.Linked ? (p.Connected ? "Linked, in sync" : "Linked, connecting…") : "Not linked";

    /// <summary>The line of a linked device that isn't connected now.</summary>
    public static string LastInSync(KnownPeer k, DateTimeOffset now, TimeZoneInfo zone) =>
        $"Linked, last in sync {TextFormat.Span(TimeMath.ToWall(k.LastSeenUtc, zone), TimeMath.ToWall(now, zone), 2)} ago";

    /// <summary>The requesting device's line while it waits for the other to accept.</summary>
    public static string Progress(Sync.PairProgress p) => p.Stage switch
    {
        Sync.PairStage.CodeShown => $"Check that {p.PeerName} shows {p.Code![..3]} {p.Code[3..]}, then accept on both devices.",
        Sync.PairStage.Accepted when p.Message is not null => p.Message,
        Sync.PairStage.Accepted => $"Linked with {p.PeerName}.",
        Sync.PairStage.Rejected => p.Message ?? $"{p.PeerName} said no.",
        _ => p.Message ?? "Linking failed.",
    };
}

/// <summary>The words and numbers both apps show, so the two can't word things differently.</summary>
public static class TextFormat
{
    private static readonly string[] UnitNames = ["year", "month", "week", "day", "hour", "minute", "second"];

    /// <summary>
    /// "2 days, 3 hours, 5 minutes": the span from <paramref name="from"/> to <paramref name="to"/>
    /// in calendar units (years and months as on the calendar, not fixed lengths), the
    /// <paramref name="maxParts"/> largest non-zero ones.
    /// </summary>
    public static string Span(DateTime from, DateTime to, int maxParts = 3)
    {
        if (to < from) (from, to) = (to, from);
        var parts = new long[7];
        var cursor = from;
        int years = 0;
        while (years < 10_000 && cursor.AddYears(years + 1) <= to) years++;
        cursor = cursor.AddYears(years);
        parts[0] = years;
        int months = 0;
        while (months < 12 && cursor.AddMonths(months + 1) <= to) months++;
        cursor = cursor.AddMonths(months);
        parts[1] = months;
        var rest = to - cursor;
        parts[2] = rest.Days / 7;
        parts[3] = rest.Days % 7;
        parts[4] = rest.Hours;
        parts[5] = rest.Minutes;
        parts[6] = rest.Seconds;

        var sb = new StringBuilder();
        int shown = 0;
        for (int i = 0; i < 7 && shown < maxParts; i++)
        {
            if (parts[i] == 0) continue;
            if (sb.Length > 0) sb.Append(", ");
            sb.Append(parts[i]).Append(' ').Append(UnitNames[i]);
            if (parts[i] != 1) sb.Append('s');
            shown++;
        }
        return sb.Length == 0 ? "less than a second" : sb.ToString();
    }

    /// <summary>The late warning for an alarm window (R13).</summary>
    /// <param name="due">When the alert should have appeared.</param>
    /// <param name="shown">When it actually appeared.</param>
    /// <param name="appStarted">When this process started; a due time before it means
    /// the app wasn't running.</param>
    public static string LateWarning(DateTimeOffset due, DateTimeOffset shown, DateTimeOffset appStarted, TimeZoneInfo zone)
    {
        var span = Span(TimeMath.ToWall(due, zone), TimeMath.ToWall(shown, zone));
        var why = due < appStarted ? "the app wasn't running at the time" : "the device was asleep or busy";
        return $"This reminder is late by {span}: {why}.";
    }

    /// <summary>"1:25" for a span until something happens: hours (any number) and minutes,
    /// rounded up so it never says 0:00 for something still in the future.</summary>
    public static string HoursMinutes(TimeSpan span)
    {
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        long minutes = (long)Math.Ceiling(span.TotalMinutes);
        return $"{minutes / 60}:{minutes % 60:00}";
    }

    /// <summary>"1 task", "3 tasks": a count of tasks.</summary>
    public static string Tasks(int n) => $"{n} task{(n == 1 ? "" : "s")}";

    /// <summary>"Exported 3 tasks." or "Exported 3 tasks to C:\…\file.json.": what an export did.</summary>
    /// <param name="count">How many tasks the file holds.</param>
    /// <param name="destination">Where it went, when worth saying.</param>
    public static string ExportSummary(int count, string? destination = null) =>
        $"Exported {Tasks(count)}{(destination is null ? "" : " to " + destination)}.";

    /// <summary>The Settings line about automatic backups (S11).</summary>
    /// <param name="on">Whether automatic backups are on.</param>
    /// <param name="newest">When this device's newest backup was taken, if known.</param>
    /// <param name="problem">Why the last attempt failed, if it did.</param>
    public static string BackupStatus(bool on, DateTimeOffset? newest, string? problem, DateTimeOffset now, TimeZoneInfo zone) =>
        !on ? "Automatic backups are off."
        : problem ?? (newest is { } at ? $"Last backup: {When(at, now, zone)}." : "No backup yet.");

    /// <summary>"Imported 3 tasks; updated 1; 2 were already up to date.": what an import did.</summary>
    public static string ImportSummary(ImportResult r)
    {
        var parts = new List<string>();
        if (r.Added > 0) parts.Add($"imported {Tasks(r.Added)}");
        if (r.Updated > 0) parts.Add($"updated {Tasks(r.Updated)}");
        if (r.Unchanged > 0) parts.Add($"{Tasks(r.Unchanged)} {(r.Unchanged == 1 ? "was" : "were")} already up to date");
        if (parts.Count == 0) return "The file has no tasks.";
        var text = string.Join("; ", parts) + ".";
        return char.ToUpperInvariant(text[0]) + text[1..];
    }

    /// <summary>"5 min", "1 h" or "1 h 30 min": a snooze preset's label.</summary>
    public static string Short(TimeSpan span)
    {
        long minutes = (long)span.TotalMinutes;
        long h = minutes / 60, m = minutes % 60;
        return h == 0 ? $"{m} min" : m == 0 ? $"{h} h" : $"{h} h {m} min";
    }

    /// <summary>"1:05:09" or "5:09": time left on a countdown, to the second.</summary>
    public static string Clock(TimeSpan span)
    {
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        long secs = (long)Math.Ceiling(span.TotalSeconds);
        long h = secs / 3600, m = secs / 60 % 60, sec = secs % 60;
        return h > 0 ? $"{h}:{m:00}:{sec:00}" : $"{m}:{sec:00}";
    }

    /// <summary>A date and time for a list row or popup: weekday, day, month, the year
    /// when it isn't this year, and the culture's short time.</summary>
    public static string When(DateTimeOffset instant, DateTimeOffset now, TimeZoneInfo zone, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        var w = TimeMath.ToWall(instant, zone);
        var n = TimeMath.ToWall(now, zone);
        string date = w.Date == n.Date ? "today"
            : w.Date == n.Date.AddDays(1) ? "tomorrow"
            : w.Date == n.Date.AddDays(-1) ? "yesterday"
            : w.ToString(w.Year == n.Year ? "ddd d MMM" : "ddd d MMM yyyy", culture);
        string time = w.ToString(w.Second == 0 ? "t" : "T", culture);
        return $"{date} {time}";
    }

    public static string WallTime(DateTime wall, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        return wall.ToString(wall.Second == 0 ? "t" : "T", culture);
    }

    public static string UnitName(IntervalUnit u, long n)
    {
        string s = u.ToString().ToLowerInvariant().TrimEnd('s');
        return n == 1 ? s : s + "s";
    }

    /// <summary>A one-line summary of when the task fires, for the list (R15).</summary>
    public static string Schedule(TaskDefinition d, TaskState s, DateTimeOffset now, TimeZoneInfo zone, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        switch (d.Kind)
        {
            case ScheduleKind.Interval:
                {
                    string every = d.IntervalCount == 1 ? $"Every {UnitName(d.Unit, 1)}" : $"Every {d.IntervalCount} {UnitName(d.Unit, d.IntervalCount)}";
                    // A FromDone grid moves with every mark-done, so its start says little;
                    // the Next column shows when it actually comes up.
                    if (d.Restart == RestartMode.FromDone) return $"{every}, restarts when done";
                    return $"{every} from {When(TimeMath.ToInstant(d.Start, zone), now, zone, culture)}";
                }
            case ScheduleKind.Weekly:
                {
                    string at = $"{Weekdays(d.Days, culture)} at {WallTime(DateTime.Today + d.TimeOfDay.ToTimeSpan(), culture)}";
                    if (d.WeekInterval <= 1) return at;
                    string from = d.WeeksFrom.ToDateTime(TimeOnly.MinValue).ToString(
                        d.WeeksFrom.Year == TimeMath.ToWall(now, zone).Year ? "d MMM" : "d MMM yyyy", culture);
                    return $"{at}, every {d.WeekInterval} weeks from the week of {from}";
                }
            case ScheduleKind.Once:
                return $"Once, {When(TimeMath.ToInstant(d.At, zone), now, zone, culture)}";
            case ScheduleKind.Countdown:
                return $"Countdown {Clock(d.Duration)}";
            default:
                return "";
        }
    }

    /// <summary>"Mon, Wed, Fri", "Weekdays", "Weekends" or "Every day".</summary>
    public static string Weekdays(int mask, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        mask &= 0x7F;
        if (mask == 0x7F) return "Every day";
        if (mask == 0b0111110) return "Weekdays";
        if (mask == 0b1000001) return "Weekends";
        if (mask == 0) return "No days";
        var names = culture.DateTimeFormat.AbbreviatedDayNames;
        var first = culture.DateTimeFormat.FirstDayOfWeek;
        var list = new List<string>();
        for (int i = 0; i < 7; i++)
        {
            int day = ((int)first + i) % 7;
            if ((mask & (1 << day)) != 0) list.Add(names[day]);
        }
        return string.Join(", ", list);
    }

    /// <summary>The first line of a description, cut to <paramref name="max"/> characters.</summary>
    public static string Snippet(string description, int max = 60)
    {
        var line = description.Replace("\r", "").Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "";
        bool more = description.Trim().Contains('\n');
        if (line.Length > max) return line[..(max - 1)].TrimEnd() + "…";
        return more ? line + " …" : line;
    }

    /// <summary>The "next" column of a list row (R15): when the task next comes up.</summary>
    public static string Next(TaskStatus st, DateTimeOffset now, TimeZoneInfo zone, CultureInfo? culture = null)
    {
        var d = st.Task.Def;
        if (st.NextOccurrence is { } n) return When(n, now, zone, culture);
        if (st.Triggered && d.Kind == ScheduleKind.Interval && d.Restart == RestartMode.FromDone)
            return $"{d.IntervalCount} {UnitName(d.Unit, d.IntervalCount)} after it's marked done";
        return "—";
    }

    /// <summary>The status column of a list row (R15): ringing, waiting with the next
    /// reminder, scheduled, or finished.</summary>
    public static string Status(TaskStatus st, DateTimeOffset now, TimeZoneInfo zone, CultureInfo? culture = null)
    {
        if (st.Paused) return st.CountdownLeft is { } left ? $"Paused, {Clock(left)} left" : "Paused";
        if (st.Task.Def.Kind == ScheduleKind.Countdown && !st.Triggered && st.NextOccurrence is { } end)
            return $"Running, {Clock(end - now)} left";
        if (st.Finished) return st.Task.Def.Kind == ScheduleKind.Weekly ? "No days chosen" : "Done";
        if (!st.Triggered) return "Scheduled";
        string missed = st.PendingCount > 1 ? $" ({st.PendingCount} times)" : "";
        if (!st.Snoozed) return $"Ringing, waiting to be marked done{missed}";
        if (st.NextReminder is { } r)
            return $"Waiting to be marked done{missed}; reminds {When(r, now, zone, culture)} (in {HoursMinutes(r - now)})";
        return $"Waiting to be marked done{missed}; no further reminders";
    }

    /// <summary>The task editor's line about an existing countdown, above its "start the
    /// countdown again" box.</summary>
    public static string CountdownStatus(TaskStatus st, DateTimeOffset now, TimeZoneInfo zone, CultureInfo? culture = null)
    {
        if (st.Paused)
            return st.CountdownLeft is { } left
                ? $"Paused, with {Clock(left)} left. It stays paused when you save; a new length replaces the time left."
                : "Paused, after it had finished. It stays paused when you save.";
        if (st.NextOccurrence is { } end)
            return $"Running: {Clock(end - now)} left, ends {When(end, now, zone, culture)}. Changing its length starts it again.";
        return st.Triggered ? "It has finished and is waiting to be marked done." : "It has finished.";
    }

    /// <summary>Whether the editor's "start the countdown again" box starts ticked: for a
    /// countdown that has finished, not for one running or paused, which saving keeps.</summary>
    public static bool RestartCountdownByDefault(TaskStatus st) => !st.Paused && st.NextOccurrence is null;
}
