using System.Text.Json;
using PalladiumWallet.Core.Chain;
using PalladiumWallet.Core.Net;

namespace PalladiumWallet.Core.Spv;

/// <summary>The server is serving a different chain than the wallet's network profile.</summary>
public sealed class WrongChainException(string host, int port, string reason) : Exception(
    $"The server {host}:{port} is not on the expected chain: {reason}")
{
    public string Host { get; } = host;
    public int Port { get; } = port;
    public string Reason { get; } = reason;
}

/// <summary>
/// Chain-identity check run right after the handshake (ADR 0036). Mainnet reuses Bitcoin's
/// genesis hash, so the genesis alone cannot tell Palladium from Bitcoin: the check also pins
/// the header at the highest hardcoded checkpoint not above the server's tip.
/// </summary>
public static class ChainVerifier
{
    /// <summary>
    /// Throws <see cref="WrongChainException"/> if the server reports another genesis, or serves
    /// at the highest checkpoint height (≤ its tip) a header other than the checkpoint. A tip
    /// below the first checkpoint (young testnet, server still syncing) has nothing to compare
    /// and is accepted on the genesis check alone.
    /// </summary>
    public static async Task VerifyChainAsync(this ElectrumClient client, ChainProfile profile,
        CancellationToken ct = default)
    {
        string? genesis = null;
        try
        {
            var features = await client.RequestAsync("server.features", ct);
            if (features.ValueKind == JsonValueKind.Object
                && features.TryGetProperty("genesis_hash", out var g) && g.ValueKind == JsonValueKind.String)
                genesis = g.GetString();
        }
        catch (ElectrumServerException) { /* reported below as a missing genesis_hash */ }

        if (genesis is null)
            throw new WrongChainException(client.Host, client.Port, "it does not report a genesis_hash");
        if (!string.Equals(genesis, profile.GenesisHash, StringComparison.OrdinalIgnoreCase))
            throw new WrongChainException(client.Host, client.Port,
                $"genesis {genesis} (expected {profile.GenesisHash})");

        var tip = await client.SubscribeHeadersAsync(ct);
        Checkpoint? best = null;
        foreach (var cp in profile.Checkpoints)
            if (cp.Height <= tip.Height && (best is null || cp.Height > best.Value.Height))
                best = cp;
        if (best is not { } anchor)
            return;

        BlockHeaderInfo header;
        try
        {
            header = BlockHeaderInfo.Parse(await client.GetBlockHeaderAsync(anchor.Height, ct));
        }
        catch (Exception ex) when (ex is ElectrumServerException or ArgumentException or FormatException
                                       or JsonException or InvalidOperationException)
        {
            throw new WrongChainException(client.Host, client.Port,
                $"no valid header at checkpoint height {anchor.Height} ({ex.Message})");
        }
        if (!header.MatchesCheckpoint(anchor))
            throw new WrongChainException(client.Host, client.Port,
                $"header at height {anchor.Height} is {header.Hash} (checkpoint {anchor.BlockHash})");
    }
}
