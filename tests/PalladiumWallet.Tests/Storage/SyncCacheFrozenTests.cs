using PalladiumWallet.Core.Storage;

namespace PalladiumWallet.Tests.Storage;

/// <summary>The Frozen flag is a user decision: it is read from the cache and handed to the synchronizer.</summary>
public class SyncCacheFrozenTests
{
    private static CachedUtxo Utxo(string txid, int vout, bool frozen = false) =>
        new() { Txid = txid, Vout = vout, Address = "x", Frozen = frozen };

    [Fact]
    public void FrozenOutpoints_ListsOnlyTheFrozenUtxos()
    {
        var cache = new SyncCache
        {
            Utxos = [Utxo("aa", 0, frozen: true), Utxo("aa", 1), Utxo("bb", 3, frozen: true)],
        };

        var frozen = cache.FrozenOutpoints();

        Assert.Equal(2, frozen.Count);
        Assert.Contains(("aa", 0), frozen);
        Assert.Contains(("bb", 3), frozen);
        Assert.DoesNotContain(("aa", 1), frozen);
    }

    [Fact]
    public void NoFrozenUtxos_GivesAnEmptySet()
    {
        var cache = new SyncCache { Utxos = [Utxo("aa", 0)] };

        Assert.Empty(cache.FrozenOutpoints());
    }

    [Fact]
    public void NoCacheYet_GivesAnEmptySet()
    {
        Assert.Empty(new SyncCache().FrozenOutpoints());
    }
}
