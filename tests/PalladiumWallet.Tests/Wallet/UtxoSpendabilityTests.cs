using PalladiumWallet.Core.Chain;
using PalladiumWallet.Core.Storage;
using PalladiumWallet.Core.Wallet;

namespace PalladiumWallet.Tests.Wallet;

/// <summary>
/// Boundary tests of the single spendability gate: heights 0 and -1 are mempool, never confirmed.
/// </summary>
public class UtxoSpendabilityTests
{
    private static readonly ChainProfile Profile = ChainProfiles.Mainnet;

    private static CachedUtxo Utxo(int height, bool verified = true, bool frozen = false, bool coinbase = false) => new()
    {
        Txid = "aa", Vout = 0, ValueSats = 1, Address = "a", Height = height,
        Verified = verified, Frozen = frozen, IsCoinbase = coinbase,
    };

    [Theory]
    [InlineData(0)]    // unconfirmed
    [InlineData(-1)]   // unconfirmed with unconfirmed parents (Electrum convention)
    public void MempoolUtxos_HaveZeroConfirmations_AndAreNeverSpendable(int height)
    {
        var utxo = Utxo(height, verified: true);
        Assert.Equal(0, utxo.Confirmations(tipHeight: 1_000));
        Assert.False(utxo.IsSpendable(Profile, tipHeight: 1_000));
    }

    [Theory]
    [InlineData(100, 100, 1)]
    [InlineData(100, 105, 6)]
    public void Confirmations_CountTheBlockItself(int height, int tip, int expected) =>
        Assert.Equal(expected, Utxo(height).Confirmations(tip));

    [Fact]
    public void RegularOutput_BecomesSpendableExactlyAtMinConfirmations()
    {
        var min = Profile.MinConfirmations;
        var utxo = Utxo(height: 100);
        Assert.False(utxo.IsSpendable(Profile, tipHeight: 100 + min - 2)); // min-1 confirmations
        Assert.True(utxo.IsSpendable(Profile, tipHeight: 100 + min - 1));  // min confirmations
    }

    [Fact]
    public void Coinbase_NeedsMaturityPlusOne_Confirmations()
    {
        var utxo = Utxo(height: 100, coinbase: true);
        var needed = Profile.CoinbaseMaturity + 1;
        Assert.Equal(needed, utxo.RequiredConfirmations(Profile));
        Assert.False(utxo.IsSpendable(Profile, tipHeight: 100 + needed - 2));
        Assert.True(utxo.IsSpendable(Profile, tipHeight: 100 + needed - 1));
    }

    [Fact]
    public void UnverifiedOrFrozen_AreNeverSpendable_EvenWhenDeeplyConfirmed()
    {
        Assert.False(Utxo(100, verified: false).IsSpendable(Profile, tipHeight: 10_000));
        Assert.False(Utxo(100, frozen: true).IsSpendable(Profile, tipHeight: 10_000));
        Assert.True(Utxo(100).IsSpendable(Profile, tipHeight: 10_000));
    }
}
