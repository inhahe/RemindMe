using Android.App;
using Android.OS;
using Android.Views;
using Android.Widget;
using AndroidX.AppCompat.App;
using RemindMe.Sync;
using AlertDialog = AndroidX.AppCompat.App.AlertDialog;

namespace RemindMe.Droid;

/// <summary>Sync (R17, D3): devices in reach and in the group, linking, and the sync
/// settings, as the desktop's Devices window. It also answers pairing requests.</summary>
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
        Find<EditText>(Resource.Id.device_name).Text = s.DeviceName;
        Find<EditText>(Resource.Id.remotes).Text = string.Join("\n", s.RemoteAddresses);
        Find<CompoundButton>(Resource.Id.accept_external).Checked = s.AcceptExternalConnections;
        Find<EditText>(Resource.Id.port).Text = s.Port.ToString();
        Find<Button>(Resource.Id.apply).Click += (_, _) => Apply();
        Find<Button>(Resource.Id.leave).Click += (_, _) => Ui.Confirm(this, "Leave the sync group",
            "This device will stop syncing with your other devices. Its tasks stay here, and the other devices keep theirs.",
            "Leave", () => { AppState.LeaveSyncGroup(); Refresh(); });
    }

    protected override void OnResume()
    {
        base.OnResume();
        AppState.SyncChanged += Refresh;
        AppState.PairProgressed += OnPairProgress;
        AppState.PairRequested += Ask;
        AppState.PromptEnded += OnPromptEnded;
        if (AppState.PendingPrompt is { } p) Ask(p);
        Tick();
    }

    protected override void OnPause()
    {
        AppState.SyncChanged -= Refresh;
        AppState.PairProgressed -= OnPairProgress;
        AppState.PairRequested -= Ask;
        AppState.PromptEnded -= OnPromptEnded;
        _handler.RemoveCallbacksAndMessages(null);
        // The dialog belongs to this window; the question stays until answered, standing as
        // a notification meanwhile (but not across a rotation, which asks again at once).
        bool asking = _asking is not null;
        CloseDialog();
        if (asking && !IsChangingConfigurations) AppState.PromptHidden();
        base.OnPause();
    }

    private AlertDialog? _dialog;
    /// <summary>The question <see cref="_dialog"/> asks.</summary>
    private PairPrompt? _asking;

    private void CloseDialog()
    {
        if (_dialog is { IsShowing: true } d && !IsDestroyed) d.Dismiss();
        _dialog = null;
        if (_asking is not null) AppState.PromptOnScreen = false;
        _asking = null;
    }

    private void OnPromptEnded(PairPrompt p)
    {
        if (_asking == p) CloseDialog();
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
            AddRow(list, k.Name, PairText.LastInSync(k, now, AppState.Clock.Zone), null);
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

    /// <summary>Asks whether to link (D3); on either device, both users must accept.</summary>
    private void Ask(PairPrompt p)
    {
        if (_asking == p) return;
        CloseDialog();
        _asking = p;
        AppState.PromptOnScreen = true;
        Notifications.Cancel(this, Notifications.PairId);
        // Withdrawn (the other device said no or went away): AppState raises PromptEnded.
        _dialog = new AlertDialog.Builder(this)
            .SetTitle("Link a device?")!
            .SetMessage($"{PairText.Question(p)}\n\n"
                + $"Both devices should be showing this code:\n\n{p.Code[..3]} {p.Code[3..]}\n\n"
                + "Accept only if the codes match.")!
            .SetPositiveButton("Link", (_, _) => AppState.AnswerPrompt(p, yes: true))!
            .SetNegativeButton("Don't link", (_, _) => AppState.AnswerPrompt(p, yes: false))!
            .SetCancelable(false)!
            .Show()!;
    }

    private void Apply()
    {
        var error = Find<TextView>(Resource.Id.error);
        if (!FieldParsing.TryInt(Find<EditText>(Resource.Id.port).Text ?? "", 1, 65535, out int port))
        {
            error.Text = "The port must be a number from 1 to 65535.";
            return;
        }
        var addresses = (Find<EditText>(Resource.Id.remotes).Text ?? "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        var bad = addresses.Where(a => !Protocol.TryParseAddress(a, out _, out _)).ToList();
        if (bad.Count > 0) { error.Text = $"Not a host or host:port: {string.Join(", ", bad)}"; return; }
        error.Text = "";
        bool enabled = Find<CompoundButton>(Resource.Id.sync_enabled).Checked;
        bool external = Find<CompoundButton>(Resource.Id.accept_external).Checked;
        var nameText = Find<EditText>(Resource.Id.device_name).Text?.Trim() ?? "";
        AppState.UpdateSettings(s =>
        {
            s.SyncEnabled = enabled;
            if (nameText.Length > 0) s.DeviceName = nameText;
            s.RemoteAddresses = addresses;
            s.AcceptExternalConnections = external;
            s.Port = port;
        });
        Ui.Toast(this, "Saved");
        Refresh();
    }

    public override bool OnSupportNavigateUp()
    {
        Finish();
        return true;
    }
}
