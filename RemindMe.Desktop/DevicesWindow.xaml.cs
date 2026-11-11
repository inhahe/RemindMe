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
        private string _name = "", _status = "", _address = "";

        public string NodeId { get; } = nodeId;
        public string Name => _name;
        public string Status => _status;
        public string Address => _address;
        /// <summary>Whether Link can ask it to join this device's group (it isn't linked).</summary>
        public bool CanLink { get; private set; }
        /// <summary>What a screen reader says after the name: the status, and the address
        /// if there is one, so two devices of one name differ.</summary>
        public string SpokenStatus => Address.Length > 0 ? $"{Status}, {Address}" : Status;

        /// <summary>Shows the device as it is now. <see cref="SpokenStatus"/> changes once,
        /// whatever parts of it change.</summary>
        public void Show(string name, string status, string address, bool canLink)
        {
            Set(ref _name, name, nameof(Name));
            CanLink = canLink;
            bool spoken = Set(ref _status, status, nameof(Status)) | Set(ref _address, address, nameof(Address));
            if (spoken) Changed(nameof(SpokenStatus));
        }

        /// <summary>The device's name: the text typing in the list finds its row by.</summary>
        public override string ToString() => Name;
    }

    private readonly ObservableCollection<DeviceRow> _rows = [];

    public DevicesWindow()
    {
        InitializeComponent();
        ScreenFit.Apply(this);
        var s = AppHost.Settings;
        SyncEnabled.IsChecked = s.SyncEnabled;
        DeviceName.Text = _shownName = s.NameOr(AppHost.OwnName());
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
    /// <summary>The name the box was filled with: Apply changes the name only if it was
    /// edited, so a box left alone can't pin the name in use, which may follow the computer's.</summary>
    private string _shownName = "";

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
            shown.Add((k.NodeId, k.Name, RemindMe.PairText.LastInSync(k, now, AppHost.Clock.Zone), k.Address ?? "", false));

        Rows.Update(DeviceList, _rows, shown, d => d.NodeId, r => r.NodeId, d => new DeviceRow(d.NodeId),
            (row, d) => row.Show(d.Name, d.Status, d.Address, d.CanLink));
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
        var addresses = AppSettings.CleanAddresses(RemoteAddresses.Text.Split('\n'));
        var bad = addresses.Where(a => !Protocol.TryParseAddress(a, out _, out _)).ToList();
        if (bad.Count > 0) { ErrorText.Text = $"Not a host or host:port: {string.Join(", ", bad)}"; return; }
        ErrorText.Text = "";
        bool enabled = SyncEnabled.IsChecked == true, external = AcceptExternal.IsChecked == true;
        var name = DeviceName.Text;
        bool renamed = name != _shownName;
        AppHost.UpdateSettings(s =>
        {
            s.SyncEnabled = enabled;
            if (renamed) s.SetDeviceName(name, AppHost.OwnName());
            s.RemoteAddresses = addresses;
            s.AcceptExternalConnections = external;
            s.Port = port;
        });
        if (renamed) DeviceName.Text = _shownName = AppHost.Settings.NameOr(AppHost.OwnName());
        Refresh();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
