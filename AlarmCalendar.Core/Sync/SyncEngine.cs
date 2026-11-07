using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;

namespace AlarmCalendar.Sync;

/// <summary>Who this node is and which group it syncs with.</summary>
public sealed record SyncIdentity(string NodeId, string Name, string GroupId, string GroupKeyBase64)
{
    internal byte[] GroupKey => Convert.FromBase64String(GroupKeyBase64);
}

public sealed record SyncOptions
{
    public int Port { get; init; } = Protocol.DefaultPort;
    /// <summary>Address the TCP listener binds; tests use loopback.</summary>
    public IPAddress BindAddress { get; init; } = IPAddress.Any;
    public bool EnableDiscovery { get; init; } = true;
    public int DiscoveryPort { get; init; } = Protocol.DiscoveryPort;
    public bool AcceptExternalConnections { get; init; }
    public IReadOnlyList<string> RemoteAddresses { get; init; } = [];
    /// <summary>Android's WiFi MulticastLock; null elsewhere.</summary>
    public Func<IDisposable?>? AcquireMulticastLock { get; init; }
    /// <summary>The first wait before redialling a remote address; it doubles up to a minute.</summary>
    public TimeSpan ReconnectDelay { get; init; } = TimeSpan.FromSeconds(5);
}

/// <summary>A device this node knows of, for the Devices screen.</summary>
/// <param name="Linked">In this node's sync group.</param>
/// <param name="Connected">A session to it is open (and, when linked, authenticated).</param>
/// <param name="Address">Where it was last seen.</param>
public sealed record PeerInfo(string NodeId, string Name, bool Linked, bool Connected, string? Address, DateTimeOffset LastSeen);

/// <summary>A pairing request awaiting the user's answer on the accepting device.</summary>
public sealed class PairPrompt
{
    internal Func<bool, Task> Answer { get; init; } = _ => Task.CompletedTask;
    public required string PeerNode { get; init; }
    public required string PeerName { get; init; }
    /// <summary>Must match the code the requesting device shows.</summary>
    public required string Code { get; init; }
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
    private static readonly TimeSpan UnlinkedIdleTimeout = TimeSpan.FromMinutes(3);

    private readonly TaskStore _store;
    private readonly SyncOptions _options;
    private readonly Action<string>? _log;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _lock = new();
    /// <summary>Open sessions whose peer has said hello, by peer node ID; one per peer.</summary>
    private readonly Dictionary<string, PeerSession> _sessions = new();
    private readonly HashSet<PeerSession> _allSessions = new();
    /// <summary>Devices seen by discovery: (announcement, address, when).</summary>
    private readonly ConcurrentDictionary<string, (SyncMessage msg, IPEndPoint ep, DateTimeOffset seen)> _nearby = new();
    private readonly ConcurrentDictionary<string, byte> _dialing = new();
    private readonly ConcurrentDictionary<string, byte> _dialFailLogged = new();
    private SyncIdentity _id;
    private TcpListener? _listener;
    private Discovery? _discovery;
    private int _disposed;

    public event Action? PeersChanged;
    public event Action<PairPrompt>? PairRequested;
    public event Action<PairProgress>? PairProgressed;
    /// <summary>This node joined another group by pairing; the app must persist the new identity.</summary>
    public event Action<SyncIdentity>? IdentityChanged;

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

