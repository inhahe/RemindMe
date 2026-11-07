using System.Globalization;
using Android.App;
using Android.Content;
using Android.OS;
using Android.Views;
using Android.Widget;
using AndroidX.AppCompat.App;

namespace RemindMe.Droid;

/// <summary>
/// Creates or edits a task (R2–R10, R14), with the same layout as the desktop editor:
/// Scheduled and Countdown each in their own rounded pane, the schedule kinds nested inside.
/// Back and Cancel leave without saving; only Save saves.
/// </summary>
[Activity(Label = "Task", Exported = false, WindowSoftInputMode = SoftInput.AdjustResize)]
public sealed class TaskEditActivity : Screen
{
    private static readonly IntervalUnit[] UnitOrder =
        [IntervalUnit.Years, IntervalUnit.Months, IntervalUnit.Weeks, IntervalUnit.Days,
         IntervalUnit.Hours, IntervalUnit.Minutes, IntervalUnit.Seconds];

    private Guid? _id;
    private TaskDefinition _original = new();
    private readonly CheckBox[] _days = new CheckBox[7];
    private SoundChooser _chooser = null!;
    private SoundRef? _sound;
    private DateTime _startDate, _onceDate, _weeksFrom;
    private TimeOnly _startTime, _weeklyTime, _onceTime;

    public static Intent IntentFor(Context c, Guid? id)
    {
        var i = new Intent(c, typeof(TaskEditActivity));
        if (id is { } g) i.PutExtra("task", g.ToString());
        return i;
    }

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        SetContentView(Resource.Layout.activity_edit);
        _chooser = new SoundChooser(this, v => { _sound = v; ShowSound(); });
        var existing = Guid.TryParse(Intent?.GetStringExtra("task"), out var g) ? AppState.Store.Get(g) : null;
        _id = existing?.Id;
        Ui.SetupToolbar(this, existing is null ? "New task" : "Edit task", back: true);

        var s = AppState.Settings;
        var nowWall = TimeMath.TruncateToSecond(TimeMath.ToWall(AppState.Clock.UtcNow, AppState.Clock.Zone));
        var nextHour = nowWall.Date.AddHours(nowWall.Hour + 1);
        _original = existing?.Def ?? new TaskDefinition
        {
            Kind = ScheduleKind.Interval, IntervalCount = 1, Unit = IntervalUnit.Days, Start = nowWall,
            Restart = s.DefaultRestartMode, TimeOfDay = s.DefaultTimeOfDay, At = nextHour,
            ReminderMinutes = s.DefaultReminderMinutes,
        };
        var d = _original;

        Find<EditText>(Resource.Id.description).Text = d.Description;
        Find<RadioButton>(Resource.Id.scheduled).Checked = d.Kind != ScheduleKind.Countdown;
        Find<RadioButton>(Resource.Id.countdown).Checked = d.Kind == ScheduleKind.Countdown;
        Find<RadioButton>(Resource.Id.interval).Checked = d.Kind is ScheduleKind.Interval or ScheduleKind.Countdown;
        Find<RadioButton>(Resource.Id.weekly).Checked = d.Kind == ScheduleKind.Weekly;
        Find<RadioButton>(Resource.Id.once).Checked = d.Kind == ScheduleKind.Once;

        Find<EditText>(Resource.Id.interval_count).Text = d.IntervalCount.ToString(CultureInfo.CurrentCulture);
        var unit = Find<Spinner>(Resource.Id.interval_unit);
        unit.Adapter = Ui.SpinnerAdapter(this, UnitOrder.Select(u => u.ToString().ToLowerInvariant()).ToList());
        unit.SetSelection(Array.IndexOf(UnitOrder, d.Unit));
        var start = d.Kind == ScheduleKind.Interval ? d.Start : nowWall;
        (_startDate, _startTime) = (start.Date, TimeOnly.FromDateTime(start));
        Find<RadioButton>(Resource.Id.from_done).Checked = d.Restart == RestartMode.FromDone;
        Find<RadioButton>(Resource.Id.on_schedule).Checked = d.Restart == RestartMode.OnSchedule;

        BuildDays(d);
        _weeklyTime = d.Kind == ScheduleKind.Weekly ? d.TimeOfDay : s.DefaultTimeOfDay;
        bool everyFew = d.Kind == ScheduleKind.Weekly && d.WeekInterval > 1;
        Find<EditText>(Resource.Id.week_interval).Text = (everyFew ? d.WeekInterval : 1).ToString(CultureInfo.CurrentCulture);
        _weeksFrom = everyFew ? d.WeeksFrom.ToDateTime(TimeOnly.MinValue) : nowWall.Date;
        var at = d.Kind == ScheduleKind.Once ? d.At : nextHour;
        (_onceDate, _onceTime) = (at.Date, TimeOnly.FromDateTime(at));

