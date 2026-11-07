using System.Runtime.InteropServices;

namespace AlarmCalendar.Desktop;

/// <summary>The editors' field parsing, which lives in Core (<see cref="FieldParsing"/>),
/// under the names the windows use.</summary>
public static class Fields
{
    public static bool TryTime(string text, out TimeOnly time) => FieldParsing.TryTime(text, out time);
    public static string FormatTime(TimeOnly t) => FieldParsing.FormatTime(t);
    public static bool TryInt(string text, int min, int max, out int value) => FieldParsing.TryInt(text, min, max, out value);
    public static bool TryHoursMinutes(string hours, string minutes, out int total, out string? error) =>
        FieldParsing.TryHoursMinutes(hours, minutes, out total, out error);
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
