using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using RemindMe.Sync;

namespace RemindMe.Tests;

/// <summary>Real engines talking over loopback TCP (discovery off; links through remote addresses).</summary>
public sealed class SyncTests : IDisposable
{
    private readonly List<SyncEngine> _engines = [];

    private static SyncIdentity NewIdentity(string name, string? group = null, string? key = null) =>
        new(Guid.NewGuid().ToString("N"), name, group ?? Guid.NewGuid().ToString("N"),
            key ?? Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));

    private (SyncEngine engine, TaskStore store) Node(SyncIdentity id, params string[] remotes) =>
        Node(id, 0, null, remotes);

    private (SyncEngine engine, TaskStore store) Node(SyncIdentity id, int linkedDevices, List<string>? log, params string[] remotes) =>
        Node(id, linkedDevices, log, TimeSpan.FromMilliseconds(200), remotes);

    private (SyncEngine engine, TaskStore store) Node(SyncIdentity id, int linkedDevices, List<string>? log,
        TimeSpan reconnectDelay, params string[] remotes) => Node(id, linkedDevices, log, reconnectDelay, IPAddress.Loopback, remotes);

    private (SyncEngine engine, TaskStore store) Node(SyncIdentity id, int linkedDevices, List<string>? log,
        TimeSpan reconnectDelay, IPAddress bind, params string[] remotes) =>
        Node(id, Options(linkedDevices, reconnectDelay, bind, remotes), log);

    private (SyncEngine engine, TaskStore store) Node(SyncIdentity id, SyncOptions options, List<string>? log = null)
    {
        var store = new TaskStore(null, id.NodeId, SystemClock.Instance);
        var engine = new SyncEngine(store, id, options, log is null ? null : line => { lock (log) log.Add(line); });
        engine.Start();
        _engines.Add(engine);
        return (engine, store);
    }

    private static SyncOptions Options(int linkedDevices, TimeSpan reconnectDelay, IPAddress bind, params string[] remotes) => new()
    {
        Port = 0,
        BindAddress = bind,
        EnableDiscovery = false,
        RemoteAddresses = remotes,
        ReconnectDelay = reconnectDelay,
        KnownPeers = LinkedTo(linkedDevices),
    };

    /// <summary><paramref name="n"/> made-up linked devices, with no address (none is redialled).</summary>
    private static Func<IReadOnlyList<KnownPeer>> LinkedTo(int n)
    {
        var peers = Enumerable.Range(0, n).Select(i => new KnownPeer(Guid.NewGuid().ToString("N"), $"Linked {i}", DateTimeOffset.UtcNow)).ToList();
        return () => peers;
    }

    private static string Addr(SyncEngine e) => $"127.0.0.1:{e.ListenPort}";

    /// <summary>This device's user accepts every pairing (after comparing the codes).</summary>
    private static void AutoAccept(SyncEngine e) => e.PairRequested += p => _ = p.AcceptAsync();

    /// <summary>A hand-driven protocol client, to play a hostile device.</summary>
    private sealed class RawPeer : IDisposable
    {
        private readonly TcpClient _c;
        private readonly NetworkStream _s;
        public string Node { get; } = Guid.NewGuid().ToString("N");

        private RawPeer(TcpClient c) { _c = c; _s = c.GetStream(); }

        public static async Task<RawPeer> ConnectAsync(SyncEngine to)
        {
            var c = new TcpClient();
            await c.ConnectAsync(IPAddress.Loopback, to.ListenPort);
            return new RawPeer(c);
        }

        public Task SendAsync(SyncMessage m) => Protocol.WriteFrameAsync(_s, Protocol.Serialize(m), default);

        /// <returns>The next message, or null when the other side closed the connection.</returns>
        public async Task<SyncMessage?> ReceiveAsync()
        {
            using var t = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                var f = await Protocol.ReadFrameAsync(_s, Protocol.MaxFrame, t.Token);
                return f is null ? null : Protocol.Deserialize(f);
            }
            catch (IOException) { return null; }
        }

        public Task HelloAsync(string group, int proto = Protocol.Version) => SendAsync(new SyncMessage
        {
            Type = MessageTypes.Hello, App = Protocol.App, Proto = proto, Node = Node, Name = "Evil",
            Group = group, Nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)), Port = 1,
        });

        public void Dispose() => _c.Dispose();
    }

    private static async Task WaitFor(Func<bool> condition, string what, int timeoutMs = 10_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail($"Timed out waiting for: {what}");
            await Task.Delay(50);
        }
    }

    private static AlarmTask Add(TaskStore s, string description) =>
        s.Save(null, new TaskDefinition { Description = description, Kind = ScheduleKind.Weekly, Days = 0b0111110 });

    [Fact]
    public async Task SameGroupSyncsBothWaysAndKeepsSyncingLaterChanges()
    {
        var a = NewIdentity("A");
        var (ea, sa) = Node(a);
        Add(sa, "from A");
        var (_, sb) = Node(NewIdentity("B", a.GroupId, a.GroupKeyBase64), Addr(ea));
        var fromB = Add(sb, "from B");
        await WaitFor(() => sa.Live().Count == 2 && sb.Live().Count == 2, "both tasks on both nodes");

        sa.Save(fromB.Id, fromB.Def with { Description = "edited on A" });
        await WaitFor(() => sb.Get(fromB.Id)!.Def.Description == "edited on A", "the edit reaching B");
    }

    [Fact]
    public async Task UpdatesCrossABridge()
    {
        // A and C each link only to B, as two LANs joined through one machine.
        var a = NewIdentity("A");
        var (eb, sb) = Node(NewIdentity("B", a.GroupId, a.GroupKeyBase64));
        var (_, sa) = Node(a, Addr(eb));
        var (_, sc) = Node(NewIdentity("C", a.GroupId, a.GroupKeyBase64), Addr(eb));
        var t = Add(sa, "made on A");
        await WaitFor(() => sc.Get(t.Id) is not null, "A's task reaching C through B");
        sc.Delete(t.Id);
        await WaitFor(() => sa.Get(t.Id)!.Def.Deleted, "C's deletion reaching A through B");
        Assert.True(sb.Get(t.Id)!.Def.Deleted);
    }

    [Fact]
    public async Task AWrongSecretGetsNothingAndChangesNothing()
    {
        var a = NewIdentity("A");
        var logA = new List<string>();
        var (ea, sa) = Node(a, 0, logA);
        Add(sa, "secret task");
        // Claims A's group but doesn't know its key.
        var (_, sm) = Node(NewIdentity("Mallory", a.GroupId), Addr(ea));
        Add(sm, "planted task");
        // The connection was made, and dropped when Mallory's first sealed frame didn't open.
        await WaitFor(() => { lock (logA) return logA.Any(l => l.StartsWith("Disconnected from Mallory")); }, "A dropping Mallory");
        Assert.DoesNotContain(sm.Live(), t => t.Def.Description == "secret task");
        Assert.DoesNotContain(sa.Live(), t => t.Def.Description == "planted task");
    }

    [Fact]
    public async Task AnImpostorCanNotDisplaceALinkedDevice()
    {
        var a = NewIdentity("A");
        var b = NewIdentity("B", a.GroupId, a.GroupKeyBase64);
        var logA = new List<string>();
        var (ea, sa) = Node(a, 0, logA);
        var (eb, sb) = Node(b, Addr(ea));
        await WaitFor(() => ea.Peers().Any(p => p.NodeId == b.NodeId && p.Connected), "A and B in sync");

        // Mallory says hello as B, in A's group, without the key; then says nothing more.
        var (_, _) = Node(b with { Name = "Mallory", GroupKeyBase64 = NewIdentity("x").GroupKeyBase64 }, Addr(ea));
        await Task.Delay(1000);
        var t = Add(sb, "after the impostor");
        await WaitFor(() => sa.Get(t.Id) is not null, "B's change still reaching A");
        Assert.Equal("B", ea.Peers().Single(p => p.NodeId == b.NodeId).Name);
        // The linked session was never interrupted. (Here B would redial at once over its
        // remote link; on a LAN, where only the lower ID dials, nobody would.)
        lock (logA) Assert.Single(logA, l => l.StartsWith("In sync with B "));
    }

    [Fact]
    public async Task TheDeviceLinkedWithFewerJoinsTheOther()
    {
        // B is already linked with two devices; A, alone, asks to link with it.
        var a = NewIdentity("A");
        var b = NewIdentity("B");
        var (ea, _) = Node(a, 0, null);
        var (eb, _) = Node(b, 2, null, Addr(ea));
        PairPrompt? prompt = null;
        eb.PairRequested += p => prompt = p;
        AutoAccept(ea);
        SyncIdentity? aAdopted = null;
        ea.IdentityChanged += id => aAdopted = id;
        await WaitFor(() => ea.Peers().Any(p => p.NodeId == b.NodeId), "A sees B");
        await ea.RequestPairingAsync(b.NodeId);
        await WaitFor(() => prompt is not null, "B asked");
        Assert.False(prompt!.ThisDeviceJoins);
        await prompt.AcceptAsync();
        await WaitFor(() => aAdopted is not null, "A joining B's group");
        Assert.Equal(b.GroupId, aAdopted!.GroupId);
        Assert.Equal(b.GroupId, eb.Identity.GroupId);

        // And the other way round: a device with linked devices asking a lone one brings it in.
        var c = NewIdentity("C");
        var (ec, _) = Node(c, 0, null);
        var (ed, _) = Node(NewIdentity("D"), 3, null, Addr(ec));
        PairPrompt? prompt2 = null;
        ec.PairRequested += p => prompt2 = p;
        AutoAccept(ed);
        await WaitFor(() => ed.Peers().Any(p => p.NodeId == c.NodeId), "D sees C");
        var dGroup = ed.Identity.GroupId;
        await ed.RequestPairingAsync(c.NodeId);
        await WaitFor(() => prompt2 is not null, "C asked");
        Assert.True(prompt2!.ThisDeviceJoins);
        await prompt2.AcceptAsync();
        await WaitFor(() => ec.Identity.GroupId == dGroup, "C joining D's group");
        Assert.Equal(dGroup, ed.Identity.GroupId);
    }

    [Fact]
    public async Task ARemoteLinkReconnectsAtOnceAfterPairing()
    {
        // D dials C over a remote link with a long backoff; C, alone, joins D's group. D
        // listens on 127.0.0.2 while its connections come from 127.0.0.1, so C can't dial
        // back (as behind the emulator's NAT): only D's link can reconnect them.
        var (ec, sc) = Node(NewIdentity("C"), 0, null);
        var (ed, sd) = Node(NewIdentity("D"), 2, null, TimeSpan.FromSeconds(30), IPAddress.Parse("127.0.0.2"), Addr(ec));
        PairPrompt? prompt = null;
        ec.PairRequested += p => prompt = p;
        AutoAccept(ed);
        await WaitFor(() => ed.Peers().Any(p => p.Name == "C"), "D connected to C");
        await ed.RequestPairingAsync(ec.Identity.NodeId);
        await WaitFor(() => prompt is not null, "C asked");
        await prompt!.AcceptAsync();
        var t = Add(sd, "after linking");
        // Well inside the 30 s backoff: the link redialled as soon as the pairing closed it.
        await WaitFor(() => sc.Get(t.Id) is not null, "D's task reaching C", timeoutMs: 8000);
    }

    [Fact]
    public async Task NoDeviceCanBePulledIntoAGroupWithoutItsUsersYes()
    {
        var a = NewIdentity("A");
        var (ea, _) = Node(a, 2, null);
        PairPrompt? prompt = null;
        ea.PairRequested += p => prompt = p;
        using var evil = await RawPeer.ConnectAsync(ea);
        var evilGroup = Guid.NewGuid().ToString("N");
        var evilKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        await evil.HelloAsync(evilGroup);
        Assert.Equal(MessageTypes.Hello, (await evil.ReceiveAsync())!.Type);

        // Claim to be linked with many devices, so that A is the one to join.
        using var key = SyncCrypto.NewPairingKey();
        var pub = key.ExportSubjectPublicKeyInfo();
        var round = Convert.ToBase64String(SyncCrypto.Commitment(pub, 10_000));
        await evil.SendAsync(new SyncMessage { Type = MessageTypes.PairRequest, Commit = round, Members = 10_000 });
        var challenge = (await evil.ReceiveAsync())!;
        var theirs = Convert.FromBase64String(challenge.PairKey!);
        var (secret, _) = SyncCrypto.PairingSecret(key, theirs, pub, theirs, 10_000, challenge.Members!.Value);
        await evil.SendAsync(new SyncMessage { Type = MessageTypes.PairReveal, Round = round, PairKey = Convert.ToBase64String(pub) });
        await WaitFor(() => prompt is not null, "A asked");
        Assert.True(prompt!.ThisDeviceJoins);

        // Push a group before A's user has answered.
        var sealedGroup = SyncCrypto.Seal(secret, SyncCrypto.RequesterGroupCounter,
            System.Text.Json.JsonSerializer.SerializeToUtf8Bytes([evilGroup, evilKey], Json.Wire.StringArray));
        await evil.SendAsync(new SyncMessage { Type = MessageTypes.PairGroup, Round = round, Sealed = Convert.ToBase64String(sealedGroup) });
        Assert.Null(await evil.ReceiveAsync());   // A hangs up on it
        Assert.Equal(a.GroupId, ea.Identity.GroupId);
    }

    [Fact]
    public async Task ADigestLargerThanThePreAuthenticationLimitStillSyncs()
    {
        var a = NewIdentity("A");
        var (ea, sa) = Node(a);
        for (int i = 0; i < 400; i++)
        {
            var t = Add(sa, $"task {i}");
            if (i % 2 == 0) sa.Delete(t.Id);   // tombstones count too
        }
        var (_, sb) = Node(NewIdentity("B", a.GroupId, a.GroupKeyBase64), Addr(ea));
        await WaitFor(() => sb.All().Count == 400, "all 400 records reaching B");
    }

    [Fact]
    public async Task LeavingAGroupDoesNotRememberItsDevicesAgain()
    {
        var a = NewIdentity("A");
        var (ea, _) = Node(a);
        var (eb, _) = Node(NewIdentity("B", a.GroupId, a.GroupKeyBase64), Addr(ea));
        await WaitFor(() => ea.Peers().Any(p => p.Connected && p.Linked), "A and B in sync");
        var seen = new List<string>();
        ea.PeerSeen += (_, name, _) => { lock (seen) seen.Add(name); };
        ea.LeaveGroup(Guid.NewGuid().ToString("N"), Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        await Task.Delay(500);
        lock (seen) Assert.Empty(seen);
    }

    /// <summary>Two identities of one group, the first with the lower node ID (the one that dials).</summary>
    private static (SyncIdentity lower, SyncIdentity higher) OrderedPair()
    {
        var a = NewIdentity("Lower");
        var b = NewIdentity("Higher", a.GroupId, a.GroupKeyBase64);
        if (string.CompareOrdinal(a.NodeId, b.NodeId) > 0) (a, b) = (b with { Name = "Lower" }, a with { Name = "Higher" });
        return (a, b);
    }

    /// <summary>Options for a node that remembers <paramref name="known"/> (discovery off, no
    /// remote addresses: a redial is the only way it can connect).</summary>
    private static SyncOptions Remembering(params KnownPeer[] known) =>
        Options(0, TimeSpan.FromMilliseconds(200), IPAddress.Loopback) with
        {
            KnownPeers = () => known,
            HousekeepingInterval = TimeSpan.FromMilliseconds(100),
            HigherIdFallback = TimeSpan.FromMilliseconds(500),
        };

    private static KnownPeer Known(SyncIdentity who, string address) => new(who.NodeId, who.Name, DateTimeOffset.UtcNow, address);

    [Fact]
    public async Task EachSideRemembersTheOthersListenerAddress()
    {
        var a = NewIdentity("A");
        var (ea, _) = Node(a);
        var aSaw = new List<string?>();
        ea.PeerSeen += (_, _, address) => { lock (aSaw) aSaw.Add(address); };
        var b = NewIdentity("B", a.GroupId, a.GroupKeyBase64);
        var store = new TaskStore(null, b.NodeId, SystemClock.Instance);
        var eb = new SyncEngine(store, b, Options(0, TimeSpan.FromMilliseconds(200), IPAddress.Loopback, Addr(ea)));
        _engines.Add(eb);
        var bSaw = new List<string?>();
        eb.PeerSeen += (_, _, address) => { lock (bSaw) bSaw.Add(address); };
        eb.Start();
        // B dialled A: where A answered. A was dialled from an ephemeral port: it probes B's
        // address with the port B's hello gave, and remembers it once B proves the key there.
        await WaitFor(() => { lock (bSaw) return bSaw.Count > 0; }, "B in sync with A");
        lock (bSaw) Assert.Equal(Addr(ea), bSaw[0]);
        await WaitFor(() => { lock (aSaw) return aSaw.Contains(Addr(eb)); }, "A remembering B's listener");
        lock (aSaw) Assert.Null(aSaw[0]);   // the inbound session's own: nothing until proved
    }

    [Fact]
    public async Task ALinkedDeviceNotHeardAnnouncingIsDialledAtItsLastAddress()
    {
        var (lower, higher) = OrderedPair();
        var (eh, sh) = Node(higher, Remembering());
        var (_, sl) = Node(lower, Remembering(Known(higher, Addr(eh))));
        var t = Add(sh, "from the higher");
        await WaitFor(() => sl.Get(t.Id) is not null, "the lower dialling the higher, and syncing");
    }

    [Fact]
    public async Task TheHigherIdRedialsTooWhenTheLowerDoesNot()
    {
        // The lower one's remembered address may be stale (a phone given a new one by DHCP).
        var (lower, higher) = OrderedPair();
        var (el, sl) = Node(lower, Remembering());
        var (_, sh) = Node(higher, Remembering(Known(lower, Addr(el))));
        var t = Add(sl, "from the lower");
        await WaitFor(() => sh.Get(t.Id) is not null, "the higher dialling the lower after the fallback, and syncing");
    }

    private static bool Logged(List<string> log, string start) { lock (log) return log.Any(l => l.StartsWith(start)); }
    private static int CountLogged(List<string> log, string start) { lock (log) return log.Count(l => l.StartsWith(start)); }

    [Fact]
    public async Task ARedialAnsweredByAStrangerHangsUpAndIsNotRepeated()
    {
        // The remembered device's address now belongs to a device of another group.
        var (lower, higher) = OrderedPair();
        var logC = new List<string>();
        var logL = new List<string>();
        var (ec, _) = Node(NewIdentity("Stranger"), Options(0, TimeSpan.FromMilliseconds(200), IPAddress.Loopback), logC);
        var (el, _) = Node(lower, Remembering(Known(higher, Addr(ec))), logL);
        await WaitFor(() => Logged(logC, "Connected to Lower"), "the lower dialling the stranger");
        await Task.Delay(1500);   // several of the 200 ms backoff's dials, were it still dialling
        Assert.Equal(1, CountLogged(logC, "Connected to Lower"));
        Assert.DoesNotContain(el.Peers(), p => p.NodeId == ec.Identity.NodeId);
        Assert.Equal(1, CountLogged(logL, "Higher's last address, 127.0.0.1:"));   // and why, once
    }

    [Fact]
    public async Task ARedialAnsweredByAnotherDeviceOfTheGroupKeepsThatConnection()
    {
        // The remembered device's address now belongs to another device of the same group: a
        // connection worth keeping (it may have no other way to be made), but not redialled.
        var (lower, higher) = OrderedPair();
        var logC = new List<string>();
        var (ec, _) = Node(NewIdentity("Other", lower.GroupId, lower.GroupKeyBase64), Options(0, TimeSpan.FromMilliseconds(200), IPAddress.Loopback), logC);
        var (el, _) = Node(lower, Remembering(Known(higher, Addr(ec))));
        await WaitFor(() => el.Peers().Any(p => p.NodeId == ec.Identity.NodeId && p.Connected && p.Linked), "the lower in sync with the other device");
        await Task.Delay(1500);   // several of the 200 ms backoff's dials, were it still dialling
        Assert.Equal(1, CountLogged(logC, "In sync with Lower"));
        Assert.Equal(0, CountLogged(logC, "Disconnected from Lower"));
    }

    [Fact]
    public async Task ARedialledDeviceThatLeftTheGroupIsNotDialledAgain()
    {
        var (lower, higher) = OrderedPair();
        var logH = new List<string>();
        var logL = new List<string>();
        var left = higher with { GroupId = Guid.NewGuid().ToString("N"), GroupKeyBase64 = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) };
        var (eh, _) = Node(left, Options(0, TimeSpan.FromMilliseconds(200), IPAddress.Loopback), logH);
        var (el, _) = Node(lower, Remembering(Known(higher, Addr(eh))), logL);
        await WaitFor(() => Logged(logH, "Connected to Lower"), "the lower dialling the device that left");
        await Task.Delay(1500);   // several of the 200 ms backoff's dials, were it still dialling
        Assert.Equal(1, CountLogged(logH, "Connected to Lower"));
        Assert.DoesNotContain(el.Peers(), p => p.NodeId == higher.NodeId && p.Connected);
        Assert.Equal(1, CountLogged(logL, "Higher answered at"));
    }

    [Fact]
    public async Task ARedialThatReachesThisDeviceItselfIsNotRepeated()
    {
        // The remembered device's address is now this device's own (a DHCP lease moved).
        var (lower, higher) = OrderedPair();
        var logL = new List<string>();
        IReadOnlyList<KnownPeer> known = [];
        var (el, _) = Node(lower, Remembering() with { KnownPeers = () => known }, logL);
        known = [Known(higher, Addr(el))];
        await WaitFor(() => Logged(logL, "Higher's last address"), "the lower giving up on its own address");
        await Task.Delay(1500);
        Assert.Equal(1, CountLogged(logL, "Higher's last address"));
        lock (logL) Assert.Contains("now answers as this device", logL.First(l => l.StartsWith("Higher's last address")));
    }

    /// <summary>
    /// As behind a NAT: B listens on 127.0.0.2, but its connection to A comes from 127.0.0.1,
    /// where C (<paramref name="cInGroup"/>: a device of the group, or a stranger) listens on
    /// B's port. A's probe of that source reaches C, so nothing is remembered for B; B's
    /// session is undisturbed, and C doesn't take the probe for a session either.
    /// </summary>
    private async Task ProbeReachingAnotherDevice(bool cInGroup)
    {
        var a = NewIdentity("A");
        var b = NewIdentity("B", a.GroupId, a.GroupKeyBase64);
        var c = cInGroup ? NewIdentity("C", a.GroupId, a.GroupKeyBase64) : NewIdentity("C");
        var logA = new List<string>();
        var (ea, _) = Node(a, Options(0, TimeSpan.FromMilliseconds(200), IPAddress.Loopback) with
        {
            KnownPeers = () => [new KnownPeer(b.NodeId, "B", DateTimeOffset.UtcNow)],   // remembered, with no address yet
        }, logA);
        var aSaw = new List<string?>();
        ea.PeerSeen += (_, _, address) => { lock (aSaw) aSaw.Add(address); };
        var logC = new List<string>();
        var (ec, _) = Node(c, Options(0, TimeSpan.FromMilliseconds(200), IPAddress.Loopback), logC);   // first: listening before any probe
        var logB = new List<string>();
        var (eb, _) = Node(b, Options(0, TimeSpan.FromMilliseconds(200), IPAddress.Parse("127.0.0.2"), Addr(ea)) with { Port = ec.ListenPort }, logB);
        await WaitFor(() => Logged(logA, "B came in from 127.0.0.1:"), "A's probe reaching C");
        await Task.Delay(500);
        lock (aSaw) Assert.All(aSaw, Assert.Null);
        Assert.Equal(1, CountLogged(logB, "In sync with A"));
        Assert.Equal(0, CountLogged(logB, "Disconnected from A"));
        Assert.Equal(0, CountLogged(logC, "Disconnected from A"));
        Assert.DoesNotContain(ec.Peers(), p => p.NodeId == a.NodeId);
    }

    [Fact]
    public Task AnInboundSourceLeadingToAStrangerIsNotRemembered() => ProbeReachingAnotherDevice(cInGroup: false);

    [Fact]
    public Task AnInboundSourceLeadingToAnotherDeviceOfTheGroupIsNotRemembered() => ProbeReachingAnotherDevice(cInGroup: true);

    [Fact]
    public async Task AStaleAddressStaysUndialledAfterTheDeviceComesAndGoes()
    {
        // B's old address is a stranger's now, and B can't be reached at its new one (only it
        // can dial out): the stranger is dialled once, not again each time B leaves.
        var (a, b) = OrderedPair();
        var logC = new List<string>();
        var (ec, _) = Node(NewIdentity("Stranger"), Options(0, TimeSpan.FromMilliseconds(200), IPAddress.Loopback), logC);
        var logA = new List<string>();
        var (ea, _) = Node(a, Remembering(Known(b, Addr(ec))), logA);
        await WaitFor(() => Logged(logA, "Higher's last address"), "A finding B's address taken");
        // B listens where A's probe can't reach it (127.0.0.2), and dials A from 127.0.0.1.
        var (eb, _) = Node(b, Options(0, TimeSpan.FromMilliseconds(200), IPAddress.Parse("127.0.0.2"), Addr(ea)));
        await WaitFor(() => ea.Peers().Any(p => p.NodeId == b.NodeId && p.Connected), "B in sync with A");
        eb.Dispose();
        await WaitFor(() => !ea.Peers().Any(p => p.NodeId == b.NodeId && p.Connected), "B gone");
        await Task.Delay(1500);   // several of the 200 ms backoff's dials, were it dialling
        Assert.Equal(1, CountLogged(logC, "Connected to Lower"));
    }

    [Fact]
    public async Task AnAddressOffEveryNetworkIsDialledAsSoonAsItIsBack()
    {
        // Away from home, the wait between redials mustn't grow: back home, the device is
        // dialled within a tick.
        var (lower, higher) = OrderedPair();
        var (eh, _) = Node(higher, Remembering());
        int reachable = 0;
        var (el, _) = Node(lower, Remembering(Known(higher, Addr(eh))) with { IsReachable = _ => Volatile.Read(ref reachable) == 1 });
        await Task.Delay(3300);   // were the wait doubling from 200 ms all along, the next dial would be ~3 s away
        Assert.DoesNotContain(el.Peers(), p => p.NodeId == higher.NodeId && p.Connected);
        Volatile.Write(ref reachable, 1);
        var back = DateTime.UtcNow;
        await WaitFor(() => el.Peers().Any(p => p.NodeId == higher.NodeId && p.Connected), "the lower dialling the higher");
        Assert.True(DateTime.UtcNow - back < TimeSpan.FromSeconds(1.5), $"took {DateTime.UtcNow - back}");
    }

    [Fact]
    public async Task ADeviceThatComesInFromANewAddressIsRememberedThere()
    {
        // B's remembered address is dead (it moved); B dials A from where it is now. A probes
        // that address, B proves the key there, and it is remembered, without disturbing the
        // session B made (the probe never sends a ready of its own).
        var (a, b) = OrderedPair();
        var (ea, _) = Node(a, Remembering(Known(b, "127.0.0.1:1")));
        var aSaw = new List<string?>();
        ea.PeerSeen += (_, _, address) => { lock (aSaw) aSaw.Add(address); };
        var logB = new List<string>();
        var (eb, _) = Node(b, Options(0, TimeSpan.FromMilliseconds(200), IPAddress.Loopback, Addr(ea)), logB);
        await WaitFor(() => { lock (aSaw) return aSaw.Contains(Addr(eb)); }, "A remembering B where it is now");
        await Task.Delay(500);
        Assert.Equal(1, CountLogged(logB, "In sync with Lower"));
        Assert.Equal(0, CountLogged(logB, "Disconnected from Lower"));
    }

    [Fact]
    public async Task ADeviceWhoseOldAddressAnswersAsAnotherIsRememberedWhereItIsNow()
    {
        var (a, b) = OrderedPair();
        var logA = new List<string>();
        var (ec, _) = Node(NewIdentity("Stranger"), Options(0, TimeSpan.FromMilliseconds(200), IPAddress.Loopback));
        var (ea, _) = Node(a, Remembering(Known(b, Addr(ec))), logA);
        var aSaw = new List<string?>();
        ea.PeerSeen += (_, _, address) => { lock (aSaw) aSaw.Add(address); };
        await WaitFor(() => Logged(logA, "Higher's last address"), "A finding B's address taken");
        var (eb, _) = Node(b, Addr(ea));   // B dials A, from where it is now
        await WaitFor(() => { lock (aSaw) return aSaw.Contains(Addr(eb)); }, "A remembering B where it is now");
    }

    [Fact]
    public async Task ARedialRefusedByTheRightDeviceKeepsItsAddress()
    {
        // The device itself answers, but in a protocol version this one doesn't speak: its
        // address is right, so it is redialled as usual, and nothing is logged as stale.
        var (a, b) = OrderedPair();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int answered = 0;
        _ = Task.Run(async () =>
        {
            while (true)
            {
                TcpClient c;
                try { c = await listener.AcceptTcpClientAsync(); }
                catch (Exception) { return; }
                Interlocked.Increment(ref answered);
                using (c)
                    await Protocol.WriteFrameAsync(c.GetStream(), Protocol.Serialize(new SyncMessage
                    {
                        Type = MessageTypes.Hello, App = Protocol.App, Proto = Protocol.Version + 1, Node = b.NodeId, Name = "Higher",
                        Group = b.GroupId, Nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)), Port = 1,
                    }), default);
            }
        });
        var logA = new List<string>();
        var (ea, _) = Node(a, Remembering(Known(b, $"127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}")), logA);
        await WaitFor(() => Volatile.Read(ref answered) >= 3, "A redialling the right device as usual", timeoutMs: 5000);
        Assert.False(Logged(logA, "Higher's last address"));
    }

    [Fact]
    public async Task ADialledDeviceIsRememberedWhereItWasDialled()
    {
        // B reaches A through a port forward: A's hello gives A's own port, but the address to
        // dial again is the forward's.
        var a = NewIdentity("A");
        var (ea, _) = Node(a);
        using var forward = new TcpListener(IPAddress.Loopback, 0);
        forward.Start();
        _ = Task.Run(async () =>
        {
            while (true)
            {
                TcpClient inbound;
                try { inbound = await forward.AcceptTcpClientAsync(); }
                catch (Exception) { return; }
                var outbound = new TcpClient();
                await outbound.ConnectAsync(IPAddress.Loopback, ea.ListenPort);
                _ = inbound.GetStream().CopyToAsync(outbound.GetStream()).ContinueWith(_ => outbound.Close());
                _ = outbound.GetStream().CopyToAsync(inbound.GetStream()).ContinueWith(_ => inbound.Close());
            }
        });
        var via = $"127.0.0.1:{((IPEndPoint)forward.LocalEndpoint).Port}";
        var b = NewIdentity("B", a.GroupId, a.GroupKeyBase64);
        var store = new TaskStore(null, b.NodeId, SystemClock.Instance);
        var eb = new SyncEngine(store, b, Options(0, TimeSpan.FromMilliseconds(200), IPAddress.Loopback, via));
        _engines.Add(eb);
        var bSaw = new List<string?>();
        eb.PeerSeen += (_, _, address) => { lock (bSaw) bSaw.Add(address); };
        eb.Start();
        await WaitFor(() => { lock (bSaw) return bSaw.Count > 0; }, "B in sync with A through the forward");
        lock (bSaw) Assert.Equal(via, bSaw[0]);
    }

    [Fact]
    public async Task MalformedRememberedDevicesDoNotStopRedialling()
    {
        // A damaged settings file: a null entry, and one without a node ID, before a good one.
        var (lower, higher) = OrderedPair();
        var (eh, sh) = Node(higher, Remembering());
        var (_, sl) = Node(lower, Remembering(null!, new KnownPeer(null!, "x", DateTimeOffset.UtcNow, "127.0.0.1:1"), Known(higher, Addr(eh))));
        var t = Add(sh, "from the higher");
        await WaitFor(() => sl.Get(t.Id) is not null, "the lower dialling the higher anyway");
    }

    [Fact]
    public async Task HousekeepingGoesOnAfterAnError()
    {
        var (lower, higher) = OrderedPair();
        var (eh, sh) = Node(higher, Remembering());
        int calls = 0;
        var logL = new List<string>();
        var good = Known(higher, Addr(eh));
        var (_, sl) = Node(lower, Remembering() with
        {
            KnownPeers = () => Interlocked.Increment(ref calls) == 1 ? throw new InvalidOperationException("boom") : [good],
        }, logL);
        var t = Add(sh, "from the higher");
        await WaitFor(() => sl.Get(t.Id) is not null, "the lower dialling the higher after the failed tick");
        Assert.Equal(1, CountLogged(logL, "Housekeeping failed"));
    }

    [Theory]
    [InlineData("192.168.1.66", "192.168.1.184", 24, true)]
    [InlineData("192.168.2.5", "192.168.1.184", 24, false)]
    [InlineData("10.0.2.2", "10.0.2.15", 24, true)]        // the Android emulator's view of its host
    [InlineData("172.17.5.4", "172.16.0.1", 12, true)]
    [InlineData("172.17.5.4", "172.16.0.1", 16, false)]
    [InlineData("::ffff:192.168.1.66", "192.168.1.184", 24, true)]
    [InlineData("fe80::1", "fe80::abcd", 64, true)]
    [InlineData("fd00::1", "fe80::abcd", 64, false)]
    [InlineData("192.168.1.66", "192.168.1.184", 0, false)]  // a prefix of 0 is no network
    [InlineData("127.0.0.1", "192.168.1.184", 24, true)]     // this machine
    public void OnlyAddressesOnALocalNetworkAreDialled(string ip, string mine, int prefix, bool on) =>
        Assert.Equal(on, Protocol.IsOnSubnet(IPAddress.Parse(ip), [(IPAddress.Parse(mine), prefix)]));

    [Theory]
    [InlineData("192.168.1.184", 24, "192.168.1.255")]
    [InlineData("10.0.2.15", 24, "10.0.2.255")]
    [InlineData("172.16.5.4", 12, "172.31.255.255")]
    [InlineData("192.168.1.184", 32, "192.168.1.184")]
    public void TheBroadcastAddressOfANetwork(string ip, int prefix, string broadcast) =>
        Assert.Equal(IPAddress.Parse(broadcast), Discovery.BroadcastOf(IPAddress.Parse(ip), prefix));

    [Fact]
    public void NoNetworkReachesNothingButThisMachine()
    {
        Assert.False(Protocol.IsOnSubnet(IPAddress.Parse("192.168.1.66"), []));
        Assert.True(Protocol.IsOnSubnet(IPAddress.Loopback, []));
    }

    [Fact]
    public async Task APairingWhoseRequesterLeavesFreesTheAccepter()
    {
        var (ea, _) = Node(NewIdentity("A"));
        PairPrompt? first = null;
        bool withdrawn = false;
        ea.PairRequested += p => { first ??= p; p.Withdrawn += () => withdrawn = true; };
        var (eb, _) = Node(NewIdentity("B"), Addr(ea));
        await WaitFor(() => eb.Peers().Any(p => p.Connected), "B connected to A");
        await eb.RequestPairingAsync(ea.Identity.NodeId);
        await WaitFor(() => first is not null, "A asked");
        eb.Dispose();
        await WaitFor(() => withdrawn, "A's prompt withdrawn");

        // A takes another pairing at once: the first one no longer holds it "busy".
        var c = NewIdentity("C");
        var (ec, _) = Node(c, Addr(ea));
        AutoAccept(ec);
        PairPrompt? second = null;
        ea.PairRequested += p => { if (p != first) second = p; };
        await WaitFor(() => ec.Peers().Any(p => p.Connected), "C connected to A");
        await ec.RequestPairingAsync(ea.Identity.NodeId);
        await WaitFor(() => second is not null, "A asked again");
    }

    [Fact]
    public async Task AfterARejectionTheSameConnectionCanTryAgain()
    {
        var (ea, _) = Node(NewIdentity("A"));
        var (eb, _) = Node(NewIdentity("B"), Addr(ea));
        AutoAccept(eb);
        int asked = 0;
        ea.PairRequested += p => { if (Interlocked.Increment(ref asked) == 1) _ = p.RejectAsync(); else _ = p.AcceptAsync(); };
        var stages = new List<PairStage>();
        eb.PairProgressed += p => { lock (stages) stages.Add(p.Stage); };
        await WaitFor(() => eb.Peers().Any(p => p.Connected), "B connected to A");
        await eb.RequestPairingAsync(ea.Identity.NodeId);
        await WaitFor(() => { lock (stages) return stages.Contains(PairStage.Rejected); }, "the first try rejected");
        await eb.RequestPairingAsync(ea.Identity.NodeId);
        await WaitFor(() => eb.Identity.GroupId == ea.Identity.GroupId, "the second try linking them");
    }

    [Fact]
    public async Task WhenTheJoiningRequesterDeclinesTheAccepterCanBeAskedAgain()
    {
        // A (linked with others) accepts; B, the one that would join, says no, then tries again.
        var (ea, _) = Node(NewIdentity("A"), 3, null);
        AutoAccept(ea);
        var (eb, _) = Node(NewIdentity("B"), 0, null, Addr(ea));
        int asked = 0;
        eb.PairRequested += p => { if (Interlocked.Increment(ref asked) == 1) _ = p.RejectAsync(); else _ = p.AcceptAsync(); };
        var stages = new List<PairProgress>();
        eb.PairProgressed += p => { lock (stages) stages.Add(p); };
        await WaitFor(() => eb.Peers().Any(p => p.Connected), "B connected to A");
        await eb.RequestPairingAsync(ea.Identity.NodeId);
        await WaitFor(() => { lock (stages) return stages.Any(p => p.Stage == PairStage.Rejected); }, "B declining");
        await eb.RequestPairingAsync(ea.Identity.NodeId);
        await WaitFor(() => eb.Identity.GroupId == ea.Identity.GroupId, "the second try linking them");
        lock (stages) Assert.DoesNotContain(stages, p => p.Message?.Contains("busy") == true);
    }

    [Fact]
    public async Task APromptLeftOverFromAnEarlierPairingAnswersNothing()
    {
        var (ea, _) = Node(NewIdentity("A"));
        var (eb, _) = Node(NewIdentity("B"), Addr(ea));
        var aPrompts = new List<PairPrompt>();
        ea.PairRequested += p => { lock (aPrompts) aPrompts.Add(p); };
        PairPrompt? bPrompt = null;
        eb.PairRequested += p => bPrompt = p;
        await WaitFor(() => eb.Peers().Any(p => p.Connected), "B connected to A");

        await eb.RequestPairingAsync(ea.Identity.NodeId);
        await WaitFor(() => { lock (aPrompts) return aPrompts.Count == 1; } , "A asked");
        await WaitFor(() => bPrompt is not null, "B asked");
        await bPrompt!.RejectAsync();
        await WaitFor(() => aPrompts[0].IsWithdrawn, "A's first prompt withdrawn");

        bPrompt = null;
        await eb.RequestPairingAsync(ea.Identity.NodeId);
        await WaitFor(() => { lock (aPrompts) return aPrompts.Count == 2; }, "A asked again");
        await WaitFor(() => bPrompt is not null, "B asked again");
        await aPrompts[0].AcceptAsync();   // the stale one
        await bPrompt!.AcceptAsync();
        await Task.Delay(700);
        Assert.NotEqual(ea.Identity.GroupId, eb.Identity.GroupId);
        await aPrompts[1].AcceptAsync();   // the real one
        await WaitFor(() => ea.Identity.GroupId == eb.Identity.GroupId, "linked on the real answer");
    }

    [Fact]
    public async Task ADeviceThatLeftTheGroupCanLinkAgain()
    {
        // A still remembers B as linked; B has left (another group) and asks to link again.
        var b = NewIdentity("B");
        var store = new TaskStore(null, Guid.NewGuid().ToString("N"), SystemClock.Instance);
        var a = NewIdentity("A");
        var ea = new SyncEngine(store, a, new SyncOptions
        {
            Port = 0, BindAddress = IPAddress.Loopback, EnableDiscovery = false,
            KnownPeers = () => [new KnownPeer(b.NodeId, "B", DateTimeOffset.UtcNow)],
        });
        ea.Start();
        _engines.Add(ea);
        PairPrompt? prompt = null;
        ea.PairRequested += p => prompt = p;
        var (eb, _) = Node(b, Addr(ea));
        AutoAccept(eb);
        await WaitFor(() => eb.Peers().Any(p => p.Connected), "B connected to A");
        Assert.DoesNotContain(ea.Peers(), p => p.NodeId == b.NodeId);   // never shown "not linked"
        await eb.RequestPairingAsync(a.NodeId);
        await WaitFor(() => prompt is not null, "A asked");
        await prompt!.AcceptAsync();
        await WaitFor(() => eb.Identity.GroupId == a.GroupId, "B linked again");
    }

    [Fact]
    public void AFloodOfStrangersDoesNotCrowdOutOurOwnGroup()
    {
        var a = NewIdentity("A");
        var (ea, _) = Node(a);
        for (int i = 0; i < 300; i++)
            ea.OnAnnounced(new SyncMessage
            {
                Type = MessageTypes.Announce, App = Protocol.App, Node = Guid.NewGuid().ToString("N"),
                Group = Guid.NewGuid().ToString("N"), Port = 9,
            }, IPAddress.Loopback);
        // Nor does a flood claiming our group, whose ID every announcement shows.
        for (int i = 0; i < 300; i++)
            ea.OnAnnounced(new SyncMessage
            {
                Type = MessageTypes.Announce, App = Protocol.App, Node = Guid.NewGuid().ToString("N"), Group = a.GroupId, Port = 9,
            }, IPAddress.Loopback);
        var (eb, _) = Node(NewIdentity("B", a.GroupId, a.GroupKeyBase64));
        ea.OnAnnounced(eb.Announcement(), IPAddress.Loopback);
        Assert.Contains(ea.Peers(), p => p.NodeId == eb.Identity.NodeId && p.Linked);
    }

    [Fact]
    public void OnlyAMemberCanAnnounceItselfAsOne()
    {
        var a = NewIdentity("A");
        var (ea, _) = Node(a);
        var forged = Guid.NewGuid().ToString("N");
        ea.OnAnnounced(new SyncMessage { Type = MessageTypes.Announce, App = Protocol.App, Node = forged, Group = a.GroupId, Port = 9 },
            IPAddress.Loopback);
        var (ex, _) = Node(NewIdentity("X", a.GroupId));   // the group's ID, but not its key
        ea.OnAnnounced(ex.Announcement(), IPAddress.Loopback);
        // A member that isn't listening (port 9), so no session to it can hide what the
        // announcements alone do.
        var b = Guid.NewGuid().ToString("N");
        SyncMessage Member(int port) => MemberAnnouncement(a, b, port, DateTimeOffset.UtcNow);
        var tampered = Member(9);
        tampered.Port = 10;
        ea.OnAnnounced(tampered, IPAddress.Loopback);
        Assert.DoesNotContain(ea.Peers(), p => p.NodeId == forged || p.NodeId == ex.Identity.NodeId || p.NodeId == b);

        ea.OnAnnounced(Member(9), IPAddress.Loopback);
        Assert.Contains(ea.Peers(), p => p.NodeId == b && p.Linked);
        // A stranger giving a member's ID can't push the member out.
        ea.OnAnnounced(new SyncMessage
        {
            Type = MessageTypes.Announce, App = Protocol.App, Node = b, Group = Guid.NewGuid().ToString("N"), Port = 9,
        }, IPAddress.Loopback);
        Assert.Contains(ea.Peers(), p => p.NodeId == b && p.Linked);
    }

    /// <summary>What member <paramref name="node"/> of <paramref name="group"/>'s group would
    /// announce at <paramref name="at"/>, as announcement <paramref name="seq"/> of its process
    /// <paramref name="boot"/>.</summary>
    private static SyncMessage MemberAnnouncement(SyncIdentity group, string node, int port, DateTimeOffset at,
        string boot = "0123456789abcdef0123456789abcdef", long seq = 1)
    {
        var m = new SyncMessage
        {
            Type = MessageTypes.Announce, App = Protocol.App, Proto = Protocol.Version, Node = node, Name = "B",
            Group = group.GroupId, Port = port, At = at.ToUnixTimeSeconds(), Boot = boot, Seq = seq,
        };
        m.Mac = SyncCrypto.AnnouncementMac(Convert.FromBase64String(group.GroupKeyBase64), m);
        return m;
    }

    [Fact]
    public void ARecordedAnnouncementCantBePassedOffAsANewOne()
    {
        var a = NewIdentity("A");
        var (ea, _) = Node(a);
        var b = Guid.NewGuid().ToString("N");
        // Recorded long ago (the member may have left the group since): ignored.
        ea.OnAnnounced(MemberAnnouncement(a, b, 9, DateTimeOffset.UtcNow.AddHours(-1)), IPAddress.Loopback);
        Assert.DoesNotContain(ea.Peers(), p => p.NodeId == b);

        // Recorded just now and replayed from elsewhere: the original counts, the replay doesn't.
        var recorded = MemberAnnouncement(a, b, 9, DateTimeOffset.UtcNow);
        ea.OnAnnounced(recorded, IPAddress.Loopback);
        ea.OnAnnounced(recorded, IPAddress.Parse("127.0.0.5"));
        Assert.Equal("127.0.0.1", ea.Peers().Single(p => p.NodeId == b).Address);

        // Nor does one from an earlier run of the member, though recent.
        ea.OnAnnounced(MemberAnnouncement(a, b, 9, DateTimeOffset.UtcNow.AddMinutes(-2), boot: Guid.NewGuid().ToString("N"), seq: 50),
            IPAddress.Parse("127.0.0.6"));
        Assert.Equal("127.0.0.1", ea.Peers().Single(p => p.NodeId == b).Address);
    }

    [Fact]
    public void AMemberWhoseClockStepsBackIsStillHeard()
    {
        var a = NewIdentity("A");
        var (ea, _) = Node(a);
        var b = Guid.NewGuid().ToString("N");
        ea.OnAnnounced(MemberAnnouncement(a, b, 9, DateTimeOffset.UtcNow, seq: 7), IPAddress.Loopback);
        // Its clock is corrected 5 minutes back; its next announcement, from a new address.
        ea.OnAnnounced(MemberAnnouncement(a, b, 9, DateTimeOffset.UtcNow.AddMinutes(-5), seq: 8), IPAddress.Parse("127.0.0.7"));
        Assert.Equal("127.0.0.7", ea.Peers().Single(p => p.NodeId == b).Address);
    }

    [Fact]
    public async Task AnUnansweredPairingTimesOutOnBothSidesAndFreesThem()
    {
        var quick = Options(0, TimeSpan.FromMilliseconds(200), IPAddress.Loopback) with
        {
            PairingTimeout = TimeSpan.FromSeconds(1.5), HousekeepingInterval = TimeSpan.FromMilliseconds(100),
        };
        var (ea, _) = Node(NewIdentity("A"), quick);
        var (eb, _) = Node(NewIdentity("B"), quick with { RemoteAddresses = [Addr(ea)] });
        var aPrompts = new List<PairPrompt>();
        ea.PairRequested += p => { lock (aPrompts) aPrompts.Add(p); };
        PairPrompt? bPrompt = null;
        eb.PairRequested += p => bPrompt = p;
        var progress = new List<PairProgress>();
        eb.PairProgressed += p => { lock (progress) progress.Add(p); };
        await WaitFor(() => eb.Peers().Any(p => p.Connected), "B connected to A");

        await eb.RequestPairingAsync(ea.Identity.NodeId);   // and nobody answers
        await WaitFor(() => { lock (progress) return progress.Any(p => p.Stage == PairStage.Failed); }, "B giving up");
        lock (progress) Assert.Equal("Nobody answered in time.", progress.Single(p => p.Stage == PairStage.Failed).Message);
        Assert.True(bPrompt!.IsWithdrawn);
        await WaitFor(() => { lock (aPrompts) return aPrompts[0].IsWithdrawn; }, "A's prompt withdrawn too");

        await eb.RequestPairingAsync(ea.Identity.NodeId);
        await WaitFor(() => { lock (aPrompts) return aPrompts.Count == 2; }, "A asked again, nothing left busy");
    }

    [Fact]
    public async Task AnAccepterKeepsThePairingOpenUntilTheRequesterAnswers()
    {
        // A, linked with others, says yes at once; B, which would join, answers well after A's
        // idle timeout for connections from outside the group.
        var quick = Options(2, TimeSpan.FromMilliseconds(200), IPAddress.Loopback) with
        {
            UnlinkedIdleTimeout = TimeSpan.FromSeconds(1.5), HousekeepingInterval = TimeSpan.FromMilliseconds(100),
        };
        var (ea, _) = Node(NewIdentity("A"), quick);
        AutoAccept(ea);
        var (eb, _) = Node(NewIdentity("B"), 0, null, Addr(ea));
        PairPrompt? bPrompt = null;
        eb.PairRequested += p => bPrompt = p;
        await WaitFor(() => eb.Peers().Any(p => p.Connected), "B connected to A");
        await eb.RequestPairingAsync(ea.Identity.NodeId);
        await WaitFor(() => bPrompt is not null, "B asked");
        await Task.Delay(3000);
        await bPrompt!.AcceptAsync();
        await WaitFor(() => eb.Identity.GroupId == ea.Identity.GroupId, "B joining A's group");
    }

    [Fact]
    public async Task TheAccepterOfARemoteLinkReconnectsAtOnceAfterPairing()
    {
        // A dials B over a remote link with a long backoff; B, alone, asks to link and joins
        // A's group. A listens on 127.0.0.2 while its connections come from 127.0.0.1, so B
        // can't dial back: only A's link can reconnect them.
        var (eb, sb) = Node(NewIdentity("B"), 0, null);
        var (ea, sa) = Node(NewIdentity("A"), 2, null, TimeSpan.FromSeconds(30), IPAddress.Parse("127.0.0.2"), Addr(eb));
        AutoAccept(ea);
        AutoAccept(eb);
        await WaitFor(() => eb.Peers().Any(p => p.Name == "A" && p.Connected), "A connected to B");
        await eb.RequestPairingAsync(ea.Identity.NodeId);
        await WaitFor(() => eb.Identity.GroupId == ea.Identity.GroupId, "B joining A's group");
        var t = Add(sa, "after linking");
        // Well inside the 30 s backoff: the link redialled as soon as the pairing closed it.
        await WaitFor(() => sb.Get(t.Id) is not null, "A's task reaching B", timeoutMs: 8000);
    }

    [Fact]
    public async Task AMessageFromAnEarlierRoundActsOnNothing()
    {
        var (ea, _) = Node(NewIdentity("A"));
        var prompts = new List<PairPrompt>();
        ea.PairRequested += p => { lock (prompts) prompts.Add(p); };
        using var other = await RawPeer.ConnectAsync(ea);
        await other.HelloAsync(Guid.NewGuid().ToString("N"));
        Assert.Equal(MessageTypes.Hello, (await other.ReceiveAsync())!.Type);
        await WaitFor(() => ea.Peers().Any(p => p.NodeId == other.Node), "A sees the other device");

        async Task<string> ChallengeAsync()
        {
            var request = await other.ReceiveAsync();
            Assert.Equal(MessageTypes.PairRequest, request?.Type);
            using var key = SyncCrypto.NewPairingKey();
            await other.SendAsync(new SyncMessage
            {
                Type = MessageTypes.PairChallenge, Round = request!.Commit, Members = 0,
                PairKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()),
            });
            Assert.Equal(MessageTypes.PairReveal, (await other.ReceiveAsync())?.Type);   // null: A hung up
            return request.Commit!;
        }

        // Round 1: A asks, and its user says no.
        await ea.RequestPairingAsync(other.Node);
        var round1 = await ChallengeAsync();
        await WaitFor(() => { lock (prompts) return prompts.Count == 1; }, "A's user asked");
        await prompts[0].RejectAsync();
        Assert.Equal(MessageTypes.PairReject, (await other.ReceiveAsync())!.Type);

        // Round 2 begins, and only then does the other device's yes to round 1 arrive.
        await ea.RequestPairingAsync(other.Node);
        await other.SendAsync(new SyncMessage { Type = MessageTypes.PairAccept, Round = round1 });
        await ChallengeAsync();   // round 2 carries on as if nothing had come
        await WaitFor(() => { lock (prompts) return prompts.Count == 2; }, "A's user asked again");
    }

    [Fact]
    public async Task AnotherProtocolVersionIsRefused()
    {
        var (ea, _) = Node(NewIdentity("A"));
        using var other = await RawPeer.ConnectAsync(ea);
        await other.HelloAsync(Guid.NewGuid().ToString("N"), proto: Protocol.Version + 1);
        Assert.Equal(MessageTypes.Hello, (await other.ReceiveAsync())!.Type);
        Assert.Null(await other.ReceiveAsync());
    }

    [Fact]
    public void AFloodOfAnnouncedDevicesIsCapped()
    {
        var (ea, _) = Node(NewIdentity("A"));
        for (int i = 0; i < 1000; i++)
            ea.OnAnnounced(new SyncMessage
            {
                Type = MessageTypes.Announce, App = Protocol.App, Node = Guid.NewGuid().ToString("N"),
                Group = Guid.NewGuid().ToString("N"), Port = 9,
            }, IPAddress.Loopback);
        Assert.True(ea.Peers().Count <= 256);
    }

    [Fact]
    public void BadAnnouncementsAreIgnored()
    {
        var good = new SyncMessage { Type = MessageTypes.Announce, App = Protocol.App, Node = Guid.NewGuid().ToString("N"), Port = 37660 };
        Assert.True(Discovery.IsValid(good));
        Assert.False(Discovery.IsValid(new SyncMessage { Type = good.Type, App = good.App, Node = good.Node, Port = -1 }));
        Assert.False(Discovery.IsValid(new SyncMessage { Type = good.Type, App = good.App, Node = good.Node, Port = 70000 }));
        Assert.False(Discovery.IsValid(new SyncMessage { Type = good.Type, App = good.App, Node = "a>b", Port = 37660 }));
    }

    [Fact]
    public async Task OversizedFramesAreRefusedBeforeAllocating()
    {
        var bytes = new byte[] { 0x02, 0x00, 0x00, 0x00 };   // claims 32 MB
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            Protocol.ReadFrameAsync(new MemoryStream(bytes), Protocol.MaxUnauthenticatedFrame, default));
    }

    [Fact]
    public void MalformedRecordsFromPeersAreSkipped()
    {
        var store = new TaskStore(null, Guid.NewGuid().ToString("N"), SystemClock.Instance);
        var good = new AlarmTask { Id = Guid.NewGuid(), Def = new TaskDefinition { Description = "ok" } };
        var changed = store.Merge([null!, good with { Id = Guid.NewGuid(), Def = null! }, good]);
        Assert.Single(changed);
        Assert.Single(store.All());
    }

    [Fact]
    public async Task PairingWithMatchingCodesJoinsTheGroupAndMergesTasks()
    {
        var a = NewIdentity("A");
        var (ea, sa) = Node(a);
        Add(sa, "A's task");
        var b = NewIdentity("B");
        var (eb, sb) = Node(b, Addr(ea));
        Add(sb, "B's task");

        PairPrompt? prompt = null, bPrompt = null;
        ea.PairRequested += p => prompt = p;
        eb.PairRequested += p => bPrompt = p;
        var progress = new List<PairProgress>();
        eb.PairProgressed += p => { lock (progress) progress.Add(p); };
        SyncIdentity? adopted = null;
        eb.IdentityChanged += id => adopted = id;

        await WaitFor(() => eb.Peers().Any(p => p.NodeId == a.NodeId && p.Connected), "B connected to A, unlinked");
        Assert.False(eb.Peers().Single(p => p.NodeId == a.NodeId).Linked);
        Assert.DoesNotContain(sb.Live(), t => t.Def.Description == "A's task");

        await eb.RequestPairingAsync(a.NodeId);
        await WaitFor(() => prompt is not null, "A asked to accept");
        await WaitFor(() => { lock (progress) return progress.Any(p => p.Stage == PairStage.CodeShown); }, "B shows a code");
        string bCode;
        lock (progress) bCode = progress.First(p => p.Stage == PairStage.CodeShown).Code!;
        Assert.Equal(prompt!.Code, bCode);
        Assert.Matches("^[0-9]{6}$", bCode);
        await WaitFor(() => bPrompt is not null, "B asked too");
        Assert.True(bPrompt!.IsRequester);
        Assert.False(prompt.IsRequester);
        Assert.Equal(prompt.Code, bPrompt.Code);

        // A's yes alone links nothing: B's user must compare the codes and say yes too.
        await prompt.AcceptAsync();
        await Task.Delay(500);
        Assert.Null(adopted);
        await bPrompt.AcceptAsync();
        await WaitFor(() => adopted is not null, "B adopting A's group");
        Assert.Equal(a.GroupId, adopted!.GroupId);
        Assert.Equal(a.GroupKeyBase64, adopted.GroupKeyBase64);
        await WaitFor(() => sa.Live().Count == 2 && sb.Live().Count == 2, "the two task lists merged");
        Assert.True(ea.Peers().Single(p => p.NodeId == b.NodeId).Linked);
    }

    [Fact]
    public async Task ARejectedPairingChangesNothing()
    {
        var (ea, _) = Node(NewIdentity("A"));
        var b = NewIdentity("B");
        var (eb, _) = Node(b, Addr(ea));
        ea.PairRequested += p => _ = p.RejectAsync();
        AutoAccept(eb);
        var done = new TaskCompletionSource<PairProgress>();
        eb.PairProgressed += p => { if (p.Stage is PairStage.Rejected or PairStage.Accepted) done.TrySetResult(p); };
        await WaitFor(() => eb.Peers().Any(p => p.Connected), "B connected to A");
        await eb.RequestPairingAsync(ea.Identity.NodeId);
        var result = await done.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(PairStage.Rejected, result.Stage);
        Assert.Equal(b.GroupId, eb.Identity.GroupId);
    }

    [Theory]
    [InlineData("192.168.1.5", true)]
    [InlineData("10.0.0.1", true)]
    [InlineData("172.20.0.1", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("fe80::1", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("172.32.0.1", false)]
    public void LanAddressClassification(string ip, bool lan) =>
        Assert.Equal(lan, Protocol.IsPrivateOrLan(IPAddress.Parse(ip)));

    [Fact]
    public void IPv6IsListenedOnOnlyWhenOutsideConnectionsAreAccepted()
    {
        // Without them, a LAN peer's IPv6 attempt is refused at once and it falls back to IPv4,
        // whose private addresses tell the LAN apart.
        Assert.False(SyncEngine.ListensOnIpv6(new SyncOptions { BindAddress = IPAddress.Any, AcceptExternalConnections = false }));
        Assert.True(SyncEngine.ListensOnIpv6(new SyncOptions { BindAddress = IPAddress.Any, AcceptExternalConnections = true }));
        Assert.False(SyncEngine.ListensOnIpv6(new SyncOptions { BindAddress = IPAddress.Loopback, AcceptExternalConnections = true }));
    }

    [Theory]
    [InlineData("example.com", "example.com", 37660)]
    [InlineData("example.com:4000", "example.com", 4000)]
    [InlineData("[::1]:5000", "::1", 5000)]
    [InlineData("[fe80::1%5]:37660", "fe80::1%5", 37660)]   // a link-local LAN address remembered for a peer
    public void AddressParsing(string text, string host, int port)
    {
        Assert.True(Protocol.TryParseAddress(text, out var h, out var p));
        Assert.Equal((host, port), (h, p));
    }

    [Theory]
    [InlineData("10.0.2.2:37671n10.0.2.2:37673")]
    [InlineData(":4000")]
    [InlineData("[]:4000")]
    [InlineData("host name")]
    [InlineData("host:99999")]
    public void BadAddressesAreRefused(string text) => Assert.False(Protocol.TryParseAddress(text, out _, out _));

    [Fact]
    public void ABareIpv6AddressIsAHost()
    {
        Assert.True(Protocol.TryParseAddress("fe80::1", out var h, out var p));
        Assert.Equal(("fe80::1", Protocol.DefaultPort), (h, p));
    }

    [Fact]
    public void SealedFramesRejectTamperingAndReplay()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var frame = SyncCrypto.Seal(key, 7, "hello"u8);
        Assert.Equal("hello"u8.ToArray(), SyncCrypto.Open(key, 7, frame));
        Assert.ThrowsAny<CryptographicException>(() => SyncCrypto.Open(key, 8, frame));
        frame[^1] ^= 1;
        Assert.ThrowsAny<CryptographicException>(() => SyncCrypto.Open(key, 7, frame));
    }

    public void Dispose()
    {
        foreach (var e in _engines) e.Dispose();
    }
}
