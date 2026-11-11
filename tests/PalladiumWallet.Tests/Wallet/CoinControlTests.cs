using PalladiumWallet.Core.Chain;
using PalladiumWallet.Core.Storage;
using PalladiumWallet.Core.Wallet;

namespace PalladiumWallet.Tests.Wallet;

/// <summary>Freezing a coin and keeping the balance in step with it.</summary>
public class CoinControlTests
{
    private static readonly ChainProfile Profile = ChainProfiles.Mainnet;

    /// <summary>Two confirmed, verified coins, 150 sat in all, at a tip where both are spendable.</summary>
    private static SyncCache TwoSpendableCoins() => new()
    {
        TipHeight = 1_000,
        SpendableSats = 150,
        Utxos =
        [
            new CachedUtxo { Txid = "aa", Vout = 0, ValueSats = 100, Address = "x", Height = 100, Verified = true },
            new CachedUtxo { Txid = "aa", Vout = 1, ValueSats = 50, Address = "x", Height = 100, Verified = true },
        ],
    };

    [Fact]
    public void Freezing_a_coin_drops_it_from_the_balance()
    {
        var cache = TwoSpendableCoins();

        Assert.True(CoinControl.SetFrozen(cache, Profile, "aa", 0, frozen: true));

        Assert.True(cache.Utxos[0].Frozen);
        Assert.Equal(50, cache.SpendableSats);
    }

    [Fact]
    public void Unfreezing_a_coin_gives_its_value_back()
    {
        var cache = TwoSpendableCoins();
        CoinControl.SetFrozen(cache, Profile, "aa", 0, frozen: true);

        CoinControl.SetFrozen(cache, Profile, "aa", 0, frozen: false);

        Assert.False(cache.Utxos[0].Frozen);
        Assert.Equal(150, cache.SpendableSats);
    }

    [Fact]
    public void AnUnknownOutpoint_IsReportedAndChangesNothing()
    {
        var cache = TwoSpendableCoins();

        Assert.False(CoinControl.SetFrozen(cache, Profile, "bb", 0, frozen: true));

        Assert.All(cache.Utxos, u => Assert.False(u.Frozen));
        Assert.Equal(150, cache.SpendableSats);
    }

    [Fact]
    public void ApplyFrozen_MakesTheFlagsEqualToTheGivenSet()
    {
        var cache = TwoSpendableCoins();
        cache.Utxos[0].Frozen = true;                       // a stale flag from before the sync

        CoinControl.ApplyFrozen(cache, Profile, new HashSet<(string Txid, int Vout)> { ("aa", 1) });

        Assert.False(cache.Utxos[0].Frozen);
        Assert.True(cache.Utxos[1].Frozen);
        Assert.Equal(100, cache.SpendableSats);
    }

    [Fact]
    public void AnUnverifiedCoin_IsNotCountedAsSpendable_WhateverItsFlag()
    {
        var cache = TwoSpendableCoins();
        cache.Utxos[1].Verified = false;

        CoinControl.RefreshSpendable(cache, Profile);

        Assert.Equal(100, cache.SpendableSats);
    }
}
