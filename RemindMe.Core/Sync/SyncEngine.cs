using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;

namespace RemindMe.Sync;

/// <summary>Who this node is and which group it syncs with.</summary>
public sealed record SyncIdentity(string NodeId, string Name, string GroupId, string GroupKeyBase64)
{
    internal byte[] GroupKey => Convert.FromBase64String(GroupKeyBase64);
}

public sealed record SyncOptions
{
    public int Port { get; init; } = Protocol.DefaultPort;
    /// <summary>Address the TCP listener binds; tests use loopback. <see cref="IPAddress.Any"/>
    /// listens on IPv4, and on IPv6 too when <see cref="AcceptExternalConnections"/> is set
    /// (<see cref="SyncEngine.ListensOnIpv6"/>).</summary>
    public IPAddress BindAddress { get; init; } = IPAddress.Any;
    public bool EnableDiscovery { get; init; } = true;
    public int DiscoveryPort { get; init; } = Protocol.DiscoveryPort;
    public bool AcceptExternalConnections { get; init; }
    public IReadOnlyList<string> RemoteAddresses { get; init; } = [];
    /// <summary>Android's WiFi MulticastLock; null elsewhere.</summary>
    public Func<IDisposable?>? AcquireMulticastLock { get; init; }
    /// <summary>The first wait before redialling a remote address, or a missing linked device
    /// at its last address; it doubles up to a minute.</summary>
    public TimeSpan ReconnectDelay { get; init; } = TimeSpan.FromSeconds(5);
    /// <summary>
    /// The devices this one is linked with: the app's remembered peers, each with the LAN
    /// address it was last in sync from. Read on the engine's threads, so the list must not
    /// change in place. They are what this device counts as linked when two devices pair (the
    /// one with fewer joins the other's group); an inbound connection claiming one's ID without
    /// the group never speaks for it here, and may only ask to pair; and one that is neither
    /// connected nor heard announcing is dialled at its address (SyncEngine.DialKnownPeers).
    /// </summary>
    public Func<IReadOnlyList<KnownPeer>>? KnownPeers { get; init; }

    /// <summary>An inbound connection from outside the group, with no pairing under way, is
    /// closed after sitting idle this long.</summary>
    internal TimeSpan UnlinkedIdleTimeout { get; init; } = TimeSpan.FromMinutes(3);
    /// <summary>A pairing left unanswered this long is abandoned.</summary>
    internal TimeSpan PairingTimeout { get; init; } = TimeSpan.FromMinutes(5);
    /// <summary>How often those two timeouts and the nearby list's expiry are checked, and
    /// missing linked devices redialled.</summary>
    internal TimeSpan HousekeepingInterval { get; init; } = TimeSpan.FromSeconds(5);
    /// <summary>A linked device that announces, or is missing, but never connects is dialled
    /// by the higher ID too after this long: the lower one's dials may be blocked by a
    /// firewall, or go to an address the device no longer has.</summary>
    internal TimeSpan HigherIdFallback { get; init; } = TimeSpan.FromSeconds(10);
}

/// <summary>A device this node knows of, for the Devices screen.</summary>
/// <param name="Linked">In this node's sync group.</param>
/// <param name="Connected">A session to it is open (and, when linked, authenticated).</param>
/// <param name="Address">Where it was last seen.</param>
public sealed record PeerInfo(string NodeId, string Name, bool Linked, bool Connected, string? Address);

/// <summary>
/// A pairing waiting for this device's user, on either side: both users compare the codes
/// and both must accept before anything is linked.
/// </summary>
public sealed class PairPrompt
{
    internal Func<bool, Task> Answer { get; init; } = _ => Task.CompletedTask;
    public required string PeerNode { get; init; }
    public required string PeerName { get; init; }
    /// <summary>Must match the code the other device shows.</summary>
    public required string Code { get; init; }
    /// <summary>This device clicked Link; otherwise the other one did.</summary>
    public bool IsRequester { get; init; }
    /// <summary>This device joins the other's group (rather than the other way round),
    /// leaving <see cref="MyLinkedDevices"/> devices behind.</summary>
    public bool ThisDeviceJoins { get; init; }
    public int MyLinkedDevices { get; init; }
    /// <summary>Raised when the pairing ends before this prompt is answered (the other
    /// device said no or went away); the screen showing it should close it.</summary>
    public event Action? Withdrawn;
    /// <summary>The pairing already ended unanswered: a screen subscribing to
    /// <see cref="Withdrawn"/> after that moment checks this instead.</summary>
    public bool IsWithdrawn { get; private set; }
    internal void Withdraw()
    {
        IsWithdrawn = true;
        Withdrawn?.Invoke();
    }
    public Task AcceptAsync() => Answer(true);
    public Task RejectAsync() => Answer(false);
}

public enum PairStage { CodeShown, Accepted, Rejected, Failed }

/// <summary>Progress of a pairing this node asked for.</summary>
public sealed record PairProgress(string PeerNode, string PeerName, PairStage Stage, string? Code, string? Message);

/// <summary>
/// Sync between this device and the others in its group (R17, D3): discovery on the LAN,
/// persistent links to remote addresses, pairing, and replication of the task store.
/// See design.md → <i>Sync network</i>. Events are raised on pool threads.
/// </summary>
public sealed class SyncEngine : IDisposable
{
    private static readonly TimeSpan PeerExpiry = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(15);
    /// <summary>Connections that haven't proved the group key, at most.</summary>
    private const int MaxUnprovenSessions = 32;
    /// <summary>Devices remembered from announcements, at most: anyone on the LAN can
    /// announce any number of made-up devices.</summary>
    private const int MaxNearby = 256;
    /// <summary>A member's announcement dated further than this from this device's clock is
    /// ignored (<see cref="AdmitMemberAnnouncement"/>).</summary>
    private static readonly TimeSpan MaxClockDifference = TimeSpan.FromMinutes(10);

    private readonly TaskStore _store;
    private readonly SyncOptions _options;
    private readonly Action<string>? _log;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _lock = new();
    /// <summary>The session that speaks for each peer, by node ID: an authenticated one
    /// whenever there is one.</summary>
    private readonly Dictionary<string, PeerSession> _sessions = new();
    private readonly HashSet<PeerSession> _allSessions = new();
    private readonly ConcurrentDictionary<string, Nearby> _nearby = new();
    private readonly ConcurrentDictionary<string, byte> _dialing = new();
    private readonly ConcurrentDictionary<string, byte> _dialFailLogged = new();
    private readonly ConcurrentDictionary<string, string> _remoteErrors = new();
    /// <summary>Linked devices being redialled at their last address, by node ID: the dial
    /// schedule, dropped when the device is connected or heard announcing.</summary>
    private readonly ConcurrentDictionary<string, Redial> _redial = new();
    /// <summary>The address each linked device was last dialled back at, and when
    /// (<see cref="DialBack"/>): the same one isn't tried again within a minute.</summary>
    private readonly ConcurrentDictionary<string, (string Address, DateTimeOffset At)> _dialedBack = new();
    /// <summary>The networks this device is on (<see cref="Protocol.LocalSubnets"/>), and when
    /// they were listed: listed again after 10 s at most, on the housekeeping thread only.</summary>
    private (DateTimeOffset At, List<(IPAddress Address, int PrefixLength)> Subnets)? _subnets;
    /// <summary>The last announcement accepted from each member (only proven members get
    /// here, so a flood can't grow it).</summary>
    private readonly ConcurrentDictionary<string, LastAnnouncement> _memberLast = new();
    private readonly ConcurrentDictionary<string, byte> _clockLogged = new();
    /// <summary>This process's ID in its announcements, and how many it has sent.</summary>
    private readonly string _boot = Guid.NewGuid().ToString("N");
    private long _announced;

