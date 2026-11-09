using PalladiumWallet.Core.Chain;
using PalladiumWallet.Core.Storage;

namespace PalladiumWallet.Core.Wallet;

/// <summary>
/// Freezing a coin: the user keeps a UTXO out of every spend (automatic and manual selection
/// alike). The flag lives on <see cref="CachedUtxo.Frozen"/>, and the balance is recomputed at once
/// so that it never counts a coin the wallet will refuse to spend.
/// </summary>
public static class CoinControl
{
    /// <summary>Sets or clears the freeze of one coin. False when the outpoint is not in the cache.</summary>
    public static bool SetFrozen(SyncCache cache, ChainProfile profile, string txid, int vout, bool frozen)
    {
        var utxo = cache.Utxos.FirstOrDefault(u => u.Txid == txid && u.Vout == vout);
        if (utxo is null)
            return false;
        utxo.Frozen = frozen;
        RefreshSpendable(cache, profile);
        return true;
    }

    /// <summary>
    /// Makes the flags of a freshly built cache equal to <paramref name="frozen"/>. Used when a sync
    /// finishes: the user may have frozen or unfrozen a coin while the sync was running, and that
    /// latest decision wins over the set the sync started with.
    /// </summary>
    public static void ApplyFrozen(SyncCache cache, ChainProfile profile, IReadOnlySet<(string Txid, int Vout)> frozen)
    {
        foreach (var utxo in cache.Utxos)
            utxo.Frozen = frozen.Contains((utxo.Txid, utxo.Vout));
        RefreshSpendable(cache, profile);
    }

    /// <summary>Recomputes the balance from the UTXOs, with the same gate coin selection uses.</summary>
    public static void RefreshSpendable(SyncCache cache, ChainProfile profile) =>
        cache.SpendableSats = cache.Utxos
            .Where(u => u.IsSpendable(profile, cache.TipHeight))
            .Sum(u => u.ValueSats);
}
