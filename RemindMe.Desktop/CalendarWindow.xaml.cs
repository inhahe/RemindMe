using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Threading;

namespace RemindMe.Desktop;

/// <summary>
/// A month of the tasks (S2): six weeks of days, each with the times its tasks come up, and
/// the chosen day's list beside it, where a task opens in the editor. It follows every
/// change to the tasks, the date, and the times it shows coming up.
/// </summary>
/// <remarks>
/// The 42 day buttons and the list's rows are made once and updated in place, never
/// rebuilt: the keyboard focus and the selection stay where they are through every
/// update, and a screen reader isn't sent back to the start.
/// </remarks>
public partial class CalendarWindow : Window
{
    private const int LinesPerCell = 4;
    // The theme's text brushes, by key: set as resource references, so that the calendar
    // follows Windows from light to dark while it is open.
    private const string Plain = "TextFillColorPrimaryBrush";
    private const string Quiet = "TextFillColorSecondaryBrush";
    private const string Faint = "TextFillColorTertiaryBrush";
    private const string Waiting = "SystemFillColorCriticalBrush";
    private const string OnAccent = "TextOnAccentFillColorPrimaryBrush";

    /// <summary>Gives an element the theme's text brush <paramref name="key"/>; returns the element.</summary>
    private static T Ink<T>(T element, string key) where T : FrameworkElement
    {
        element.SetResourceReference(TextElement.ForegroundProperty, key);
        return element;
    }

    /// <summary>One time a task comes up on the chosen day, as the day's list shows it.</summary>
    public sealed class DayEntry(Guid taskId, int fromEnd) : Observable
    {
        private string _time = "", _title = "", _waitingNote = "", _schedule = "";

        public Guid TaskId { get; } = taskId;
        /// <summary>Which of the task's times on the day this is, counted from its last (0).
        /// With the task, it keeps the row from one update to the next: the time itself can
        /// move (an estimate), and marking a time done takes the task's first time away.</summary>
        public int FromEnd { get; } = fromEnd;
        public string Time => _time;
        public string Title => _title;
        /// <summary>" · waiting to be marked done", or nothing.</summary>
        public string WaitingNote => _waitingNote;
        public string Schedule => _schedule;
        /// <summary>What a screen reader says for the row (bound in CalendarWindow.xaml).</summary>
        public string Spoken => $"{Time} {Title}{(WaitingNote.Length > 0 ? ", waiting to be marked done" : "")}, {Schedule}";

        /// <summary>Shows the time as it is now. <see cref="Spoken"/> changes once, whatever
        /// parts of it change.</summary>
        public void Show(string time, string title, bool waiting, string schedule)
        {
            bool spoken = Set(ref _time, time, nameof(Time)) | Set(ref _title, title, nameof(Title))
                | Set(ref _waitingNote, waiting ? " · waiting to be marked done" : "", nameof(WaitingNote))
                | Set(ref _schedule, schedule, nameof(Schedule));
            if (spoken) Changed(nameof(Spoken));
        }

        public override string ToString() => Spoken;
    }

    private readonly MainWindow _main;
    /// <summary>The styles of a day in the grid, and of the chosen one (CalendarWindow.xaml).</summary>
    private readonly Style _cell, _chosenCell;
    /// <summary>The grid's 42 day buttons, in order; each one's Tag is the date it shows.</summary>
    private readonly Button[] _cells = new Button[42];
    private readonly ObservableCollection<DayEntry> _entries = [];
    /// <summary>Wakes at the next time shown coming up, and at least once a minute for the date.</summary>
    private readonly DispatcherTimer _clock = new();
    private DateTime _month;
    private DateOnly _chosen;
    private DateTime _builtOn;
    private DateTimeOffset? _nextChange;
    private bool _closed;
    /// <summary>1 while a rebuild for store changes is queued: changes arriving together
    /// rebuild once.</summary>
    private int _buildQueued;
    private IReadOnlyList<CalendarDay> _days = [];

    public CalendarWindow(MainWindow main)
    {
        InitializeComponent();
        _main = main;
        _cell = (Style)FindResource("DayCell");
        _chosenCell = (Style)FindResource("ChosenDayCell");
        MakeGrid();
        DayList.ItemsSource = _entries;
        var today = Today();
        _month = new DateTime(today.Year, today.Month, 1);
        _chosen = DateOnly.FromDateTime(today);
        AppHost.Store.Changed += OnStoreChanged;
        _clock.Tick += (_, _) =>
        {
            if (CalendarMonth.Stale(_builtOn, _nextChange, AppHost.Clock.UtcNow, AppHost.Clock.Zone)) Build();
            else ScheduleTick();
        };
        Closed += (_, _) => { _closed = true; AppHost.Store.Changed -= OnStoreChanged; _clock.Stop(); };
        Build();
    }

