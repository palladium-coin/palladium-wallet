using System.Text.Json;

namespace PalladiumWallet.Core.Net;

/// <summary>History entry for a scripthash (blockchain.scripthash.get_history).</summary>
public readonly record struct HistoryItem(string TxHash, int Height, long FeeSats);

/// <summary>UTXO reported by the server (blockchain.scripthash.listunspent).</summary>
public readonly record struct UnspentItem(string TxHash, int TxPos, long ValueSats, int Height);

/// <summary>The server answered a broadcast with a txid other than the one of the transaction sent.</summary>
public sealed class BroadcastTxidMismatchException(string expectedTxid, string reportedTxid) : Exception(
    $"The server reported txid {reportedTxid}, but the transaction sent is {expectedTxid}.")
{
    public string ExpectedTxid { get; } = expectedTxid;
    public string ReportedTxid { get; } = reportedTxid;
}

/// <summary>Merkle proof (blockchain.transaction.get_merkle).</summary>
public sealed record MerkleProofResponse(int BlockHeight, int Pos, IReadOnlyList<string> Merkle);

/// <summary>Range of headers (blockchain.block.headers): Hex is Count concatenated 80-byte headers.</summary>
public readonly record struct HeaderRangeResponse(int Count, string Hex);

/// <summary>Chain tip notified by blockchain.headers.subscribe.</summary>
public readonly record struct ChainTip(int Height, string HeaderHex);

/// <summary>Peer announced by server.peers.subscribe (null ports = not offered).</summary>
public sealed record PeerInfo(string Host, int? TcpPort, int? SslPort, string? Version);

/// <summary>
/// Typed helpers over the protocol methods (blueprint §10), on top of the
/// generic JSON-RPC transport of <see cref="ElectrumClient"/>.
/// </summary>
public static class ElectrumApi
{
    public static async Task<ChainTip> SubscribeHeadersAsync(this ElectrumClient c, CancellationToken ct = default)
    {
        var r = await c.RequestAsync("blockchain.headers.subscribe", ct);
        return new ChainTip(r.GetProperty("height").GetInt32(), r.GetProperty("hex").GetString()!);
    }

    /// <summary>Current status of the scripthash (null = never used).</summary>
    public static async Task<string?> SubscribeScripthashAsync(this ElectrumClient c, string scripthash,
        CancellationToken ct = default)
    {
        var r = await c.RequestAsync("blockchain.scripthash.subscribe", ct, scripthash);
        return r.ValueKind == JsonValueKind.Null ? null : r.GetString();
    }

    public static async Task<IReadOnlyList<HistoryItem>> GetHistoryAsync(this ElectrumClient c,
        string scripthash, CancellationToken ct = default)
    {
        var r = await c.RequestAsync("blockchain.scripthash.get_history", ct, scripthash);
        return [.. r.EnumerateArray().Select(e => new HistoryItem(
            e.GetProperty("tx_hash").GetString()!,
            e.GetProperty("height").GetInt32(),
            e.TryGetProperty("fee", out var fee) ? fee.GetInt64() : 0))];
    }

    public static async Task<IReadOnlyList<UnspentItem>> ListUnspentAsync(this ElectrumClient c,
        string scripthash, CancellationToken ct = default)
    {
        var r = await c.RequestAsync("blockchain.scripthash.listunspent", ct, scripthash);
        return [.. r.EnumerateArray().Select(e => new UnspentItem(
            e.GetProperty("tx_hash").GetString()!,
            e.GetProperty("tx_pos").GetInt32(),
            e.GetProperty("value").GetInt64(),
            e.GetProperty("height").GetInt32()))];
    }

    public static async Task<string> GetTransactionAsync(this ElectrumClient c, string txid,
        CancellationToken ct = default)
    {
        var r = await c.RequestAsync("blockchain.transaction.get", ct, txid);
        return r.GetString()!;
    }

    public static async Task<MerkleProofResponse> GetMerkleAsync(this ElectrumClient c, string txid,
        int height, CancellationToken ct = default)
    {
        var r = await c.RequestAsync("blockchain.transaction.get_merkle", ct, txid, height);
        return new MerkleProofResponse(
            r.GetProperty("block_height").GetInt32(),
            r.GetProperty("pos").GetInt32(),
            [.. r.GetProperty("merkle").EnumerateArray().Select(m => m.GetString()!)]);
    }

    public static async Task<string> GetBlockHeaderAsync(this ElectrumClient c, int height,
        CancellationToken ct = default)
    {
        var r = await c.RequestAsync("blockchain.block.header", ct, height);
        return r.GetString()!;
    }

    /// <summary>
    /// Range fetch (blockchain.block.headers): one RPC returns up to <paramref name="count"/>
    /// concatenated 80-byte headers starting at <paramref name="startHeight"/>. Used to anchor
    /// a tx height to a hardcoded checkpoint (§7.3) without one round-trip per header — critical
    /// on high-latency links (mobile) where thousands of serial single-header calls stall sync.
    /// The server may return fewer than requested (see <see cref="HeaderRangeResponse.Count"/>);
    /// callers must loop until the full range is covered.
    /// </summary>
    public static async Task<HeaderRangeResponse> GetBlockHeadersAsync(this ElectrumClient c,
        int startHeight, int count, CancellationToken ct = default)
    {
        var r = await c.RequestAsync("blockchain.block.headers", ct, startHeight, count);
        return new HeaderRangeResponse(
            r.GetProperty("count").GetInt32(),
            r.GetProperty("hex").GetString()!);
    }