        var dur = d.Kind == ScheduleKind.Countdown ? d.Duration : TimeSpan.FromMinutes(5);
        Find<EditText>(Resource.Id.cd_hours).Text = ((int)dur.TotalHours).ToString(CultureInfo.CurrentCulture);
        Find<EditText>(Resource.Id.cd_minutes).Text = dur.Minutes.ToString(CultureInfo.CurrentCulture);
        Find<EditText>(Resource.Id.cd_seconds).Text = dur.Seconds.ToString(CultureInfo.CurrentCulture);
        ShowCountdownStatus(existing);

        Find<EditText>(Resource.Id.rem_hours).Text = (d.ReminderMinutes / 60).ToString(CultureInfo.CurrentCulture);
        Find<EditText>(Resource.Id.rem_minutes).Text = (d.ReminderMinutes % 60).ToString(CultureInfo.CurrentCulture);
        _sound = d.SoundFor(Platforms.Android);
        var modeName = s.DefaultSoundMode == SoundMode.Once ? "once each time" : "over and over";
        var mode = Find<Spinner>(Resource.Id.sound_mode);
        mode.Adapter = Ui.SpinnerAdapter(this,
            [$"as the default says ({modeName})", "once each time the alarm appears", "over and over until it's closed or marked done"]);
        mode.SetSelection(d.SoundMode switch { SoundMode.Once => 1, SoundMode.Repeat => 2, _ => 0 });
        var volume = Find<Spinner>(Resource.Id.volume);
        volume.Adapter = Ui.SpinnerAdapter(this,
            [$"default ({SoundLevels.VolumeText(s.DefaultVolume)})", .. SoundLevels.Volumes.Select(SoundLevels.VolumeText)]);
        volume.SetSelection(d.Volume is { } v ? 1 + SoundLevels.Nearest(SoundLevels.Volumes, v) : 0);
        var fade = Find<Spinner>(Resource.Id.fade_in);
        fade.Adapter = Ui.SpinnerAdapter(this,
            [$"default ({SoundLevels.FadeInText(s.DefaultFadeInSeconds)})", .. SoundLevels.FadeIns.Select(SoundLevels.FadeInText)]);
        fade.SetSelection(d.FadeInSeconds is { } f ? 1 + SoundLevels.Nearest(SoundLevels.FadeIns, f) : 0);

        Find<Button>(Resource.Id.start_date).Click += (_, _) => Ui.PickDate(this, _startDate, v => { _startDate = v; ShowTimes(); });
        Find<Button>(Resource.Id.start_time).Click += (_, _) => Ui.PickTime(this, _startTime, v => { _startTime = v; ShowTimes(); });
        Find<Button>(Resource.Id.start_now).Click += (_, _) =>
        {
            var n = TimeMath.TruncateToSecond(TimeMath.ToWall(AppState.Clock.UtcNow, AppState.Clock.Zone));
            (_startDate, _startTime) = (n.Date, TimeOnly.FromDateTime(n));
            ShowTimes();
        };
        Find<Button>(Resource.Id.weekly_time).Click += (_, _) => Ui.PickTime(this, _weeklyTime, v => { _weeklyTime = v; ShowTimes(); });
        Find<Button>(Resource.Id.weeks_from).Click += (_, _) => Ui.PickDate(this, _weeksFrom, v => { _weeksFrom = v; ShowTimes(); });
        Find<Button>(Resource.Id.once_date).Click += (_, _) => Ui.PickDate(this, _onceDate, v => { _onceDate = v; ShowTimes(); });
        Find<Button>(Resource.Id.once_time).Click += (_, _) => Ui.PickTime(this, _onceTime, v => { _onceTime = v; ShowTimes(); });
        Find<Button>(Resource.Id.sound).Click += (_, _) => _chooser.Show(_sound, allowDefault: true);
        Find<Button>(Resource.Id.save).Click += (_, _) => Save();
        Find<Button>(Resource.Id.cancel).Click += (_, _) => Finish();
        foreach (var id in new[] { Resource.Id.scheduled, Resource.Id.countdown, Resource.Id.interval, Resource.Id.weekly, Resource.Id.once })
            Find<RadioButton>(id).CheckedChange += (_, _) => UpdatePanes();

