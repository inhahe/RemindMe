using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AlarmCalendar.Sync;

public static class MessageTypes
{
    // Plaintext, before a session is authenticated.
    public const string Hello = "hello";
    public const string PairRequest = "pair_request";
    public const string PairChallenge = "pair_challenge";
    public const string PairReveal = "pair_reveal";
    public const string PairAccept = "pair_accept";
    public const string PairGroup = "pair_group";
    public const string PairReject = "pair_reject";
    // Encrypted, within a group.
    /// <summary>The first sealed frame each way: tiny, so the peer can open it (proving the
    /// key) under the small pre-authentication frame limit, whatever the size of the digest.</summary>
    public const string Ready = "ready";
    public const string Digest = "digest";
    public const string Tasks = "tasks";
    /// <summary>Sent after answering a peer's digest: every record the peer lacked has
    /// been sent (frames arrive in order), so the first exchange with it is complete.</summary>
    public const string DigestDone = "digest_done";
    // UDP discovery.
    public const string Announce = "announce";
}

/// <summary>Every message of the protocol: one flat shape, fields per kind (as in LanLink).</summary>
public sealed class SyncMessage
{
    public string Type { get; set; } = "";

    // hello / announce
    public string? App { get; set; }
    public int? Proto { get; set; }
    public string? Node { get; set; }
    public string? Name { get; set; }
    public string? Group { get; set; }
    public string? Nonce { get; set; }
    public int? Port { get; set; }
    /// <summary>An announcement's HMAC under the group key (<see cref="SyncCrypto.AnnouncementMac"/>):
    /// the group ID is public, so only this shows that a device really is a member.</summary>
    public string? Mac { get; set; }
    /// <summary>When an announcement was sent, in Unix seconds; covered by <see cref="Mac"/>, so a
    /// recorded announcement can't be passed off as a later one.</summary>
    public long? At { get; set; }
    /// <summary>A random ID of the sending process, and the number of its announcement within
    /// it, both covered by <see cref="Mac"/>: within one process the number must grow, which no
    /// change of either clock can upset.</summary>
    public string? Boot { get; set; }
    public long? Seq { get; set; }

    // replication
    public List<DigestEntry>? Digest { get; set; }
    public List<AlarmTask>? Tasks { get; set; }

    // pairing
    /// <summary>An ephemeral ECDH P-256 public key (SubjectPublicKeyInfo, base64).</summary>
    public string? PairKey { get; set; }
    /// <summary>The requester's commitment: SHA-256 of the key it reveals later (base64).</summary>
    public string? Commit { get; set; }
    /// <summary>The pairing attempt every later pairing message belongs to: its request's
    /// <see cref="Commit"/>.</summary>
    public string? Round { get; set; }
    /// <summary>How many other devices the sender is linked with; the side with fewer joins
    /// the other's group.</summary>
    public int? Members { get; set; }
    /// <summary>The group's ID and secret, AES-GCM sealed under the pairing secret (base64).</summary>
    public string? Sealed { get; set; }
    public string? Reason { get; set; }
}

public static class Protocol
{
    public const string App = "AlarmCalendar";
    /// <summary>Raise on every incompatible change, so mismatched builds refuse each other
    /// with a clear message instead of failing obscurely. 2: the "ready" frame, two-sided
    /// pairing acceptance, member counts in the commitment. 3: pairing rounds, MACed and
    /// dated announcements.</summary>
    public const int Version = 3;
    public const int DefaultPort = 37660;
    /// <summary>Discovery always uses this UDP port, whatever TCP port a node listens on,
    /// so several instances on one machine (or a custom port) still find each other.</summary>
    public const int DiscoveryPort = 37660;
    /// <summary>The largest frame from a peer that has proved the group key.</summary>
    public const int MaxFrame = 32 * 1024 * 1024;
    /// <summary>The largest frame before that: hellos and pairing messages are small, and
    /// anything bigger from an unproven peer is only a way to exhaust memory.</summary>
    public const int MaxUnauthenticatedFrame = 64 * 1024;

    public static byte[] Serialize(SyncMessage m) => JsonSerializer.SerializeToUtf8Bytes(m, Json.Wire.SyncMessage);