    private sealed record LastAnnouncement(string Boot, long Seq, long At);
    /// <param name="MissingSince">When the device was first found not connected.</param>
    /// <param name="Next">When it may be dialled next.</param>
    /// <param name="Delay">The wait after that dial, doubling up to a minute.</param>
    /// <param name="Stale">The address answered as another device, or as the device without the
    /// group: not dialled again until the device is back.</param>
    private sealed record Redial(DateTimeOffset MissingSince, DateTimeOffset Next, TimeSpan Delay, bool Stale = false);
    private SyncIdentity _id;
    private TcpListener? _listener;
    private Discovery? _discovery;
    /// <summary>The one pairing in progress on each side, if any.</summary>
    private PairRound? _accepting, _requesting;
    private int _peersChangedQueued;
    private int _disposed;

    /// <param name="Member">The announcement carried a valid MAC under the key of the group
    /// it claims, which was this node's group at the time.</param>
    private sealed record Nearby(SyncMessage Msg, IPEndPoint Ep, DateTimeOffset Seen, DateTimeOffset FirstSeen, bool Member);

    /// <summary>Announced by a member of this node's current group.</summary>
    private static bool IsMember(Nearby n, string group) => n.Member && n.Msg.Group == group;

    public event Action? PeersChanged;
    /// <summary>A pairing needs this device's user: on the accepting device when the
    /// request arrives, on the requesting one once the codes are known.</summary>
    public event Action<PairPrompt>? PairRequested;
    public event Action<PairProgress>? PairProgressed;
    /// <summary>This node joined another group by pairing; the app must persist the new identity.</summary>
    public event Action<SyncIdentity>? IdentityChanged;
    /// <summary>(node, name, address): a device of this group was in sync just now (its
    /// session authenticated, or closed after being in sync). The address is its listener's
    /// on the LAN ("ip:port"), for <see cref="SyncOptions.KnownPeers"/>; null when it was
    /// reached from outside the LAN or its port is unknown.</summary>
    public event Action<string, string, string?>? PeerSeen;
    /// <summary>The first complete exchange with a linked device finished (see <see cref="FirstSyncDone"/>).</summary>
    public event Action? FirstSyncCompleted;

    public SyncEngine(TaskStore store, SyncIdentity identity, SyncOptions options, Action<string>? log = null)
    {
        _store = store;
        _id = identity;
        _options = options;
        _log = log;
    }

    public SyncIdentity Identity { get { lock (_lock) return _id; } }

    /// <summary>The port actually listened on (differs from the option when it was 0).</summary>
    public int ListenPort { get; private set; }

    /// <summary>Some linked device has sent everything this one lacked. Until then, an
    /// alarm missed while the app wasn't running may already have been dealt with elsewhere.</summary>
    public bool FirstSyncDone { get; private set; }

    /// <summary>Binds the listener and starts discovery and remote links.</summary>
    /// <exception cref="SocketException">The port is in use.</exception>
    public void Start()
    {
        _listener = ListensOnIpv6(_options) ? StartDualModeListener(_options.Port) : new TcpListener(_options.BindAddress, _options.Port);
        _listener.Start();
        ListenPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = AcceptLoopAsync(_cts.Token);
        _store.Changed += OnStoreChanged;
        if (_options.EnableDiscovery)
        {
            try
            {
                _discovery = new Discovery(_id.NodeId, _options.DiscoveryPort, Announcement, _options.AcquireMulticastLock, _log);
                _discovery.Announced += OnAnnounced;
                _discovery.Start();
            }
            catch (SocketException e)
            {
                _log?.Invoke($"LAN discovery is off: {e.Message}");
                _discovery?.Dispose();
                _discovery = null;
            }
        }
        foreach (var address in _options.RemoteAddresses) _ = RemoteLinkLoopAsync(address, _cts.Token);
        _ = HousekeepingLoopAsync(_cts.Token);
        _log?.Invoke($"Sync listening on port {ListenPort}.");
    }

    // ---- What the UI shows -----------------------------------------------------------

