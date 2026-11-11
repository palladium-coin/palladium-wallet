using System.Text.Json;
using PalladiumWallet.Core.Chain;

namespace PalladiumWallet.Core.Net;

/// <summary>Known indexing server (bootstrap or discovered from peers).</summary>
public sealed record KnownServer(string Host, int TcpPort, int SslPort, string? Version = null)
{
    public int PortFor(bool useSsl) => useSsl ? SslPort : TcpPort;

    public override string ToString() =>
        $"{Host}  ·  tcp {TcpPort} / ssl {SslPort}" + (Version is null ? "" : $"  ·  v{Version}");
}

/// <summary>
/// Server registry (blueprint §9): starts from the profile bootstrap list (§3),
/// is enriched with peers announced via server.peers.subscribe, and persists
/// discovered servers to file for subsequent sessions.
/// </summary>
public sealed class ServerRegistry
{
    private readonly ChainProfile _profile;
    private readonly string _filePath;
    private readonly List<KnownServer> _servers = [];
    private readonly object _lock = new();

    public ServerRegistry(ChainProfile profile, string filePath)
    {
        _profile = profile;
        _filePath = filePath;

        foreach (var s in profile.BootstrapServers)
            _servers.Add(new KnownServer(s.Host, s.TcpPort, s.SslPort));

        if (File.Exists(filePath))
        {
            try
            {
                var saved = JsonSerializer.Deserialize<List<KnownServer>>(File.ReadAllText(filePath)) ?? [];
                foreach (var s in saved)
                    AddIfNew(s);
            }
            catch (JsonException)
            {
                // Corrupted file: fall back to bootstrap servers only.
            }
        }
    }

    public IReadOnlyList<KnownServer> All
    {
        get { lock (_lock) return [.. _servers]; }
    }

    /// <summary>
    /// The servers shipped with the wallet (the profile's bootstrap list). Only these are tried
    /// automatically as a fallback: a server announced by a peer was named by someone else, so it
    /// is listed and selectable but never connected to without the user choosing it.
    /// </summary>
    public IReadOnlyList<KnownServer> Trusted
    {
        get { lock (_lock) return [.. _servers.Where(IsBootstrap)]; }
    }

    /// <summary>First trusted server: default when the user does not specify one.</summary>
    public KnownServer? Default
    {
        get { lock (_lock) return _servers.FirstOrDefault(IsBootstrap); }
    }

    private bool IsBootstrap(KnownServer s) =>
        _profile.BootstrapServers.Any(b => string.Equals(b.Host, s.Host, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Queries the connected server for its announced peers and adds new ones to the
    /// registry (missing ports fall back to the profile defaults). Returns the count of new entries.
    /// </summary>
    public async Task<int> DiscoverAsync(ElectrumClient client, CancellationToken ct = default)
    {
        var peers = await client.GetPeersAsync(ct);
        var added = 0;
        lock (_lock)
        {
            foreach (var peer in peers)
            {
                if (!IsPublicHost(peer.Host))
                    continue; // a peer list naming this machine or a private network is not a server to add
                var server = new KnownServer(
                    peer.Host,
                    peer.TcpPort is > 0 and <= 65535 ? peer.TcpPort.Value : _profile.DefaultTcpPort,
                    peer.SslPort is > 0 and <= 65535 ? peer.SslPort.Value : _profile.DefaultSslPort,
                    peer.Version);
                if (AddIfNew(server))
                    added++;
            }
            if (added > 0)
                Save();
        }
        return added;
    }

    /// <summary>
    /// False for a host that cannot be a public indexing server: empty or oversized, "localhost",
    /// a loopback / private / link-local / unspecified IP address, or a name with characters a host
    /// name cannot have. Peers are untrusted data; connecting to such an address would make the
    /// wallet probe the user's own machine or network on a stranger's say-so.
    /// </summary>
    public static bool IsPublicHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host) || host.Length > 253)
            return false;
        if (System.Net.IPAddress.TryParse(host, out var ip))
        {
            if (ip.IsIPv4MappedToIPv6)
                ip = ip.MapToIPv4();
            if (System.Net.IPAddress.IsLoopback(ip) || ip.Equals(System.Net.IPAddress.Any)
                || ip.Equals(System.Net.IPAddress.IPv6Any) || ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal
                || ip.IsIPv6Multicast)
                return false;
            var b = ip.GetAddressBytes();
            if (b.Length == 16)
                return (b[0] & 0xFE) != 0xFC; // fc00::/7 unique local
            return !(b[0] is 0 or 10 or 127 || b[0] >= 224
                || (b[0] == 169 && b[1] == 254)
                || (b[0] == 172 && b[1] is >= 16 and <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 100 && b[1] is >= 64 and <= 127)); // CGNAT
        }
        if (!host.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-'))
            return false;
        var lower = host.ToLowerInvariant();
        return lower != "localhost" && !lower.EndsWith(".localhost") && !lower.EndsWith(".local")
            && !lower.EndsWith(".internal") && !lower.EndsWith(".lan");
    }

    private bool AddIfNew(KnownServer server)
    {
        if (_servers.Any(s => string.Equals(s.Host, server.Host, StringComparison.OrdinalIgnoreCase)))
            return false;
        _servers.Add(server);
        return true;
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        // Only discovered servers are saved; bootstrap servers come from the profile.
        var discovered = _servers
            .Where(s => !_profile.BootstrapServers.Any(b =>
                string.Equals(b.Host, s.Host, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        File.WriteAllText(_filePath, JsonSerializer.Serialize(discovered,
            new JsonSerializerOptions { WriteIndented = true }));
    }
}
