using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace AlarmCalendar.Desktop;

/// <summary>
/// Creates or edits a task (R2–R10, R14). Not modal: a modal WPF window disables every
/// other window, which would leave a ringing alarm impossible to dismiss while this is
/// open. Esc and Cancel close it without saving; only Save saves.
/// </summary>
public partial class TaskEditorWindow : Window
{
    private static readonly IntervalUnit[] UnitOrder =
        [IntervalUnit.Years, IntervalUnit.Months, IntervalUnit.Weeks, IntervalUnit.Days,
         IntervalUnit.Hours, IntervalUnit.Minutes, IntervalUnit.Seconds];

    private readonly Guid? _id;
    private readonly TaskDefinition _original;
    private readonly CheckBox[] _days = new CheckBox[7];
    private SoundRef? _sound;

    /// <summary>The task being edited, or null for a new one.</summary>
    public Guid? TaskId => _id;

    public TaskEditorWindow(AlarmTask? existing)
    {
        InitializeComponent();
        _id = existing?.Id;
        Title = existing is null ? "New task" : "Edit task";

        foreach (var u in UnitOrder) IntervalUnitBox.Items.Add(u.ToString().ToLowerInvariant());
        BuildDayBoxes();
        SoundModeBox.Items.Add("");   // the "default" entry; its text depends on Settings
        SoundModeBox.Items.Add("once each time the alarm appears");
        SoundModeBox.Items.Add("over and over until it's closed or marked done");
        VolumeBox.Items.Add("");   // the "default" entries; their text depends on Settings
        foreach (var v in SoundLevels.Volumes) VolumeBox.Items.Add(SoundLevels.VolumeText(v));
        FadeInBox.Items.Add("");
        foreach (var f in SoundLevels.FadeIns) FadeInBox.Items.Add(SoundLevels.FadeInText(f));

        var s = AppHost.Settings;
        var now = AppHost.Clock.UtcNow;
        var zone = AppHost.Clock.Zone;
        var nowWall = TimeMath.TruncateToSecond(TimeMath.ToWall(now, zone));
        var nextHour = nowWall.Date.AddHours(nowWall.Hour + 1);

        // A new task starts from the defaults (R5: today and now; R6: the default time of day).
        _original = existing?.Def ?? new TaskDefinition
        {
            Kind = ScheduleKind.Interval,
            IntervalCount = 1,
            Unit = IntervalUnit.Days,
            Start = nowWall,
            Restart = s.DefaultRestartMode,
            TimeOfDay = s.DefaultTimeOfDay,
            At = nextHour,
            ReminderMinutes = s.DefaultReminderMinutes,
        };
        var d = _original;

        Description.Text = d.Description;
        ScheduledRadio.IsChecked = d.Kind != ScheduleKind.Countdown;
        CountdownRadio.IsChecked = d.Kind == ScheduleKind.Countdown;
        IntervalRadio.IsChecked = d.Kind is ScheduleKind.Interval or ScheduleKind.Countdown;
        WeeklyRadio.IsChecked = d.Kind == ScheduleKind.Weekly;
        OnceRadio.IsChecked = d.Kind == ScheduleKind.Once;

        IntervalCount.Text = d.IntervalCount.ToString(CultureInfo.CurrentCulture);
        IntervalUnitBox.SelectedIndex = Array.IndexOf(UnitOrder, d.Unit);
        var start = d.Kind == ScheduleKind.Interval ? d.Start : nowWall;
        StartDate.SelectedDate = start.Date;
        StartTime.Text = FormatWall(start);
        FromDoneRadio.IsChecked = d.Restart == RestartMode.FromDone;
        OnScheduleRadio.IsChecked = d.Restart == RestartMode.OnSchedule;

        // Fields of the kinds not in use show the defaults, so switching kind starts sensibly.
        var weeklyTime = d.Kind == ScheduleKind.Weekly ? d.TimeOfDay : s.DefaultTimeOfDay;
        WeeklyTime.Text = Fields.FormatTime(weeklyTime);
        for (int i = 0; i < 7; i++) _days[i].IsChecked = d.HasDay((DayOfWeek)i);
        bool everyFew = d.Kind == ScheduleKind.Weekly && d.WeekInterval > 1;
        WeekIntervalBox.Text = (everyFew ? d.WeekInterval : 1).ToString(CultureInfo.CurrentCulture);
        WeeksFromDate.SelectedDate = everyFew ? d.WeeksFrom.ToDateTime(TimeOnly.MinValue) : nowWall.Date;
        WeekIntervalBox.TextChanged += (_, _) => UpdateWeeksFrom();
        UpdateWeeksFrom();

        var at = d.Kind == ScheduleKind.Once ? d.At : nextHour;
        OnceDate.SelectedDate = at.Date;
        OnceTime.Text = FormatWall(at);

        var dur = d.Kind == ScheduleKind.Countdown ? d.Duration : TimeSpan.FromMinutes(5);
        CountdownHours.Text = ((int)dur.TotalHours).ToString(CultureInfo.CurrentCulture);
        CountdownMinutes.Text = dur.Minutes.ToString(CultureInfo.CurrentCulture);
        CountdownSeconds.Text = dur.Seconds.ToString(CultureInfo.CurrentCulture);
        ShowCountdownStatus(existing);

        ReminderHours.Text = (d.ReminderMinutes / 60).ToString(CultureInfo.CurrentCulture);
        ReminderMinutes.Text = (d.ReminderMinutes % 60).ToString(CultureInfo.CurrentCulture);
        _sound = d.SoundFor(Platforms.Windows);
        SoundModeBox.SelectedIndex = d.SoundMode switch { SoundMode.Once => 1, SoundMode.Repeat => 2, _ => 0 };
        VolumeBox.SelectedIndex = d.Volume is { } vol ? 1 + SoundLevels.Nearest(SoundLevels.Volumes, vol) : 0;
        FadeInBox.SelectedIndex = d.FadeInSeconds is { } fade ? 1 + SoundLevels.Nearest(SoundLevels.FadeIns, fade) : 0;
        RefreshDefaults();
        AppHost.SettingsChanged += RefreshDefaults;
        Closed += (_, _) => AppHost.SettingsChanged -= RefreshDefaults;

        Loaded += (_, _) => { Description.Focus(); Description.CaretIndex = Description.Text.Length; };
    }

