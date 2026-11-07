using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace AlarmCalendar.Desktop;

/// <summary>Parsing and formatting for the editors' text fields.</summary>
public static partial class Fields
{
    [GeneratedRegex(@"^\s*(\d{1,2})(?:[:.](\d{1,2}))?(?:[:.](\d{1,2}))?\s*(a|am|p|pm)?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex TimePattern();

    /// <summary>Reads a time of day: "9", "9:30", "09:30:15", "9:30 pm", "21:30".</summary>
    public static bool TryTime(string text, out TimeOnly time)
    {
        time = default;
        var m = TimePattern().Match(text);
        if (!m.Success) return false;
        int h = int.Parse(m.Groups[1].Value);
        int min = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : 0;
        int sec = m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0;
        if (m.Groups[4].Success)
        {
            if (h < 1 || h > 12) return false;
            bool pm = m.Groups[4].Value.StartsWith("p", StringComparison.OrdinalIgnoreCase);
            h = h % 12 + (pm ? 12 : 0);
        }
        if (h > 23 || min > 59 || sec > 59) return false;
        time = new TimeOnly(h, min, sec);
        return true;
    }

    /// <summary>A time of day in the culture's short format, with seconds only when non-zero.</summary>
    public static string FormatTime(TimeOnly t) =>
        t.ToString(t.Second == 0 ? "t" : "T", CultureInfo.CurrentCulture);

    public static bool TryInt(string text, int min, int max, out int value) =>
        int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out value) && value >= min && value <= max;

    /// <summary>Reads an hours + minutes pair (R8) into total minutes.</summary>
    public static bool TryHoursMinutes(string hours, string minutes, out int total, out string? error)
    {
        total = 0;
        error = null;
        if (!TryInt(hours.Length == 0 ? "0" : hours, 0, 9999, out int h)
            || !TryInt(minutes.Length == 0 ? "0" : minutes, 0, 59, out int m))
        {
            error = "The reminder interval needs whole hours (0 or more) and minutes (0–59).";
            return false;
        }
        total = h * 60 + m;
        return true;
    }
}

internal static class NativeMethods
{
    [StructLayout(LayoutKind.Sequential)]
    private struct FLASHWINFO
    {
        public uint cbSize;
        public IntPtr hwnd;
        public uint dwFlags;
        public uint uCount;
        public uint dwTimeout;
    }

    private const uint FLASHW_ALL = 3, FLASHW_TIMERNOFG = 12;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlashWindowEx(ref FLASHWINFO pwfi);

    /// <summary>Flashes the taskbar button until the window comes to the foreground.</summary>
    public static void Flash(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        var info = new FLASHWINFO
        {
            cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(),
            hwnd = hwnd,
            dwFlags = FLASHW_ALL | FLASHW_TIMERNOFG,
        };
        FlashWindowEx(ref info);
    }
}
