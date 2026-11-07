using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using Android.Widget;
using AndroidX.AppCompat.App;
using AndroidX.RecyclerView.Widget;
using AlertDialog = AndroidX.AppCompat.App.AlertDialog;

namespace AlarmCalendar.Droid;

/// <summary>The task list (R1, R15): tap to edit, long-press for more, + to add.</summary>
[Activity(Label = "@string/app_name", MainLauncher = true, Exported = true, LaunchMode = LaunchMode.SingleTop)]
public sealed class MainActivity : Screen
{
    private static bool _permissionsAsked;
    private readonly Handler _handler = new(Looper.MainLooper!);
    private TaskAdapter _adapter = null!;
    private TextView _empty = null!, _status = null!;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        SetContentView(Resource.Layout.activity_main);
        Ui.SetupToolbar(this, "Alarm & Calendar", back: false);
        _empty = FindViewById<TextView>(Resource.Id.empty)!;
        _status = FindViewById<TextView>(Resource.Id.status)!;
        var list = FindViewById<RecyclerView>(Resource.Id.list)!;
        list.SetLayoutManager(new LinearLayoutManager(this));
        list.AddItemDecoration(new DividerItemDecoration(this, DividerItemDecoration.Vertical));
        _adapter = new TaskAdapter(this, id => StartActivity(TaskEditActivity.IntentFor(this, id)), ShowActions);
        list.SetAdapter(_adapter);
        FindViewById(Resource.Id.add)!.Click += (_, _) => StartActivity(TaskEditActivity.IntentFor(this, null));
    }

    protected override void OnResume()
    {
        base.OnResume();
        AppState.TasksChanged += Refresh;
        Tick();
        AskForPermissionsOnce();
        // The app is in the foreground, so the ringing service may start from here.
        if (AlarmScheduler.RingingNow(AppState.Clock.UtcNow, out _).Count > 0 && !RingingService.IsRunning) RingingService.TryStart(this);
        if (AppState.Settings.SyncEnabled && AppState.Sync is null) SyncService.Start(this);
    }

    protected override void OnPause()
    {
        AppState.TasksChanged -= Refresh;
        _handler.RemoveCallbacksAndMessages(null);
        base.OnPause();
    }

    private void Tick()
    {
        Refresh();
        _handler.PostDelayed(Tick, 1000);
    }

    private void Refresh()
    {
        var now = AppState.Clock.UtcNow;
        var zone = AppState.Clock.Zone;
        var statuses = AppState.Store.Live().Select(t => AlarmEngine.Evaluate(t, now, zone))
            .OrderBy(AlarmEngine.ListRank)
            .ThenBy(st => st.NextReminder ?? st.NextOccurrence ?? DateTimeOffset.MaxValue)
            .ThenBy(st => st.Task.Def.Description, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        _adapter.Update(statuses, now);
        _empty.Visibility = statuses.Count == 0 ? ViewStates.Visible : ViewStates.Gone;
        int ringing = statuses.Count(s => s.ShouldAlert), waiting = statuses.Count(s => s.Snoozed);
        _status.Text = $"{statuses.Count} task{(statuses.Count == 1 ? "" : "s")}"
            + (ringing > 0 ? $" · {ringing} ringing" : "") + (waiting > 0 ? $" · {waiting} waiting to be marked done" : "")
            + (AppState.Settings.SyncEnabled ? $" · {SyncSummary()}" : "");
    }

    private static string SyncSummary()
    {
        var n = AppState.Sync?.Peers().Count(p => p.Linked && p.Connected) ?? 0;
        return n == 0 ? "no device connected" : $"in sync with {n} device{(n == 1 ? "" : "s")}";
    }

    private void ShowActions(TaskStatus st, View anchor)
    {
        var menu = new PopupMenu(this, anchor);
        var items = menu.Menu!;
        items.Add(0, 1, 0, "Edit");
        if (st.Triggered) items.Add(0, 2, 1, "Mark done");
        if (st.Snoozed) items.Add(0, 3, 2, "Show alarm");
        if (st.Task.Def.Kind == ScheduleKind.Countdown) items.Add(0, 4, 3, "Restart countdown");
        items.Add(0, 6, 4, st.Paused ? "Resume" : "Pause");
        items.Add(0, 5, 5, "Delete");
        menu.MenuItemClick += (_, e) =>
        {
            var id = st.Task.Id;
            switch (e.Item!.ItemId)
            {
                case 1: StartActivity(TaskEditActivity.IntentFor(this, id)); break;
                case 2: AppState.Store.MarkDone(id); break;
                case 3: StartActivity(AlarmActivity.IntentFor(this, id, onRequest: true)); break;
                case 4:
                    if (AppState.Store.Get(id) is { } t) AppState.Store.Save(id, t.Def, restartCountdown: true);
                    break;
                case 6:
                    if (st.Paused) AppState.Store.Resume(id);
                    else AppState.Store.Pause(id);
                    break;
                case 5:
                    var name = TextFormat.Snippet(st.Task.Def.Description, 40);
                    Ui.Confirm(this, "Delete", $"Delete \"{(name.Length > 0 ? name : "this task")}\"?", "Delete",
                        () => AppState.Store.Delete(id));
                    break;
            }
        };
        menu.Show();
    }

    public override bool OnCreateOptionsMenu(IMenu? menu)
    {
        MenuInflater.Inflate(Resource.Menu.main, menu);
        return true;
    }

    public override bool OnOptionsItemSelected(IMenuItem item)
    {
        if (item.ItemId == Resource.Id.menu_calendar) { StartActivity(new Intent(this, typeof(CalendarActivity))); return true; }
        if (item.ItemId == Resource.Id.menu_devices) { StartActivity(new Intent(this, typeof(DevicesActivity))); return true; }
        if (item.ItemId == Resource.Id.menu_settings) { StartActivity(new Intent(this, typeof(SettingsActivity))); return true; }
        return base.OnOptionsItemSelected(item);
    }

    /// <summary>The permissions an alarm needs to reach the user, asked for once per run;
    /// Settings shows their state and offers to fix them any time.</summary>
    private void AskForPermissionsOnce()
    {
        if (_permissionsAsked) return;
        _permissionsAsked = true;
        if (OperatingSystem.IsAndroidVersionAtLeast(33)
            && CheckSelfPermission(Android.Manifest.Permission.PostNotifications) != Permission.Granted)
            RequestPermissions([Android.Manifest.Permission.PostNotifications], 1);
        else AskForSpecialAccess();
    }

    public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        if (requestCode == 1) AskForSpecialAccess();
    }

    private void AskForSpecialAccess()
    {
        var missing = Permissions.Missing(this).FirstOrDefault();
        if (missing is null) return;
        new AlertDialog.Builder(this).SetTitle(missing.Title)!.SetMessage(missing.Why)!
            .SetPositiveButton("Open settings", (_, _) => missing.Fix(this))!
            .SetNegativeButton("Not now", (_, _) => { })!.Show();
    }
}

