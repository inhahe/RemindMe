using System.Globalization;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using Android.Widget;

namespace RemindMe.Droid;

/// <summary>
/// The alarm screen, shown over the lock screen (D4), one ringing task at a time. As on the
/// desktop, leaving it any way but Mark done (Back included) dismisses the task: it stays
/// due and reminds again after its reminder interval (R8). The restart mode (R7) and the
/// reminder interval can be changed here; only fields the user changed are written back.
/// When its task stops ringing for any other reason (a notification button, another
/// device), it moves on to the next ringing task, or closes, in the background too. It
/// plays nothing itself: the ringing service does, or, when that couldn't start, the
/// notification of the safety net.
/// </summary>
[Activity(Label = "Alarm", Exported = false, LaunchMode = LaunchMode.SingleTask, TaskAffinity = "com.inhahe.remindme.alarm",
    ExcludeFromRecents = true, Theme = "@style/AlarmTheme")]
public sealed class AlarmActivity : Screen
{
    private readonly Handler _handler = new(Looper.MainLooper!);
    private Guid _id;
    /// <summary>Opened from the list's "Show alarm" for a snoozed task: stays up while the
    /// task is waiting, not only while it rings.</summary>
    private bool _shownOnRequest;
    private int _shownMinutes;
    private RestartMode _shownRestart;
    private bool _leaving;
    /// <summary>The alert the user silenced here: a later alert of the same task sounds
    /// again, and can be silenced again.</summary>
    private DateTimeOffset? _silencedAlert;

    public static Intent IntentFor(Context c, Guid task, bool onRequest = false) =>
        new Intent(c, typeof(AlarmActivity)).PutExtra("task", task.ToString()).PutExtra("show", onRequest)
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
            _silencedAlert = AppState.Store.Status(_id)?.AlertDue;
            Find<Button>(Resource.Id.silence).Enabled = false;
        };
        var snooze = Find<LinearLayout>(Resource.Id.snooze_row);
        foreach (var preset in AlarmEngine.SnoozePresets)
        {
            var b = (Button)LayoutInflater.Inflate(Resource.Layout.snooze_button, snooze, false)!;
            b.Text = TextFormat.Short(preset);
            b.Click += (_, _) => Leave(done: false, remindIn: preset);
            snooze.AddView(b);
        }
        OnBackPressedDispatcher.AddCallback(this, new BackCallback(() => Leave(done: false)));
        AppState.TasksChanged += Refresh;
        Load(Intent);
    }

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        Load(intent);
    }

    private void Load(Intent? intent)
    {
        if (!Guid.TryParse(intent?.GetStringExtra("task"), out var id) || AppState.Store.Get(id) is not { } t)
        {
            NextOrFinish();
            return;
        }
        bool onRequest = intent!.GetBooleanExtra("show", false);
        if (id == _id && !_leaving)
        {
            // Opened again for the task already shown: keep whatever the user has typed.
            _shownOnRequest |= onRequest;
            Refresh();
            return;
        }
        Show(t, onRequest);
    }

    private void Show(AlarmTask t, bool onRequest)
    {
        _id = t.Id;
        _shownOnRequest = onRequest;
        ShowChoices(t.Def);
        Find<View>(Resource.Id.restart_panel).Visibility = t.Def.Kind == ScheduleKind.Interval ? ViewStates.Visible : ViewStates.Gone;
        _silencedAlert = null;
        Find<Button>(Resource.Id.silence).Enabled = true;
        Find<TextView>(Resource.Id.error).Text = "";
        Refresh();
    }

    /// <summary>A request to link waits in its notification: it mustn't get in the way here.</summary>
    protected override bool AsksToLink => false;

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

    protected override void OnDestroy()
    {
        AppState.TasksChanged -= Refresh;
        base.OnDestroy();
    }

    private void Tick()
    {
        Refresh();
        _handler.PostDelayed(Tick, 1000);
    }

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
        if (_leaving) return;
        var now = AppState.Clock.UtcNow;
        var zone = AppState.Clock.Zone;
        if (AppState.Store.Get(_id) is not { Def.Deleted: false } t) { NextOrFinish(); return; }
        var st = AlarmEngine.Evaluate(t, now, zone);
        // Dealt with elsewhere (a notification button, another device): nothing to show here.
        if (!(st.ShouldAlert || (_shownOnRequest && st.Triggered))) { NextOrFinish(); return; }
        if (_silencedAlert is not null && _silencedAlert != st.AlertDue)
        {
            _silencedAlert = null;
            Find<Button>(Resource.Id.silence).Enabled = true;
        }
        var d = t.Def;
        string when = st.Latest is { } l ? TextFormat.When(l, now, zone) : "";
        Find<TextView>(Resource.Id.heading).Text = $"⏰ {AlarmEngine.AlertTitle(st)} · {when} · {TextFormat.Schedule(d, t.State, now, zone)}";
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

    /// <summary>Shows the next task that is ringing, or closes when none is.</summary>
    private void NextOrFinish()
    {
        if (IsFinishing) return;
        var next = AlarmScheduler.RingingNow(AppState.Clock.UtcNow, out _).FirstOrDefault(s => s.Task.Id != _id);
        if (next is null) { Finish(); return; }
        _leaving = false;
        Show(next.Task, onRequest: false);
    }

    /// <param name="remindIn">A snooze preset (S5), overriding the task's reminder interval this once.</param>
    private void Leave(bool done, TimeSpan? remindIn = null)
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
        _leaving = true;
        if (done) AppState.Store.MarkDone(_id);
        else AppState.Store.Dismiss(_id, remindIn);
        NextOrFinish();
    }

    private sealed class BackCallback(Action onBack) : AndroidX.Activity.OnBackPressedCallback(true)
    {
        public override void HandleOnBackPressed() => onBack();
    }
}
