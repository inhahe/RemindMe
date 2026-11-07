using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using Forms = System.Windows.Forms;

namespace RemindMe.Desktop;

/// <summary>One row of the task list (R15).</summary>
public sealed class TaskRow : INotifyPropertyChanged
{
    public Guid Id { get; init; }
    private string _description = "", _schedule = "", _status = "", _next = "";
    private bool _ringing, _waiting, _finished, _paused;

    public string Description { get => _description; set => Set(ref _description, value); }
    public string Schedule { get => _schedule; set => Set(ref _schedule, value); }
    public string Status { get => _status; set => Set(ref _status, value); }
    public string Next { get => _next; set => Set(ref _next, value); }
    public bool Ringing { get => _ringing; set => Set(ref _ringing, value); }
    public bool Waiting { get => _waiting; set => Set(ref _waiting, value); }
    public bool Finished { get => _finished; set => Set(ref _finished, value); }
    public bool Paused { get => _paused; set => Set(ref _paused, value); }
    public TaskStatus? Latest { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

/// <summary>The task list, the tray icon, and the entry points to every other window.</summary>
public partial class MainWindow : Window
{
    private readonly ObservableCollection<TaskRow> _rows = [];
    private readonly Forms.NotifyIcon _tray;
    private readonly Dictionary<Guid, TaskEditorWindow> _editors = new();
    private SettingsWindow? _settings;
    private DevicesWindow? _devices;
    private CalendarWindow? _calendar;
    private bool _exiting;
    private bool _trayHintShown;

    public MainWindow()
    {
        InitializeComponent();
        List.ItemsSource = _rows;
        RestorePlacement();

        _tray = new Forms.NotifyIcon { Icon = LoadTrayIcon(), Text = "RemindMe", Visible = true };
        _tray.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) ShowFromTray(); };
        // On the button's release, as Windows opens its own tray menus.
        _tray.MouseUp += (_, e) => { if (e.Button == Forms.MouseButtons.Right) ShowTrayMenu(); };

        AppHost.Alarms.Evaluated += OnEvaluated;
        OnEvaluated(AppHost.Alarms.Current);
        AppHost.Alarms.Evaluate();
        UpdateButtons();
    }

