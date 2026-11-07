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
    /// <summary>How many other devices this one is linked with (the app's remembered
    /// peers). When two devices pair, the one with fewer joins the other's group.</summary>
    public Func<int>? LinkedDeviceCount { get; init; }
}

/// <summary>A device this node knows of, for the Devices screen.</summary>
/// <param name="Linked">In this node's sync group.</param>
/// <param name="Connected">A session to it is open (and, when linked, authenticated).</param>
/// <param name="Address">Where it was last seen.</param>
public sealed record PeerInfo(string NodeId, string Name, bool Linked, bool Connected, string? Address);

/// <summary>A pairing request awaiting the user's answer on the accepting device.</summary>
public sealed class PairPrompt
{
    internal Func<bool, Task> Answer { get; init; } = _ => Task.CompletedTask;
    public required string PeerNode { get; init; }
    public required string PeerName { get; init; }
    /// <summary>Must match the code the requesting device shows.</summary>
    public required string Code { get; init; }
    /// <summary>This device would join the requester's group, rather than the other way
    /// round, leaving <see cref="MyLinkedDevices"/> devices behind.</summary>
    public bool ThisDeviceJoins { get; init; }
    public int MyLinkedDevices { get; init; }
    public Task AcceptAsync() => Answer(true);
    public Task RejectAsync() => Answer(false);
}

public enum PairStage { CodeShown, Accepted, Rejected, Failed }

