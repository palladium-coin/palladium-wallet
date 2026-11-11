using NBitcoin;
using PalladiumWallet.Core.Net;

namespace PalladiumWallet.Core.Spv;

/// <summary>
/// Binds a server-supplied raw transaction to the txid it was requested under (ADR 0043).
/// A Merkle proof only proves that a txid is in a block; without this check a hostile server
/// could answer a request for a real txid with a different, fabricated transaction and still
/// pass the proof, since the proof never sees the transaction bytes.
/// </summary>
public static class VerifiedTransactions
{
    /// <summary>
    /// Parses <paramref name="rawHex"/> and requires its txid (double SHA-256 of the
    /// non-witness serialisation) to equal <paramref name="txid"/>.
    /// </summary>
    /// <exception cref="SpvVerificationException">The transaction does not hash to <paramref name="txid"/>.</exception>
    public static Transaction Parse(string rawHex, string txid, Network network)
    {
        var tx = Transaction.Parse(rawHex, network);
        if (!uint256.TryParse(txid, out var expected) || tx.GetHash() != expected)
            throw new SpvVerificationException(
                $"Server returned a transaction that does not hash to {txid}: server is not trustworthy.");
        return tx;
    }

    /// <summary>Fetches a transaction and verifies it against <paramref name="txid"/>.</summary>
    public static async Task<Transaction> GetVerifiedTransactionAsync(this ElectrumClient client,
        string txid, Network network, CancellationToken ct = default) =>
        Parse(await client.GetTransactionAsync(txid, ct), txid, network);
}
