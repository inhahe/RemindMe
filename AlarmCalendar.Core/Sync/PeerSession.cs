using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace AlarmCalendar.Sync;

/// <summary>
/// One TCP connection to another node. Both ends open with a plaintext <c>hello</c>.
/// Between members of the same group, every later frame is AES-GCM sealed with
/// per-direction session keys; otherwise only the pairing messages may follow, in plaintext.
/// </summary>
internal sealed class PeerSession : IDisposable
{
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly byte[] _myNonce = RandomNumberGenerator.GetBytes(SyncCrypto.NonceBytes);
    private byte[]? _sendKey, _receiveKey;
    private ulong _sendCounter, _receiveCounter;
    private int _closed;

    public bool IsOutgoing { get; }
    public IPEndPoint? RemoteEndPoint { get; }
    /// <summary>The configured remote address this session was dialled for, if any.</summary>
    public string? RemoteAddress { get; init; }
    public DateTimeOffset Opened { get; } = DateTimeOffset.UtcNow;

    // Filled in by the peer's hello.
    public string? PeerNode { get; private set; }
    public string? PeerName { get; private set; }
    public string? PeerGroup { get; private set; }
    public int? PeerPort { get; private set; }

    /// <summary>The peer proved knowledge of the group secret (its first sealed frame opened).</summary>
    public bool Authenticated { get; private set; }
    /// <summary>Keys are derived: the peer claims the same group, and frames are now sealed.</summary>
    public bool Sealed => _sendKey is not null;

    // Pairing state (a session between different groups).
    public ECDiffieHellman? PairKey { get; set; }
    public byte[]? PairPublic { get; set; }
    public byte[]? PairSecret { get; set; }
    public string? PairCode { get; set; }
    public bool PairInitiator { get; set; }

    public event Action<PeerSession, SyncMessage>? Received;
    public event Action<PeerSession>? Closed;

    public PeerSession(TcpClient client, bool isOutgoing)
    {
        _client = client;
        _stream = client.GetStream();
        IsOutgoing = isOutgoing;
        RemoteEndPoint = client.Client.RemoteEndPoint as IPEndPoint;
        ConfigureKeepAlive(client);
    }

    public string Describe() => $"{PeerName ?? "?"} ({RemoteEndPoint})";

    /// <summary>Sends our hello, then starts reading. In that order: the peer's hello
    /// switches this session to sealed frames, and ours must have gone out in plaintext first.</summary>
    public async Task StartAsync(SyncMessage hello)
    {
        hello.Nonce = Convert.ToBase64String(_myNonce);
        await SendAsync(hello).ConfigureAwait(false);
        _ = ReadLoopAsync();
    }

    /// <summary>Records the peer's hello and, for a peer claiming our group, derives the
    /// session keys so every later frame is sealed.</summary>
    /// <returns>False if the hello is malformed.</returns>
    public bool AcceptHello(SyncMessage hello, string myNode, string myGroup, byte[] groupKey)
    {
        if (hello.Node is null || hello.Nonce is null) return false;
        byte[] peerNonce;
        try { peerNonce = Convert.FromBase64String(hello.Nonce); }
        catch (FormatException) { return false; }
        if (peerNonce.Length != SyncCrypto.NonceBytes || hello.Node == myNode) return false;
        PeerNode = hello.Node;
        PeerName = string.IsNullOrWhiteSpace(hello.Name) ? hello.Node[..Math.Min(8, hello.Node.Length)] : hello.Name;
        PeerGroup = hello.Group;
        PeerPort = hello.Port;
        if (hello.Group == myGroup)
            (_sendKey, _receiveKey) = SyncCrypto.SessionKeys(groupKey, myNode, _myNonce, hello.Node, peerNonce);
        return true;
    }

    public async Task SendAsync(SyncMessage m)
    {
        if (_closed != 0) return;
        var body = Protocol.Serialize(m);
        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_sendKey is not null) body = SyncCrypto.Seal(_sendKey, _sendCounter++, body);
            await Protocol.WriteFrameAsync(_stream, body, _cts.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException or SocketException)
        {
            Close();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var frame = await Protocol.ReadFrameAsync(_stream, _cts.Token).ConfigureAwait(false);
                if (frame is null) break;
                byte[] plain = frame;
                if (_receiveKey is not null)
                {
                    plain = SyncCrypto.Open(_receiveKey, _receiveCounter++, frame);
                    Authenticated = true;
                }
                var msg = Protocol.Deserialize(plain);
                if (msg is null) break;
                Received?.Invoke(this, msg);
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException
            or SocketException or InvalidDataException or CryptographicException)
        {
            // A wrong group secret surfaces here as a CryptographicException on the
            // first sealed frame: the session simply ends.
        }
        finally
        {
            Close();
        }
    }

    /// <summary>Ends the session; raises <see cref="Closed"/> once.</summary>
    public void Close()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        try { _cts.Cancel(); } catch (ObjectDisposedException) { }
        try { _client.Close(); } catch (SocketException) { }
        PairKey?.Dispose();
        Closed?.Invoke(this);
    }

    public bool IsClosed => _closed != 0;

    public void Dispose() => Close();

    /// <summary>A peer that vanishes without a FIN (WiFi off, sleep) is noticed within
    /// about 30 s rather than never. Values from LanLink.</summary>
    private static void ConfigureKeepAlive(TcpClient client)
    {
        try
        {
            var s = client.Client;
            s.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            s.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 15);
            s.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 5);
            s.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 3);
        }
        catch (Exception e) when (e is SocketException or PlatformNotSupportedException) { }
    }
}
