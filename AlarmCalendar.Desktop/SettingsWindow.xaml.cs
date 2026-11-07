using System.Globalization;
using System.Windows;

namespace AlarmCalendar.Desktop;

/// <summary>Per-device settings: the defaults for tasks (R11, R14, R6) and startup (D1).
/// Not modal, for the same reason as the task editor. Save applies only the fields this
/// window shows, onto the settings as they are by then.</summary>
public partial class SettingsWindow : Window
{
    private SoundRef? _sound;

    public SettingsWindow()
    {
        InitializeComponent();
        var s = AppHost.Settings;
        _sound = s.DefaultSound;
        ModeOnce.IsChecked = s.DefaultSoundMode == SoundMode.Once;
        ModeRepeat.IsChecked = s.DefaultSoundMode == SoundMode.Repeat;
        foreach (var v in SoundLevels.Volumes) VolumeBox.Items.Add(SoundLevels.VolumeText(v));
        foreach (var f in SoundLevels.FadeIns) FadeInBox.Items.Add(SoundLevels.FadeInText(f));
        VolumeBox.SelectedIndex = SoundLevels.Nearest(SoundLevels.Volumes, s.DefaultVolume);
        FadeInBox.SelectedIndex = SoundLevels.Nearest(SoundLevels.FadeIns, s.DefaultFadeInSeconds);
        ReminderHours.Text = (s.DefaultReminderMinutes / 60).ToString(CultureInfo.CurrentCulture);
        ReminderMinutes.Text = (s.DefaultReminderMinutes % 60).ToString(CultureInfo.CurrentCulture);
        TimeOfDay.Text = Fields.FormatTime(s.DefaultTimeOfDay);
        RestartFromDone.IsChecked = s.DefaultRestartMode == RestartMode.FromDone;
        RestartOnSchedule.IsChecked = s.DefaultRestartMode == RestartMode.OnSchedule;
        StartWithWindows.IsChecked = Autostart.IsEnabled();
        CloseToTray.IsChecked = s.CloseToTray;
        ShowSound();
    }

    private void ShowSound() => SoundButton.Content = (_sound ?? SoundService.BuiltIn).Name + "…";

    private void Sound_Click(object sender, RoutedEventArgs e)
    {
        SoundPickerWindow.Open(this, _sound, allowDefault: false, chosen =>
        {
            _sound = chosen;
            ShowSound();
        });
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var errors = new List<string>();
        if (!Fields.TryHoursMinutes(ReminderHours.Text, ReminderMinutes.Text, out int reminder, out var err)) errors.Add(err!);
        if (!Fields.TryTime(TimeOfDay.Text, out var tod)) errors.Add("The time of day isn't a time, e.g. 9:00 or 21:30.");
        if (errors.Count > 0) { ErrorText.Text = string.Join("\n", errors); return; }

        var sound = _sound;
        var mode = ModeOnce.IsChecked == true ? SoundMode.Once : SoundMode.Repeat;
        int volume = SoundLevels.Volumes[Math.Max(0, VolumeBox.SelectedIndex)];
        int fadeIn = SoundLevels.FadeIns[Math.Max(0, FadeInBox.SelectedIndex)];
        var restart = RestartOnSchedule.IsChecked == true ? RestartMode.OnSchedule : RestartMode.FromDone;
        bool startWithWindows = StartWithWindows.IsChecked == true, closeToTray = CloseToTray.IsChecked == true;
        AppHost.UpdateSettings(s =>
        {
            s.DefaultSound = sound;
            s.DefaultSoundMode = mode;
            s.DefaultVolume = volume;
            s.DefaultFadeInSeconds = fadeIn;
            s.DefaultReminderMinutes = reminder;
            s.DefaultTimeOfDay = tod;
            s.DefaultRestartMode = restart;
            s.StartWithWindows = startWithWindows;
            s.CloseToTray = closeToTray;
        });
        Close();
    }
}
