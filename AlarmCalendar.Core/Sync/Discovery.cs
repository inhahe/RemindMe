using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace AlarmCalendar.Sync;

/// <summary>
/// UDP broadcast discovery, after LanLink's: every few seconds each node announces itself
/// on the subnet broadcast address of every active IPv4 interface (plus the limited
/// broadcast), and listens for the others' announcements. Several instances can share
/// the port (ReuseAddress), so discovery works between instances on one machine too.
/// </summary>
internal sealed class Discovery : IDisposable
{
    private readonly Func<SyncMessage> _announcement;
    private readonly string _nodeId;
    private readonly int _port;
    private readonly Func<IDisposable?>? _acquireMulticastLock;
    private readonly Action<string>? _log;
    private readonly CancellationTokenSource _cts = new();
    private IDisposable? _multicastLock;
    private UdpClient? _udp;

    /// <summary>(announcement, sender address)</summary>
    public event Action<SyncMessage, IPAddress>? Announced;

    /// <param name="announcement">Builds the current announcement (the name or group may change).</param>
    /// <param name="acquireMulticastLock">Android: holds a WiFi MulticastLock, without which
    /// broadcasts are filtered out before they reach the app.</param>
    public Discovery(string nodeId, int port, Func<SyncMessage> announcement, Func<IDisposable?>? acquireMulticastLock, Action<string>? log)
    {
        _nodeId = nodeId;
        _port = port;
        _announcement = announcement;
        _acquireMulticastLock = acquireMulticastLock;
        _log = log;
    }

    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(3);

    public void Start()
    {
        _multicastLock = _acquireMulticastLock?.Invoke();
        _udp = new UdpClient(AddressFamily.InterNetwork);
        _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, true);
        _udp.Client.Bind(new IPEndPoint(IPAddress.Any, _port));
        _udp.EnableBroadcast = true;
        _ = BroadcastLoopAsync(_cts.Token);
        _ = ListenLoopAsync(_cts.Token);
    }

    /// <summary>Announces now rather than at the next tick (after a change of name or group).</summary>
    public void AnnounceNow() => _ = SendAnnouncementAsync(_cts.Token);

    private async Task BroadcastLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await SendAnnouncementAsync(ct).ConfigureAwait(false);
            try { await Task.Delay(Interval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task SendAnnouncementAsync(CancellationToken ct)
    {
        try
        {
            var bytes = Protocol.Serialize(_announcement());
            foreach (var dest in BroadcastEndpoints())
            {
                try { await _udp!.SendAsync(bytes, dest, ct).ConfigureAwait(false); }
                catch (SocketException) { }
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception e) { _log?.Invoke($"Discovery announce failed: {e.Message}"); }
    }

    private List<IPEndPoint> BroadcastEndpoints()
    {
        var result = new List<IPEndPoint>();
        try
        {
            foreach (var iface in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (iface.OperationalStatus != OperationalStatus.Up || iface.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;
                foreach (var uni in iface.GetIPProperties().UnicastAddresses)
                {
                    if (uni.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    byte[] ip = uni.Address.GetAddressBytes(), mask = uni.IPv4Mask.GetAddressBytes(), b = new byte[4];
                    for (int i = 0; i < 4; i++) b[i] = (byte)(ip[i] | ~mask[i]);
                    result.Add(new IPEndPoint(new IPAddress(b), _port));
                }
            }
        }
        catch (NetworkInformationException) { }
        result.Add(new IPEndPoint(IPAddress.Broadcast, _port));
        return result;
    }

    private async Task ListenLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var r = await _udp!.ReceiveAsync(ct).ConfigureAwait(false);
                var msg = Protocol.Deserialize(r.Buffer);
                if (msg is { Type: MessageTypes.Announce, App: Protocol.App, Node: not null, Port: not null } && msg.Node != _nodeId)
                    Announced?.Invoke(msg, r.RemoteEndPoint.Address);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException)
            {
                // A WiFi blip or sleep/wake: retry rather than ending discovery for good.
                try { await Task.Delay(1000, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _udp?.Close();
        try { _multicastLock?.Dispose(); } catch (Exception) { }
        _multicastLock = null;
    }
}