    public static async Task<string> BroadcastAsync(this ElectrumClient c, string rawTxHex,
        CancellationToken ct = default)
    {
        var r = await c.RequestAsync("blockchain.transaction.broadcast", ct, rawTxHex);
        return r.GetString()!;
    }

    /// <summary>
    /// Broadcasts a signed transaction and accepts the reply only if it names the txid computed
    /// locally. The txid is fixed by the signed bytes, so a different one means the server is not
    /// reporting what was sent. Used by every send path (GUI and CLI).
    /// </summary>
    public static async Task<string> BroadcastVerifiedAsync(this ElectrumClient c, string rawTxHex,
        string expectedTxid, CancellationToken ct = default)
    {
        var reported = await c.BroadcastAsync(rawTxHex, ct);
        if (!string.Equals(reported, expectedTxid, StringComparison.OrdinalIgnoreCase))
            throw new BroadcastTxidMismatchException(expectedTxid, reported);
        return reported;
    }

    /// <summary>
    /// Whether the server holds the transaction with this txid, in its mempool or in the chain. It is
    /// true only when the raw transaction the server returns hashes to <paramref name="txid"/>: the
    /// reply of a broadcast is not evidence, the transaction itself is. A server that does not know the
    /// transaction answers with a JSON-RPC error, and so does a server that returns something that is
    /// not a transaction: both are "no". A transport failure is not an answer and propagates.
    /// </summary>
    public static async Task<bool> HasTransactionAsync(this ElectrumClient c, string txid,
        NBitcoin.Network network, CancellationToken ct = default)
    {
        string raw;
        try
        {
            raw = await c.GetTransactionAsync(txid, ct);
            return string.Equals(NBitcoin.Transaction.Parse(raw, network).GetHash().ToString(), txid,
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ElectrumServerException or FormatException)
        {
            return false;
        }
    }

    /// <summary>Estimated fee in coin/kB for the given target; -1 if the server cannot estimate.</summary>
    public static async Task<decimal> EstimateFeeAsync(this ElectrumClient c, int targetBlocks,
        CancellationToken ct = default)
    {
        var r = await c.RequestAsync("blockchain.estimatefee", ct, targetBlocks);
        return r.GetDecimal();
    }

    public static async Task<decimal> RelayFeeAsync(this ElectrumClient c, CancellationToken ct = default)
    {
        var r = await c.RequestAsync("blockchain.relayfee", ct);
        return r.GetDecimal();
    }

    public static async Task<string> BannerAsync(this ElectrumClient c, CancellationToken ct = default)
    {
        var r = await c.RequestAsync("server.banner", ct);
        return r.GetString()!;
    }

    public static Task PingAsync(this ElectrumClient c, CancellationToken ct = default) =>
        c.RequestAsync("server.ping", ct);

    /// <summary>Peers announced by the server (discovery of other servers, §9).</summary>
    public static async Task<IReadOnlyList<PeerInfo>> GetPeersAsync(this ElectrumClient c,
        CancellationToken ct = default)
    {
        var r = await c.RequestAsync("server.peers.subscribe", ct);
        return ParsePeers(r);
    }

    /// <summary>
    /// Parses the server.peers.subscribe response: a list of
    /// [ip, hostname, ["v1.4.2", "pN", "tPORT", "sPORT", ...]];
    /// "t"/"s" without a number = network default port (resolved by the caller).
    /// The response is untrusted server data: any unexpected shape (non-array,
    /// wrong element types) is skipped, never thrown on.
    /// </summary>
    public static IReadOnlyList<PeerInfo> ParsePeers(JsonElement response)
    {
        var peers = new List<PeerInfo>();
        if (response.ValueKind != JsonValueKind.Array)
            return peers;
        foreach (var entry in response.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Array || entry.GetArrayLength() < 3
                || entry[2].ValueKind != JsonValueKind.Array)
                continue;
            var host = AsString(entry[1]);
            if (string.IsNullOrWhiteSpace(host))
                host = AsString(entry[0]);
            if (string.IsNullOrWhiteSpace(host))
                continue;

            int? tcp = null, ssl = null;
            string? version = null;
            foreach (var feature in entry[2].EnumerateArray())
            {
                var f = AsString(feature);
                if (string.IsNullOrEmpty(f))
                    continue;
                switch (f[0])
                {
                    case 'v': version = f[1..]; break;
                    case 't': tcp = int.TryParse(f[1..], out var t) ? t : 0; break;
                    case 's': ssl = int.TryParse(f[1..], out var s) ? s : 0; break;
                }
            }
            if (tcp is not null || ssl is not null)
                peers.Add(new PeerInfo(host!, tcp, ssl, version));
        }
        return peers;
    }

    private static string? AsString(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.String)
            return null;
        try
        {
            return element.GetString();
        }
        catch (InvalidOperationException)
        {
            // Syntactically valid JSON string that cannot be transcoded to UTF-16
            // (invalid UTF-8 bytes / lone surrogates) — untrusted server data,
            // treat as absent (found by fuzzing).
            return null;
        }
    }
}
