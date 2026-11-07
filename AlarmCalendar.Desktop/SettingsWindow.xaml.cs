using System.Globalization;
using System.Windows;

namespace AlarmCalendar.Desktop;

/// <summary>Per-device settings: the defaults for tasks (R11, R14, R6) and startup (D1).
/// Not modal, for the same reason as the task editor.</summary>
public partial class SettingsWindow : Window
{
    private readonly AppSettings _draft;

    public SettingsWindow()
    {
        InitializeComponent();
        _draft = AppHost.Settings.Clone();
        var s = _draft;
        ModeOnce.IsChecked = s.DefaultSoundMode == SoundMode.Once;
        ModeRepeat.IsChecked = s.DefaultSoundMode == SoundMode.Repeat;
        ReminderHours.Text = (s.DefaultReminderMinutes / 60).ToString(CultureInfo.CurrentCulture);
        ReminderMinutes.Text = (s.DefaultReminderMinutes % 60).ToString(CultureInfo.CurrentCulture);
        TimeOfDay.Text = Fields.FormatTime(s.DefaultTimeOfDay);
        RestartFromDone.IsChecked = s.DefaultRestartMode == RestartMode.FromDone;
        RestartOnSchedule.IsChecked = s.DefaultRestartMode == RestartMode.OnSchedule;
        StartWithWindows.IsChecked = Autostart.IsEnabled();
        CloseToTray.IsChecked = s.CloseToTray;
        ShowSound();
    }

    private void ShowSound() =>
        SoundButton.Content = (_draft.DefaultSound ?? SoundService.BuiltIn).Name + "…";

    private void Sound_Click(object sender, RoutedEventArgs e)
    {
        var picker = new SoundPickerWindow(_draft.DefaultSound, allowDefault: false) { Owner = this };
        if (picker.ShowDialog() != true) return;
        _draft.DefaultSound = picker.Choice;
        ShowSound();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var errors = new List<string>();
        if (!Fields.TryHoursMinutes(ReminderHours.Text, ReminderMinutes.Text, out int reminder, out var err)) errors.Add(err!);
        if (!Fields.TryTime(TimeOfDay.Text, out var tod)) errors.Add("The time of day isn't a time, e.g. 9:00 or 21:30.");
        if (errors.Count > 0) { ErrorText.Text = string.Join("\n", errors); return; }

        _draft.DefaultSoundMode = ModeOnce.IsChecked == true ? SoundMode.Once : SoundMode.Repeat;
        _draft.DefaultReminderMinutes = reminder;
        _draft.DefaultTimeOfDay = tod;
        _draft.DefaultRestartMode = RestartOnSchedule.IsChecked == true ? RestartMode.OnSchedule : RestartMode.FromDone;
        _draft.StartWithWindows = StartWithWindows.IsChecked == true;
        _draft.CloseToTray = CloseToTray.IsChecked == true;
        AppHost.ApplySettings(_draft);
        Close();
    }
}
