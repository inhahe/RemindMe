using System.Net;
using System.Net.Sockets;

namespace RemindMe.Sync;

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

    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(3);

    /// <summary>An announcement worth acting on: ours, with a valid node ID and port.</summary>
    public static bool IsValid(SyncMessage? m) =>
        m is { Type: MessageTypes.Announce, App: Protocol.App, Port: >= 1 and <= 65535 } && Protocol.IsValidNodeId(m.Node);

    public void Start()
    {
        _multicastLock = _acquireMulticastLock?.Invoke();
        _udp = new UdpClient(AddressFamily.InterNetwork);
        _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
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

    /// <summary>The subnet broadcast address of each IPv4 network this machine is on (not
    /// loopback), then the limited broadcast.</summary>
    private List<IPEndPoint> BroadcastEndpoints()
    {
        var result = new List<IPEndPoint>();
        foreach (var (address, prefix) in Protocol.LocalSubnets())
        {
            if (address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address))
                result.Add(new IPEndPoint(BroadcastOf(address, prefix), _port));
        }
        result.Add(new IPEndPoint(IPAddress.Broadcast, _port));
        return result;
    }

    /// <summary>The broadcast address of the IPv4 network of <paramref name="address"/> with
    /// that prefix length: its host bits all set.</summary>
    internal static IPAddress BroadcastOf(IPAddress address, int prefix)
    {
        // A shift by 32 is a shift by 0 in C#, hence the first case.
        uint host = prefix >= 32 ? 0 : uint.MaxValue >> Math.Max(prefix, 0);
        var b = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(b,
            System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(address.GetAddressBytes()) | host);
        return new IPAddress(b);
    }

    private async Task ListenLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var r = await _udp!.ReceiveAsync(ct).ConfigureAwait(false);
                var msg = Protocol.Deserialize(r.Buffer);
                if (IsValid(msg) && msg!.Node != _nodeId) Announced?.Invoke(msg, r.RemoteEndPoint.Address);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException)
            {
                // A WiFi blip or sleep/wake: retry rather than ending discovery for good.
                try { await Task.Delay(1000, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
            catch (Exception e)
            {
                // Anyone on the LAN can send anything here; no single packet may end the loop.
                _log?.Invoke($"Ignored a bad discovery packet: {e.Message}");
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