    /// <summary>Node IDs are 32 lowercase hex digits (a GUID in "N" form). Anything else is
    /// refused: IDs go into key-derivation labels, which must not be ambiguous.</summary>
    public static bool IsValidNodeId(string? id) =>
        id is { Length: 32 } && id.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    /// <summary>A peer-supplied device name, trimmed to something displayable.</summary>
    public static string CleanName(string? name, string fallback)
    {
        var n = new string((name ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (n.Length > 64) n = n[..64];
        return n.Length > 0 ? n : fallback;
    }

    public static SyncMessage? Deserialize(ReadOnlySpan<byte> bytes)
    {
        try { return JsonSerializer.Deserialize(bytes, Json.Wire.SyncMessage); }
        catch (JsonException) { return null; }
    }

    /// <summary>Writes one frame: a 4-byte big-endian length, then the body, in a single
    /// write (two writes would meet Nagle's algorithm and the peer's delayed ACK).</summary>
    public static async Task WriteFrameAsync(Stream s, ReadOnlyMemory<byte> body, CancellationToken ct)
    {
        var frame = new byte[4 + body.Length];
        BinaryPrimitives.WriteInt32BigEndian(frame, body.Length);
        body.CopyTo(frame.AsMemory(4));
        await s.WriteAsync(frame, ct).ConfigureAwait(false);
        await s.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Reads one frame body; null at a clean end of stream.</summary>
    /// <param name="maxLength">The largest body accepted; a longer header ends the stream
    /// with <see cref="InvalidDataException"/> before anything is allocated.</param>
    public static async Task<byte[]?> ReadFrameAsync(Stream s, int maxLength, CancellationToken ct)
    {
        var header = new byte[4];
        int got = await s.ReadAtLeastAsync(header, 4, throwOnEndOfStream: false, ct).ConfigureAwait(false);
        if (got == 0) return null;
        if (got < 4) throw new EndOfStreamException();
        int len = BinaryPrimitives.ReadInt32BigEndian(header);
        if (len < 0 || len > maxLength) throw new InvalidDataException($"Frame length {len} is out of range.");
        var body = new byte[len];
        await s.ReadExactlyAsync(body, ct).ConfigureAwait(false);
        return body;
    }

    /// <summary>
    /// True for loopback, private (RFC 1918), link-local and private IPv6 (ULA, link-local)
    /// addresses: anything reachable only on this machine or the LAN. Copied from LanLink.
    /// </summary>
    public static bool IsPrivateOrLan(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip)) return true;
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
            else
            {
                if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal) return true;
                return (ip.GetAddressBytes()[0] & 0xFE) == 0xFC;
            }
        }
        if (ip.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = ip.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168)
            || (b[0] == 169 && b[1] == 254) || b[0] == 127;
    }

    /// <summary>Splits "host" or "host:port" (or "[v6]:port").</summary>
    public static bool TryParseAddress(string text, out string host, out int port)
    {
        host = "";
        port = DefaultPort;
        text = text.Trim();
        if (text.Length == 0) return false;
        if (text.StartsWith('['))
        {
            int close = text.IndexOf(']');
            if (close < 0) return false;
            host = text[1..close];
            var rest = text[(close + 1)..];
            return host.Length > 0
                && (rest.Length == 0 || (rest.StartsWith(':') && int.TryParse(rest[1..], out port) && port is > 0 and < 65536));
        }
        int colon = text.LastIndexOf(':');
        if (colon >= 0 && text.IndexOf(':') == colon)
        {
            host = text[..colon].Trim();
            return host.Length > 0 && int.TryParse(text[(colon + 1)..], out port) && port is > 0 and < 65536;
        }
        host = text;
        // Several colons without brackets can only be a bare IPv6 address.
        if (text.Contains(':')) return IPAddress.TryParse(text, out var v6) && v6.AddressFamily == AddressFamily.InterNetworkV6;
        return text.All(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_');
    }
}

/// <summary>Key derivation and sealing for sessions and pairing.</summary>
public static class SyncCrypto
{
    public const int NonceBytes = 16;

