using System.Globalization;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using Android.Widget;
using AndroidX.AppCompat.App;

namespace AlarmCalendar.Droid;

/// <summary>
/// The alarm screen of one ringing task, shown over the lock screen (D4). As on the
/// desktop, leaving it any way but Mark done (Back included) dismisses the task: it stays
/// due and reminds again after its reminder interval (R8). The restart mode (R7) and the
/// reminder interval can be changed here; only fields the user changed are written back.
/// </summary>
[Activity(Label = "Alarm", Exported = false, LaunchMode = LaunchMode.SingleTask, TaskAffinity = "com.inhahe.alarmcalendar.alarm",
    ExcludeFromRecents = true, ShowWhenLocked = true, TurnScreenOn = true, Theme = "@style/AlarmTheme")]
public sealed class AlarmActivity : AppCompatActivity
{
    private readonly Handler _handler = new(Looper.MainLooper!);
    private Guid _id;
    private int _shownMinutes;
    private RestartMode _shownRestart;
    private bool _finishedByUs;

    public static Intent IntentFor(Context c, Guid task) =>
        new Intent(c, typeof(AlarmActivity)).PutExtra("task", task.ToString())
            .AddFlags(ActivityFlags.NewTask | ActivityFlags.NoUserAction);

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        SetShowWhenLocked(true);
        SetTurnScreenOn(true);
        Window?.AddFlags(WindowManagerFlags.KeepScreenOn);
        SetContentView(Resource.Layout.activity_alarm);
        Find<Button>(Resource.Id.done).Click += (_, _) => Leave(done: true);
        Find<Button>(Resource.Id.later).Click += (_, _) => Leave(done: false);
        Find<Button>(Resource.Id.silence).Click += (_, _) =>
        {
            RingingService.Silence(_id);
            Find<Button>(Resource.Id.silence).Enabled = false;
        };
        OnBackPressedDispatcher.AddCallback(this, new BackCallback(() => Leave(done: false)));
        Load(Intent);
    }

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        Load(intent);
    }

    private void Load(Intent? intent)
    {
        if (!Guid.TryParse(intent?.GetStringExtra("task"), out _id) || AppState.Store.Get(_id) is not { } t)
        {
            Finish();
            return;
        }
        ShowChoices(t.Def);
        Find<View>(Resource.Id.restart_panel).Visibility = t.Def.Kind == ScheduleKind.Interval ? ViewStates.Visible : ViewStates.Gone;
        Find<Button>(Resource.Id.silence).Enabled = true;
        Refresh();
    }

    protected override void OnResume()
    {
        base.OnResume();
        Tick();
    }

    protected override void OnPause()
    {
        _handler.RemoveCallbacksAndMessages(null);
        base.OnPause();
    }

    private void Tick()
    {
        Refresh();
        _handler.PostDelayed(Tick, 1000);
    }

    private T Find<[System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(Ui.ViewCtors)] T>(int id) where T : View => FindViewById<T>(id)!;

    private void ShowChoices(TaskDefinition d)
    {
        _shownMinutes = d.ReminderMinutes;
        _shownRestart = d.Restart;
        Find<RadioButton>(Resource.Id.from_done).Checked = d.Restart == RestartMode.FromDone;
        Find<RadioButton>(Resource.Id.on_schedule).Checked = d.Restart == RestartMode.OnSchedule;
        Find<EditText>(Resource.Id.rem_hours).Text = (d.ReminderMinutes / 60).ToString(CultureInfo.CurrentCulture);
        Find<EditText>(Resource.Id.rem_minutes).Text = (d.ReminderMinutes % 60).ToString(CultureInfo.CurrentCulture);
    }

    private RestartMode CurrentRestart() =>
        Find<RadioButton>(Resource.Id.on_schedule).Checked ? RestartMode.OnSchedule : RestartMode.FromDone;

    private void Refresh()
    {
        if (_finishedByUs) return;
        if (AppState.Store.Get(_id) is not { Def.Deleted: false } t) { Finish(); return; }
        var now = AppState.Clock.UtcNow;
        var zone = AppState.Clock.Zone;
        var st = AlarmEngine.Evaluate(t, now, zone);
        // Dealt with elsewhere (a notification button, another device): nothing to show.
        if (!st.Triggered) { Finish(); return; }
        var d = t.Def;
        string what = d.Kind == ScheduleKind.Countdown ? "Countdown finished" : st.IsReminder ? "Reminder" : "Alarm";
        string when = st.Latest is { } l ? TextFormat.When(l, now, zone) : "";
        Find<TextView>(Resource.Id.heading).Text = $"⏰ {what} · {when} · {TextFormat.Schedule(d, t.State, now, zone)}";
        Find<TextView>(Resource.Id.description).Text = string.IsNullOrWhiteSpace(d.Description) ? "(no description)" : d.Description;
        var missed = Find<TextView>(Resource.Id.missed);
        missed.Text = st.PendingCount > 1
            ? $"It has come up {st.PendingCount} times since it was last marked done, first {TextFormat.When(st.Pending!.Value, now, zone)}."
            : "";
        missed.Visibility = missed.Text.Length > 0 ? ViewStates.Visible : ViewStates.Gone;
        var info = RingingService.InfoFor(_id);
        var late = Find<TextView>(Resource.Id.late);
        late.Text = info?.LateText ?? "";
        late.Visibility = late.Text.Length > 0 ? ViewStates.Visible : ViewStates.Gone;
        var problem = Find<TextView>(Resource.Id.sound_problem);
        problem.Text = info?.SoundProblem ?? "";
        problem.Visibility = problem.Text.Length > 0 ? ViewStates.Visible : ViewStates.Gone;
    }

    private void Leave(bool done)
    {
        if (!FieldParsing.TryHoursMinutes(Find<EditText>(Resource.Id.rem_hours).Text ?? "",
                Find<EditText>(Resource.Id.rem_minutes).Text ?? "", out int minutes, out var error))
        {
            Find<TextView>(Resource.Id.error).Text = error;
            return;
        }
        var restart = CurrentRestart();
        bool minutesEdited = minutes != _shownMinutes, restartEdited = restart != _shownRestart;
        if (minutesEdited || restartEdited)
            AppState.Store.Edit(_id, d => d with
            {
                ReminderMinutes = minutesEdited ? minutes : d.ReminderMinutes,
                Restart = restartEdited && d.Kind == ScheduleKind.Interval ? restart : d.Restart,
            });
        if (done) AppState.Store.MarkDone(_id);
        else AppState.Store.Dismiss(_id);
        _finishedByUs = true;
        Finish();
    }

    private sealed class BackCallback(Action onBack) : AndroidX.Activity.OnBackPressedCallback(true)
    {
        public override void HandleOnBackPressed() => onBack();
    }
}
