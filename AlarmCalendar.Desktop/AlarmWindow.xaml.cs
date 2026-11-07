using System.ComponentModel;
using System.Windows;

namespace AlarmCalendar.Desktop;

/// <summary>
/// The popup for one triggered task. Closing it any way the user can (the close button,
/// Esc, the title-bar X) dismisses it: the task stays triggered and reminds again after
/// its reminder interval (R8). The restart mode (R7) and the reminder interval can be
/// changed here; they are saved to the task when the window closes either way.
/// </summary>
public partial class AlarmWindow : Window
{
    private readonly AlarmCoordinator _owner;
    private readonly Guid _id;
    private bool _closingQuietly;
    private bool _markedDone;

    /// <summary>The due time of the alert this window is showing; a different one from the
    /// engine means a new alert has begun.</summary>
    public DateTimeOffset? AlertDue { get; private set; }

    public AlarmWindow(AlarmCoordinator owner, TaskStatus st)
    {
        InitializeComponent();
        _owner = owner;
        _id = st.Task.Id;
        var d = st.Task.Def;
        RestartPanel.Visibility = d.Kind == ScheduleKind.Interval ? Visibility.Visible : Visibility.Collapsed;
        FromDoneRadio.IsChecked = d.Restart == RestartMode.FromDone;
        OnScheduleRadio.IsChecked = d.Restart == RestartMode.OnSchedule;
        ReminderHours.Text = (d.ReminderMinutes / 60).ToString();
        ReminderMinutes.Text = (d.ReminderMinutes % 60).ToString();
        Refresh(st);
    }

    /// <summary>Starts a new alert: updates the text, works out whether it is late (R13),
    /// and sounds it.</summary>
    public void Alert(TaskStatus st, DateTimeOffset now)
    {
        AlertDue = st.AlertDue;
        Refresh(st);
        var zone = AppHost.Clock.Zone;
        if (AlarmEngine.Lateness(st, now) is not null && st.AlertDue is { } due)
        {
            LateText.Text = TextFormat.LateWarning(due, now, AppHost.StartedUtc, zone);
            LatePanel.Visibility = Visibility.Visible;
        }
        else LatePanel.Visibility = Visibility.Collapsed;

        var path = SoundService.Resolve(st.Task.Def.SoundFor(Platforms.Windows), out var problem);
        SoundProblem.Text = problem ?? "";
        SoundProblem.Visibility = problem is null ? Visibility.Collapsed : Visibility.Visible;
        bool loop = AppHost.Settings.SoundModeFor(st.Task.Def) == SoundMode.Repeat;
        _owner.Sounds.Play(this, path, loop);
        SilenceButton.IsEnabled = true;
        if (IsLoaded) { Activate(); FlashIfInactive(); }
    }

    /// <summary>Updates the text from the task's current status without sounding.</summary>
    public void Refresh(TaskStatus st)
    {
        var d = st.Task.Def;
        var now = AppHost.Clock.UtcNow;
        var zone = AppHost.Clock.Zone;
        string what = d.Kind == ScheduleKind.Countdown ? "Countdown finished" : st.IsReminder ? "Reminder" : "Alarm";
        Title = $"{what}: {TextFormat.Snippet(d.Description, 40)}";
        string when = st.Latest is { } l ? TextFormat.When(l, now, zone) : "";
        Heading.Text = $"⏰ {what} · {when} · {TextFormat.Schedule(d, st.Task.State, now, zone)}";
        DescriptionText.Text = string.IsNullOrWhiteSpace(d.Description) ? "(no description)" : d.Description;
        MissedText.Text = st.PendingCount > 1
            ? $"It has come up {st.PendingCount} times since it was last marked done, first {TextFormat.When(st.Pending!.Value, now, zone)}."
            : "";
        MissedText.Visibility = MissedText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Closes the window because the task no longer needs it, without dismissing
    /// or saving anything.</summary>
    public void CloseQuietly()
    {
        _closingQuietly = true;
        _owner.Sounds.Stop(this);
        Close();
    }

    private void Silence_Click(object sender, RoutedEventArgs e)
    {
        _owner.Sounds.Stop(this);
        SilenceButton.IsEnabled = false;
    }

    private void Done_Click(object sender, RoutedEventArgs e)
    {
        if (!SaveChoices()) return;
        _markedDone = true;
        Close();
    }

    private void Dismiss_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (_closingQuietly) return;
        if (!_markedDone && !SaveChoices()) { e.Cancel = true; return; }
        _owner.Sounds.Stop(this);
        if (_markedDone) _owner.UserMarkedDone(_id);
        else _owner.UserDismissed(_id);
    }

    /// <summary>Writes the restart mode and reminder interval back to the task.</summary>
    /// <returns>False, with the problem shown, when the interval isn't valid.</returns>
    private bool SaveChoices()
    {
        if (!Fields.TryHoursMinutes(ReminderHours.Text, ReminderMinutes.Text, out int minutes, out var error))
        {
            ErrorText.Text = error;
            ReminderHours.Focus();
            return false;
        }
        var restart = OnScheduleRadio.IsChecked == true ? RestartMode.OnSchedule : RestartMode.FromDone;
        AppHost.Store.Edit(_id, d => d with
        {
            ReminderMinutes = minutes,
            Restart = d.Kind == ScheduleKind.Interval ? restart : d.Restart,
        });
        return true;
    }

    private void FlashIfInactive()
    {
        if (IsActive) return;
        var helper = new System.Windows.Interop.WindowInteropHelper(this);
        NativeMethods.Flash(helper.Handle);
    }
}