    private void BuildDayBoxes()
    {
        var names = CultureInfo.CurrentCulture.DateTimeFormat.AbbreviatedDayNames;
        var first = (int)CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;
        for (int i = 0; i < 7; i++)
        {
            int day = (first + i) % 7;
            var box = new CheckBox { Content = names[day], Margin = new Thickness(0, 0, 14, 0) };
            _days[day] = box;
            DayBoxes.Children.Add(box);
        }
    }

    /// <summary>Texts that show a default's current value.</summary>
    private void RefreshDefaults()
    {
        SoundButton.Content = SoundService.Describe(_sound) + "…";
        string mode = AppHost.Settings.DefaultSoundMode == SoundMode.Once ? "once each time" : "over and over";
        int sel = SoundModeBox.SelectedIndex;
        SoundModeBox.Items[0] = $"as the default says ({mode})";
        SoundModeBox.SelectedIndex = sel;
        sel = VolumeBox.SelectedIndex;
        VolumeBox.Items[0] = $"default ({SoundLevels.VolumeText(AppHost.Settings.DefaultVolume)})";
        VolumeBox.SelectedIndex = sel;
        sel = FadeInBox.SelectedIndex;
        FadeInBox.Items[0] = $"default ({SoundLevels.FadeInText(AppHost.Settings.DefaultFadeInSeconds)})";
        FadeInBox.SelectedIndex = sel;
    }

    private void ShowCountdownStatus(AlarmTask? existing)
    {
        bool isCountdown = existing?.Def.Kind == ScheduleKind.Countdown;
        RestartCountdown.Visibility = isCountdown ? Visibility.Visible : Visibility.Collapsed;
        CountdownStatus.Visibility = isCountdown ? Visibility.Visible : Visibility.Collapsed;
        if (!isCountdown) return;
        var now = AppHost.Clock.UtcNow;
        var st = AlarmEngine.Evaluate(existing!, now, AppHost.Clock.Zone);
        CountdownStatus.Text = TextFormat.CountdownStatus(st, now, AppHost.Clock.Zone);
        RestartCountdown.IsChecked = TextFormat.RestartCountdownByDefault(st);
    }

    /// <summary>The first week matters only when it isn't every week.</summary>
    private void UpdateWeeksFrom() =>
        WeeksFromDate.IsEnabled = !Fields.TryInt(WeekIntervalBox.Text, 1, Occurrences.MaxWeekInterval, out int n) || n > 1;

    private static string FormatWall(DateTime t) => Fields.FormatTime(TimeOnly.FromDateTime(t));

    private void StartNow_Click(object sender, RoutedEventArgs e)
    {
        var now = TimeMath.TruncateToSecond(TimeMath.ToWall(AppHost.Clock.UtcNow, AppHost.Clock.Zone));
        StartDate.SelectedDate = now.Date;
        StartTime.Text = FormatWall(now);
    }

    private void Sound_Click(object sender, RoutedEventArgs e)
    {
        SoundPickerWindow.Open(this, _sound, allowDefault: true, chosen =>
        {
            _sound = chosen;
            RefreshDefaults();
        });
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var errors = new List<string>();
        var def = Build(errors);
        if (def is null)
        {
            ErrorText.Text = string.Join("\n", errors);
            return;
        }
        bool restart = RestartCountdown.Visibility == Visibility.Visible && RestartCountdown.IsChecked == true;
        AppHost.Store.Save(_id, def, restart);
        Close();
    }

