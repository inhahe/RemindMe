using Android.App;
using Android.OS;
using Android.Views;
using Android.Widget;
using RemindMe.Sync;

namespace RemindMe.Droid;

/// <summary>Sync (R17, D3): devices in reach and in the group, linking, and the sync
/// settings, as the desktop's Devices window. (Every screen answers requests to link: Screen.)</summary>
[Activity(Label = "Devices and sync", Exported = false, LaunchMode = Android.Content.PM.LaunchMode.SingleTop)]
public sealed class DevicesActivity : Screen
{
    private readonly Handler _handler = new(Looper.MainLooper!);

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        SetContentView(Resource.Layout.activity_devices);
        Ui.SetupToolbar(this, "Devices and sync", back: true);
        var s = AppState.Settings;
        Find<CompoundButton>(Resource.Id.sync_enabled).Checked = s.SyncEnabled;
        ShowName();
        Find<EditText>(Resource.Id.remotes).Text = string.Join("\n", s.RemoteAddresses);
        Find<CompoundButton>(Resource.Id.accept_external).Checked = s.AcceptExternalConnections;
        Find<EditText>(Resource.Id.port).Text = s.Port.ToString();
        Find<Button>(Resource.Id.apply).Click += (_, _) => Apply();
        Find<Button>(Resource.Id.leave).Click += (_, _) => Ui.Confirm(this, "Leave the sync group",
            "This device will stop syncing with your other devices. Its tasks stay here, and the other devices keep theirs.",
            "Leave", () => { AppState.LeaveSyncGroup(); Refresh(); });
    }

    /// <summary>The name the box was filled with: Apply changes the name only if it was
    /// edited, so a box left alone can't pin the name in use, which may follow the phone's.</summary>
    private string _shownName = "";

    private void ShowName() =>
        Find<EditText>(Resource.Id.device_name).Text = _shownName = AppState.Settings.NameOr(AppState.OwnName());

    protected override void OnResume()
    {
        base.OnResume();
        // Back from renaming the phone, say: an unedited box shows the name in use now.
        if ((Find<EditText>(Resource.Id.device_name).Text ?? "") == _shownName) ShowName();
        AppState.SyncChanged += Refresh;
        AppState.PairProgressed += OnPairProgress;
        Tick();
    }

    protected override void OnPause()
    {
        AppState.SyncChanged -= Refresh;
        AppState.PairProgressed -= OnPairProgress;
        _handler.RemoveCallbacksAndMessages(null);
        base.OnPause();
    }

    private void Tick()
    {
        Refresh();
        _handler.PostDelayed(Tick, 3000);
    }

    private void Refresh()
    {
        var remotes = AppState.Sync?.RemoteStates() ?? [];
        Find<TextView>(Resource.Id.status).Text = AppState.SyncStatus + string.Concat(remotes.Select(r => $"\n{r.Address}: {r.State}"));
        var list = Find<LinearLayout>(Resource.Id.device_list);
        list.RemoveAllViews();
        var now = AppState.Clock.UtcNow;
        var live = AppState.Sync?.Peers() ?? [];
        foreach (var p in live)
            AddRow(list, p.Name, PairText.DeviceStatus(p), p.Linked ? null : p.NodeId);
        foreach (var k in AppState.Settings.KnownPeers.Where(k => live.All(p => p.NodeId != k.NodeId)))
            AddRow(list, k.Name, PairText.LastInSync(k, now, AppState.Clock.Zone) + (k.Address is { } at ? $" · {at}" : ""), null);
        if (list.ChildCount == 0)
            list.AddView(new TextView(this) { Text = AppState.Sync is null ? "Sync is off." : "No other devices found yet." });
    }

    private void AddRow(LinearLayout list, string name, string status, string? linkNode)
    {
        var row = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        row.SetPadding(0, 8, 0, 8);
        var text = new TextView(this) { Text = $"{name}\n{status}" };
        row.AddView(text, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1));
        if (linkNode is not null && AppState.Sync is { } sync)
        {
            var b = new Button(this) { Text = "Link" };
            b.Click += (_, _) =>
            {
                Find<TextView>(Resource.Id.pair_text).Text = $"Asking {name}…";
                _ = sync.RequestPairingAsync(linkNode);
            };
            row.AddView(b);
        }
        list.AddView(row);
    }

    private void OnPairProgress(PairProgress p) => Find<TextView>(Resource.Id.pair_text).Text = PairText.Progress(p);

    private void Apply()
    {
        var error = Find<TextView>(Resource.Id.error);
        if (!FieldParsing.TryInt(Find<EditText>(Resource.Id.port).Text ?? "", 1, 65535, out int port))
        {
            error.Text = "The port must be a number from 1 to 65535.";
            return;
        }
        var addresses = AppSettings.CleanAddresses((Find<EditText>(Resource.Id.remotes).Text ?? "").Split('\n'));
        var bad = addresses.Where(a => !Protocol.TryParseAddress(a, out _, out _)).ToList();
        if (bad.Count > 0) { error.Text = $"Not a host or host:port: {string.Join(", ", bad)}"; return; }
        error.Text = "";
        bool enabled = Find<CompoundButton>(Resource.Id.sync_enabled).Checked;
        bool external = Find<CompoundButton>(Resource.Id.accept_external).Checked;
        var nameText = Find<EditText>(Resource.Id.device_name).Text ?? "";
        bool renamed = nameText != _shownName;
        var ownName = AppState.OwnName();
        AppState.UpdateSettings(s =>
        {
            s.SyncEnabled = enabled;
            if (renamed) s.SetDeviceName(nameText, ownName);
            s.RemoteAddresses = addresses;
            s.AcceptExternalConnections = external;
            s.Port = port;
        });
        if (renamed) ShowName();
        Ui.Toast(this, "Saved");
        Refresh();
    }

    public override bool OnSupportNavigateUp()
    {
        Finish();
        return true;
    }
}