        if (savedInstanceState is { } saved) Restore(saved);
        ShowTimes();
        ShowSound();
        UpdatePanes();
    }

    // Recreating the activity (rotation, dark mode, the process restarted behind a picker)
    // restores fields with IDs by itself; the picked dates, times and sound, and the weekday
    // boxes (made in code, without IDs), are kept here.
    protected override void OnSaveInstanceState(Bundle outState)
    {
        base.OnSaveInstanceState(outState);
        outState.PutLong("startDate", _startDate.Ticks);
        outState.PutLong("startTime", _startTime.Ticks);
        outState.PutLong("weeklyTime", _weeklyTime.Ticks);
        outState.PutLong("weeksFrom", _weeksFrom.Ticks);
        outState.PutLong("onceDate", _onceDate.Ticks);
        outState.PutLong("onceTime", _onceTime.Ticks);
        int mask = 0;
        for (int i = 0; i < 7; i++) if (_days[i].Checked) mask |= 1 << i;
        outState.PutInt("days", mask);
        SoundChooser.Save(outState, "sound", _sound);
    }

    private void Restore(Bundle s)
    {
        _startDate = new DateTime(s.GetLong("startDate"));
        _startTime = new TimeOnly(s.GetLong("startTime"));
        _weeklyTime = new TimeOnly(s.GetLong("weeklyTime"));
        _weeksFrom = new DateTime(s.GetLong("weeksFrom"));
        _onceDate = new DateTime(s.GetLong("onceDate"));
        _onceTime = new TimeOnly(s.GetLong("onceTime"));
        int mask = s.GetInt("days");
        for (int i = 0; i < 7; i++) _days[i].Checked = (mask & (1 << i)) != 0;
        _sound = SoundChooser.Restore(s, "sound");
    }


    private void BuildDays(TaskDefinition d)
    {
        var grid = Find<GridLayout>(Resource.Id.days);
        var names = CultureInfo.CurrentCulture.DateTimeFormat.AbbreviatedDayNames;
        int first = (int)CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;
        for (int i = 0; i < 7; i++)
        {
            int day = (first + i) % 7;
            var box = new CheckBox(this) { Text = names[day], Checked = d.HasDay((DayOfWeek)day) };
            _days[day] = box;
            grid.AddView(box);
        }
    }

    private void ShowTimes()
    {
        Find<Button>(Resource.Id.start_date).Text = Ui.FormatDate(_startDate);
        Find<Button>(Resource.Id.start_time).Text = Ui.FormatTime(this, _startTime);
        Find<Button>(Resource.Id.weekly_time).Text = Ui.FormatTime(this, _weeklyTime);
        Find<Button>(Resource.Id.weeks_from).Text = Ui.FormatDate(_weeksFrom);
        Find<Button>(Resource.Id.once_date).Text = Ui.FormatDate(_onceDate);
        Find<Button>(Resource.Id.once_time).Text = Ui.FormatTime(this, _onceTime);
    }

    private void ShowSound() => Find<Button>(Resource.Id.sound).Text = SoundChooser.Describe(_sound);

    private void UpdatePanes()
    {
        bool scheduled = Find<RadioButton>(Resource.Id.scheduled).Checked;
        Ui.SetPaneEnabled(Find<View>(Resource.Id.scheduled_pane), scheduled);
        if (scheduled)
        {
            Ui.SetPaneEnabled(Find<View>(Resource.Id.interval_pane), Find<RadioButton>(Resource.Id.interval).Checked);
            Ui.SetPaneEnabled(Find<View>(Resource.Id.weekly_pane), Find<RadioButton>(Resource.Id.weekly).Checked);
            Ui.SetPaneEnabled(Find<View>(Resource.Id.once_pane), Find<RadioButton>(Resource.Id.once).Checked);
        }
        Ui.SetPaneEnabled(Find<View>(Resource.Id.countdown_pane), Find<RadioButton>(Resource.Id.countdown).Checked);
    }

    private void ShowCountdownStatus(AlarmTask? existing)
    {
        bool isCountdown = existing?.Def.Kind == ScheduleKind.Countdown;
        var status = Find<TextView>(Resource.Id.cd_status);
        var restart = Find<CheckBox>(Resource.Id.cd_restart);
        status.Visibility = restart.Visibility = isCountdown ? ViewStates.Visible : ViewStates.Gone;
        if (!isCountdown) return;
        var now = AppState.Clock.UtcNow;
        var st = AlarmEngine.Evaluate(existing!, now, AppState.Clock.Zone);
        status.Text = TextFormat.CountdownStatus(st, now, AppState.Clock.Zone);
        restart.Checked = TextFormat.RestartCountdownByDefault(st);
    }

    private void Save()
    {
        var errors = new List<string>();
        var d = _original with { Description = Find<EditText>(Resource.Id.description).Text?.TrimEnd() ?? "" };
        if (Find<RadioButton>(Resource.Id.countdown).Checked)
        {
            bool ok = FieldParsing.TryInt(Find<EditText>(Resource.Id.cd_hours).Text ?? "", 0, 9999, out int h)
                & FieldParsing.TryInt(Find<EditText>(Resource.Id.cd_minutes).Text ?? "", 0, 59, out int m)
                & FieldParsing.TryInt(Find<EditText>(Resource.Id.cd_seconds).Text ?? "", 0, 59, out int sec);
            var dur = new TimeSpan(h, m, sec);
            if (!ok) errors.Add("The countdown needs whole hours, minutes (0–59) and seconds (0–59).");
            else if (dur < TimeSpan.FromSeconds(1)) errors.Add("The countdown must be at least a second long.");
            d = d with { Kind = ScheduleKind.Countdown, Duration = dur };
        }
        else if (Find<RadioButton>(Resource.Id.interval).Checked)
        {
            if (!FieldParsing.TryInt(Find<EditText>(Resource.Id.interval_count).Text ?? "", 1, 100_000, out int n))
                errors.Add("\"Every\" needs a whole number from 1 up.");
            d = d with
            {
                Kind = ScheduleKind.Interval,
                IntervalCount = n,
                Unit = UnitOrder[Math.Max(0, Find<Spinner>(Resource.Id.interval_unit).SelectedItemPosition)],
                Start = _startDate.Date + _startTime.ToTimeSpan(),
                Restart = Find<RadioButton>(Resource.Id.on_schedule).Checked ? RestartMode.OnSchedule : RestartMode.FromDone,
            };
        }
        else if (Find<RadioButton>(Resource.Id.weekly).Checked)
        {
            int mask = 0;
            for (int i = 0; i < 7; i++) if (_days[i].Checked) mask |= 1 << i;
            if (mask == 0) errors.Add("Tick at least one day of the week.");
            if (!FieldParsing.TryInt(Find<EditText>(Resource.Id.week_interval).Text ?? "", 1, Occurrences.MaxWeekInterval, out int weeks))
                errors.Add($"\"Every … weeks\" needs a whole number from 1 to {Occurrences.MaxWeekInterval}.");
            d = d.AsWeekly(mask, _weeklyTime, weeks, DateOnly.FromDateTime(_weeksFrom), CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek);
        }
        else
        {
            d = d with { Kind = ScheduleKind.Once, At = _onceDate.Date + _onceTime.ToTimeSpan() };
            bool changed = _original.Kind != ScheduleKind.Once || _original.At != d.At;
            if (changed && TimeMath.ToInstant(d.At, AppState.Clock.Zone) <= AppState.Clock.UtcNow)
                errors.Add("That date and time has already passed.");
        }

        if (!FieldParsing.TryHoursMinutes(Find<EditText>(Resource.Id.rem_hours).Text ?? "",
                Find<EditText>(Resource.Id.rem_minutes).Text ?? "", out int reminder, out var rErr))
            errors.Add(rErr!);
        d = (d with
        {
            ReminderMinutes = reminder,
            SoundMode = Find<Spinner>(Resource.Id.sound_mode).SelectedItemPosition switch { 1 => SoundMode.Once, 2 => SoundMode.Repeat, _ => null },
            Volume = Find<Spinner>(Resource.Id.volume).SelectedItemPosition is > 0 and var vi ? SoundLevels.Volumes[vi - 1] : null,
            FadeInSeconds = Find<Spinner>(Resource.Id.fade_in).SelectedItemPosition is > 0 and var fi ? SoundLevels.FadeIns[fi - 1] : null,
        }).WithSound(Platforms.Android, _sound);

        if (errors.Count > 0)
        {
            Find<TextView>(Resource.Id.error).Text = string.Join("\n", errors);
            return;
        }
        var restart = Find<CheckBox>(Resource.Id.cd_restart);
        AppState.Store.Save(_id, d, restart.Visibility == ViewStates.Visible && restart.Checked);
        Finish();
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        if (!_chooser.HandleResult(requestCode, resultCode, data)) base.OnActivityResult(requestCode, resultCode, data);
    }

    public override bool OnSupportNavigateUp()
    {
        Finish();
        return true;
    }
}
