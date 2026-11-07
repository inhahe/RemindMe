using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace RemindMe;

/// <summary>Reading what users type into the editors' fields.</summary>
public static partial class FieldParsing
{
    [GeneratedRegex(@"^\s*([0-9]{1,2})(?:[:.]([0-9]{1,2}))?(?:[:.]([0-9]{1,2}))?\s*(a|am|p|pm)?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex TimePattern();

    /// <summary>
    /// Reads a time of day: the forms people type anywhere ("9", "9:30", "09:30:15",
    /// "9:30 pm", "21.30"), and otherwise the culture's own format, so whatever a field was
    /// filled with ("9:00 a.m." in en-CA, "오전 9:00" in ko-KR) reads back. Digits from other
    /// scripts (full-width ones from an IME) count as digits.
    /// </summary>
    public static bool TryTime(string text, out TimeOnly time, CultureInfo? culture = null)
    {
        time = default;
        culture ??= CultureInfo.CurrentCulture;
        text = AsciiDigits(text).Trim();
        if (text.Length == 0) return false;
        var m = TimePattern().Match(text);
        if (!m.Success)
        {
            // Not a plain typed time: the culture's own format ("9:00 a.m.", "오전 9:00").
            // Only after the strict pattern, which refuses what .NET's parser lets through
            // ("13 pm" as 13:00).
            if (DateTime.TryParse(text, culture, DateTimeStyles.NoCurrentDateDefault | DateTimeStyles.AllowWhiteSpaces, out var dt)
                && dt.Date == DateTime.MinValue.Date)
            {
                time = TimeOnly.FromDateTime(dt);
                return true;
            }
            return false;
        }
        int h = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        int min = m.Groups[2].Success ? int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) : 0;
        int sec = m.Groups[3].Success ? int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture) : 0;
        if (m.Groups[4].Success)
        {
            if (h < 1 || h > 12) return false;
            bool pm = m.Groups[4].Value.StartsWith('p') || m.Groups[4].Value.StartsWith('P');
            h = h % 12 + (pm ? 12 : 0);
        }
        if (h > 23 || min > 59 || sec > 59) return false;
        time = new TimeOnly(h, min, sec);
        return true;
    }

    /// <summary>A time of day in the culture's short format, with seconds only when non-zero.</summary>
    public static string FormatTime(TimeOnly t, CultureInfo? culture = null) =>
        t.ToString(t.Second == 0 ? "t" : "T", culture ?? CultureInfo.CurrentCulture);

    /// <summary>Reads a whole number from <paramref name="min"/> to <paramref name="max"/>;
    /// a blank field counts as 0.</summary>
    public static bool TryInt(string text, int min, int max, out int value)
    {
        text = AsciiDigits(text).Trim();
        if (text.Length == 0) text = "0";
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) && value >= min && value <= max;
    }

    /// <summary>Reads an hours + minutes pair (R8) into total minutes.</summary>
    /// <param name="error">Why it can't be read, for showing the user.</param>
    public static bool TryHoursMinutes(string hours, string minutes, out int total, out string? error)
    {
        total = 0;
        error = null;
        if (!TryInt(hours, 0, 9999, out int h) || !TryInt(minutes, 0, 59, out int m))
        {
            error = "The reminder interval needs whole hours (0 or more) and minutes (0–59).";
            return false;
        }
        total = h * 60 + m;
        return true;
    }

    /// <summary>Turns digits of any script into ASCII ones.</summary>
    private static string AsciiDigits(string s)
    {
        if (s.All(c => c < 128)) return s;
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
            sb.Append(char.IsDigit(c) && c >= 128 ? (char)('0' + (int)char.GetNumericValue(c)) : c);
        return sb.ToString();
    }
}
