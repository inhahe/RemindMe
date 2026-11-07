using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace AlarmCalendar.Desktop;

/// <summary>
/// A month of the tasks (S2): six weeks of days, each with the times its tasks come up, and
/// the chosen day's list beside it, where a task opens in the editor. It follows every
/// change to the tasks, and the date.
/// </summary>
public partial class CalendarWindow : Window
{
    private const int LinesPerCell = 4;
    private static readonly Brush Line = new SolidColorBrush(Color.FromRgb(0xDD, 0xE2, 0xE7));
    private static readonly Brush TodayBack = new SolidColorBrush(Color.FromRgb(0xE8, 0xF1, 0xFD));
    private static readonly Brush ChosenLine = new SolidColorBrush(Color.FromRgb(0x1A, 0x73, 0xE8));
    private static readonly Brush Dim = new SolidColorBrush(Color.FromRgb(0x8A, 0x94, 0x9E));
    private static readonly Brush WaitingText = new SolidColorBrush(Color.FromRgb(0xB0, 0x00, 0x20));

    private readonly MainWindow _main;
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromMinutes(1) };
    private DateTime _month;
    private DateOnly _chosen;
    private DateTime _builtOn;
    private IReadOnlyList<CalendarDay> _days = [];

    public CalendarWindow(MainWindow main)
    {
        InitializeComponent();
        _main = main;
        var today = TimeMath.ToWall(AppHost.Clock.UtcNow, AppHost.Clock.Zone).Date;
        _month = new DateTime(today.Year, today.Month, 1);
        _chosen = DateOnly.FromDateTime(today);
        AppHost.Store.Changed += OnStoreChanged;
        _clock.Tick += (_, _) => { if (Today() != _builtOn) Build(); };
        _clock.Start();
        Closed += (_, _) => { AppHost.Store.Changed -= OnStoreChanged; _clock.Stop(); };
        Build();
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

    private void Build()
    {
        var culture = CultureInfo.CurrentCulture;
        _builtOn = Today();
        _days = CalendarMonth.Build(AppHost.Store.Live(), _month.Year, _month.Month, AppHost.Clock.UtcNow, AppHost.Clock.Zone, culture);
        MonthTitle.Text = _month.ToString("MMMM yyyy", culture);

        MonthGrid.Children.Clear();
        MonthGrid.RowDefinitions.Clear();
        MonthGrid.ColumnDefinitions.Clear();
        for (int c = 0; c < 7; c++) MonthGrid.ColumnDefinitions.Add(new ColumnDefinition());
        MonthGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (int r = 0; r < 6; r++) MonthGrid.RowDefinitions.Add(new RowDefinition());

        var names = culture.DateTimeFormat.AbbreviatedDayNames;
        int firstDay = (int)culture.DateTimeFormat.FirstDayOfWeek;
        for (int c = 0; c < 7; c++)
        {
            var head = new TextBlock { Text = names[(firstDay + c) % 7], Foreground = Dim, Margin = new Thickness(4, 0, 0, 4) };
            Grid.SetColumn(head, c);
            MonthGrid.Children.Add(head);
        }
        for (int i = 0; i < _days.Count; i++)
        {
            var cell = Cell(_days[i]);
            Grid.SetRow(cell, 1 + i / 7);
            Grid.SetColumn(cell, i % 7);
            MonthGrid.Children.Add(cell);
        }
        ShowDay();
    }

    private Border Cell(CalendarDay day)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = day.Date.Day.ToString(CultureInfo.CurrentCulture),
            FontWeight = day.IsToday ? FontWeights.Bold : FontWeights.Normal,
            Foreground = day.InMonth ? Brushes.Black : Dim,
            Margin = new Thickness(0, 0, 0, 2),
        });
        foreach (var e in day.Entries.Take(day.Entries.Count > LinesPerCell ? LinesPerCell - 1 : LinesPerCell))
            panel.Children.Add(new TextBlock
            {
                Text = $"{e.Text} {e.Title}",
                FontSize = 11,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = e.Waiting ? WaitingText : day.Past || !day.InMonth ? Dim : Brushes.Black,
                FontWeight = e.Waiting ? FontWeights.SemiBold : FontWeights.Normal,
                FontStyle = e.Estimated ? FontStyles.Italic : FontStyles.Normal,
                ToolTip = $"{e.Text} {e.Title}",
            });
        if (day.Entries.Count > LinesPerCell)
            panel.Children.Add(new TextBlock { Text = $"+{day.Entries.Count - (LinesPerCell - 1)} more", FontSize = 11, Foreground = Dim });

        bool chosen = day.Date == _chosen;
        var cell = new Border
        {
            Child = panel,
            Padding = new Thickness(4, 2, 4, 2),
            Margin = new Thickness(1),
            BorderBrush = chosen ? ChosenLine : Line,
            BorderThickness = new Thickness(chosen ? 2 : 1),
            Background = day.IsToday ? TodayBack : Brushes.White,
            Cursor = Cursors.Hand,
        };
        cell.MouseLeftButtonUp += (_, _) => { _chosen = day.Date; Build(); };
        return cell;
    }

    /// <summary>The chosen day's tasks, in full, beside the grid.</summary>
    private void ShowDay()
    {
        var culture = CultureInfo.CurrentCulture;
        var day = _days.FirstOrDefault(d => d.Date == _chosen);
        DayTitle.Text = _chosen.ToDateTime(TimeOnly.MinValue).ToString("dddd d MMMM yyyy", culture);
        DayList.Items.Clear();
        if (day is null || day.Entries.Count == 0)
        {
            DayList.Items.Add(new ListBoxItem { Content = "Nothing comes up.", Foreground = Dim, IsEnabled = false });
            return;
        }
        var now = AppHost.Clock.UtcNow;
        var zone = AppHost.Clock.Zone;
        foreach (var e in day.Entries)
        {
            if (AppHost.Store.Get(e.TaskId) is not { } t) continue;
            var text = new TextBlock { TextWrapping = TextWrapping.Wrap };
            text.Inlines.Add(new System.Windows.Documents.Run($"{e.Text}  ") { FontWeight = FontWeights.SemiBold });
            text.Inlines.Add(new System.Windows.Documents.Run(e.Title));
            if (e.Waiting)
                text.Inlines.Add(new System.Windows.Documents.Run(" · waiting to be marked done") { Foreground = WaitingText });
            text.Inlines.Add(new System.Windows.Documents.LineBreak());
            text.Inlines.Add(new System.Windows.Documents.Run(TextFormat.Schedule(t.Def, t.State, now, zone, culture))
            {
                Foreground = Dim, FontSize = 11,
            });
            DayList.Items.Add(new ListBoxItem { Content = text, Tag = e.TaskId, Padding = new Thickness(2, 4, 2, 4) });
        }
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
