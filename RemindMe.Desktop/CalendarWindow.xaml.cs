using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace RemindMe.Desktop;

/// <summary>
/// A month of the tasks (S2): six weeks of days, each with the times its tasks come up, and
/// the chosen day's list beside it, where a task opens in the editor. It follows every
/// change to the tasks, the date, and the times it shows coming up.
/// </summary>
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

    /// <summary>The same for a run of text.</summary>
    private static Run Ink(Run run, string key)
    {
        run.SetResourceReference(TextElement.ForegroundProperty, key);
        return run;
    }

    private readonly MainWindow _main;
    /// <summary>The styles of a day in the grid, and of the chosen one (CalendarWindow.xaml).</summary>
    private readonly Style _cell, _chosenCell;
    /// <summary>The grid's day buttons, by date, as last built.</summary>
    private readonly Dictionary<DateOnly, Button> _cells = new();
    /// <summary>Wakes at the next time shown coming up, and at least once a minute for the date.</summary>
    private readonly DispatcherTimer _clock = new();
    private DateTime _month;
    private DateOnly _chosen;
    private DateTime _builtOn;
    private DateTimeOffset? _nextChange;
    private bool _closed;
    private IReadOnlyList<CalendarDay> _days = [];

    public CalendarWindow(MainWindow main)
    {
        InitializeComponent();
        _main = main;
        _cell = (Style)FindResource("DayCell");
        _chosenCell = (Style)FindResource("ChosenDayCell");
        var today = TimeMath.ToWall(AppHost.Clock.UtcNow, AppHost.Clock.Zone).Date;
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

    private void ScheduleTick()
    {
        if (_closed) return;   // a store change queued before closing still builds once
        _clock.Stop();
        _clock.Interval = CalendarMonth.TickDelay(_nextChange, AppHost.Clock.UtcNow);
        _clock.Start();
    }

    private static DateTime Today() => TimeMath.ToWall(AppHost.Clock.UtcNow, AppHost.Clock.Zone).Date;

    private void OnStoreChanged(StoreChange _) => Dispatcher.BeginInvoke(Build);

    private void Prev_Click(object sender, RoutedEventArgs e) => ShowMonth(_month.AddMonths(-1));

    private void Next_Click(object sender, RoutedEventArgs e) => ShowMonth(_month.AddMonths(1));

    private void Today_Click(object sender, RoutedEventArgs e)
    {
        _chosen = DateOnly.FromDateTime(Today());
        ShowMonth(new DateTime(_chosen.Year, _chosen.Month, 1));
    }

    private void ShowMonth(DateTime month)
    {
        _month = month;
        // Keep the chosen day if it is still in view; otherwise the 1st.
        if (_chosen.Year != month.Year || _chosen.Month != month.Month) _chosen = DateOnly.FromDateTime(month);
        Build();
    }

    /// <summary>Builds the month and the chosen day's list. The keyboard focus, if it was on
    /// a day, goes to the rebuilt <paramref name="focus"/>, or to the rebuilt same day.</summary>
    private void Build() => Build(null);

    private void Build(DateOnly? focus)
    {
        var culture = CultureInfo.CurrentCulture;
        _builtOn = Today();
        var now = AppHost.Clock.UtcNow;
        _days = CalendarMonth.Build(AppHost.Store.Live(), _month.Year, _month.Month, now, AppHost.Clock.Zone, culture);
        _nextChange = CalendarMonth.NextChange(_days, now);
        ScheduleTick();
        MonthTitle.Text = _month.ToString("MMMM yyyy", culture);

        // The day with the focus, to give it back to the rebuilt day: the old one goes.
        var focusedDay = focus ?? (FocusedIn(MonthGrid) is Button { Tag: DateOnly date } ? date : null);
        MonthGrid.Children.Clear();
        _cells.Clear();
        MonthGrid.RowDefinitions.Clear();
        MonthGrid.ColumnDefinitions.Clear();
        for (int c = 0; c < 7; c++) MonthGrid.ColumnDefinitions.Add(new ColumnDefinition());
        MonthGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (int r = 0; r < 6; r++) MonthGrid.RowDefinitions.Add(new RowDefinition());

        var names = culture.DateTimeFormat.AbbreviatedDayNames;
        int firstDay = (int)culture.DateTimeFormat.FirstDayOfWeek;
        for (int c = 0; c < 7; c++)
        {
            var head = Ink(new TextBlock { Text = names[(firstDay + c) % 7], Margin = new Thickness(8, 0, 0, 4) }, Quiet);
            Grid.SetColumn(head, c);
            MonthGrid.Children.Add(head);
        }
        for (int i = 0; i < _days.Count; i++)
        {
            var cell = Cell(_days[i]);
            Grid.SetRow(cell, 1 + i / 7);
            Grid.SetColumn(cell, i % 7);
            MonthGrid.Children.Add(cell);
            _cells[_days[i].Date] = cell;
        }
        ShowDay();
        if (focusedDay is { } day && _cells.TryGetValue(day, out var again)) FocusSoon(again);
    }

    /// <summary>The arrow keys move between days: a day left or right, a week up or down,
    /// choosing each (a day with the focus is the chosen one). Past the six weeks shown,
    /// they turn the month.</summary>
    private void MonthGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        int step = e.Key switch { Key.Left => -1, Key.Right => 1, Key.Up => -7, Key.Down => 7, _ => 0 };
        if (step == 0 || FocusedIn(MonthGrid) is not Button { Tag: DateOnly from }) return;
        e.Handled = true;
        var to = from.AddDays(step);
        if (_cells.TryGetValue(to, out var cell)) { cell.Focus(); return; }
        _chosen = to;
        _month = new DateTime(to.Year, to.Month, 1);
        Build(to);
    }

    /// <summary>Makes <paramref name="date"/> the chosen day, and shows its tasks.</summary>
    private void Choose(DateOnly date)
    {
        if (date == _chosen) return;
        if (_cells.TryGetValue(_chosen, out var old)) { old.Style = _cell; old.IsTabStop = false; }
        _chosen = date;
        if (_cells.TryGetValue(date, out var cell)) { cell.Style = _chosenCell; cell.IsTabStop = true; }
        ShowDay();
    }

    /// <summary>The element within <paramref name="scope"/> that has this window's focus
    /// (logical focus, so also while another window is active), or null.</summary>
    private DependencyObject? FocusedIn(Visual scope) =>
        FocusManager.GetFocusedElement(this) is DependencyObject f && scope.IsAncestorOf(f) ? f : null;

    /// <summary>Focuses <paramref name="element"/> once it is laid out: a new element can't
    /// take the focus before then.</summary>
    private void FocusSoon(UIElement element) =>
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => element.Focus());

    private static string LongDate(DateOnly date) =>
        date.ToDateTime(TimeOnly.MinValue).ToString("dddd d MMMM yyyy", CultureInfo.CurrentCulture);

    private Button Cell(CalendarDay day)
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

        bool chosen = day.Date == _chosen;
        // Only the chosen day is a tab stop: Tab enters the grid there, and arrows move on.
        var cell = new Button { Content = panel, Tag = day.Date, Style = chosen ? _chosenCell : _cell, IsTabStop = chosen };
        // A day with the focus is the chosen one, so the arrow keys choose as they move.
        cell.Click += (_, _) => Choose(day.Date);
        cell.GotKeyboardFocus += (_, _) => Choose(day.Date);
        int count = day.Entries.Count;
        AutomationProperties.SetName(cell, LongDate(day.Date) + (day.IsToday ? ", today" : "")
            + (count == 0 ? ", nothing comes up" : count == 1 ? ", 1 task" : $", {count} tasks"));
        return cell;
    }

    /// <summary>The chosen day's tasks, in full, beside the grid. A task selected before,
    /// and the focus if it was on one, stay on that task.</summary>
    private void ShowDay()
    {
        var culture = CultureInfo.CurrentCulture;
        var day = _days.FirstOrDefault(d => d.Date == _chosen);
        DayTitle.Text = LongDate(_chosen);
        var selected = (DayList.SelectedItem as ListBoxItem)?.Tag;
        bool focused = FocusedIn(DayList) is not null;
        DayList.Items.Clear();
        if (day is null || day.Entries.Count == 0)
        {
            DayList.Items.Add(new ListBoxItem { Content = "Nothing comes up.", IsEnabled = false });
            return;
        }
        var now = AppHost.Clock.UtcNow;
        var zone = AppHost.Clock.Zone;
        foreach (var e in day.Entries)
        {
            if (AppHost.Store.Get(e.TaskId) is not { } t) continue;
            var text = new TextBlock { TextWrapping = TextWrapping.Wrap };
            text.Inlines.Add(new Run($"{e.Text}  ") { FontWeight = FontWeights.SemiBold });
            text.Inlines.Add(new Run(e.Title));
            if (e.Waiting) text.Inlines.Add(Ink(new Run(" · waiting to be marked done"), Waiting));
            text.Inlines.Add(new LineBreak());
            text.Inlines.Add(Ink(new Run(TextFormat.Schedule(t.Def, t.State, now, zone, culture)) { FontSize = 11 }, Quiet));
            DayList.Items.Add(new ListBoxItem { Content = text, Tag = e.TaskId });
        }
        if (selected is null) return;
        if (DayList.Items.OfType<ListBoxItem>().FirstOrDefault(i => Equals(i.Tag, selected)) is not { } again) return;
        DayList.SelectedItem = again;
        if (focused) FocusSoon(again);
    }

    private void OpenChosen()
    {
        if (DayList.SelectedItem is ListBoxItem { Tag: Guid id }) _main.OpenEditor(id);
    }

    private void DayList_DoubleClick(object sender, MouseButtonEventArgs e) => OpenChosen();

    private void DayList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        OpenChosen();
        e.Handled = true;
    }
}