/// <summary>The rows of the task list.</summary>
public sealed class TaskAdapter(Context context, Action<Guid> open, Action<TaskStatus, View> actions) : RecyclerView.Adapter
{
    /// <summary>What a row shows; rows are redrawn only when this changes, so the
    /// once-a-second refresh costs nothing while nothing visible moves.</summary>
    private sealed record Row(TaskStatus Status, string Description, string Schedule, string State, string Next, int Look)
    {
        public bool SameLook(Row o) => Description == o.Description && Schedule == o.Schedule
            && State == o.State && Next == o.Next && Look == o.Look;
    }

    private List<TaskStatus> _items = [];
    private List<Row> _rows = [];

    public void Update(List<TaskStatus> items, DateTimeOffset now)
    {
        var zone = AppState.Clock.Zone;
        var rows = items.Select(st =>
        {
            var snippet = TextFormat.Snippet(st.Task.Def.Description);
            var next = TextFormat.Next(st, now, zone);
            return new Row(st, snippet.Length > 0 ? snippet : "(no description)",
                TextFormat.Schedule(st.Task.Def, st.Task.State, now, zone), TextFormat.Status(st, now, zone),
                next == "—" ? "" : $"Next: {next}", st.ShouldAlert ? 1 : st.Snoozed ? 2 : st.Finished || st.Paused ? 3 : 0);
        }).ToList();
        bool sameOrder = items.Count == _items.Count && items.Select(i => i.Task.Id).SequenceEqual(_items.Select(i => i.Task.Id));
        var old = _rows;
        _items = items;
        _rows = rows;
        if (!sameOrder) { NotifyDataSetChanged(); return; }
        for (int i = 0; i < rows.Count; i++)
            if (!rows[i].SameLook(old[i])) NotifyItemChanged(i);
    }

    public override int ItemCount => _items.Count;

    public override RecyclerView.ViewHolder OnCreateViewHolder(ViewGroup parent, int viewType)
    {
        var v = LayoutInflater.From(context)!.Inflate(Resource.Layout.task_row, parent, false)!;
        var h = new Holder(v);
        v.Click += (_, _) => { if (h.BindingAdapterPosition >= 0) open(_items[h.BindingAdapterPosition].Task.Id); };
        v.LongClick += (_, _) => { if (h.BindingAdapterPosition >= 0) actions(_items[h.BindingAdapterPosition], v); };
        return h;
    }

    public override void OnBindViewHolder(RecyclerView.ViewHolder holder, int position)
    {
        var h = (Holder)holder;
        var r = _rows[position];
        h.Description.Text = r.Description;
        h.Schedule.Text = r.Schedule;
        h.Status.Text = r.State;
        h.Next.Text = r.Next;
        h.Next.Visibility = r.Next.Length > 0 ? ViewStates.Visible : ViewStates.Gone;
        h.ItemView.SetBackgroundColor(r.Look == 1 ? new Android.Graphics.Color(context.GetColor(Resource.Color.ringing_bg))
            : r.Look == 2 ? new Android.Graphics.Color(context.GetColor(Resource.Color.waiting_bg)) : Android.Graphics.Color.Transparent);
        h.ItemView.Alpha = r.Look == 3 ? 0.55f : 1f;
    }

    private sealed class Holder(View v) : RecyclerView.ViewHolder(v)
    {
        public TextView Description { get; } = v.FindViewById<TextView>(Resource.Id.description)!;
        public TextView Schedule { get; } = v.FindViewById<TextView>(Resource.Id.schedule)!;
        public TextView Status { get; } = v.FindViewById<TextView>(Resource.Id.status)!;
        public TextView Next { get; } = v.FindViewById<TextView>(Resource.Id.next)!;
    }
}
