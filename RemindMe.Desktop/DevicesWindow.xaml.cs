using System.Collections.ObjectModel;
using System.Windows;
using RemindMe.Sync;

namespace RemindMe.Desktop;

/// <summary>Sync (R17, D3): the devices in reach and in the group, linking, and the
/// sync settings. The device list is live; the settings take effect on Apply.</summary>
public partial class DevicesWindow : Window
{
    /// <summary>One device in the list, kept while the device is listed and updated in
    /// place by <see cref="Refresh"/>.</summary>
    public sealed class DeviceRow(string nodeId) : Observable
    {
        private string _name = "", _status = "", _address = "", _spokenStatus = "";
        private bool _canLink;

        public string NodeId { get; } = nodeId;
        public string Name { get => _name; set => Set(ref _name, value); }
        public string Status { get => _status; set => Set(ref _status, value); }
        public string Address { get => _address; set => Set(ref _address, value); }
        public bool CanLink { get => _canLink; set => Set(ref _canLink, value); }
        /// <summary>What a screen reader says after the name: the status, and the address
        /// if there is one, so two devices of one name differ.</summary>
        public string SpokenStatus { get => _spokenStatus; set => Set(ref _spokenStatus, value); }

        /// <summary>The device's name: what a screen reader says for its row, and the text
        /// typing in the list finds it by.</summary>
        public override string ToString() => Name;
    }

    private readonly ObservableCollection<DeviceRow> _rows = [];

    public DevicesWindow()
    {
        InitializeComponent();
        ScreenFit.Apply(this);
        var s = AppHost.Settings;
        SyncEnabled.IsChecked = s.SyncEnabled;
        DeviceName.Text = s.DeviceName;
        RemoteAddresses.Text = string.Join(Environment.NewLine, s.RemoteAddresses);
        AcceptExternal.IsChecked = s.AcceptExternalConnections;
        Port.Text = s.Port.ToString();
        DeviceList.ItemsSource = _rows;
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

    private bool _pairing;

    /// <summary>Shows the devices in reach, then the linked ones out of reach. Rows are
    /// updated in place, so the selection and the keyboard focus stay on their device.</summary>
    private void Refresh()
    {
        var remotes = AppHost.Sync?.RemoteStates() ?? [];
        StatusText.Text = AppHost.SyncStatus + string.Concat(remotes.Select(r => $"\n{r.Address}: {r.State}"));
        var now = AppHost.Clock.UtcNow;
        var live = AppHost.Sync?.Peers() ?? [];
        var shown = live.Select(p => (p.NodeId, p.Name, Status: RemindMe.PairText.DeviceStatus(p), Address: p.Address ?? "", CanLink: !p.Linked)).ToList();
        foreach (var k in AppHost.Settings.KnownPeers.Where(k => shown.All(r => r.NodeId != k.NodeId)))
            shown.Add((k.NodeId, k.Name, RemindMe.PairText.LastInSync(k, now, AppHost.Clock.Zone), "", false));

        for (int i = 0; i < shown.Count; i++)
        {
            var d = shown[i];
            var row = _rows.FirstOrDefault(r => r.NodeId == d.NodeId);
            if (row is null) _rows.Insert(i, row = new DeviceRow(d.NodeId));
            else if (_rows.IndexOf(row) is var at && at != i) _rows.Move(at, i);   // Move keeps the selection
            row.Name = d.Name;
            row.Status = d.Status;
            row.Address = d.Address;
            row.CanLink = d.CanLink;
            row.SpokenStatus = d.Address.Length > 0 ? $"{d.Status}, {d.Address}" : d.Status;
        }
        // The first shown.Count rows are the devices shown, in order; any after went away.
        while (_rows.Count > shown.Count) _rows.RemoveAt(_rows.Count - 1);
        LinkButton.IsEnabled = CanLink();
    }

    private void DeviceList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) =>
        LinkButton.IsEnabled = CanLink();

    private void Link_Click(object sender, RoutedEventArgs e)
    {
        if (!CanLink() || DeviceList.SelectedItem is not DeviceRow row || AppHost.Sync is not { } sync) return;
        _pairing = true;
        LinkButton.IsEnabled = false;
        PairStatus.Text = $"Asking {row.Name}…";
        _ = sync.RequestPairingAsync(row.NodeId);
    }

    private void OnPairProgress(PairProgress p)
    {
        PairStatus.Text = RemindMe.PairText.Progress(p);
        if (p.Stage != PairStage.CodeShown) _pairing = false;
        LinkButton.IsEnabled = CanLink();
    }

    private bool CanLink() => !_pairing && AppHost.Sync is not null && DeviceList.SelectedItem is DeviceRow { CanLink: true };

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
