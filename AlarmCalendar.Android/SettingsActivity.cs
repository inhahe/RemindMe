using System.Globalization;
using Android.App;
using Android.Content;
using Android.OS;
using Android.Views;
using Android.Widget;
using AndroidX.AppCompat.App;

namespace AlarmCalendar.Droid;

/// <summary>The defaults for tasks (R6, R11, R14), and the state of the permissions alarms
/// depend on, each with a way to fix it.</summary>
[Activity(Label = "Settings", Exported = false)]
public sealed class SettingsActivity : Screen
{
    private SoundChooser _chooser = null!;
    private SoundRef? _sound;
    private TimeOnly _timeOfDay;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        SetContentView(Resource.Layout.activity_settings);
        Ui.SetupToolbar(this, "Settings", back: true);
        _chooser = new SoundChooser(this, v => { _sound = v; Show(); });
        var s = AppState.Settings;
        _sound = s.DefaultSound;
        _timeOfDay = s.DefaultTimeOfDay;
        Find<RadioButton>(Resource.Id.mode_once).Checked = s.DefaultSoundMode == SoundMode.Once;
        Find<RadioButton>(Resource.Id.mode_repeat).Checked = s.DefaultSoundMode == SoundMode.Repeat;
        var volume = Find<Spinner>(Resource.Id.default_volume);
        volume.Adapter = Ui.SpinnerAdapter(this, SoundLevels.Volumes.Select(SoundLevels.VolumeText).ToList());
        volume.SetSelection(SoundLevels.Nearest(SoundLevels.Volumes, s.DefaultVolume));
        var fade = Find<Spinner>(Resource.Id.default_fade_in);
        fade.Adapter = Ui.SpinnerAdapter(this, SoundLevels.FadeIns.Select(SoundLevels.FadeInText).ToList());
        fade.SetSelection(SoundLevels.Nearest(SoundLevels.FadeIns, s.DefaultFadeInSeconds));
        Find<EditText>(Resource.Id.rem_hours).Text = (s.DefaultReminderMinutes / 60).ToString(CultureInfo.CurrentCulture);
        Find<EditText>(Resource.Id.rem_minutes).Text = (s.DefaultReminderMinutes % 60).ToString(CultureInfo.CurrentCulture);
        Find<RadioButton>(Resource.Id.restart_from_done).Checked = s.DefaultRestartMode == RestartMode.FromDone;
        Find<RadioButton>(Resource.Id.restart_on_schedule).Checked = s.DefaultRestartMode == RestartMode.OnSchedule;
        Find<Button>(Resource.Id.sound).Click += (_, _) => _chooser.Show(_sound, allowDefault: false);
        Find<Button>(Resource.Id.time_of_day).Click += (_, _) => Ui.PickTime(this, _timeOfDay, v => { _timeOfDay = v; Show(); });
        Find<Button>(Resource.Id.cancel).Click += (_, _) => Finish();
        Find<Button>(Resource.Id.save).Click += (_, _) => Save();
        if (savedInstanceState is { } saved)
        {
            _timeOfDay = new TimeOnly(saved.GetLong("timeOfDay"));
            _sound = SoundChooser.Restore(saved, "sound");
        }
        Show();
    }

    /// <summary>Keeps the picked sound and time across the activity being recreated.</summary>
    protected override void OnSaveInstanceState(Bundle outState)
    {
        base.OnSaveInstanceState(outState);
        outState.PutLong("timeOfDay", _timeOfDay.Ticks);
        SoundChooser.Save(outState, "sound", _sound);
    }

    protected override void OnResume()
    {
        base.OnResume();
        ShowPermissions();   // the user may be back from a system settings screen
    }


    private void Show()
    {
        Find<Button>(Resource.Id.sound).Text = _sound?.Name ?? "The phone's alarm sound";
        Find<Button>(Resource.Id.time_of_day).Text = Ui.FormatTime(this, _timeOfDay);
    }

    private void ShowPermissions()
    {
        var box = Find<LinearLayout>(Resource.Id.permissions);
        box.RemoveAllViews();
        var missing = Permissions.Missing(this).ToList();
        foreach (var need in missing)
        {
            box.AddView(new TextView(this) { Text = $"⚠ {need.Title}. {need.Why}" });
            var b = new Button(this) { Text = "Fix" };
            b.Click += (_, _) => need.Fix(this);
            box.AddView(b);
        }
        if (missing.Count == 0) box.AddView(new TextView(this) { Text = "✓ Notifications, exact alarms and full-screen alarms are all allowed." });
        if (Permissions.BatteryOptimized(this))
        {
            box.AddView(new TextView(this)
            {
                Text = "Battery optimization is on for this app. Alarms still ring, but syncing in the background may pause; "
                    + "for reliable sync, set the app to \"Unrestricted\" (or \"Not optimized\").",
            });
            var b = new Button(this) { Text = "Battery settings" };
            b.Click += (_, _) => Permissions.OpenBatterySettings(this);
            box.AddView(b);
        }
    }

    private void Save()
    {
        var errors = new List<string>();
        if (!FieldParsing.TryHoursMinutes(Find<EditText>(Resource.Id.rem_hours).Text ?? "",
                Find<EditText>(Resource.Id.rem_minutes).Text ?? "", out int reminder, out var err))
        {
            Find<TextView>(Resource.Id.error).Text = err;
            return;
        }
        var sound = _sound;
        var tod = _timeOfDay;
        var mode = Find<RadioButton>(Resource.Id.mode_once).Checked ? SoundMode.Once : SoundMode.Repeat;
        int volume = SoundLevels.Volumes[Math.Max(0, Find<Spinner>(Resource.Id.default_volume).SelectedItemPosition)];
        int fadeIn = SoundLevels.FadeIns[Math.Max(0, Find<Spinner>(Resource.Id.default_fade_in).SelectedItemPosition)];
        var restart = Find<RadioButton>(Resource.Id.restart_on_schedule).Checked ? RestartMode.OnSchedule : RestartMode.FromDone;
        AppState.UpdateSettings(s =>
        {
            s.DefaultSound = sound;
            s.DefaultSoundMode = mode;
            s.DefaultVolume = volume;
            s.DefaultFadeInSeconds = fadeIn;
            s.DefaultReminderMinutes = reminder;
            s.DefaultTimeOfDay = tod;
            s.DefaultRestartMode = restart;
        });
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
