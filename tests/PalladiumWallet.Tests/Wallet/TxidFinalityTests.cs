using NBitcoin;
using PalladiumWallet.Core.Chain;
using PalladiumWallet.Core.Crypto;
using PalladiumWallet.Core.Storage;
using PalladiumWallet.Core.Wallet;

namespace PalladiumWallet.Tests.Wallet;

/// <summary>
/// A watch-only transaction carries the txid of the unsigned transaction. That txid is final only
/// when signing cannot change it: native segwit inputs have an empty scriptSig, while legacy and
/// wrapped inputs get one (the signature, or the redeem script) and so get a new txid.
/// </summary>
public class TxidFinalityTests
{
    private static readonly ChainProfile Profile = ChainProfiles.Regtest;

    private const string Mnemonic =
        "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about";

    private static (HdAccount Full, HdAccount Watch) Accounts(ScriptKind kind)
    {
        Assert.True(Bip39.TryParse(Mnemonic, out var m));
        var full = HdAccount.FromMnemonic(m!, null, kind, Profile);
        return (full, HdAccount.FromAccountXpub(full.AccountXpub, kind, Profile));
    }

    /// <summary>Builds a watch-only payment from one confirmed UTXO of <paramref name="watch"/>.</summary>
    private static BuiltTransaction WatchOnlyPayment(HdAccount full, HdAccount watch)
    {
        var net = PalladiumNetworks.Regtest;
        var funding = net.CreateTransaction();
        funding.Inputs.Add(new TxIn(new OutPoint(uint256.One, 0)));
        funding.Outputs.Add(Money.Satoshis(100_000), full.GetReceiveAddress(0));
        var utxo = new CachedUtxo
        {
            Txid = funding.GetHash().ToString(), Vout = 0, ValueSats = 100_000,
            Address = full.GetReceiveAddress(0).ToString(),
            AddressIndex = 0, Height = 100, Verified = true,
        };
        return new TransactionFactory(watch).Build(
            [utxo], new Dictionary<string, Transaction> { [utxo.Txid] = funding },
            full.GetReceiveAddress(5), 10_000, feeRateSatPerVByte: 1, changeIndex: 0, tipHeight: 1_000);
    }

    [Theory]
    [InlineData(ScriptKind.NativeSegwit, true)]
    [InlineData(ScriptKind.WrappedSegwit, false)]
    [InlineData(ScriptKind.Legacy, false)]
    public void WatchOnly_TxidIsFinal_OnlyForNativeSegwit(ScriptKind kind, bool expectedFinal)
    {
        var (full, watch) = Accounts(kind);

        var built = WatchOnlyPayment(full, watch);

        Assert.False(built.Signed);
        Assert.Equal(expectedFinal, built.TxidIsFinal);
    }

    [Theory]
    [InlineData(ScriptKind.NativeSegwit)]
    [InlineData(ScriptKind.WrappedSegwit)]
    [InlineData(ScriptKind.Legacy)]
    public void SignedTransaction_TxidIsAlwaysFinal(ScriptKind kind)
    {
        var (full, _) = Accounts(kind);

        var built = WatchOnlyPayment(full, full);

        Assert.True(built.Signed);
        Assert.True(built.TxidIsFinal);
    }
}
