using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AlarmCalendar.Desktop;

/// <summary>
/// Wakes the computer from sleep for the next alarm (S3), with a waitable timer that may
/// resume the system, and keeps the computer and its display awake while an alarm rings.
/// Windows must allow wake timers (Power Options → Sleep → Allow wake timers), which many
/// laptops don't on battery. Used from the UI thread only: the execution state it sets is
/// the calling thread's.
/// </summary>
internal sealed class WakeTimer : IDisposable
{
    /// <summary>How long before an alarm the computer wakes: resuming, and the first
    /// evaluation after it, take a few seconds, and the alarm should ring on time.</summary>
    public static readonly TimeSpan Lead = TimeSpan.FromSeconds(20);

    private readonly SafeWaitHandle? _timer;
    /// <summary>The alarm the timer is armed for, and when it goes off.</summary>
    private DateTimeOffset? _armedFor;
    private DateTimeOffset _firesAt;
    /// <summary>The alarm whose arming last failed, so a failure retried every second is
    /// logged once.</summary>
    private DateTimeOffset? _failedFor;
    private bool _keepingAwake;
    private bool _unsupportedLogged;

    public WakeTimer()
    {
        var h = CreateWaitableTimerExW(IntPtr.Zero, null, 0, TimerModifyState | Synchronize);
        if (h.IsInvalid)
        {
            AppHost.Log($"Can't wake the computer for alarms: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
            h.Dispose();
            return;
        }
        _timer = h;
    }

    /// <summary>Arms the timer to wake the computer just before <paramref name="due"/>, or
    /// disarms it when that is null. Called on every evaluation: it does nothing while the
    /// timer is armed for <paramref name="due"/> and hasn't gone off. One that went off while
    /// the computer was awake is armed again, so a computer put to sleep in the last seconds
    /// before the alarm still wakes; one that couldn't be armed is tried again.</summary>
    public void WakeFor(DateTimeOffset? due, DateTimeOffset now)
    {
        if (_timer is null || (due == _armedFor && (due is null || _firesAt > now))) return;
        if (due is not { } d)
        {
            CancelWaitableTimer(_timer);
            _armedFor = null;
            return;
        }
        // An alarm due within the lead still wakes a computer going to sleep right now.
        var at = d - Lead > now + TimeSpan.FromSeconds(2) ? d - Lead : now + TimeSpan.FromSeconds(2);
        long fileTime = at.UtcDateTime.ToFileTimeUtc();   // positive: an absolute UTC time
        if (!SetWaitableTimer(_timer, ref fileTime, 0, IntPtr.Zero, IntPtr.Zero, resume: true))
        {
            if (_failedFor != d)
                AppHost.Log($"Couldn't set the wake timer: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
            _failedFor = d;
            _armedFor = null;
            return;
        }
        bool rearmed = _armedFor == d;
        _armedFor = d;
        _firesAt = at;
        if (rearmed) return;
        if (Marshal.GetLastWin32Error() == ErrorNotSupported && !_unsupportedLogged)
        {
            _unsupportedLogged = true;
            AppHost.Log("This computer can't be woken from sleep by a timer; alarms ring once it wakes.");
        }
        else if (at - now > TimeSpan.FromMinutes(1)) AppHost.Log($"Wake timer set for {at.ToLocalTime():yyyy-MM-dd HH:mm:ss}.");
    }

    /// <summary>Keeps the computer and display awake while an alarm's sound plays: after a
    /// wake by the timer, Windows leaves the display off and would put the computer back to
    /// sleep within about two minutes. Once the sound stops (played once, silenced), the
    /// computer may sleep again; the alarm window waits on screen.</summary>
    public void KeepAwake(bool ringing)
    {
        if (ringing == _keepingAwake) return;
        _keepingAwake = ringing;
        SetThreadExecutionState(ringing ? EsContinuous | EsSystemRequired | EsDisplayRequired : EsContinuous);
    }

    public void Dispose()
    {
        KeepAwake(false);
        if (_timer is null) return;
        CancelWaitableTimer(_timer);
        _timer.Dispose();
    }

    private const uint TimerModifyState = 0x0002, Synchronize = 0x00100000;
    private const int ErrorNotSupported = 50;
    private const uint EsContinuous = 0x80000000, EsSystemRequired = 0x00000001, EsDisplayRequired = 0x00000002;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeWaitHandle CreateWaitableTimerExW(IntPtr attributes, string? name, uint flags, uint access);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWaitableTimer(SafeWaitHandle timer, ref long dueTime, int period, IntPtr completion,
        IntPtr completionArg, [MarshalAs(UnmanagedType.Bool)] bool resume);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CancelWaitableTimer(SafeWaitHandle timer);

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint flags);
}