    /// <summary>Binds the listener and starts discovery and remote links.</summary>
    /// <exception cref="SocketException">The port is in use.</exception>
    public void Start()
    {
        _listener = new TcpListener(_options.BindAddress, _options.Port);
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
        foreach (var (node, (msg, ep, seen)) in _nearby)
        {
            if (now - seen > PeerExpiry) continue;
            result[node] = new PeerInfo(node, msg.Name ?? node, msg.Group == group, false, ep.Address.ToString(), seen);
        }
        lock (_lock)
        {
            foreach (var (node, s) in _sessions)
            {
                bool linked = s.PeerGroup == group;
                result[node] = new PeerInfo(node, s.PeerName ?? node, linked, !linked || s.Authenticated,
                    s.RemoteAddress ?? s.RemoteEndPoint?.Address.ToString(), now);
            }
        }
        return result.Values.OrderByDescending(p => p.Linked).ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    // ---- Connections -------------------------------------------------------------------

    private SyncMessage Announcement()
    {
        var id = Identity;
        return new SyncMessage
        {
            Type = MessageTypes.Announce, App = Protocol.App, Proto = Protocol.Version,
            Node = id.NodeId, Name = id.Name, Group = id.GroupId, Port = ListenPort,
        };
    }

    private SyncMessage Hello()
    {
        var id = Identity;
        return new SyncMessage
        {
            Type = MessageTypes.Hello, App = Protocol.App, Proto = Protocol.Version,
            Node = id.NodeId, Name = id.Name, Group = id.GroupId, Port = ListenPort,
        };
    }

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
                _log?.Invoke($"Refused a connection from {ep.Address}, outside the LAN (Accept connections from outside the LAN is off).");
                client.Dispose();
                continue;
            }
            var session = new PeerSession(client, isOutgoing: false);
            await BeginSessionAsync(session).ConfigureAwait(false);
        }
    }

    /// <summary>Connects to <paramref name="host"/>:<paramref name="port"/>.</summary>
    /// <returns>The session, its hello sent; null when the connection failed.</returns>
    private async Task<PeerSession?> DialAsync(string host, int port, string? remoteAddress, CancellationToken ct)
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
            return null;
        }
        var session = new PeerSession(client, isOutgoing: true) { RemoteAddress = remoteAddress };
        await BeginSessionAsync(session).ConfigureAwait(false);
        return session;
    }

    private async Task BeginSessionAsync(PeerSession session)
    {
        session.Received += OnSessionMessage;
        session.Closed += OnSessionClosed;
        lock (_lock) _allSessions.Add(session);
        _ = CloseIfSilentAsync(session);
        await session.StartAsync(Hello()).ConfigureAwait(false);
    }

    /// <summary>A connection that never says hello (a port scan, a stuck peer) is dropped
    /// rather than held forever, as in LanLink.</summary>
    private async Task CloseIfSilentAsync(PeerSession s)
    {
        try { await Task.Delay(HandshakeTimeout, _cts.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }
        if (s.PeerNode is null) s.Close();
    }

    private void OnSessionClosed(PeerSession s)
    {
        bool changed;
        lock (_lock)
        {
            _allSessions.Remove(s);
            changed = s.PeerNode is not null && _sessions.TryGetValue(s.PeerNode, out var cur) && cur == s && _sessions.Remove(s.PeerNode);
        }
        if (changed)
        {
            _log?.Invoke($"Disconnected from {s.Describe()}.");
            PeersChanged?.Invoke();
        }
    }

    private void OnAnnounced(SyncMessage msg, IPAddress from)
    {
        var node = msg.Node!;
        bool isNew = !_nearby.ContainsKey(node);
        _nearby[node] = (msg, new IPEndPoint(from, msg.Port!.Value), DateTimeOffset.UtcNow);
        if (isNew) PeersChanged?.Invoke();

        // As in LanLink, only the node with the lower ID dials, so two peers don't open two
        // connections to each other. Only group members are dialled; others wait for a pairing.
        var id = Identity;
        if (msg.Group != id.GroupId || string.CompareOrdinal(id.NodeId, node) > 0) return;
        lock (_lock) if (_sessions.ContainsKey(node)) return;
        _ = DialNodeAsync(node, new IPEndPoint(from, msg.Port!.Value));
    }

    private async Task DialNodeAsync(string node, IPEndPoint ep)
    {
        if (!_dialing.TryAdd(node, 0)) return;
        try
        {
            var s = await DialAsync(ep.Address.ToString(), ep.Port, null, _cts.Token).ConfigureAwait(false);
            if (s is null && _dialFailLogged.TryAdd(node, 0)) _log?.Invoke($"Could not connect to {ep}; will keep trying.");
        }
        finally { _dialing.TryRemove(node, out _); }
    }

    /// <summary>Keeps a link to a configured address, redialling with a backoff from 5 s to 60 s.</summary>
    private async Task RemoteLinkLoopAsync(string address, CancellationToken ct)
    {
        if (!Protocol.TryParseAddress(address, out var host, out var port))
        {
            _log?.Invoke($"Ignoring the remote address \"{address}\": it isn't host or host:port.");
            return;
        }
        var first = _options.ReconnectDelay;
        var delay = first;
        string? knownNode = null;
        while (!ct.IsCancellationRequested)
        {
            bool alreadyLinked;
            lock (_lock) alreadyLinked = knownNode is not null && _sessions.ContainsKey(knownNode);
            if (!alreadyLinked)
            {
                var s = await DialAsync(host, port, address, ct).ConfigureAwait(false);
                if (s is null) delay = TimeSpan.FromSeconds(Math.Min(60, Math.Max(delay.TotalSeconds * 2, first.TotalSeconds)));
                else
                {
                    delay = first;
                    var closed = new TaskCompletionSource();
                    s.Closed += _ => closed.TrySetResult();
                    if (s.IsClosed) closed.TrySetResult();
                    await Task.WhenAny(closed.Task, Task.Delay(Timeout.Infinite, ct)).ConfigureAwait(false);
                    knownNode = s.PeerNode ?? knownNode;
                }
            }
            try { await Task.Delay(delay, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task HousekeepingLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            var now = DateTimeOffset.UtcNow;
            bool expired = false;
            foreach (var (node, entry) in _nearby)
                if (now - entry.seen > PeerExpiry && _nearby.TryRemove(node, out _)) expired = true;
            List<PeerSession> idle;
            var group = Identity.GroupId;
            lock (_lock)
                idle = _allSessions.Where(s => !s.IsOutgoing && s.PeerGroup != group && s.PairSecret is null
                    && now - s.Opened > UnlinkedIdleTimeout).ToList();
            foreach (var s in idle) s.Close();
            if (expired) PeersChanged?.Invoke();
        }
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
                case MessageTypes.Digest when s.Authenticated:
                    var newer = _store.NewerThan(m.Digest ?? []);
                    if (newer.Count > 0) _ = s.SendAsync(new SyncMessage { Type = MessageTypes.Tasks, Tasks = newer.ToList() });
                    break;
                case MessageTypes.Tasks when s.Authenticated:
                    var changed = _store.Merge(m.Tasks ?? []);
                    if (changed.Count > 0) Broadcast(changed, except: s);
                    break;
                case MessageTypes.PairRequest when !s.Sealed:
                    OnPairRequest(s, m);
                    break;
                case MessageTypes.PairChallenge when !s.Sealed && s.PairInitiator:
                    OnPairChallenge(s, m);
                    break;
                case MessageTypes.PairAccept when !s.Sealed && s.PairInitiator:
                    OnPairAccept(s, m);
                    break;
                case MessageTypes.PairReject when !s.Sealed && s.PairInitiator:
                    PairProgressed?.Invoke(new PairProgress(s.PeerNode, s.PeerName!, PairStage.Rejected, s.PairCode, "The other device said no."));
                    if (s.RemoteAddress is null) s.Close();
                    break;
                default:
                    _log?.Invoke($"Unexpected {m.Type} from {s.Describe()}; closing.");
                    s.Close();
                    break;
            }
        }
        catch (Exception e) when (e is JsonException or FormatException or CryptographicException or ArgumentException)
        {
            _log?.Invoke($"Bad {m.Type} from {s.Describe()}: {e.Message}");
            s.Close();
        }
    }

    private void OnHello(PeerSession s, SyncMessage hello)
    {
        var id = Identity;
        if (!s.AcceptHello(hello, id.NodeId, id.GroupId, id.GroupKey)) { s.Close(); return; }
        PeerSession? replaced = null;
        lock (_lock)
        {
            // A fresh hello from a node we already hold a session with replaces it: the old
            // one is nearly always stale (the peer restarted without a FIN). Same rule as LanLink.
            if (_sessions.TryGetValue(s.PeerNode!, out var old) && old != s) replaced = old;
            _sessions[s.PeerNode!] = s;
        }
        replaced?.Close();
        _dialFailLogged.TryRemove(s.PeerNode!, out _);
        if (s.Sealed)
        {
            _log?.Invoke($"Connected to {s.Describe()}; syncing.");
            // The digest is the first sealed frame each way; the peer's opening proves the key.
            _ = s.SendAsync(new SyncMessage { Type = MessageTypes.Digest, Digest = _store.Digest().ToList() });
        }
        else _log?.Invoke($"Connected to {s.Describe()}, which isn't linked to this device.");
        PeersChanged?.Invoke();
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
        lock (_lock) targets = _sessions.Values.Where(x => x != except && x.Sealed).ToList();
        var msg = new SyncMessage { Type = MessageTypes.Tasks, Tasks = tasks.ToList() };
        foreach (var t in targets) _ = t.SendAsync(msg);
    }

    // ---- Pairing ------------------------------------------------------------------------

    /// <summary>Asks the device <paramref name="node"/> to let this one join its group.
    /// Progress arrives through <see cref="PairProgressed"/>.</summary>
    public async Task RequestPairingAsync(string node)
    {
        PeerSession? s;
        lock (_lock) _sessions.TryGetValue(node, out s);
        if (s is null && _nearby.TryGetValue(node, out var seen))
            s = await DialAsync(seen.ep.Address.ToString(), seen.ep.Port, null, _cts.Token).ConfigureAwait(false);
        if (s is null)
        {
            PairProgressed?.Invoke(new PairProgress(node, node, PairStage.Failed, null, "That device can't be reached."));
            return;
        }
        // The session must have exchanged hellos before pairing messages may follow.
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
        s.PairInitiator = true;
        s.PairKey?.Dispose();
        s.PairKey = SyncCrypto.NewPairingKey();
        s.PairPublic = s.PairKey.ExportSubjectPublicKeyInfo();
        await s.SendAsync(new SyncMessage { Type = MessageTypes.PairRequest, PairKey = Convert.ToBase64String(s.PairPublic) }).ConfigureAwait(false);
    }

    /// <summary>On the accepting device: answer with our key, work out the code, ask the user.</summary>
    private void OnPairRequest(PeerSession s, SyncMessage m)
    {
        var theirs = Convert.FromBase64String(m.PairKey ?? "");
        s.PairKey?.Dispose();
        s.PairKey = SyncCrypto.NewPairingKey();
        var mine = s.PairKey.ExportSubjectPublicKeyInfo();
        var (secret, code) = SyncCrypto.PairingSecret(s.PairKey, theirs, requesterKey: theirs, accepterKey: mine);
        s.PairSecret = secret;
        s.PairCode = code;
        _ = s.SendAsync(new SyncMessage { Type = MessageTypes.PairChallenge, PairKey = Convert.ToBase64String(mine) });
        PairRequested?.Invoke(new PairPrompt
        {
            PeerNode = s.PeerNode!,
            PeerName = s.PeerName!,
            Code = code,
            Answer = accept => AnswerPairingAsync(s, accept),
        });
    }

    private async Task AnswerPairingAsync(PeerSession s, bool accept)
    {
        if (s.IsClosed || s.PairSecret is null) return;
        if (!accept)
        {
            await s.SendAsync(new SyncMessage { Type = MessageTypes.PairReject }).ConfigureAwait(false);
            return;
        }
        var id = Identity;
        var payload = JsonSerializer.SerializeToUtf8Bytes(new[] { id.GroupId, id.GroupKeyBase64 });
        var sealedGroup = SyncCrypto.Seal(s.PairSecret, 0, payload);
        await s.SendAsync(new SyncMessage { Type = MessageTypes.PairAccept, Sealed = Convert.ToBase64String(sealedGroup) }).ConfigureAwait(false);
        _log?.Invoke($"Linked {s.Describe()} to this device.");
    }

    /// <summary>On the requesting device: work out the same code and show it.</summary>
    private void OnPairChallenge(PeerSession s, SyncMessage m)
    {
        var theirs = Convert.FromBase64String(m.PairKey ?? "");
        var (secret, code) = SyncCrypto.PairingSecret(s.PairKey!, theirs, requesterKey: s.PairPublic!, accepterKey: theirs);
        s.PairSecret = secret;
        s.PairCode = code;
        PairProgressed?.Invoke(new PairProgress(s.PeerNode!, s.PeerName!, PairStage.CodeShown, code, null));
    }

    /// <summary>On the requesting device: adopt the group, then reconnect everything under it.</summary>
    private void OnPairAccept(PeerSession s, SyncMessage m)
    {
        if (s.PairSecret is null) throw new CryptographicException("Accept before challenge.");
        var plain = SyncCrypto.Open(s.PairSecret, 0, Convert.FromBase64String(m.Sealed ?? ""));
        var parts = JsonSerializer.Deserialize<string[]>(plain);
        if (parts is not [var groupId, var groupKey] || Convert.FromBase64String(groupKey).Length != 32)
            throw new CryptographicException("Malformed group.");
        var peerEp = s.RemoteEndPoint;
        var peerPort = s.PeerPort;
        SyncIdentity adopted;
        lock (_lock) adopted = _id = _id with { GroupId = groupId, GroupKeyBase64 = groupKey };
        IdentityChanged?.Invoke(adopted);
        PairProgressed?.Invoke(new PairProgress(s.PeerNode!, s.PeerName!, PairStage.Accepted, s.PairCode, null));
        _log?.Invoke($"Joined the sync group of {s.Describe()}.");
        ResetSessions();
        _discovery?.AnnounceNow();
        // Reconnect at once rather than waiting for discovery (or a remote link's backoff).
        if (s.RemoteAddress is null && peerEp is not null && peerPort is { } port)
            _ = DialAsync(peerEp.Address.ToString(), port, null, _cts.Token);
    }

    /// <summary>Leaves the current group for a new one of this device alone.</summary>
    public void LeaveGroup(string newGroupId, string newGroupKey)
    {
        SyncIdentity changed;
        lock (_lock) changed = _id = _id with { GroupId = newGroupId, GroupKeyBase64 = newGroupKey };
        IdentityChanged?.Invoke(changed);
        ResetSessions();
        _discovery?.AnnounceNow();
    }

    /// <summary>Closes every session; they reconnect under the current identity (remote
    /// links redial themselves; LAN peers through discovery).</summary>
    private void ResetSessions()
    {
        List<PeerSession> all;
        lock (_lock) all = _allSessions.ToList();
        foreach (var x in all) x.Close();
        PeersChanged?.Invoke();
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
