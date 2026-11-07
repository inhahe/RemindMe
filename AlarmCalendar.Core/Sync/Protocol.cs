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
    public const string PairAccept = "pair_accept";
    public const string PairReject = "pair_reject";
    // Encrypted, within a group.
    public const string Digest = "digest";
    public const string Tasks = "tasks";
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

    // replication
    public List<DigestEntry>? Digest { get; set; }
    public List<AlarmTask>? Tasks { get; set; }

    // pairing
    /// <summary>An ephemeral ECDH P-256 public key (SubjectPublicKeyInfo, base64).</summary>
    public string? PairKey { get; set; }
    /// <summary>The group's ID and secret, AES-GCM sealed under the pairing secret (base64).</summary>
    public string? Sealed { get; set; }
    public string? Reason { get; set; }
}

public static class Protocol
{
    public const string App = "AlarmCalendar";
    public const int Version = 1;
    public const int DefaultPort = 37660;
    /// <summary>Discovery always uses this UDP port, whatever TCP port a node listens on,
    /// so several instances on one machine (or a custom port) still find each other.</summary>
    public const int DiscoveryPort = 37660;
    public const int MaxFrame = 32 * 1024 * 1024;

    public static byte[] Serialize(SyncMessage m) => JsonSerializer.SerializeToUtf8Bytes(m, Json.Wire);

    public static SyncMessage? Deserialize(ReadOnlySpan<byte> bytes)
    {
        try { return JsonSerializer.Deserialize<SyncMessage>(bytes, Json.Wire); }
        catch (JsonException) { return null; }
    }

    /// <summary>Writes one frame: a 4-byte big-endian length, then the body.</summary>
    public static async Task WriteFrameAsync(Stream s, ReadOnlyMemory<byte> body, CancellationToken ct)
    {
        var header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, body.Length);
        await s.WriteAsync(header, ct).ConfigureAwait(false);
        await s.WriteAsync(body, ct).ConfigureAwait(false);
        await s.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Reads one frame body; null at a clean end of stream.</summary>
    public static async Task<byte[]?> ReadFrameAsync(Stream s, CancellationToken ct)
    {
        var header = new byte[4];
        if (!await ReadExactlyOrEndAsync(s, header, ct).ConfigureAwait(false)) return null;
        int len = BinaryPrimitives.ReadInt32BigEndian(header);
        if (len < 0 || len > MaxFrame) throw new InvalidDataException($"Frame length {len} is out of range.");
        var body = new byte[len];
        if (!await ReadExactlyOrEndAsync(s, body, ct).ConfigureAwait(false)) throw new EndOfStreamException();
        return body;
    }

    private static async Task<bool> ReadExactlyOrEndAsync(Stream s, byte[] buffer, CancellationToken ct)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = await s.ReadAsync(buffer.AsMemory(read), ct).ConfigureAwait(false);
            if (n == 0)
            {
                if (read == 0) return false;
                throw new EndOfStreamException();
            }
            read += n;
        }
        return true;
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
            return rest.Length == 0 || (rest.StartsWith(':') && int.TryParse(rest[1..], out port) && port is > 0 and < 65536);
        }
        int colon = text.LastIndexOf(':');
        if (colon > 0 && text.IndexOf(':') == colon)
        {
            host = text[..colon];
            return int.TryParse(text[(colon + 1)..], out port) && port is > 0 and < 65536;
        }
        host = text;
        return true;
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
        bool iAmLow = string.CompareOrdinal(myNode, peerNode) < 0;
        var (lowNode, highNode) = iAmLow ? (myNode, peerNode) : (peerNode, myNode);
        var salt = iAmLow ? Concat(myNonce, peerNonce) : Concat(peerNonce, myNonce);
        byte[] lowToHigh = HKDF.DeriveKey(HashAlgorithmName.SHA256, groupKey, 32, salt, Encoding.UTF8.GetBytes($"alarmcal v1 {lowNode}>{highNode}"));
        byte[] highToLow = HKDF.DeriveKey(HashAlgorithmName.SHA256, groupKey, 32, salt, Encoding.UTF8.GetBytes($"alarmcal v1 {highNode}>{lowNode}"));
        return iAmLow ? (lowToHigh, highToLow) : (highToLow, lowToHigh);
    }

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

    /// <summary>The secret and the confirmation code of a pairing, from the ECDH agreement.</summary>
    /// <param name="requesterKey">The joining device's public key (SPKI).</param>
    /// <param name="accepterKey">The accepting device's public key (SPKI).</param>
    public static (byte[] secret, string code) PairingSecret(ECDiffieHellman mine, byte[] theirPublic, byte[] requesterKey, byte[] accepterKey)
    {
        using var other = ECDiffieHellman.Create();
        other.ImportSubjectPublicKeyInfo(theirPublic, out _);
        byte[] raw = mine.DeriveRawSecretAgreement(other.PublicKey);
        var salt = Concat(requesterKey, accepterKey);
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
