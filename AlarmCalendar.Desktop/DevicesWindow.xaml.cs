using System.Windows;
using AlarmCalendar.Sync;

namespace AlarmCalendar.Desktop;

/// <summary>Sync (R17, D3): the devices in reach and in the group, linking, and the
/// sync settings. The device list is live; the settings take effect on Apply.</summary>
public partial class DevicesWindow : Window
{
    public sealed record DeviceRow(string NodeId, string Name, string Status, string Address, bool CanLink);

    public DevicesWindow()
    {
        InitializeComponent();
        var s = AppHost.Settings;
        SyncEnabled.IsChecked = s.SyncEnabled;
        DeviceName.Text = s.DeviceName;
        RemoteAddresses.Text = string.Join(Environment.NewLine, s.RemoteAddresses);
        AcceptExternal.IsChecked = s.AcceptExternalConnections;
        Port.Text = s.Port.ToString();
        AppHost.SyncChanged += Refresh;
        AppHost.PairProgressed += OnPairProgress;
        Closed += (_, _) => { AppHost.SyncChanged -= Refresh; AppHost.PairProgressed -= OnPairProgress; };
        // Peers expire quietly when they stop announcing; refresh for "last seen" too.
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        timer.Tick += (_, _) => Refresh();
        timer.Start();
        Closed += (_, _) => timer.Stop();
        Refresh();
    }

    private void Refresh()
    {
        StatusText.Text = AppHost.SyncStatus;
        var now = AppHost.Clock.UtcNow;
        var live = AppHost.Sync?.Peers() ?? [];
        var rows = live.Select(p => new DeviceRow(p.NodeId, p.Name,
            p.Linked ? (p.Connected ? "Linked, in sync" : "Linked, connecting…") : "Not linked",
            p.Address ?? "", !p.Linked)).ToList();
        foreach (var k in AppHost.Settings.KnownPeers.Where(k => rows.All(r => r.NodeId != k.NodeId)))
            rows.Add(new DeviceRow(k.NodeId, k.Name,
                $"Linked, last in sync {TextFormat.Span(TimeMath.ToWall(k.LastSeenUtc, AppHost.Clock.Zone), TimeMath.ToWall(now, AppHost.Clock.Zone), 2)} ago",
                "", false));
        var selected = (DeviceList.SelectedItem as DeviceRow)?.NodeId;
        DeviceList.ItemsSource = rows;
        DeviceList.SelectedItem = rows.FirstOrDefault(r => r.NodeId == selected);
        LinkButton.IsEnabled = AppHost.Sync is not null && DeviceList.SelectedItem is DeviceRow { CanLink: true };
    }

    private void DeviceList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) =>
        LinkButton.IsEnabled = AppHost.Sync is not null && DeviceList.SelectedItem is DeviceRow { CanLink: true };

    private void Link_Click(object sender, RoutedEventArgs e)
    {
        if (DeviceList.SelectedItem is not DeviceRow { CanLink: true } row || AppHost.Sync is not { } sync) return;
        PairText.Text = $"Asking {row.Name}…";
        _ = sync.RequestPairingAsync(row.NodeId);
    }

    private void OnPairProgress(PairProgress p) =>
        PairText.Text = p.Stage switch
        {
            PairStage.CodeShown => $"On {p.PeerName}, check that it shows {p.Code![..3]} {p.Code[3..]} and accept.",
            PairStage.Accepted => $"Linked with {p.PeerName}.",
            PairStage.Rejected => $"{p.PeerName} said no.",
            _ => p.Message ?? "Linking failed.",
        };

    private void Leave_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this,
                "This device will stop syncing with your other devices. Its tasks stay here, and the other devices keep theirs.\n\nLeave the sync group?",
                "Leave the sync group", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;
        AppHost.LeaveSyncGroup();
        Refresh();
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (!Fields.TryInt(Port.Text, 1, 65535, out int port)) { ErrorText.Text = "The port must be a number from 1 to 65535."; return; }
        var addresses = RemoteAddresses.Text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        var bad = addresses.Where(a => !Protocol.TryParseAddress(a, out _, out _)).ToList();
        if (bad.Count > 0) { ErrorText.Text = $"Not a host or host:port: {string.Join(", ", bad)}"; return; }
        ErrorText.Text = "";
        bool enabled = SyncEnabled.IsChecked == true, external = AcceptExternal.IsChecked == true;
        var name = DeviceName.Text.Trim().Length > 0 ? DeviceName.Text.Trim() : Environment.MachineName;
        AppHost.UpdateSettings(s =>
        {
            s.SyncEnabled = enabled;
            s.DeviceName = name;
            s.RemoteAddresses = addresses;
            s.AcceptExternalConnections = external;
            s.Port = port;
        });
        Refresh();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