    /// <summary>The weekday names and the 42 day buttons, which <see cref="Build"/> fills.</summary>
    private void MakeGrid()
    {
        var format = CultureInfo.CurrentCulture.DateTimeFormat;
        for (int c = 0; c < 7; c++) MonthGrid.ColumnDefinitions.Add(new ColumnDefinition());
        MonthGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (int r = 0; r < 6; r++) MonthGrid.RowDefinitions.Add(new RowDefinition());
        for (int c = 0; c < 7; c++)
        {
            var head = Ink(new TextBlock
            {
                Text = format.AbbreviatedDayNames[((int)format.FirstDayOfWeek + c) % 7],
                Margin = new Thickness(8, 0, 0, 4),
            }, Quiet);
            Grid.SetColumn(head, c);
            MonthGrid.Children.Add(head);
        }
        for (int i = 0; i < _cells.Length; i++)
        {
            var cell = new Button { Style = _cell, IsTabStop = false };
            // A day with the focus is the chosen one, so the arrow keys choose as they move.
            cell.Click += (_, _) => Choose(DateOf(cell));
            cell.GotKeyboardFocus += (_, _) => Choose(DateOf(cell));
            Grid.SetRow(cell, 1 + i / 7);
            Grid.SetColumn(cell, i % 7);
            MonthGrid.Children.Add(cell);
            _cells[i] = cell;
        }
    }

    private static DateOnly DateOf(Button cell) => (DateOnly)cell.Tag;

    private Button? CellOf(DateOnly date) => _cells.FirstOrDefault(c => c.Tag is DateOnly d && d == date);

    private void ScheduleTick()
    {
        if (_closed) return;   // a store change queued before closing still builds once
        _clock.Stop();
        _clock.Interval = CalendarMonth.TickDelay(_nextChange, AppHost.Clock.UtcNow);
        _clock.Start();
    }

    private static DateTime Today() => TimeMath.ToWall(AppHost.Clock.UtcNow, AppHost.Clock.Zone).Date;

    /// <summary>Rebuilds once for changes that come together (several tasks marked done at
    /// once, a sync), from whichever thread they come.</summary>
    private void OnStoreChanged(StoreChange _)
    {
        if (Interlocked.Exchange(ref _buildQueued, 1) == 1) return;
        Dispatcher.BeginInvoke(() =>
        {
            Volatile.Write(ref _buildQueued, 0);   // a change from here on rebuilds again
            Build();
        });
    }

    private void Prev_Click(object sender, RoutedEventArgs e) => ShowMonth(_month.AddMonths(-1));

    private void Next_Click(object sender, RoutedEventArgs e) => ShowMonth(_month.AddMonths(1));

    private void Today_Click(object sender, RoutedEventArgs e)
    {
        _chosen = DateOnly.FromDateTime(Today());
        ShowMonth(new DateTime(_chosen.Year, _chosen.Month, 1));
    }

    /// <summary>Shows <paramref name="month"/>, keeping the chosen day if it is in it, and
    /// otherwise choosing the 1st. A keyboard focus in the grid goes to the chosen day.</summary>
    private void ShowMonth(DateTime month)
    {
        _month = month;
        if (_chosen.Year != month.Year || _chosen.Month != month.Month) _chosen = DateOnly.FromDateTime(month);
        bool inGrid = MonthGrid.IsKeyboardFocusWithin;
        Build();
        if (!inGrid || CellOf(_chosen) is not { } cell) return;
        if (!cell.IsKeyboardFocused) { cell.Focus(); return; }
        // The button with the focus now shows the chosen day (the buttons stay; their dates
        // change): no focus change to tell a screen reader of it, so say so.
        UIElementAutomationPeer.CreatePeerForElement(cell)?.RaiseAutomationEvent(AutomationEvents.AutomationFocusChanged);
    }

    /// <summary>Fills the grid with <see cref="_month"/>'s six weeks, and the list with the
    /// chosen day's tasks, as they are now.</summary>
    private void Build()
    {
        var culture = CultureInfo.CurrentCulture;
        _builtOn = Today();
        var now = AppHost.Clock.UtcNow;
        _days = CalendarMonth.Build(AppHost.Store.Live(), _month.Year, _month.Month, now, AppHost.Clock.Zone, culture);
        if (_days.Count != _cells.Length) throw new InvalidOperationException($"A month came as {_days.Count} days, not {_cells.Length}.");
        _nextChange = CalendarMonth.NextChange(_days, now);
        ScheduleTick();
        MonthTitle.Text = _month.ToString("MMMM yyyy", culture);
        for (int i = 0; i < _cells.Length; i++) Fill(_cells[i], _days[i]);
        ShowDay();
    }

