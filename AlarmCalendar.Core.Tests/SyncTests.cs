using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using AlarmCalendar.Sync;

namespace AlarmCalendar.Tests;

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
        TimeSpan reconnectDelay, IPAddress bind, params string[] remotes)
    {
        var store = new TaskStore(null, id.NodeId, SystemClock.Instance);
        var engine = new SyncEngine(store, id, new SyncOptions
        {
            Port = 0,
            BindAddress = bind,
            EnableDiscovery = false,
            RemoteAddresses = remotes,
            ReconnectDelay = reconnectDelay,
            LinkedDeviceCount = () => linkedDevices,
        }, log is null ? null : line => { lock (log) log.Add(line); });
        engine.Start();
        _engines.Add(engine);
        return (engine, store);
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
        await evil.SendAsync(new SyncMessage { Type = MessageTypes.PairRequest, Commit = Convert.ToBase64String(SyncCrypto.Commitment(pub, 10_000)), Members = 10_000 });
        var challenge = (await evil.ReceiveAsync())!;
        var theirs = Convert.FromBase64String(challenge.PairKey!);
        var (secret, _) = SyncCrypto.PairingSecret(key, theirs, pub, theirs, 10_000, challenge.Members!.Value);
        await evil.SendAsync(new SyncMessage { Type = MessageTypes.PairReveal, PairKey = Convert.ToBase64String(pub) });
        await WaitFor(() => prompt is not null, "A asked");
        Assert.True(prompt!.ThisDeviceJoins);

        // Push a group before A's user has answered.
        var sealedGroup = SyncCrypto.Seal(secret, SyncCrypto.RequesterGroupCounter,
            System.Text.Json.JsonSerializer.SerializeToUtf8Bytes([evilGroup, evilKey], Json.Wire.StringArray));
        await evil.SendAsync(new SyncMessage { Type = MessageTypes.PairGroup, Sealed = Convert.ToBase64String(sealedGroup) });
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
        ea.PeerSeen += (_, name) => { lock (seen) seen.Add(name); };
        ea.LeaveGroup(Guid.NewGuid().ToString("N"), Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        await Task.Delay(500);
        lock (seen) Assert.Empty(seen);
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
            IsLinkedPeer = node => node == b.NodeId,
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
        var mine = Guid.NewGuid().ToString("N");
        ea.OnAnnounced(new SyncMessage { Type = MessageTypes.Announce, App = Protocol.App, Node = mine, Group = a.GroupId, Port = 9 },
            IPAddress.Loopback);
        Assert.Contains(ea.Peers(), p => p.NodeId == mine && p.Linked);
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

    [Theory]
    [InlineData("example.com", "example.com", 37660)]
    [InlineData("example.com:4000", "example.com", 4000)]
    [InlineData("[::1]:5000", "::1", 5000)]
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