    /// <summary>
    /// The two per-direction keys of a session, derived from the group secret and both
    /// hello nonces. Both ends compute the same pair; each sends with the key of its own
    /// direction, so no (key, counter) is ever used twice.
    /// </summary>
    /// <returns>(the key this node sends with, the key it receives with).</returns>
    public static (byte[] send, byte[] receive) SessionKeys(byte[] groupKey, string myNode, byte[] myNonce, string peerNode, byte[] peerNonce)
    {
        if (!Protocol.IsValidNodeId(myNode) || !Protocol.IsValidNodeId(peerNode) || myNode == peerNode)
            throw new ArgumentException("Session keys need two distinct, valid node IDs.");
        bool iAmLow = string.CompareOrdinal(myNode, peerNode) < 0;
        var (lowNode, highNode) = iAmLow ? (myNode, peerNode) : (peerNode, myNode);
        var salt = iAmLow ? Concat(myNonce, peerNonce) : Concat(peerNonce, myNonce);
        // Fixed-length IDs make each label unambiguous: no choice of IDs can make the label of
        // one direction equal a label of another session (a reflection attack).
        byte[] lowToHigh = HKDF.DeriveKey(HashAlgorithmName.SHA256, groupKey, 32, salt, Encoding.UTF8.GetBytes($"alarmcal v1 from {lowNode} to {highNode}"));
        byte[] highToLow = HKDF.DeriveKey(HashAlgorithmName.SHA256, groupKey, 32, salt, Encoding.UTF8.GetBytes($"alarmcal v1 from {highNode} to {lowNode}"));
        return iAmLow ? (lowToHigh, highToLow) : (highToLow, lowToHigh);
    }

    /// <summary>The MAC a member puts on its announcement, under a key derived from the
    /// group secret. It covers every field acted on: anyone can claim the group's ID, which
    /// every announcement carries, but only a member can produce this.</summary>
    public static string AnnouncementMac(byte[] groupKey, SyncMessage m) => Convert.ToBase64String(AnnouncementMacBytes(groupKey, m));

    /// <summary>Whether <paramref name="m"/> carries a valid <see cref="AnnouncementMac"/> under <paramref name="groupKey"/>.</summary>
    public static bool HasValidMac(byte[] groupKey, SyncMessage m)
    {
        if (m.Mac is null || !Protocol.IsValidNodeId(m.Group) || !Protocol.IsValidNodeId(m.Boot)) return false;
        byte[] given;
        try { given = Convert.FromBase64String(m.Mac); }
        catch (FormatException) { return false; }
        return CryptographicOperations.FixedTimeEquals(given, AnnouncementMacBytes(groupKey, m));
    }

    private static byte[] AnnouncementMacBytes(byte[] groupKey, SyncMessage m)
    {
        // The node, group and boot IDs have a fixed length (or are refused) and the port,
        // version, time and number are numbers, so with the name last no two announcements
        // share this text.
        var data = Encoding.UTF8.GetBytes($"{m.Node}\n{m.Group}\n{m.Port}\n{m.Proto}\n{m.At}\n{m.Boot}\n{m.Seq}\n{m.Name}");
        return HMACSHA256.HashData(AnnouncementKey(groupKey), data);
    }

    /// <summary>The last group key's announcement key: derived once, not for every packet
    /// (a flood of announcements claiming the group makes many). A reference, swapped whole,
    /// so no thread sees one group's key paired with another's.</summary>
    private static DerivedKey? _announcementKey;

    private sealed record DerivedKey(byte[] GroupKey, byte[] Key);

    private static byte[] AnnouncementKey(byte[] groupKey)
    {
        if (Volatile.Read(ref _announcementKey) is { } c && c.GroupKey.AsSpan().SequenceEqual(groupKey)) return c.Key;
        var key = HKDF.DeriveKey(HashAlgorithmName.SHA256, groupKey, 32, info: "alarmcal announce"u8.ToArray());
        Volatile.Write(ref _announcementKey, new DerivedKey(groupKey.ToArray(), key));
        return key;
    }