    /// <summary>The definition the fields describe, or null with the problems in <paramref name="errors"/>.</summary>
    private TaskDefinition? Build(List<string> errors)
    {
        var d = _original with { Description = Description.Text.TrimEnd() };

        if (CountdownRadio.IsChecked == true)
        {
            bool ok = Fields.TryInt(OrZero(CountdownHours.Text), 0, 9999, out int h)
                & Fields.TryInt(OrZero(CountdownMinutes.Text), 0, 59, out int m)
                & Fields.TryInt(OrZero(CountdownSeconds.Text), 0, 59, out int sec);
            var dur = new TimeSpan(h, m, sec);
            if (!ok) errors.Add("The countdown needs whole hours, minutes (0–59) and seconds (0–59).");
            else if (dur < TimeSpan.FromSeconds(1)) errors.Add("The countdown must be at least a second long.");
            d = d with { Kind = ScheduleKind.Countdown, Duration = dur };
        }
        else if (IntervalRadio.IsChecked == true)
        {
            if (!Fields.TryInt(IntervalCount.Text, 1, 100_000, out int n)) errors.Add("\"Every\" needs a whole number from 1 up.");
            if (StartDate.SelectedDate is not { } date) errors.Add("Choose a start date.");
            if (!Fields.TryTime(StartTime.Text, out var time)) errors.Add("The start time isn't a time of day, e.g. 9:30 or 21:30.");
            d = d with
            {
                Kind = ScheduleKind.Interval,
                IntervalCount = n,
                Unit = UnitOrder[Math.Max(0, IntervalUnitBox.SelectedIndex)],
                Start = (StartDate.SelectedDate ?? DateTime.Today).Date + time.ToTimeSpan(),
                Restart = OnScheduleRadio.IsChecked == true ? RestartMode.OnSchedule : RestartMode.FromDone,
            };
        }
        else if (WeeklyRadio.IsChecked == true)
        {
            int mask = 0;
            for (int i = 0; i < 7; i++) if (_days[i].IsChecked == true) mask |= 1 << i;
            if (mask == 0) errors.Add("Tick at least one day of the week.");
            if (!Fields.TryTime(WeeklyTime.Text, out var time)) errors.Add("The time of day isn't a time, e.g. 9:30 or 21:30.");
            if (!Fields.TryInt(WeekIntervalBox.Text, 1, Occurrences.MaxWeekInterval, out int weeks))
                errors.Add($"\"Every … weeks\" needs a whole number from 1 to {Occurrences.MaxWeekInterval}.");
            d = d with { Kind = ScheduleKind.Weekly, Days = mask, TimeOfDay = time, WeekInterval = Math.Max(1, weeks) };
            if (weeks > 1)
            {
                if (WeeksFromDate.SelectedDate is not { } from) errors.Add("Choose the week it starts.");
                d = d with
                {
                    WeeksFrom = DateOnly.FromDateTime(WeeksFromDate.SelectedDate ?? DateTime.Today),
                    // An existing task keeps the week start it was saved with, so opening it on a
                    // device whose weeks start on another day doesn't move its schedule.
                    FirstDayOfWeek = _original is { Kind: ScheduleKind.Weekly, WeekInterval: > 1 }
                        ? _original.FirstDayOfWeek : CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek,
                };
            }
        }
        else
        {
            if (OnceDate.SelectedDate is not { } date) errors.Add("Choose a date.");
            if (!Fields.TryTime(OnceTime.Text, out var time)) errors.Add("The time isn't a time of day, e.g. 9:30 or 21:30.");
            d = d with { Kind = ScheduleKind.Once, At = (OnceDate.SelectedDate ?? DateTime.Today).Date + time.ToTimeSpan() };
            // Saving a schedule only lets occurrences after the save fire, so a time already
            // past would never ring; say so instead of silently saving a dead task.
            bool changed = _original.Kind != ScheduleKind.Once || _original.At != d.At;
            if (errors.Count == 0 && changed && TimeMath.ToInstant(d.At, AppHost.Clock.Zone) <= AppHost.Clock.UtcNow)
                errors.Add("That date and time has already passed.");
        }

        if (!Fields.TryHoursMinutes(ReminderHours.Text, ReminderMinutes.Text, out int reminder, out var rErr))
            errors.Add(rErr!);
        d = (d with
        {
            ReminderMinutes = reminder,
            SoundMode = SoundModeBox.SelectedIndex switch { 1 => SoundMode.Once, 2 => SoundMode.Repeat, _ => null },
            Volume = VolumeBox.SelectedIndex > 0 ? SoundLevels.Volumes[VolumeBox.SelectedIndex - 1] : null,
            FadeInSeconds = FadeInBox.SelectedIndex > 0 ? SoundLevels.FadeIns[FadeInBox.SelectedIndex - 1] : null,
        }).WithSound(Platforms.Windows, _sound);

        return errors.Count == 0 ? d : null;
    }

    private static string OrZero(string s) => string.IsNullOrWhiteSpace(s) ? "0" : s;
}
