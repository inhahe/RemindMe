using System.Globalization;
using Android.App;
using Android.Graphics;
using Android.OS;
using Android.Views;
using Android.Widget;

namespace RemindMe.Droid;

/// <summary>
/// A month of the tasks (S2), as the desktop's Calendar window: six weeks of days, each
/// showing its first time and how many more, and the chosen day's tasks below, where one
/// opens in the editor. It follows every change to the tasks, the date, and the times it
/// shows coming up.
/// </summary>
[Activity(Label = "Calendar", Exported = false)]
public sealed class CalendarActivity : Screen
{
    private DateTime _month;
    private DateOnly _chosen;
    private IReadOnlyList<CalendarDay> _days = [];
    private readonly Handler _ticks = new(Looper.MainLooper!);
    private DateTime _builtOn;
    private DateTimeOffset? _nextChange;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        SetContentView(Resource.Layout.activity_calendar);
        Ui.SetupToolbar(this, "Calendar", back: true);
        var today = TimeMath.ToWall(AppState.Clock.UtcNow, AppState.Clock.Zone).Date;
        _chosen = savedInstanceState?.GetInt("chosen") is int n and > 0 ? DateOnly.FromDayNumber(n) : DateOnly.FromDateTime(today);
        _month = new DateTime(_chosen.Year, _chosen.Month, 1);
        Find<Button>(Resource.Id.prev).Click += (_, _) => ShowMonth(_month.AddMonths(-1));
        Find<Button>(Resource.Id.next).Click += (_, _) => ShowMonth(_month.AddMonths(1));
        Find<Button>(Resource.Id.today).Click += (_, _) =>
        {
            _chosen = DateOnly.FromDateTime(TimeMath.ToWall(AppState.Clock.UtcNow, AppState.Clock.Zone).Date);
            ShowMonth(new DateTime(_chosen.Year, _chosen.Month, 1));
        };
    }

    protected override void OnSaveInstanceState(Bundle outState)
    {
        base.OnSaveInstanceState(outState);
        outState.PutInt("chosen", _chosen.DayNumber);
    }

    protected override void OnResume()
    {
        base.OnResume();
        AppState.TasksChanged += Build;
        Build();   // the date, or the tasks, may have changed meanwhile
    }

    protected override void OnPause()
    {
        AppState.TasksChanged -= Build;
        _ticks.RemoveCallbacksAndMessages(null);
        base.OnPause();
    }

    private DateTime Today() => TimeMath.ToWall(AppState.Clock.UtcNow, AppState.Clock.Zone).Date;

    /// <summary>Wakes at the next time shown coming up, and at least once a minute for the date.</summary>
    private void ScheduleTick()
    {
        _ticks.RemoveCallbacksAndMessages(null);
        _ticks.PostDelayed(() =>
        {
            if (CalendarMonth.Stale(_builtOn, _nextChange, AppState.Clock.UtcNow, AppState.Clock.Zone)) Build();
            else ScheduleTick();
        }, (long)CalendarMonth.TickDelay(_nextChange, AppState.Clock.UtcNow).TotalMilliseconds);
    }

    private void ShowMonth(DateTime month)
    {
        _month = month;
        if (_chosen.Year != month.Year || _chosen.Month != month.Month) _chosen = DateOnly.FromDateTime(month);
        Build();
    }

    private void Build()
    {
        var culture = CultureInfo.CurrentCulture;
        var now = AppState.Clock.UtcNow;
        _builtOn = Today();
        _days = CalendarMonth.Build(AppState.Store.Live(), _month.Year, _month.Month, now, AppState.Clock.Zone, culture);
        _nextChange = CalendarMonth.NextChange(_days, now);
        ScheduleTick();   // Build runs only while resumed: OnPause unsubscribes and cancels
        Find<TextView>(Resource.Id.month).Text = _month.ToString("MMMM yyyy", culture);

        var grid = Find<GridLayout>(Resource.Id.grid);
        grid.RemoveAllViews();
        var names = culture.DateTimeFormat.ShortestDayNames;
        int firstDay = (int)culture.DateTimeFormat.FirstDayOfWeek;
        for (int c = 0; c < 7; c++)
            grid.AddView(new TextView(this) { Text = names[(firstDay + c) % 7], Gravity = GravityFlags.Center }, CellParams());
        foreach (var day in _days) grid.AddView(Cell(day), CellParams());
        ShowDay();
    }

    /// <summary>Seven equal columns.</summary>
    private static GridLayout.LayoutParams CellParams() => new(GridLayout.InvokeSpec(GridLayout.Undefined), GridLayout.InvokeSpec(GridLayout.Undefined, 1f))
    {
        Width = 0,
        Height = ViewGroup.LayoutParams.WrapContent,
    };

    private View Cell(CalendarDay day)
    {
        var cell = new LinearLayout(this) { Orientation = Orientation.Vertical };
        cell.SetPadding(4, 6, 4, 6);
        cell.SetMinimumHeight(Dp(52));
        var number = new TextView(this) { Text = day.Date.Day.ToString(CultureInfo.CurrentCulture), Gravity = GravityFlags.Center };
        if (day.IsToday) number.SetTypeface(null, TypefaceStyle.Bold);
        if (!day.InMonth) number.Alpha = 0.4f;
        cell.AddView(number);
        if (day.Entries.Count > 0)
        {
            var first = day.Entries[0];
            var line = new TextView(this)
            {
                Text = day.Entries.Count > 1 ? $"{ShortText(first)} +{day.Entries.Count - 1}" : ShortText(first),
                Gravity = GravityFlags.Center,
                TextSize = 9,
            };
            line.SetMaxLines(1);
            line.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
            if (day.Entries.Any(e => e.Waiting)) line.SetTextColor(new Color(GetColor(Resource.Color.error)));
            if (day.Past || !day.InMonth) line.Alpha = 0.5f;
            cell.AddView(line);
        }
        if (day.Date == _chosen) cell.SetBackgroundColor(new Color(GetColor(Resource.Color.waiting_bg)));
        else if (day.IsToday) cell.SetBackgroundColor(Color.Argb(0x22, 0x19, 0x76, 0xD2));
        cell.Click += (_, _) => { _chosen = day.Date; Build(); };
        return cell;
    }

    /// <summary>A cell's line, as short as a phone's cell needs: "9:00a", "6:00p" (or "18:00",
    /// as the phone's clock setting says), "~" before an estimate, or "15 min" for a
    /// repeating task. Formatted here rather than trimmed from the full text, whose spacing
    /// before AM/PM differs between Android versions.</summary>
    private string ShortText(CalendarEntry e)
    {
        if (e.Repeating) return e.Text.Replace("every ", "");
        var t = TimeMath.ToWall(e.At, AppState.Clock.Zone);
        string time = Ui.Is24Hour(this) ? $"{t.Hour}:{t.Minute:00}"
            : $"{(t.Hour % 12 == 0 ? 12 : t.Hour % 12)}:{t.Minute:00}{(t.Hour < 12 ? "a" : "p")}";
        return (e.Estimated ? "~" : "") + time;
    }

    private void ShowDay()
    {
        var culture = CultureInfo.CurrentCulture;
        Find<TextView>(Resource.Id.day_title).Text = _chosen.ToDateTime(TimeOnly.MinValue).ToString("dddd d MMMM", culture);
        var list = Find<LinearLayout>(Resource.Id.day_list);
        list.RemoveAllViews();
        var day = _days.FirstOrDefault(d => d.Date == _chosen);
        if (day is null || day.Entries.Count == 0)
        {
            list.AddView(new TextView(this) { Text = "Nothing comes up." });
            return;
        }
        var now = AppState.Clock.UtcNow;
        var zone = AppState.Clock.Zone;
        foreach (var e in day.Entries)
        {
            if (AppState.Store.Get(e.TaskId) is not { } t) continue;
            var row = new TextView(this)
            {
                Text = $"{e.Text}  {e.Title}{(e.Waiting ? " · waiting to be marked done" : "")}\n"
                    + TextFormat.Schedule(t.Def, t.State, now, zone, culture),
            };
            row.SetPadding(0, Dp(8), 0, Dp(8));
            if (e.Waiting) row.SetTextColor(new Color(GetColor(Resource.Color.error)));
            if (e.Estimated) row.SetTypeface(null, TypefaceStyle.Italic);
            row.Click += (_, _) => StartActivity(TaskEditActivity.IntentFor(this, e.TaskId));
            list.AddView(row);
        }
    }

    private int Dp(int dp) => (int)(dp * Resources!.DisplayMetrics!.Density + 0.5f);

    public override bool OnSupportNavigateUp()
    {
        Finish();
        return true;
    }
}