    /// <summary>The requester's commitment to its pairing key and its member count, sent
    /// before it has seen the other side's key. Without it, a man in the middle could try
    /// keys until both screens showed the same code; with it, every choice is fixed before
    /// the code can be known.</summary>
    public static byte[] Commitment(byte[] publicKey, int members)
    {
        var data = new byte[publicKey.Length + 4];
        publicKey.CopyTo(data, 0);
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(publicKey.Length), members);
        return SHA256.HashData(data);
    }

    /// <summary>Counters for the two group payloads a pairing may seal under its secret, so
    /// no (key, nonce) pair is ever used twice, whatever either side is told.</summary>
    public const ulong AccepterGroupCounter = 0, RequesterGroupCounter = 1;

    /// <summary>Encrypts <paramref name="plain"/> as frame number <paramref name="counter"/>:
    /// [8-byte counter][ciphertext][16-byte tag].</summary>
    public static byte[] Seal(byte[] key, ulong counter, ReadOnlySpan<byte> plain)
    {
        var output = new byte[8 + plain.Length + 16];
        BinaryPrimitives.WriteUInt64BigEndian(output, counter);
        Span<byte> nonce = stackalloc byte[12];
        BinaryPrimitives.WriteUInt64BigEndian(nonce[4..], counter);
        using var gcm = new AesGcm(key, 16);
        gcm.Encrypt(nonce, plain, output.AsSpan(8, plain.Length), output.AsSpan(8 + plain.Length, 16));
        return output;
    }

    /// <summary>Decrypts a frame sealed by <see cref="Seal"/>, which must carry
    /// <paramref name="expectedCounter"/> (frames arrive in order, so a replayed or
    /// reordered frame is rejected).</summary>
    /// <exception cref="CryptographicException">Wrong key, tampering, or replay.</exception>
    public static byte[] Open(byte[] key, ulong expectedCounter, ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 24) throw new CryptographicException("Frame too short.");
        ulong counter = BinaryPrimitives.ReadUInt64BigEndian(frame);
        if (counter != expectedCounter) throw new CryptographicException("Frame out of sequence.");
        Span<byte> nonce = stackalloc byte[12];
        BinaryPrimitives.WriteUInt64BigEndian(nonce[4..], counter);
        var plain = new byte[frame.Length - 24];
        using var gcm = new AesGcm(key, 16);
        gcm.Decrypt(nonce, frame.Slice(8, plain.Length), frame.Slice(8 + plain.Length, 16), plain);
        return plain;
    }

    /// <summary>The secret and the confirmation code of a pairing, from the ECDH agreement.
    /// The member counts go into the code too: they decide which device joins which, so a
    /// tampered count shows up as codes that differ.</summary>
    /// <param name="requesterKey">The requesting device's public key (SPKI).</param>
    /// <param name="accepterKey">The accepting device's public key (SPKI).</param>
    public static (byte[] secret, string code) PairingSecret(ECDiffieHellman mine, byte[] theirPublic, byte[] requesterKey, byte[] accepterKey,
        int requesterMembers, int accepterMembers)
    {
        using var other = ECDiffieHellman.Create();
        other.ImportSubjectPublicKeyInfo(theirPublic, out _);
        byte[] raw = mine.DeriveRawSecretAgreement(other.PublicKey);
        var counts = new byte[8];
        BinaryPrimitives.WriteInt32BigEndian(counts, requesterMembers);
        BinaryPrimitives.WriteInt32BigEndian(counts.AsSpan(4), accepterMembers);
        var salt = Concat(Concat(requesterKey, accepterKey), counts);
        byte[] secret = HKDF.DeriveKey(HashAlgorithmName.SHA256, raw, 32, salt, "alarmcal pair key"u8.ToArray());
        byte[] codeBytes = HKDF.DeriveKey(HashAlgorithmName.SHA256, raw, 8, salt, "alarmcal pair code"u8.ToArray());
        ulong n = BinaryPrimitives.ReadUInt64BigEndian(codeBytes) % 1_000_000;
        return (secret, n.ToString("000000"));
    }

    public static ECDiffieHellman NewPairingKey() => ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

    private static byte[] Concat(byte[] a, byte[] b)
    {
        var r = new byte[a.Length + b.Length];
        a.CopyTo(r, 0);
        b.CopyTo(r, a.Length);
        return r;
    }
}