/// <summary>Progress of a pairing this node asked for.</summary>
/// <param name="ThisDeviceJoins">This device joins the other's group (when accepted).</param>
public sealed record PairProgress(string PeerNode, string PeerName, PairStage Stage, string? Code, string? Message,
    bool ThisDeviceJoins = true, int MyLinkedDevices = 0);

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
    /// <summary>A same-group peer that announces but never connects is dialled by the
    /// higher ID too after this long: the lower one's dials may be blocked by a firewall.</summary>
    private static readonly TimeSpan HigherIdFallback = TimeSpan.FromSeconds(10);
    /// <summary>Connections that haven't proved the group key, at most.</summary>
    private const int MaxUnprovenSessions = 32;

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
    private SyncIdentity _id;
    private TcpListener? _listener;
    private Discovery? _discovery;
    private int _pendingPrompts;
    private int _disposed;

    private sealed record Nearby(SyncMessage Msg, IPEndPoint Ep, DateTimeOffset Seen, DateTimeOffset FirstSeen);

    public event Action? PeersChanged;
    public event Action<PairPrompt>? PairRequested;
    public event Action<PairProgress>? PairProgressed;
    /// <summary>This node joined another group by pairing; the app must persist the new identity.</summary>
    public event Action<SyncIdentity>? IdentityChanged;
    /// <summary>(node, name): a linked device was in sync just now (its session
    /// authenticated, or closed after being in sync).</summary>
    public event Action<string, string>? PeerSeen;
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
        _listener = new TcpListener(_options.BindAddress, _options.Port);
        _listener.Start();
        ListenPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = AcceptLoopAsync(_cts.Token);
        _store.Changed += OnStoreChanged;
        if (_options.EnableDiscovery)
        {
            try
            {
                _discovery = new Discovery(_id.NodeId, _options.DiscoveryPort, () => Presence(MessageTypes.Announce),
                    _options.AcquireMulticastLock, _log);
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
                result[node] = new PeerInfo(node, Protocol.CleanName(n.Msg.Name, node[..8]), n.Msg.Group == group, false, n.Ep.Address.ToString());
        lock (_lock)
        {
            foreach (var (node, s) in _sessions)
            {
                bool linked = s.PeerGroup == group;
                result[node] = new PeerInfo(node, s.PeerName ?? node, linked, !linked || s.Authenticated,
                    s.RemoteAddress ?? s.RemoteEndPoint?.Address.ToString());
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

    // ---- Connections -------------------------------------------------------------------

    /// <summary>This node's announcement or hello: the same fields, different type.</summary>
    private SyncMessage Presence(string type)
    {
        var id = Identity;
        return new SyncMessage
        {
            Type = type, App = Protocol.App, Proto = Protocol.Version,
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
            int unproven;
            lock (_lock) unproven = _allSessions.Count(s => !s.Authenticated && !s.PairStarted);
            if (unproven >= MaxUnprovenSessions)
            {
                client.Dispose();
                continue;
            }
            await BeginSessionAsync(new PeerSession(client, isOutgoing: false)).ConfigureAwait(false);
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
            if (remoteAddress is not null)
                _remoteErrors[remoteAddress] = e is SocketException se ? $"can't connect: {se.Message}" : "can't connect: no answer";
            return null;
        }
        var session = new PeerSession(client, isOutgoing: true) { RemoteAddress = remoteAddress };
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
        lock (_lock)
        {
            _allSessions.Remove(s);
            wasCurrent = s.PeerNode is not null && _sessions.TryGetValue(s.PeerNode, out var cur) && cur == s && _sessions.Remove(s.PeerNode);
        }
        if (!wasCurrent) return;
        _log?.Invoke($"Disconnected from {s.Describe()}.");
        if (s.Authenticated) PeerSeen?.Invoke(s.PeerNode!, s.PeerName!);
        PeersChanged?.Invoke();
    }

    private void OnAnnounced(SyncMessage msg, IPAddress from)
    {
        var node = msg.Node!;
        var now = DateTimeOffset.UtcNow;
        var ep = new IPEndPoint(from, msg.Port!.Value);
        var first = _nearby.TryGetValue(node, out var old) ? old.FirstSeen : now;
        _nearby[node] = new Nearby(msg, ep, now, first);
        if (old is null) PeersChanged?.Invoke();

        // As in LanLink, the node with the lower ID dials, so two peers don't open two
        // connections to each other; but if nothing connects for a while (a firewall may be
        // refusing the lower one's dials), the higher one tries too. Only group members are
        // dialled; others wait for a pairing.
        var id = Identity;
        if (msg.Group != id.GroupId) return;
        lock (_lock) if (_sessions.ContainsKey(node)) { _nearby[node] = _nearby[node] with { FirstSeen = now }; return; }
        if (string.CompareOrdinal(id.NodeId, node) < 0 || now - first > HigherIdFallback)
            _ = DialNodeAsync(node, ep);
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
            bool alreadyLinked;
            lock (_lock) alreadyLinked = knownNode is not null && _sessions.TryGetValue(knownNode, out var cur) && cur.Authenticated;
            if (!alreadyLinked)
            {
                var s = await DialAsync(host, port, address, ct).ConfigureAwait(false);
                bool good = false;
                if (s is not null)
                {
                    var closed = new TaskCompletionSource();
                    s.Closed += _ => closed.TrySetResult();
                    if (s.IsClosed) closed.TrySetResult();
                    await Task.WhenAny(closed.Task, Task.Delay(Timeout.Infinite, ct)).ConfigureAwait(false);
                    knownNode = s.PeerNode ?? knownNode;
                    good = s.Authenticated || DateTimeOffset.UtcNow - s.Opened > TimeSpan.FromSeconds(30);
                    if (!good) _remoteErrors[address] = s.PeerNode is null ? "it answered but didn't say hello"
                        : s.Sealed ? $"{s.PeerName} doesn't have this group's key" : $"connected to {s.PeerName}, not linked";
                }
                if (good) { delay = first; failureLogged = false; }
                else
                {
                    if (!failureLogged) _log?.Invoke($"Remote link {address}: {_remoteErrors.GetValueOrDefault(address, "failed")}; will keep trying.");
                    failureLogged = true;
                    delay = TimeSpan.FromSeconds(Math.Min(60, Math.Max(delay.TotalSeconds * 2, first.TotalSeconds)));
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
                if (now - entry.Seen > PeerExpiry && _nearby.TryRemove(node, out _)) expired = true;
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
                    _ = s.SendAsync(new SyncMessage { Type = MessageTypes.DigestDone });
                    break;
                case MessageTypes.DigestDone when s.Authenticated:
                    s.FirstSyncDone = true;
                    if (!FirstSyncDone) { FirstSyncDone = true; FirstSyncCompleted?.Invoke(); }
                    break;
                case MessageTypes.Tasks when s.Authenticated:
                    var changed = _store.Merge(m.Tasks ?? []);
                    if (changed.Count > 0) Broadcast(changed, except: s);
                    break;
                case MessageTypes.PairRequest when !s.Sealed:
                    OnPairRequest(s, m);
                    break;
                case MessageTypes.PairChallenge when !s.Sealed && s.PairInitiator && s.PairPeerPublic is null:
                    OnPairChallenge(s, m);
                    break;
                case MessageTypes.PairReveal when !s.Sealed && !s.PairInitiator && s.PairCommit is not null && s.PairPeerPublic is null:
                    OnPairReveal(s, m);
                    break;
                case MessageTypes.PairAccept when !s.Sealed && s.PairInitiator && s.PairSecret is not null:
                    OnPairAccept(s, m);
                    break;
                case MessageTypes.PairGroup when !s.Sealed && !s.PairInitiator && s.PairSecret is not null && !s.RequesterJoins:
                    Adopt(s, m.Sealed);
                    break;
                case MessageTypes.PairReject when !s.Sealed:
                    if (s.PairInitiator)
                        PairProgressed?.Invoke(new PairProgress(s.PeerNode, s.PeerName!, PairStage.Rejected, s.PairCode,
                            m.Reason == "busy" ? "The other device is already busy linking." : "The other device said no."));
                    if (s.RemoteAddress is null) s.Close();
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
            s.Close();
        }
    }

    private void OnHello(PeerSession s, SyncMessage hello)
    {
        var id = Identity;
        if (!s.AcceptHello(hello, id.NodeId, id.GroupId, id.GroupKey)) { s.Close(); return; }
        bool installed;
        PeerSession? replaced = null;
        lock (_lock)
        {
            _sessions.TryGetValue(s.PeerNode!, out var old);
            // A fresh hello replaces a stale session (the peer restarted without a FIN), as in
            // LanLink, but never one that has proved the group key: a hello proves nothing, so
            // anyone could otherwise cut two linked devices apart by claiming one's ID. A
            // session claiming the group takes over only once it proves the key (OnAuthenticated).
            installed = old is null || old.IsClosed || (!old.Authenticated && !(old.Sealed && !s.Sealed));
            if (installed)
            {
                if (old is not null && old != s) replaced = old;
                _sessions[s.PeerNode!] = s;
            }
        }
        replaced?.Close();
        if (!installed && !s.Sealed)
        {
            // Not linked, yet claiming the ID of a device that is: an impostor, or a stale copy.
            s.Close();
            return;
        }
        _dialFailLogged.TryRemove(s.PeerNode!, out _);
        if (s.Sealed)
        {
            // The digest is the first sealed frame each way; its opening proves the key.
            _ = s.SendAsync(new SyncMessage { Type = MessageTypes.Digest, Digest = _store.Digest().ToList() });
        }
        else _log?.Invoke($"Connected to {s.Describe()}, which isn't linked to this device.");
        if (installed) PeersChanged?.Invoke();
    }

    private void OnAuthenticated(PeerSession s)
    {
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
        PeerSeen?.Invoke(s.PeerNode!, s.PeerName!);
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
        lock (_lock) targets = _sessions.Values.Where(x => x != except && x.Authenticated).ToList();
        var msg = new SyncMessage { Type = MessageTypes.Tasks, Tasks = tasks.ToList() };
        foreach (var t in targets) _ = t.SendAsync(msg);
    }

    // ---- Pairing ------------------------------------------------------------------------
    //
    // requester R                         accepter A
    //   pair_request {commit = H(R.pub), members} →
    //                                     ← pair_challenge {A.pub, members}
    //   pair_reveal {R.pub} →             (checks H(R.pub) = commit)
    //   shows the code                    shows the code, asks the user
    //                                     ← pair_accept {sealed: A's group}   when R joins
    //                                     ← pair_accept {}                    when A joins
    //   pair_group {sealed: R's group} →  (when A joins)
    //
    // R commits to its key before seeing A's, and A reveals its key before seeing R's, so
    // nobody in the middle can choose a key to make the two codes agree. The device linked
    // with fewer others joins, so linking a new device from an established one brings the
    // new one in rather than pulling the established one out of its group.

    private int LinkedCount() => Math.Max(0, _options.LinkedDeviceCount?.Invoke() ?? 0);

    /// <summary>Starts pairing with the device <paramref name="node"/>. Progress arrives
    /// through <see cref="PairProgressed"/>.</summary>
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
        if (s.PairStarted)
        {
            PairProgressed?.Invoke(new PairProgress(node, s.PeerName!, PairStage.Failed, null, "Linking with it is already under way."));
            return;
        }
        s.PairStarted = true;
        s.PairInitiator = true;
        s.MyMembers = LinkedCount();
        s.PairKey = SyncCrypto.NewPairingKey();
        s.PairPublic = s.PairKey.ExportSubjectPublicKeyInfo();
        await s.SendAsync(new SyncMessage
        {
            Type = MessageTypes.PairRequest,
            Commit = Convert.ToBase64String(SyncCrypto.Commitment(s.PairPublic)),
            Members = s.MyMembers,
        }).ConfigureAwait(false);
    }

    /// <summary>Accepter: answer with our key. One pairing per session, and one prompt at a
    /// time, so nobody can flood the screen with requests.</summary>
    private void OnPairRequest(PeerSession s, SyncMessage m)
    {
        var commit = Convert.FromBase64String(m.Commit ?? "");
        if (s.PairStarted || Volatile.Read(ref _pendingPrompts) > 0 || commit.Length != 32)
        {
            _ = s.SendAsync(new SyncMessage { Type = MessageTypes.PairReject, Reason = "busy" });
            return;
        }
        s.PairStarted = true;
        s.PairCommit = commit;
        s.PeerMembers = Math.Clamp(m.Members ?? 0, 0, 10_000);
        s.MyMembers = LinkedCount();
        s.PairKey = SyncCrypto.NewPairingKey();
        s.PairPublic = s.PairKey.ExportSubjectPublicKeyInfo();
        _ = s.SendAsync(new SyncMessage { Type = MessageTypes.PairChallenge, PairKey = Convert.ToBase64String(s.PairPublic), Members = s.MyMembers });
    }

    /// <summary>Requester: reveal our key, work out the code, show it.</summary>
    private void OnPairChallenge(PeerSession s, SyncMessage m)
    {
        var theirs = Convert.FromBase64String(m.PairKey ?? "");
        s.PairPeerPublic = theirs;
        s.PeerMembers = Math.Clamp(m.Members ?? 0, 0, 10_000);
        (s.PairSecret, s.PairCode) = SyncCrypto.PairingSecret(s.PairKey!, theirs, requesterKey: s.PairPublic!, accepterKey: theirs);
        s.RequesterJoins = s.MyMembers <= s.PeerMembers;
        _ = s.SendAsync(new SyncMessage { Type = MessageTypes.PairReveal, PairKey = Convert.ToBase64String(s.PairPublic!) });
        PairProgressed?.Invoke(new PairProgress(s.PeerNode!, s.PeerName!, PairStage.CodeShown, s.PairCode, null,
            ThisDeviceJoins: s.RequesterJoins, MyLinkedDevices: s.MyMembers));
    }

    /// <summary>Accepter: check the revealed key against the commitment, work out the code,
    /// and ask the user.</summary>
    private void OnPairReveal(PeerSession s, SyncMessage m)
    {
        var theirs = Convert.FromBase64String(m.PairKey ?? "");
        if (!CryptographicOperations.FixedTimeEquals(SyncCrypto.Commitment(theirs), s.PairCommit))
            throw new CryptographicException("The revealed pairing key doesn't match its commitment.");
        s.PairPeerPublic = theirs;
        (s.PairSecret, s.PairCode) = SyncCrypto.PairingSecret(s.PairKey!, theirs, requesterKey: theirs, accepterKey: s.PairPublic!);
        s.RequesterJoins = s.PeerMembers <= s.MyMembers;
        Interlocked.Increment(ref _pendingPrompts);
        int answered = 0;
        PairRequested?.Invoke(new PairPrompt
        {
            PeerNode = s.PeerNode!,
            PeerName = s.PeerName!,
            Code = s.PairCode,
            ThisDeviceJoins = !s.RequesterJoins,
            MyLinkedDevices = s.MyMembers,
            Answer = accept =>
            {
                if (Interlocked.Exchange(ref answered, 1) != 0) return Task.CompletedTask;
                Interlocked.Decrement(ref _pendingPrompts);
                return AnswerPairingAsync(s, accept);
            },
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
        if (s.RequesterJoins)
        {
            await s.SendAsync(new SyncMessage { Type = MessageTypes.PairAccept, Sealed = SealGroup(s) }).ConfigureAwait(false);
            _log?.Invoke($"Linked {s.Describe()} to this device.");
        }
        else await s.SendAsync(new SyncMessage { Type = MessageTypes.PairAccept }).ConfigureAwait(false);
    }

    /// <summary>Requester: join the accepter's group, or send ours for it to join.</summary>
    private void OnPairAccept(PeerSession s, SyncMessage m)
    {
        if (s.RequesterJoins)
        {
            var (node, name, code) = (s.PeerNode!, s.PeerName!, s.PairCode);
            Adopt(s, m.Sealed);
            PairProgressed?.Invoke(new PairProgress(node, name, PairStage.Accepted, code, null));
            return;
        }
        _ = s.SendAsync(new SyncMessage { Type = MessageTypes.PairGroup, Sealed = SealGroup(s) });
        _log?.Invoke($"{s.Describe()} joins this device's sync group.");
        PairProgressed?.Invoke(new PairProgress(s.PeerNode!, s.PeerName!, PairStage.Accepted, s.PairCode, null, ThisDeviceJoins: false));
    }

    private string SealGroup(PeerSession s)
    {
        var id = Identity;
        var payload = JsonSerializer.SerializeToUtf8Bytes(new[] { id.GroupId, id.GroupKeyBase64 });
        return Convert.ToBase64String(SyncCrypto.Seal(s.PairSecret!, 0, payload));
    }

    /// <summary>Joins the group sealed in <paramref name="sealedGroup"/>, then reconnects
    /// everything under it.</summary>
    private void Adopt(PeerSession s, string? sealedGroup)
    {
        var plain = SyncCrypto.Open(s.PairSecret!, 0, Convert.FromBase64String(sealedGroup ?? ""));
        var parts = JsonSerializer.Deserialize<string[]>(plain);
        if (parts is not [var groupId, var groupKey] || !Protocol.IsValidNodeId(groupId) || Convert.FromBase64String(groupKey).Length != 32)
            throw new CryptographicException("Malformed group.");
        var peerEp = s.RemoteEndPoint;
        var peerPort = s.PeerPort;
        var remote = s.RemoteAddress;
        SyncIdentity adopted;
        lock (_lock) adopted = _id = _id with { GroupId = groupId, GroupKeyBase64 = groupKey };
        IdentityChanged?.Invoke(adopted);
        _log?.Invoke($"Joined the sync group of {s.Describe()}.");
        ResetSessions();
        _discovery?.AnnounceNow();
        // Reconnect at once rather than waiting for discovery (or a remote link's backoff).
        if (remote is null && peerEp is not null && peerPort is { } port)
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