    private static System.Drawing.Icon LoadTrayIcon()
    {
        var res = Application.GetResourceStream(new Uri("pack://application:,,,/app.ico"));
        using var stream = res!.Stream;
        return new System.Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize);
    }

    public void ShowFromTray()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private void TrayShow_Click(object sender, RoutedEventArgs e) => ShowFromTray();

    /// <summary>Opens the tray icon's menu (MainWindow.xaml's TrayMenu) at the mouse.</summary>
    private void ShowTrayMenu()
    {
        var menu = (System.Windows.Controls.ContextMenu)Resources["TrayMenu"];
        menu.IsOpen = true;
        // Windows closes a menu when another window is clicked only if the menu's own window
        // is in the foreground, which a click on the tray doesn't make it.
        if (PresentationSource.FromVisual(menu) is HwndSource source) NativeMethods.SetForegroundWindow(source.Handle);
        menu.Focus();
    }

    // ---- The list ----------------------------------------------------------------

    private void OnEvaluated(IReadOnlyList<TaskStatus> statuses)
    {
        var now = AppHost.Clock.UtcNow;
        var zone = AppHost.Clock.Zone;
        var byId = _rows.ToDictionary(r => r.Id);
        var ordered = statuses.OrderBy(AlarmEngine.ListRank).ThenBy(st => st.NextReminder ?? st.NextOccurrence ?? DateTimeOffset.MaxValue)
            .ThenBy(st => st.Task.Def.Description, StringComparer.CurrentCultureIgnoreCase).ToList();

        for (int i = 0; i < ordered.Count; i++)
        {
            var st = ordered[i];
            if (!byId.TryGetValue(st.Task.Id, out var row))
            {
                row = new TaskRow { Id = st.Task.Id };
                _rows.Insert(Math.Min(i, _rows.Count), row);
            }
            row.Latest = st;
            row.Description = TextFormat.Snippet(st.Task.Def.Description) is { Length: > 0 } s ? s : "(no description)";
            row.Schedule = TextFormat.Schedule(st.Task.Def, st.Task.State, now, zone);
            row.Status = TextFormat.Status(st, now, zone);
            row.Next = TextFormat.Next(st, now, zone);
            row.Ringing = st.ShouldAlert;
            row.Waiting = st.Snoozed;
            row.Finished = st.Finished;
            row.Paused = st.Paused;
            int at = _rows.IndexOf(row);
            if (at != i) _rows.Move(at, i);   // Move keeps the selection, unlike remove + insert
        }
        var keep = ordered.Select(st => st.Task.Id).ToHashSet();
        for (int i = _rows.Count - 1; i >= 0; i--)
            if (!keep.Contains(_rows[i].Id)) _rows.RemoveAt(i);

        int ringing = statuses.Count(s => s.ShouldAlert), waiting = statuses.Count(s => s.Snoozed);
        StatusText.Text = $"{statuses.Count} task{(statuses.Count == 1 ? "" : "s")}"
            + (ringing > 0 ? $" · {ringing} ringing" : "") + (waiting > 0 ? $" · {waiting} waiting to be marked done" : "");
        EmptyHint.Visibility = statuses.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var tip = "RemindMe" + (ringing > 0 ? $" — {ringing} ringing" : waiting > 0 ? $" — {waiting} waiting" : "");
        _tray.Text = tip.Length > 63 ? tip[..63] : tip;
        UpdateButtons();
    }

    private List<TaskRow> SelectedRows() => List.SelectedItems.Cast<TaskRow>().ToList();

    private void UpdateButtons()
    {
        var sel = SelectedRows();
        EditButton.IsEnabled = sel.Count == 1;
        DeleteButton.IsEnabled = sel.Count > 0;
        DoneButton.IsEnabled = sel.Any(r => r.Latest?.Triggered == true);
        ShowAlarmButton.IsEnabled = sel.Any(r => r.Latest?.Snoozed == true);
        PauseButton.IsEnabled = sel.Count > 0;
        // Resume when everything selected is paused; otherwise Pause.
        bool resume = sel.Count > 0 && sel.All(r => r.Latest?.Paused == true);
        PauseButton.Content = PauseMenuItem.Header = resume ? "Res_ume" : "_Pause";
        Icons.SetGlyph(PauseButton, PauseMenuIcon.Text = resume ? Icons.Play : Icons.Pause);
    }

    private void List_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => UpdateButtons();

    private void Row_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is System.Windows.Controls.ListViewItem { DataContext: TaskRow row }) OpenEditor(row.Id);
    }

    private void List_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete) { Delete_Click(sender, e); e.Handled = true; }
        else if (e.Key == Key.Enter && SelectedRows() is [var row]) { OpenEditor(row.Id); e.Handled = true; }
    }

    // ---- Commands ----------------------------------------------------------------

    private void New_Click(object sender, RoutedEventArgs e) => OpenEditor(null);

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedRows() is [var row]) OpenEditor(row.Id);
    }

    private void Done_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in SelectedRows().Where(r => r.Latest?.Triggered == true))
            AppHost.Store.MarkDone(row.Id);
    }

    private void ShowAlarm_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in SelectedRows().Where(r => r.Latest?.Triggered == true))
            AppHost.Alarms.ShowNow(row.Id);
    }

    private void PauseResume_Click(object sender, RoutedEventArgs e)
    {
        var sel = SelectedRows();
        bool resume = sel.Count > 0 && sel.All(r => r.Latest?.Paused == true);
        foreach (var row in sel)
        {
            if (resume) AppHost.Store.Resume(row.Id);
            else AppHost.Store.Pause(row.Id);
        }
    }

    private void RestartCountdown_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in SelectedRows())
            if (AppHost.Store.Get(row.Id) is { Def.Kind: ScheduleKind.Countdown } t)
                AppHost.Store.Save(t.Id, t.Def, restartCountdown: true);
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        var sel = SelectedRows();
        if (sel.Count == 0) return;
        string what = sel.Count == 1 ? $"\"{sel[0].Description}\"" : $"these {sel.Count} tasks";
        if (MessageBox.Show(this, $"Delete {what}?", "Delete", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;
        foreach (var row in sel)
        {
            if (_editors.TryGetValue(row.Id, out var ed)) ed.Close();
            AppHost.Store.Delete(row.Id);
        }
    }

    private void Settings_Click(object sender, RoutedEventArgs e) => OpenSettings();

    private void Devices_Click(object sender, RoutedEventArgs e) => OpenDevices();

    private void Calendar_Click(object sender, RoutedEventArgs e) => OpenCalendar();

    private void OpenCalendar()
    {
        ShowFromTray();
        if (_calendar is not null) { _calendar.Activate(); return; }
        _calendar = new CalendarWindow(this) { Owner = this };
        _calendar.Closed += (_, _) => _calendar = null;
        _calendar.Show();
    }

    private void OpenDevices()
    {
        ShowFromTray();
        if (_devices is not null) { _devices.Activate(); return; }
        _devices = new DevicesWindow { Owner = this };
        _devices.Closed += (_, _) => _devices = null;
        _devices.Show();
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => ExitApp();

    /// <summary>Opens the editor for <paramref name="id"/> (null: a new task), or brings an
    /// already-open editor for it forward.</summary>
    public void OpenEditor(Guid? id)
    {
        ShowFromTray();
        if (id is { } existingId && _editors.TryGetValue(existingId, out var open)) { open.Activate(); return; }
        var task = id is { } i ? AppHost.Store.Get(i) : null;
        var ed = new TaskEditorWindow(task) { Owner = this };
        if (task is not null)
        {
            _editors[task.Id] = ed;
            ed.Closed += (_, _) => _editors.Remove(task.Id);
        }
        ed.Show();
    }

    private void OpenSettings()
    {
        ShowFromTray();
        if (_settings is not null) { _settings.Activate(); return; }
        _settings = new SettingsWindow { Owner = this };
        _settings.Closed += (_, _) => _settings = null;
        _settings.Show();
    }

    // ---- Closing, the tray, and exit ---------------------------------------------

    protected override void OnClosing(CancelEventArgs e)
    {
        SavePlacement();
        if (!_exiting && AppHost.Settings.CloseToTray)
        {
            e.Cancel = true;
            Hide();   // open editors stay open: they hold unsaved work
            if (!_trayHintShown)
            {
                _trayHintShown = true;
                _tray.ShowBalloonTip(4000, "RemindMe is still running",
                    "Alarms will still ring. Use the tray icon to open it again, or Exit to quit.", Forms.ToolTipIcon.Info);
            }
            return;
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (!_exiting) ExitApp();
    }

    public void ExitApp()
    {
        if (_exiting) return;
        _exiting = true;
        AppHost.BeginExit();
        SavePlacement();
        _tray.Visible = false;
        _tray.Dispose();
        AppHost.Alarms.Evaluated -= OnEvaluated;
        Application.Current.Shutdown();
    }

    private void RestorePlacement()
    {
        var s = AppHost.Settings;
        if (s.WindowWidth is not { } w || s.WindowHeight is not { } h || s.WindowLeft is not { } l || s.WindowTop is not { } t) return;
        // Only restore a position that is still on a screen (a monitor may have gone).
        var area = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        if (!area.IntersectsWith(new Rect(l, t, w, h))) return;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = l; Top = t; Width = w; Height = h;
    }

    private void SavePlacement()
    {
        if (WindowState != WindowState.Normal || !IsLoaded) return;
        var s = AppHost.Settings;
        (s.WindowLeft, s.WindowTop, s.WindowWidth, s.WindowHeight) = (Left, Top, Width, Height);
        AppHost.SaveSettings();
    }
}
