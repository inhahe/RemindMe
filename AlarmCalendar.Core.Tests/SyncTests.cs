using System.Net;
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

    private (SyncEngine engine, TaskStore store) Node(SyncIdentity id, params string[] remotes)
    {
        var store = new TaskStore(null, id.NodeId, SystemClock.Instance);
        var engine = new SyncEngine(store, id, new SyncOptions
        {
            Port = 0,
            BindAddress = IPAddress.Loopback,
            EnableDiscovery = false,
            RemoteAddresses = remotes,
            ReconnectDelay = TimeSpan.FromMilliseconds(200),
        });
        engine.Start();
        _engines.Add(engine);
        return (engine, store);
    }

    private static string Addr(SyncEngine e) => $"127.0.0.1:{e.ListenPort}";

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

        sa.MarkDone(fromB.Id);   // nothing is triggered, so this changes nothing
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
    public async Task AWrongSecretGetsNothing()
    {
        var a = NewIdentity("A");
        var (ea, sa) = Node(a);
        Add(sa, "secret task");
        // Claims A's group but doesn't know its key.
        var (_, sm) = Node(NewIdentity("Mallory", a.GroupId), Addr(ea));
        await Task.Delay(1500);
        Assert.Empty(sm.Live());
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

        PairPrompt? prompt = null;
        ea.PairRequested += p => prompt = p;
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

        await prompt.AcceptAsync();
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