    public IReadOnlyList<PeerInfo> Peers()
    {
        var result = new Dictionary<string, PeerInfo>();
        var group = Identity.GroupId;
        var now = DateTimeOffset.UtcNow;
        foreach (var (node, n) in _nearby)
            if (now - n.Seen <= PeerExpiry)
                result[node] = new PeerInfo(node, Protocol.CleanName(n.Msg.Name, node[..8]), IsMember(n, group), false, n.Ep.Address.ToString());
        lock (_lock)
        {
            foreach (var (node, s) in _sessions)
            {
                bool linked = s.PeerGroup == group;
                result[node] = new PeerInfo(node, s.PeerName ?? node, linked, !linked || s.Authenticated,
                    s.RemoteAddress ?? (s.RemoteEndPoint is { } ep ? Plain(ep.Address).ToString() : null));
            }
        }
        return result.Values.OrderByDescending(p => p.Linked).ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>The state of each configured remote address, for the Devices screen.</summary>
    public IReadOnlyList<(string Address, string State)> RemoteStates()
    {
        var result = new List<(string, string)>();
        var group = Identity.GroupId;
        foreach (var address in _options.RemoteAddresses)
        {
            PeerSession? s;
            lock (_lock) s = _allSessions.FirstOrDefault(x => x.RemoteAddress == address && !x.IsClosed);
            string state = s switch
            {
                null => _remoteErrors.TryGetValue(address, out var e) ? e : "connecting…",
                { PeerNode: null } => "connecting…",
                { Authenticated: true } => $"connected to {s.PeerName}, in sync",
                { PeerGroup: var g } when g == group => $"connecting to {s.PeerName}…",
                _ => $"connected to {s.PeerName}, not linked",
            };
            result.Add((address, state));
        }
        return result;
    }

    /// <summary>Raises <see cref="PeersChanged"/> at most about four times a second, however
    /// fast announcements or connections come and go.</summary>
    private void RaisePeersChanged()
    {
        if (Interlocked.Exchange(ref _peersChangedQueued, 1) != 0) return;
        _ = Task.Delay(250).ContinueWith(_ =>
        {
            Volatile.Write(ref _peersChangedQueued, 0);
            if (_disposed == 0) PeersChanged?.Invoke();
        }, TaskScheduler.Default);
    }

    // ---- Connections -------------------------------------------------------------------

    /// <summary>
    /// Whether the listener takes IPv6 as well as IPv4: only when bound to every address and
    /// accepting connections from outside the LAN. Otherwise IPv4 alone, where private
    /// addresses tell the LAN apart: a global IPv6 address doesn't say whether its owner is
    /// down the hall or a customer of the same ISP. A LAN peer dialling a host name that
    /// resolves to both is then refused on IPv6 at connect time, and falls back to IPv4.
    /// (Accepting the connection and dropping it would leave it no reason to.)
    /// </summary>
    internal static bool ListensOnIpv6(SyncOptions o) => o.BindAddress.Equals(IPAddress.Any) && o.AcceptExternalConnections;

    /// <summary>A listener on IPv4 and IPv6 both, so IPv6 remote addresses work; IPv4 only
    /// where the system has no IPv6.</summary>
    private static TcpListener StartDualModeListener(int port)
    {
        try
        {
            var l = new TcpListener(IPAddress.IPv6Any, port);
            l.Server.DualMode = true;
            return l;
        }
        catch (SocketException) { return new TcpListener(IPAddress.Any, port); }
    }

    /// <summary>An IPv4 address as itself, not in its IPv6-mapped form (which the dual-mode
    /// listener reports).</summary>
    private static IPAddress Plain(IPAddress a) => a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a;

    /// <summary>This node's announcement or hello: the same fields, different type.</summary>
    private SyncMessage Presence(string type)
    {
        var id = Identity;
        var m = new SyncMessage
        {
            Type = type, App = Protocol.App, Proto = Protocol.Version,
            Node = id.NodeId, Name = id.Name, Group = id.GroupId, Port = ListenPort,
        };
        if (type == MessageTypes.Announce)
        {
            m.At = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            m.Boot = _boot;
            m.Seq = Interlocked.Increment(ref _announced);
            m.Mac = SyncCrypto.AnnouncementMac(id.GroupKey, m);
        }
        return m;
    }

    /// <summary>What this node announces on the LAN.</summary>
    internal SyncMessage Announcement() => Presence(MessageTypes.Announce);

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener!.AcceptTcpClientAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { continue; }

            if (!_options.AcceptExternalConnections && client.Client.RemoteEndPoint is IPEndPoint ep && !Protocol.IsPrivateOrLan(ep.Address))
            {
                _log?.Invoke($"Refused a connection from {Plain(ep.Address)}, outside the LAN (Accept connections from outside the LAN is off).");
                client.Dispose();
                continue;
            }
            int unproven;
            lock (_lock) unproven = _allSessions.Count(s => !s.Authenticated);
            if (unproven >= MaxUnprovenSessions)
            {
                client.Dispose();
                continue;
            }
            await BeginSessionAsync(new PeerSession(client, isOutgoing: false)).ConfigureAwait(false);
        }
    }

    /// <summary>Connects to <paramref name="host"/>:<paramref name="port"/>.</summary>
    /// <param name="remoteAddress">The configured remote address dialled, if any.</param>
    /// <param name="expectedNode">The node meant to answer, if known (<see cref="KeepDialledSession"/>).</param>
    /// <param name="redial">Dialled at the address <paramref name="expectedNode"/> was last in sync from.</param>
    /// <param name="probe">Only to prove that <paramref name="expectedNode"/> answers there (<see cref="DialBack"/>).</param>
    /// <returns>The session, its hello sent; null when the connection failed.</returns>
    private async Task<PeerSession?> DialAsync(string host, int port, string? remoteAddress, CancellationToken ct,
        string? expectedNode = null, bool redial = false, bool probe = false)
    {
        var client = new TcpClient();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            await client.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is SocketException or OperationCanceledException or ObjectDisposedException)
        {
            client.Dispose();
            if (remoteAddress is not null)
                _remoteErrors[remoteAddress] = e is SocketException se ? $"can't connect: {se.Message}" : "can't connect: no answer";
            return null;
        }
        var session = new PeerSession(client, isOutgoing: true) { RemoteAddress = remoteAddress, ExpectedNode = expectedNode, IsRedial = redial, IsProbe = probe };
        await BeginSessionAsync(session).ConfigureAwait(false);
        return session;
    }

    private async Task BeginSessionAsync(PeerSession session)
    {
        session.Received += OnSessionMessage;
        session.AuthenticatedNow += OnAuthenticated;
        session.Closed += OnSessionClosed;
        lock (_lock) _allSessions.Add(session);
        if (_disposed != 0) { session.Close(); return; }
        _ = CloseIfUnprovenAsync(session);
        await session.StartAsync(Presence(MessageTypes.Hello)).ConfigureAwait(false);
    }

    /// <summary>A connection that never says hello (a port scan, a stuck peer), or that
    /// claims our group but never proves the key, is dropped rather than held forever.</summary>
    private async Task CloseIfUnprovenAsync(PeerSession s)
    {
        try { await Task.Delay(HandshakeTimeout, _cts.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }
        if (s.PeerNode is null || (s.Sealed && !s.Authenticated)) s.Close();
    }

    private void OnSessionClosed(PeerSession s)
    {
        bool wasCurrent;
        PairRound? round;
        lock (_lock)
        {
            _allSessions.Remove(s);
            wasCurrent = s.PeerNode is not null && _sessions.TryGetValue(s.PeerNode, out var cur) && cur == s && _sessions.Remove(s.PeerNode);
            round = s.Round;
        }
        if (round is not null) EndPairing(s, round, PairStage.Failed, "The connection was lost.");
        if (!wasCurrent) return;
        _log?.Invoke($"Disconnected from {s.Describe()}.");
        // Only for this group: sessions closed by leaving a group must not bring the old
        // group's devices back into the list of linked ones.
        if (s.Authenticated && s.PeerGroup == Identity.GroupId) PeerSeen?.Invoke(s.PeerNode!, s.PeerName!, AddressToRemember(s));
        RaisePeersChanged();
    }

    /// <summary>The peer's listener on the LAN ("ip:port"), or null when it is outside the LAN
    /// or gave no port. A dialled session's is just where it was dialled (a port forward on
    /// the way may have a port of its own); an inbound one's is its source address with the
    /// port its hello gave.</summary>
    private static string? LanAddressOf(PeerSession s)
    {
        if (s.RemoteEndPoint is not { } ep) return null;
        int? port = s.IsOutgoing ? ep.Port : s.PeerPort;
        var address = Plain(ep.Address);
        return port is { } p && Protocol.IsPrivateOrLan(address) ? new IPEndPoint(address, p).ToString() : null;
    }

    /// <summary>
    /// The address to remember for the peer of <paramref name="s"/> (for <see cref="PeerSeen"/>),
    /// or null to keep the one remembered: a dialled session's, where the device answered. An
    /// inbound session's source may be a NAT's (a router hairpinning a connection to its
    /// public address, a guest network): it is dialled back instead (<see cref="DialBack"/>).
    /// </summary>
    private static string? AddressToRemember(PeerSession s) => s.IsOutgoing ? LanAddressOf(s) : null;

    /// <summary>
    /// Proves the address the device of the inbound session <paramref name="s"/> came from (with
    /// the port its hello gave), when it isn't the one remembered: a probe dials it, and if the
    /// device answers there and proves the key, that address is remembered. A NAT's address
    /// leads elsewhere, or nowhere, and is forgotten. So a device whose address changed is found
    /// where it is now, whatever became of the old address (silent, another device's, or a
    /// stranger's that never proves a key). The same address isn't probed again within a minute.
    /// </summary>
    private void DialBack(PeerSession s)
    {
        if (s.IsOutgoing || LanAddressOf(s) is not { } address) return;
        var node = s.PeerNode!;
        if (KnownPeers().FirstOrDefault(k => k.NodeId == node)?.Address == address) return;
        var now = DateTimeOffset.UtcNow;
        if (_dialedBack.TryGetValue(node, out var last) && last.Address == address && now - last.At < TimeSpan.FromMinutes(1)) return;
        _dialedBack[node] = (address, now);
        if (Protocol.TryParseAddress(address, out var host, out var port))
            _ = DialAsync(host, port, null, _cts.Token, expectedNode: node, probe: true);
    }

    internal void OnAnnounced(SyncMessage msg, IPAddress from)
    {
        var node = msg.Node!;
        var id = Identity;
        var now = DateTimeOffset.UtcNow;
        var ep = new IPEndPoint(from, msg.Port!.Value);
        // Anyone on the LAN can announce anything, and our group's ID is no secret (every
        // announcement carries it), so only the MAC makes a device a member here. A claim to
        // the group without it is a forgery, and dropped.
        bool member = msg.Group == id.GroupId;
        if (member && !AdmitMemberAnnouncement(msg, id, now)) return;
        bool known = _nearby.TryGetValue(node, out var old);
        // Nor may a stranger's announcement give a member's ID and push the member out.
        if (known && !member && IsMember(old!, id.GroupId) && now - old!.Seen <= PeerExpiry) return;
        if (!known && _nearby.Count >= MaxNearby)
        {
            // Full (a flood of made-up devices, likely): a member still gets in, in place of
            // the stalest stranger.
            if (!member) return;
            var victim = _nearby.Where(kv => !IsMember(kv.Value, id.GroupId))
                .OrderBy(kv => kv.Value.Seen).Select(kv => kv.Key).FirstOrDefault();
            if (victim is null) return;
            _nearby.TryRemove(victim, out _);
        }
        _nearby[node] = new Nearby(msg, ep, now, old?.FirstSeen ?? now, member);
        if (!known) RaisePeersChanged();

        // Only group members are dialled; others wait for a pairing.
        if (!member) return;
        lock (_lock) if (_sessions.ContainsKey(node)) { _nearby[node] = _nearby[node] with { FirstSeen = now }; return; }
        if (MayDial(node, old?.FirstSeen ?? now, now)) _ = DialNodeAsync(node, ep);
    }

    /// <summary>
    /// Whether this node should dial <paramref name="node"/>, missing (unconnected) since
    /// <paramref name="since"/>. As in LanLink, the lower ID dials, so two peers don't open two
    /// connections to each other; but if nothing connects for a while (a firewall may refuse
    /// the lower one's dials, or its address for the other may be stale), the higher one does too.
    /// </summary>
    private bool MayDial(string node, DateTimeOffset since, DateTimeOffset now) =>
        string.CompareOrdinal(Identity.NodeId, node) < 0 || now - since > _options.HigherIdFallback;

    /// <summary>The wait after a failed dial that waited <paramref name="delay"/>: doubled, from
    /// <paramref name="first"/> up to a minute.</summary>
    private static TimeSpan NextDelay(TimeSpan delay, TimeSpan first) =>
        TimeSpan.FromSeconds(Math.Min(60, Math.Max(delay.TotalSeconds * 2, first.TotalSeconds)));

    /// <summary>
    /// Decides whether a member's announcement is a new one, and if so records it as the last.
    /// The MAC shows that a member sent it once; the rest, that it isn't a recording. Within
    /// one process of the sender (<see cref="SyncMessage.Boot"/>), its number must grow, so a
    /// replay, from anywhere, is ignored whatever either clock does. One from another process
    /// of the sender must be newer than the last accepted, and every one must be within
    /// <see cref="MaxClockDifference"/> of this device's clock, so a recording made before
    /// this process started is ignored too. Otherwise a recording would keep a device that
    /// has left the group a member here for good, at whatever address it was replayed from.
    /// </summary>
    private bool AdmitMemberAnnouncement(SyncMessage msg, SyncIdentity id, DateTimeOffset now)
    {
        if (!SyncCrypto.HasValidMac(id.GroupKey, msg) || msg.At is not { } at || msg.Seq is not { } seq) return false;
        var name = Protocol.CleanName(msg.Name, msg.Node![..8]);
        if (Math.Abs(now.ToUnixTimeSeconds() - at) > MaxClockDifference.TotalSeconds)
        {
            if (_clockLogged.TryAdd(msg.Node, 0))
                _log?.Invoke($"Ignored announcements from {name}: its clock is "
                    + $"{TextFormat.Span(DateTimeOffset.FromUnixTimeSeconds(at).UtcDateTime, now.UtcDateTime, 2)} off this device's.");
            return false;
        }
        if (_memberLast.TryGetValue(msg.Node, out var last))
        {
            if (msg.Boot == last.Boot ? seq <= last.Seq : at <= last.At)
            {
                if (msg.Boot != last.Boot && _clockLogged.TryAdd(msg.Node + " back", 0))
                    _log?.Invoke($"Ignored announcements from {name} dated before ones already seen: "
                        + "its clock went back since it restarted, or they are being replayed.");
                return false;
            }
        }
        _memberLast[msg.Node] = new LastAnnouncement(msg.Boot!, seq, at);
        return true;
    }

    /// <summary>Dials <paramref name="node"/> at <paramref name="ep"/>, unless a dial to it is
    /// already under way.</summary>
    /// <param name="redial">At the address it was last in sync from (<see cref="DialKnownPeers"/>).</param>
    private async Task DialNodeAsync(string node, IPEndPoint ep, bool redial = false)
    {
        if (!_dialing.TryAdd(node, 0)) return;
        try
        {
            var s = await DialAsync(ep.Address.ToString(), ep.Port, null, _cts.Token, expectedNode: node, redial).ConfigureAwait(false);
            if (s is null && _dialFailLogged.TryAdd(node, 0)) _log?.Invoke($"Could not connect to {ep}; will keep trying.");
        }
        finally { _dialing.TryRemove(node, out _); }
    }

    /// <summary>Keeps a link to a configured address, redialling with a backoff from 5 s to
    /// 60 s. The backoff resets only after a session that got into sync (or lasted), so an
    /// address that answers and hangs up at once isn't hammered every 5 s.</summary>
    private async Task RemoteLinkLoopAsync(string address, CancellationToken ct)
    {
        if (!Protocol.TryParseAddress(address, out var host, out var port))
        {
            _remoteErrors[address] = "not a host or host:port";
            return;
        }
        var first = _options.ReconnectDelay;
        var delay = first;
        string? knownNode = null;
        bool failureLogged = false;
        while (!ct.IsCancellationRequested)
        {
            // Taken before dialling, so that a change of group (pairing, leaving) at any
            // moment from here on cuts the wait below short: the next try should work.
            var kick = Volatile.Read(ref _kick).Task;
            bool alreadyLinked;
            lock (_lock) alreadyLinked = knownNode is not null && _sessions.TryGetValue(knownNode, out var cur) && cur.Authenticated;
            if (!alreadyLinked)
            {
                var s = await DialAsync(host, port, address, ct).ConfigureAwait(false);
                bool good = false;
                if (s is not null)
                {
                    var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    s.Closed += _ => closed.TrySetResult();
                    if (s.IsClosed) closed.TrySetResult();
                    using (ct.Register(() => closed.TrySetResult())) await closed.Task.ConfigureAwait(false);
                    if (ct.IsCancellationRequested) break;
                    knownNode = s.PeerNode ?? knownNode;
                    if (s.LinkedJustNow)
                    {
                        // Linked just now: reconnect under the shared group straight away.
                        delay = first;
                        continue;
                    }
                    good = s.Authenticated || DateTimeOffset.UtcNow - s.Opened > TimeSpan.FromSeconds(30);
                    if (!good) _remoteErrors[address] = s.PeerNode is null ? "it answered but didn't say hello"
                        : s.Sealed ? $"{s.PeerName} doesn't have this group's key" : $"connected to {s.PeerName}, not linked";
                }
                if (good) { delay = first; failureLogged = false; }
                else
                {
                    if (!failureLogged) _log?.Invoke($"Remote link {address}: {_remoteErrors.GetValueOrDefault(address, "failed")}; will keep trying.");
                    failureLogged = true;
                    delay = NextDelay(delay, first);
                }
            }
            if (!kick.IsCompleted)
            {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
                await Task.WhenAny(Task.Delay(delay, wait.Token), kick).ConfigureAwait(false);
                wait.Cancel();   // ends the delay when the kick came first
            }
            if (ct.IsCancellationRequested) break;
            if (kick.IsCompleted) delay = first;
        }
    }

    /// <summary>Completed (and replaced) to make every remote link redial at once.</summary>
    private TaskCompletionSource _kick = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void KickRemoteLinks() =>
        Interlocked.Exchange(ref _kick, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();

    private async Task HousekeepingLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(_options.HousekeepingInterval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            // An exception must not end the loop: nothing else closes idle sessions, ends
            // unanswered pairings or redials missing devices. Each distinct one is logged once.
            try { Housekeep(); _housekeepingError = null; }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                if (e.Message != _housekeepingError) _log?.Invoke($"Housekeeping failed: {e}");
                _housekeepingError = e.Message;
            }
        }
    }

    private string? _housekeepingError;

    private void Housekeep()
    {
        var now = DateTimeOffset.UtcNow;
        bool expired = false;
        foreach (var (node, entry) in _nearby)
            if (now - entry.Seen > PeerExpiry && _nearby.TryRemove(node, out _)) expired = true;
        List<PeerSession> idle;
        List<(PeerSession Session, PairRound Round)> unanswered;
        var group = Identity.GroupId;
        lock (_lock)
        {
            idle = _allSessions.Where(s => s.PeerGroup != group && s.Round is null && !s.IsOutgoing
                && now - s.LastActive > _options.UnlinkedIdleTimeout).ToList();
            unanswered = _allSessions.Where(s => s.Round is { } r && now - r.StartedAt > TimeoutOf(r))
                .Select(s => (s, s.Round!)).ToList();
        }
        foreach (var s in idle) s.Close();
        // LinkedJustNow stays as it is: the other side may have linked a moment ago, and its
        // pair_reject is what says otherwise. At worst, a remote link redials once at once.
        foreach (var (s, r) in unanswered)
        {
            _ = s.SendAsync(new SyncMessage { Type = MessageTypes.PairReject, Round = r.Id, Reason = "timeout" });
            EndPairing(s, r, PairStage.Failed, "Nobody answered in time.");
        }
        if (expired) RaisePeersChanged();
        DialKnownPeers(now);
    }

    /// <summary>
    /// Dials each linked device that is neither connected nor heard announcing, at the LAN
    /// address it was last in sync from. Announcements are broadcasts, which don't always get
    /// through (a phone's Wi-Fi power saving, some routers); without this, two devices whose
    /// connection dropped would wait for one that may never come. As with announcements, the
    /// lower ID dials, and the higher one too once the device has been missing for
    /// <see cref="SyncOptions.HigherIdFallback"/> (<see cref="MayDial"/>). After each dial it
    /// waits <see cref="SyncOptions.ReconnectDelay"/>, doubling up to a minute. Only addresses
    /// on a network this device is on now are dialled: away from home (on mobile data, say),
    /// the home LAN's can't answer. That leaves the wait alone, so a device back home is dialled
    /// within a tick of the networks being listed again (<see cref="LocalSubnets"/>).
    /// </summary>
    private void DialKnownPeers(DateTimeOffset now)
    {
        var id = Identity;
        foreach (var k in KnownPeers())
        {
            if (k.Address is null || !Protocol.TryParseAddress(k.Address, out var host, out var port)
                || !IPAddress.TryParse(host, out var ip)) continue;
            bool connected;
            lock (_lock) connected = _sessions.ContainsKey(k.NodeId);
            bool announcing = _nearby.TryGetValue(k.NodeId, out var n) && now - n.Seen <= PeerExpiry && IsMember(n, id.GroupId);
            if (connected || announcing) { _redial.TryRemove(k.NodeId, out _); continue; }
            var r = _redial.GetOrAdd(k.NodeId, _ => new Redial(now, now, _options.ReconnectDelay));
            if (r.Stale || !MayDial(k.NodeId, r.MissingSince, now) || now < r.Next) continue;
            if (!Protocol.IsOnSubnet(ip, LocalSubnets(now))) continue;
            // Only if nothing else changed the entry meanwhile (a hello marking the address
            // stale, or a session proving the key and removing it).
            if (!_redial.TryUpdate(k.NodeId, r with { Next = now + r.Delay, Delay = NextDelay(r.Delay, _options.ReconnectDelay) }, r))
                continue;
            _ = DialNodeAsync(k.NodeId, new IPEndPoint(ip, port), redial: true);
        }
    }

    /// <summary>The networks this device is on (<see cref="Protocol.LocalSubnets"/>), listed again
    /// after 10 s at most: on the housekeeping thread only.</summary>
    private List<(IPAddress Address, int PrefixLength)> LocalSubnets(DateTimeOffset now)
    {
        if (_subnets is not { } cached || now - cached.At > TimeSpan.FromSeconds(10))
            _subnets = cached = (now, Protocol.LocalSubnets());
        return cached.Subnets;
    }

    // ---- Messages ----------------------------------------------------------------------

    private void OnSessionMessage(PeerSession s, SyncMessage m)
    {
        try
        {
            if (s.PeerNode is null)
            {
                if (m.Type != MessageTypes.Hello || m.App != Protocol.App) { s.Close(); return; }
                OnHello(s, m);
                return;
            }
            switch (m.Type)
            {
                case MessageTypes.Ready when s.Authenticated:
                    break;   // its opening already proved the key (OnAuthenticated)
                case MessageTypes.Digest when s.Authenticated:
                    var newer = _store.NewerThan(m.Digest ?? []);
                    if (newer.Count > 0) _ = s.SendAsync(new SyncMessage { Type = MessageTypes.Tasks, Tasks = newer.ToList() });
                    _ = s.SendAsync(new SyncMessage { Type = MessageTypes.DigestDone });
                    break;
                case MessageTypes.DigestDone when s.Authenticated:
                    if (!FirstSyncDone) { FirstSyncDone = true; FirstSyncCompleted?.Invoke(); }
                    break;
                case MessageTypes.Tasks when s.Authenticated:
                    var changed = _store.Merge(m.Tasks ?? []);
                    if (changed.Count > 0) Broadcast(changed, except: s);
                    break;
                case MessageTypes.PairRequest when !s.Sealed:
                    OnPairRequest(s, m);
                    break;
                case MessageTypes.PairChallenge or MessageTypes.PairReveal or MessageTypes.PairAccept
                    or MessageTypes.PairGroup or MessageTypes.PairReject when !s.Sealed:
                    OnPairMessage(s, m);
                    break;
                default:
                    _log?.Invoke($"Unexpected {m.Type} from {s.Describe()}; closing.");
                    s.Close();
                    break;
            }
        }
        catch (Exception e)
        {
            // Whatever a peer sends, the worst it may cause is the end of its own session.
            _log?.Invoke($"Bad {m.Type} from {s.Describe()}: {e.Message}");
            PairRound? round;
            lock (_lock) round = s.Round;
            if (round is not null) EndPairing(s, round, PairStage.Failed, $"{s.PeerName ?? "The other device"} sent something invalid.");
            s.Close();
        }
    }

    /// <summary>
    /// Checks who answered a dial made to reach <paramref name="expected"/>: another device of
    /// this group goes on (it proves the key like any session); any other device, or this one
    /// itself, is hung up on. When the dial was a redial at the device's last address
    /// (<see cref="PeerSession.IsRedial"/>), another device answering (the address is someone
    /// else's now, maybe this device's own) or the device without the group (it has left)
    /// makes that address stale (<see cref="MarkStale"/>).
    /// </summary>
    /// <param name="accepted">Whether the hello was accepted. A refused one from the expected
    /// device (another protocol version, say) says nothing about the address: the caller
    /// closes it like any refused hello.</param>
    /// <param name="helloNode">The node ID the hello gave, valid or not.</param>
    /// <returns>Whether the session goes on; false when it was closed.</returns>
    private bool KeepDialledSession(PeerSession s, string expected, bool accepted, string? helloNode)
    {
        bool someoneElse = helloNode != expected;
        bool leftGroup = !someoneElse && accepted && !s.Sealed;
        if (!someoneElse && !(leftGroup && s.IsRedial)) return true;
        if (s.IsRedial)
            MarkStale(s, expected, leftGroup ? null
                : helloNode == Identity.NodeId ? "this device" : s.PeerName ?? "a device that didn't say hello properly");
        if (accepted && someoneElse && s.Sealed) return true;
        s.Close();
        return false;
    }

    /// <summary>
    /// Marks <paramref name="node"/>'s remembered address stale: not dialled again until the
    /// device is back (heard announcing, or in sync) or the engine restarts. Logged once.
    /// </summary>
    /// <param name="s">The session that found it so.</param>
    /// <param name="who">Who answered there instead; null for the device itself, without the group.</param>
    private void MarkStale(PeerSession s, string node, string? who)
    {
        // Again if the dial schedule moved on meanwhile; not at all once the entry is gone (the
        // device is back) or already stale.
        while (true)
        {
            if (!_redial.TryGetValue(node, out var r) || r.Stale) return;
            if (_redial.TryUpdate(node, r with { Stale = true }, r)) break;
        }
        var name = KnownPeers().FirstOrDefault(k => k.NodeId == node)?.Name ?? node[..8];
        var at = s.RemoteEndPoint is { } ep ? new IPEndPoint(Plain(ep.Address), ep.Port).ToString() : "?";
        _log?.Invoke(who is null
            ? $"{name} answered at {at} without this sync group (it has left it); not dialling it again until it is back."
            : $"{name}'s last address, {at}, now answers as {who}; not dialling it there again until it is back.");
    }

    private void OnHello(PeerSession s, SyncMessage hello)
    {
        var id = Identity;
        if (hello.Proto != Protocol.Version)
            _log?.Invoke($"A device speaking sync protocol {hello.Proto} connected from {s.RemoteEndPoint}; this one speaks {Protocol.Version}. Update both to the same version.");
        bool accepted = s.AcceptHello(hello, id.NodeId, id.GroupId, id.GroupKey);
        if (s.IsProbe)
        {
            // Proving an address (DialBack): never installed, and no ready of its own, so the
            // device keeps its session; its ready proves the key (OnAuthenticated).
            if (!accepted || s.PeerNode != s.ExpectedNode || !s.Sealed) s.Close();
            return;
        }
        if (s.ExpectedNode is { } expected && !KeepDialledSession(s, expected, accepted, hello.Node)) return;
        if (!accepted) { s.Close(); return; }
        // Claims a remembered linked device's ID without the group: an impostor angling to have
        // that device shown "not linked" (and re-linked), or a device that has left the group.
        // It never speaks for that device here, but it may still ask to pair, through the
        // usual codes on both screens, so a device that left can link again.
        bool claimsLinkedDevice = !s.Sealed && !s.IsOutgoing && KnownPeers().Any(k => k.NodeId == s.PeerNode);
        bool installed;
        PeerSession? replaced = null;
        lock (_lock)
        {
            _sessions.TryGetValue(s.PeerNode!, out var old);
            // A fresh hello replaces a stale session (the peer restarted without a FIN), as in
            // LanLink, but never one that is proving the key, has proved it, or is pairing: a
            // hello proves nothing, so anyone could otherwise knock a linked device (or a
            // pairing) off its connection. A session claiming the group takes over only once
            // it proves the key (OnAuthenticated).
            installed = !claimsLinkedDevice
                && (old is null || old.IsClosed || (!old.Authenticated && !old.Sealed && old.Round is null));
            if (installed)
            {
                if (old is not null && old != s) replaced = old;
                _sessions[s.PeerNode!] = s;
            }
        }
        replaced?.Close();
        if (!installed && !s.Sealed && !claimsLinkedDevice)
        {
            s.Close();
            return;
        }
        _dialFailLogged.TryRemove(s.PeerNode!, out _);
        // The first sealed frame each way is tiny: opening it proves the key; the digest
        // follows once the peer has proved it too (OnAuthenticated).
        if (s.Sealed) _ = s.SendAsync(new SyncMessage { Type = MessageTypes.Ready });
        else _log?.Invoke($"Connected to {s.Describe()}, which isn't linked to this device.");
        if (installed) RaisePeersChanged();
    }

    private void OnAuthenticated(PeerSession s)
    {
        if (s.IsProbe)
        {
            // The device proved the key at the address it came from (DialBack): that is where
            // to dial it. The probe's work is done.
            if (s.PeerGroup == Identity.GroupId && LanAddressOf(s) is { } at) PeerSeen?.Invoke(s.PeerNode!, s.PeerName!, at);
            s.Close();
            return;
        }
        PeerSession? replaced = null;
        lock (_lock)
        {
            if (s.IsClosed) return;
            if (_sessions.TryGetValue(s.PeerNode!, out var old) && old != s) replaced = old;
            _sessions[s.PeerNode!] = s;
        }
        replaced?.Close();
        _remoteErrors.TryRemove(s.RemoteAddress ?? "", out _);
        _log?.Invoke($"In sync with {s.Describe()}.");
        _ = s.SendAsync(new SyncMessage { Type = MessageTypes.Digest, Digest = _store.Digest().ToList() });
        _redial.TryRemove(s.PeerNode!, out _);
        if (s.PeerGroup == Identity.GroupId)
        {
            PeerSeen?.Invoke(s.PeerNode!, s.PeerName!, AddressToRemember(s));
            DialBack(s);
        }
        RaisePeersChanged();
    }

    private void OnStoreChanged(StoreChange change)
    {
        // Changes merged from a peer are forwarded by OnSessionMessage, which knows whom to skip.
        if (!change.FromSync) Broadcast(change.Tasks, except: null);
    }

    /// <summary>Sends <paramref name="tasks"/> to every authenticated session but
    /// <paramref name="except"/>. Forwarding what a peer sent on to the others is what
    /// bridges LANs: no routing table is needed, and since only records that changed
    /// something are forwarded, a loop dies out after one round.</summary>
    private void Broadcast(IReadOnlyList<AlarmTask> tasks, PeerSession? except)
    {
        List<PeerSession> targets;
        lock (_lock) targets = _sessions.Values.Where(x => x != except && x.Authenticated).ToList();
        var msg = new SyncMessage { Type = MessageTypes.Tasks, Tasks = tasks.ToList() };
        foreach (var t in targets) _ = t.SendAsync(msg);
    }

    // ---- Pairing ------------------------------------------------------------------------
    //
    // requester R (clicked Link)              accepter A
    //   pair_request {commit = H(R.pub, R.members), R.members} →
    //                                          ← pair_challenge {A.pub, A.members}
    //   pair_reveal {R.pub} →                  (checks the commitment)
    //   both show the code (it covers both keys and both member counts) and ask their user
    //                                          ← pair_accept {A's group, sealed} if R joins,
    //                                                        {} if A joins   (A's user said yes)
    //   when R's user says yes too:
    //     R joins: adopts A's group;  A joins: pair_group {R's group, sealed} →
    //   either user's no, or the timeout: pair_reject →
    //
    // R commits before seeing A's key and A reveals before seeing R's, so nobody in the
    // middle can choose a key that makes the two codes agree. Each side links only on its
    // own user's yes, so no device can be pulled into a group, or handed its group's key,
    // without the person in front of it comparing the codes. The device linked with fewer
    // others joins, so linking a new device from an established one brings the new one in.
    //
    // Every message after the request names its round (the commitment), and each side keeps
    // a round open until it is decided for both, so a message from an earlier try never acts
    // on a later one, however the two users' answers interleave. One round at a time on each
    // side, and per connection.

    private int LinkedCount() => ClampMembers(KnownPeers().Count);

    /// <summary>The app's remembered linked devices (<see cref="SyncOptions.KnownPeers"/>), less
    /// malformed entries: a damaged settings file may hold a null, or one without a node ID.</summary>
    private List<KnownPeer> KnownPeers() =>
        (_options.KnownPeers?.Invoke() ?? []).Where(KnownPeer.IsUsable).ToList();

    /// <summary>A member count as both sides hash it into the commitment and the code.</summary>
    private static int ClampMembers(int? members) => Math.Clamp(members ?? 0, 0, 10_000);

    /// <summary>How long a round may wait for its answers. The accepter waits a little longer,
    /// so the requester, which starts first, always gives up first: a pair_group it sent just
    /// before then can't reach an accepter that has already given up, leaving the requester
    /// reporting a link that never happened.</summary>
    private TimeSpan TimeoutOf(PairRound r) => _options.PairingTimeout + (r.Initiator ? TimeSpan.Zero : AccepterGrace);

    private static readonly TimeSpan AccepterGrace = TimeSpan.FromSeconds(30);

    /// <summary>Starts pairing with the device <paramref name="node"/>. Progress arrives
    /// through <see cref="PairProgressed"/>, and the code through <see cref="PairRequested"/>.</summary>
    public async Task RequestPairingAsync(string node)
    {
        PeerSession? s;
        lock (_lock) _sessions.TryGetValue(node, out s);
        if (s is null && _nearby.TryGetValue(node, out var seen))
            s = await DialAsync(seen.Ep.Address.ToString(), seen.Ep.Port, null, _cts.Token).ConfigureAwait(false);
        if (s is null)
        {
            PairProgressed?.Invoke(new PairProgress(node, node, PairStage.Failed, null, "That device can't be reached."));
            return;
        }
        for (int i = 0; i < 50 && s.PeerNode is null && !s.IsClosed; i++) await Task.Delay(100).ConfigureAwait(false);
        if (s.PeerNode is null || s.IsClosed)
        {
            PairProgressed?.Invoke(new PairProgress(node, node, PairStage.Failed, null, "That device didn't answer."));
            return;
        }
        if (s.Sealed)
        {
            PairProgressed?.Invoke(new PairProgress(node, s.PeerName!, PairStage.Accepted, null, "Already linked."));
            return;
        }
        int members = LinkedCount();
        var key = SyncCrypto.NewPairingKey();
        var pub = key.ExportSubjectPublicKeyInfo();
        var round = new PairRound { Initiator = true, Key = key, Public = pub, Commit = SyncCrypto.Commitment(pub, members), MyMembers = members };
        bool busy;
        lock (_lock)
        {
            // Under the lock, and only if the session is open: a session closed before the
            // round is installed would never end it, holding the one-at-a-time slot for good.
            busy = s.IsClosed || s.Round is not null || _requesting is { Ended: false };
            if (!busy) { _requesting = round; s.Round = round; s.LastActive = DateTimeOffset.UtcNow; }
        }
        if (busy)
        {
            key.Dispose();
            PairProgressed?.Invoke(new PairProgress(node, s.PeerName!, PairStage.Failed, null,
                s.IsClosed ? "The connection was lost." : "Another link is already under way."));
            return;
        }
        await s.SendAsync(new SyncMessage { Type = MessageTypes.PairRequest, Commit = round.Id, Members = members }).ConfigureAwait(false);
    }

    /// <summary>Accepter: answer with our key, if no other pairing is under way.</summary>
    private void OnPairRequest(PeerSession s, SyncMessage m)
    {
        var commit = Convert.FromBase64String(m.Commit ?? "");
        if (commit.Length != 32) throw new InvalidDataException("A pairing request without a commitment.");
        var key = SyncCrypto.NewPairingKey();
        var round = new PairRound
        {
            Initiator = false, Key = key, Public = key.ExportSubjectPublicKeyInfo(), Commit = commit,
            MyMembers = LinkedCount(), PeerMembers = ClampMembers(m.Members),
        };
        bool busy;
        lock (_lock)
        {
            busy = s.IsClosed || s.Round is not null || _accepting is { Ended: false };
            if (!busy) { _accepting = round; s.Round = round; s.LastActive = DateTimeOffset.UtcNow; }
        }
        if (busy)
        {
            key.Dispose();
            _ = s.SendAsync(new SyncMessage { Type = MessageTypes.PairReject, Round = round.Id, Reason = "busy" });
            return;
        }
        _ = s.SendAsync(new SyncMessage
        {
            Type = MessageTypes.PairChallenge, Round = round.Id, PairKey = Convert.ToBase64String(round.Public), Members = round.MyMembers,
        });
    }

    /// <summary>Every pairing message after the request: acted on only within its own round.</summary>
    private void OnPairMessage(PeerSession s, SyncMessage m)
    {
        PairRound? round;
        lock (_lock) round = s.Round;
        // For a round already over on this side (each user answers on their own device, so
        // the two ends finish at different moments), or none at all: ignored, leaving the
        // connection free for another try.
        if (round is null || m.Round != round.Id) return;
        switch (m.Type)
        {
            case MessageTypes.PairChallenge when round.Initiator && round.Secret is null:
                OnPairChallenge(s, round, m);
                break;
            case MessageTypes.PairReveal when !round.Initiator && round.Secret is null:
                OnPairReveal(s, round, m);
                break;
            case MessageTypes.PairAccept when round.Initiator && round.Secret is not null:
                lock (round.Gate)
                {
                    if (round.PeerAccepted) throw new InvalidDataException("A second pair_accept.");
                    round.PeerSealedGroup = m.Sealed;
                    round.PeerAccepted = true;
                }
                TryCompleteAsRequester(s, round);
                break;
            case MessageTypes.PairGroup when !round.Initiator && round.LocalAccepted && !round.RequesterJoins:
                // Only ever after this device's user said yes: a group pushed at any other
                // moment would let anyone pull this device into their group unasked.
                Adopt(s, round, m.Sealed, SyncCrypto.RequesterGroupCounter);
                break;
            case MessageTypes.PairReject:
                s.LinkedJustNow = false;
                EndPairing(s, round, PairStage.Rejected, m.Reason switch
                {
                    "busy" => $"{s.PeerName} is busy linking another device.",
                    "timeout" => "Nobody answered in time.",
                    _ => $"{s.PeerName} said no.",
                });
                break;
            default:
                throw new InvalidDataException($"Unexpected {m.Type} in a pairing.");
        }
    }

    /// <summary>Requester: reveal our key, work out the code, and ask our user.</summary>
    private void OnPairChallenge(PeerSession s, PairRound round, SyncMessage m)
    {
        var theirs = Convert.FromBase64String(m.PairKey ?? "");
        round.PeerMembers = ClampMembers(m.Members);
        (round.Secret, round.Code) = SyncCrypto.PairingSecret(round.Key, theirs,
            requesterKey: round.Public, accepterKey: theirs, requesterMembers: round.MyMembers, accepterMembers: round.PeerMembers);
        round.RequesterJoins = round.MyMembers <= round.PeerMembers;
        round.Key.Dispose();
        _ = s.SendAsync(new SyncMessage { Type = MessageTypes.PairReveal, Round = round.Id, PairKey = Convert.ToBase64String(round.Public) });
        PairProgressed?.Invoke(new PairProgress(s.PeerNode!, s.PeerName!, PairStage.CodeShown, round.Code, null));
        Ask(s, round);
    }

    /// <summary>Accepter: check the revealed key against the commitment, work out the code,
    /// and ask our user.</summary>
    private void OnPairReveal(PeerSession s, PairRound round, SyncMessage m)
    {
        var theirs = Convert.FromBase64String(m.PairKey ?? "");
        if (!CryptographicOperations.FixedTimeEquals(SyncCrypto.Commitment(theirs, round.PeerMembers), round.Commit))
            throw new CryptographicException("The revealed pairing key doesn't match its commitment.");
        (round.Secret, round.Code) = SyncCrypto.PairingSecret(round.Key, theirs,
            requesterKey: theirs, accepterKey: round.Public, requesterMembers: round.PeerMembers, accepterMembers: round.MyMembers);
        round.RequesterJoins = round.PeerMembers <= round.MyMembers;
        round.Key.Dispose();
        Ask(s, round);
    }

    private void Ask(PeerSession s, PairRound round)
    {
        int answered = 0;
        PairPrompt? prompt = null;
        prompt = new PairPrompt
        {
            PeerNode = s.PeerNode!,
            PeerName = s.PeerName!,
            Code = round.Code!,
            IsRequester = round.Initiator,
            ThisDeviceJoins = round.Initiator ? round.RequesterJoins : !round.RequesterJoins,
            MyLinkedDevices = round.MyMembers,
            Answer = accept => Interlocked.Exchange(ref answered, 1) != 0 ? Task.CompletedTask : AnswerAsync(s, round, prompt!, accept),
        };
        lock (round.Gate)
        {
            if (round.Ended) return;
            round.Prompt = prompt;
        }
        PairRequested?.Invoke(prompt);
    }

    /// <summary>This device's user answered <paramref name="prompt"/> (on either side). An
    /// answer counts only for the round it was asked about, and acts only on that round.</summary>
    private async Task AnswerAsync(PeerSession s, PairRound round, PairPrompt prompt, bool accept)
    {
        try
        {
            lock (round.Gate)
            {
                if (round.Ended || round.Prompt != prompt) return;
                round.Prompt = null;
                if (accept) round.LocalAccepted = true;
            }
            if (!accept)
            {
                await s.SendAsync(new SyncMessage { Type = MessageTypes.PairReject, Round = round.Id }).ConfigureAwait(false);
                EndPairing(s, round, PairStage.Rejected, "Linking cancelled.");
                return;
            }
            if (round.Initiator)
            {
                TryCompleteAsRequester(s, round);
                return;
            }
            // Accepter: say yes, handing over our group if the requester is the one joining.
            // Nothing is final yet: the round stays open until the requester's user answers
            // too (with pair_group, or by joining and reconnecting, or with a no), or it times out.
            if (round.RequesterJoins) s.LinkedJustNow = true;
            var sealedGroup = round.RequesterJoins ? SealGroup(round, SyncCrypto.AccepterGroupCounter) : null;
            await s.SendAsync(new SyncMessage { Type = MessageTypes.PairAccept, Round = round.Id, Sealed = sealedGroup }).ConfigureAwait(false);
            if (round.RequesterJoins) _log?.Invoke($"Accepted the link with {s.Describe()}.");
        }
        catch (Exception e)
        {
            // An answer goes through a Task the screen discards: report here, not into the void.
            _log?.Invoke($"Linking with {s.Describe()} failed: {e.Message}");
            EndPairing(s, round, PairStage.Failed, e.Message);
            s.Close();
        }
    }

    /// <summary>Requester: once both users have said yes, join the accepter's group or send
    /// ours. Called from the read loop (pair_accept) and from the answer (the user's yes);
    /// <see cref="PairRound.Finishing"/>, set under the round's lock, makes exactly one of
    /// them finish.</summary>
    private void TryCompleteAsRequester(PeerSession s, PairRound round)
    {
        lock (round.Gate)
        {
            if (round.Ended || !round.LocalAccepted || !round.PeerAccepted || round.Finishing) return;
            round.Finishing = true;
        }
        var (node, name, code) = (s.PeerNode!, s.PeerName!, round.Code);
        if (round.RequesterJoins)
        {
            Adopt(s, round, round.PeerSealedGroup, SyncCrypto.AccepterGroupCounter);
            PairProgressed?.Invoke(new PairProgress(node, name, PairStage.Accepted, code, null));
            return;
        }
        s.LinkedJustNow = true;
        _ = s.SendAsync(new SyncMessage { Type = MessageTypes.PairGroup, Round = round.Id, Sealed = SealGroup(round, SyncCrypto.RequesterGroupCounter) });
        _log?.Invoke($"{s.Describe()} joins this device's sync group.");
        EndPairing(s, round, PairStage.Accepted);
    }

    /// <summary>Ends <paramref name="round"/> (once; later calls do nothing): frees the
    /// one-at-a-time slots, withdraws a prompt nobody answered, and on the requesting side
    /// reports <paramref name="stage"/>, if given, with <paramref name="message"/>.</summary>
    private void EndPairing(PeerSession s, PairRound round, PairStage? stage = null, string? message = null)
    {
        PairPrompt? prompt;
        lock (round.Gate)
        {
            if (round.Ended) return;
            round.Ended = true;
            prompt = round.Prompt;
            round.Prompt = null;
        }
        lock (_lock)
        {
            if (_accepting == round) _accepting = null;
            if (_requesting == round) _requesting = null;
            if (s.Round == round) s.Round = null;
            s.LastActive = DateTimeOffset.UtcNow;
        }
        round.Key.Dispose();
        prompt?.Withdraw();
        if (round.Initiator && stage is { } st)
            PairProgressed?.Invoke(new PairProgress(s.PeerNode ?? "", s.PeerName ?? "?", st, round.Code, message));
    }

    private string SealGroup(PairRound round, ulong counter)
    {
        var id = Identity;
        var payload = JsonSerializer.SerializeToUtf8Bytes([id.GroupId, id.GroupKeyBase64], Json.Wire.StringArray);
        return Convert.ToBase64String(SyncCrypto.Seal(round.Secret!, counter, payload));
    }

    /// <summary>Joins the group sealed in <paramref name="sealedGroup"/>, then reconnects
    /// everything under it.</summary>
    private void Adopt(PeerSession s, PairRound round, string? sealedGroup, ulong counter)
    {
        var plain = SyncCrypto.Open(round.Secret!, counter, Convert.FromBase64String(sealedGroup ?? ""));
        var parts = JsonSerializer.Deserialize(plain, Json.Wire.StringArray);
        if (parts is not [var groupId, var groupKey] || !Protocol.IsValidNodeId(groupId) || Convert.FromBase64String(groupKey).Length != 32)
            throw new CryptographicException("Malformed group.");
        var peerEp = s.RemoteEndPoint;
        var peerPort = s.PeerPort;
        var remote = s.RemoteAddress;
        s.LinkedJustNow = true;
        EndPairing(s, round);
        SyncIdentity adopted;
        lock (_lock) adopted = _id = _id with { GroupId = groupId, GroupKeyBase64 = groupKey };
        IdentityChanged?.Invoke(adopted);
        _log?.Invoke($"Joined the sync group of {s.Describe()}.");
        ResetSessions();
        _discovery?.AnnounceNow();
        KickRemoteLinks();
        // Reconnect at once rather than waiting for discovery (or a remote link's backoff).
        if (remote is null && peerEp is not null && peerPort is { } port)
            _ = DialAsync(Plain(peerEp.Address).ToString(), port, null, _cts.Token);
    }

    /// <summary>Leaves the current group for a new one of this device alone.</summary>
    public void LeaveGroup(string newGroupId, string newGroupKey)
    {
        SyncIdentity changed;
        lock (_lock) changed = _id = _id with { GroupId = newGroupId, GroupKeyBase64 = newGroupKey };
        IdentityChanged?.Invoke(changed);
        ResetSessions();
        _discovery?.AnnounceNow();
        KickRemoteLinks();
    }

    /// <summary>Closes every session; they reconnect under the current identity (remote
    /// links redial themselves; LAN peers through discovery).</summary>
    private void ResetSessions()
    {
        List<PeerSession> all;
        lock (_lock) all = _allSessions.ToList();
        foreach (var x in all) x.Close();
        RaisePeersChanged();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _store.Changed -= OnStoreChanged;
        _cts.Cancel();
        _discovery?.Dispose();
        try { _listener?.Stop(); } catch (SocketException) { }
        ResetSessions();
    }
}