    /// <summary>The arrow keys move between days: a day left or right, a week up or down,
    /// choosing each. Past the six weeks shown, they turn the month.</summary>
    private void MonthGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        int step = e.Key switch { Key.Left => -1, Key.Right => 1, Key.Up => -7, Key.Down => 7, _ => 0 };
        if (step == 0 || Keyboard.FocusedElement is not Button { Tag: DateOnly from }) return;
        e.Handled = true;
        var to = from.AddDays(step);
        if (CellOf(to) is { } cell) { cell.Focus(); return; }
        _chosen = to;
        ShowMonth(new DateTime(to.Year, to.Month, 1));
    }

    /// <summary>Makes <paramref name="date"/> the chosen day, and shows its tasks.</summary>
    private void Choose(DateOnly date)
    {
        if (date == _chosen) return;
        _chosen = date;
        foreach (var cell in _cells) MarkChosen(cell);
        ShowDay();
    }

    /// <summary>Outlines the button if it is the chosen day's, the only one that is a tab
    /// stop: Tab enters the grid there, and the arrow keys move on.</summary>
    private void MarkChosen(Button cell)
    {
        bool chosen = DateOf(cell) == _chosen;
        cell.Style = chosen ? _chosenCell : _cell;
        cell.IsTabStop = chosen;
    }

    private static string LongDate(DateOnly date) =>
        date.ToDateTime(TimeOnly.MinValue).ToString("dddd d MMMM yyyy", CultureInfo.CurrentCulture);

    /// <summary>Shows <paramref name="day"/> on <paramref name="cell"/>: its number and up
    /// to four of its times.</summary>
    private void Fill(Button cell, CalendarDay day)
    {
        var panel = new StackPanel();
        var number = Ink(new TextBlock
        {
            Text = day.Date.Day.ToString(CultureInfo.CurrentCulture),
            FontWeight = day.IsToday ? FontWeights.SemiBold : FontWeights.Normal,
        }, day.IsToday ? OnAccent : day.InMonth ? Plain : Faint);
        // Today's number sits on an accent-coloured pill, as in Windows' own calendar.
        var numberBack = new Border
        {
            Child = number,
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(6, 0, 6, 1),
            Margin = new Thickness(-2, 0, 0, 3),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        if (day.IsToday) numberBack.SetResourceReference(Border.BackgroundProperty, "AccentFillColorDefaultBrush");
        panel.Children.Add(numberBack);
        foreach (var e in day.Entries.Take(day.Entries.Count > LinesPerCell ? LinesPerCell - 1 : LinesPerCell))
            panel.Children.Add(Ink(new TextBlock
            {
                Text = $"{e.Text} {e.Title}",
                FontSize = 11,
                TextTrimming = TextTrimming.CharacterEllipsis,
                FontWeight = e.Waiting ? FontWeights.SemiBold : FontWeights.Normal,
                FontStyle = e.Estimated ? FontStyles.Italic : FontStyles.Normal,
                ToolTip = $"{e.Text} {e.Title}",
            }, e.Waiting ? Waiting : day.Past || !day.InMonth ? Faint : Plain));
        if (day.Entries.Count > LinesPerCell)
            panel.Children.Add(Ink(new TextBlock { Text = $"+{day.Entries.Count - (LinesPerCell - 1)} more", FontSize = 11 }, Quiet));

        cell.Content = panel;
        cell.Tag = day.Date;
        MarkChosen(cell);
        int count = day.Entries.Count;
        AutomationProperties.SetName(cell, LongDate(day.Date) + (day.IsToday ? ", today" : "")
            + ", " + (count == 0 ? "nothing comes up" : TextFormat.Tasks(count)));
    }

    /// <summary>The chosen day's tasks, in full, beside the grid.</summary>
    private void ShowDay()
    {
        var culture = CultureInfo.CurrentCulture;
        var now = AppHost.Clock.UtcNow;
        var zone = AppHost.Clock.Zone;
        DayTitle.Text = LongDate(_chosen);
        // Each time with its task, and which of the task's times on the day it is, from the end.
        var entries = _days.FirstOrDefault(d => d.Date == _chosen)?.Entries ?? [];
        var left = entries.GroupBy(e => e.TaskId).ToDictionary(g => g.Key, g => g.Count());
        var shown = new List<(CalendarEntry Entry, AlarmTask Task, int FromEnd)>();
        foreach (var e in entries)
        {
            int fromEnd = --left[e.TaskId];
            if (AppHost.Store.Get(e.TaskId) is { } task) shown.Add((e, task, fromEnd));
        }
        // With the list emptied, the focus passes from it to the chosen day.
        Rows.Update(DayList, _entries, shown, x => (x.Entry.TaskId, x.FromEnd), r => (r.TaskId, r.FromEnd),
            x => new DayEntry(x.Entry.TaskId, x.FromEnd),
            (row, x) => row.Show(x.Entry.Text, x.Entry.Title, x.Entry.Waiting,
                TextFormat.Schedule(x.Task.Def, x.Task.State, now, zone, culture)),
            whenEmpty: () => CellOf(_chosen));
        bool empty = _entries.Count == 0;
        DayList.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        NothingText.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OpenChosen()
    {
        if (DayList.SelectedItem is DayEntry entry) _main.OpenEditor(entry.TaskId);
    }

    private void DayList_DoubleClick(object sender, MouseButtonEventArgs e) => OpenChosen();

    private void DayList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        OpenChosen();
        e.Handled = true;
    }
}
