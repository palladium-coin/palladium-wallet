using NBitcoin;
using PalladiumWallet.Core.Chain;
using PalladiumWallet.Core.Crypto;
using PalladiumWallet.Core.Storage;
using PalladiumWallet.Core.Wallet;

namespace PalladiumWallet.Tests.Wallet;

/// <summary>Spend-policy limits: send-all affordability and the standard-size cap.</summary>
public class TransactionFactoryLimitsTests
{
    private static readonly Network Net = PalladiumNetworks.Regtest;

    private static HdAccount Account()
    {
        Assert.True(Bip39.TryParse(
            "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about",
            out var mnemonic));
        return HdAccount.FromMnemonic(mnemonic!, null, ScriptKind.NativeSegwit, ChainProfiles.Regtest);
    }

    /// <summary>One funding tx with <paramref name="count"/> outputs to receive/0, each a verified UTXO.</summary>
    private static (List<CachedUtxo>, Dictionary<string, Transaction>) Fund(HdAccount account, int count, long satsEach)
    {
        var funding = Net.CreateTransaction();
        funding.Inputs.Add(new TxIn(new OutPoint(uint256.One, 0)));
        for (var i = 0; i < count; i++)
            funding.Outputs.Add(Money.Satoshis(satsEach), account.GetReceiveAddress(0));
        var txid = funding.GetHash().ToString();
        var utxos = Enumerable.Range(0, count).Select(i => new CachedUtxo
        {
            Txid = txid, Vout = i, ValueSats = satsEach,
            Address = account.GetReceiveAddress(0).ToString(),
            AddressIndex = 0, Height = 100, Verified = true,
        }).ToList();
        return (utxos, new Dictionary<string, Transaction> { [txid] = funding });
    }

    [Fact]
    public void ZeroAmount_IsRejectedBeforeCoinSelection()
    {
        var account = Account();
        var (utxos, txs) = Fund(account, 1, 100_000);

        var ex = Assert.Throws<WalletSpendException>(() => new TransactionFactory(account).Build(
            utxos, txs, account.GetReceiveAddress(5), amountSats: 0,
            feeRateSatPerVByte: 1, changeIndex: 0, tipHeight: 1_000));
        Assert.Contains("greater than zero", ex.Message);
    }

    [Fact]
    public void Payment_BelowTheDustLimitOfItsScript_IsRejected()
    {
        var account = Account();
        var (utxos, txs) = Fund(account, 1, 100_000);
        var to = account.GetReceiveAddress(5);
        Assert.Equal(294, TransactionFactory.DustLimitSats(to.ScriptPubKey, account.Profile)); // P2WPKH

        var ex = Assert.Throws<WalletSpendException>(() => new TransactionFactory(account).Build(
            utxos, txs, to, amountSats: 293,
            feeRateSatPerVByte: 1, changeIndex: 0, tipHeight: 1_000));
        Assert.Contains("dust", ex.Message);
    }

    [Fact]
    public void Payment_AtTheDustLimit_IsAccepted()
    {
        var account = Account();
        var (utxos, txs) = Fund(account, 1, 100_000);
        var to = account.GetReceiveAddress(5);

        var built = new TransactionFactory(account).Build(
            utxos, txs, to, amountSats: 294,
            feeRateSatPerVByte: 1, changeIndex: 0, tipHeight: 1_000);

        Assert.Contains(built.Transaction.Outputs, o => o.Value.Satoshi == 294 && o.ScriptPubKey == to.ScriptPubKey);
    }

    // Golden values measured on Palladium Core v2.1.0 (regtest, -acceptnonstdtxn=0, testmempoolaccept):
    // P2PKH 545 refused / 546 accepted, P2SH 539 / 540, P2WPKH 293 / 294.
    [Fact]
    public void DustLimit_MatchesPalladiumCore_PerScriptType()
    {
        var account = Account();
        var profile = account.Profile;
        Assert.Equal(294, TransactionFactory.DustLimitSats(account.GetReceiveAddress(0).ScriptPubKey, profile));
        Assert.Equal(546, TransactionFactory.DustLimitSats(new Key().PubKey.Hash.ScriptPubKey, profile));
        Assert.Equal(540, TransactionFactory.DustLimitSats(new Key().PubKey.Hash.ScriptPubKey.Hash.ScriptPubKey, profile));
        Assert.Equal(330, TransactionFactory.DustLimitSats(new Key().PubKey.GetScriptPubKey(ScriptPubKeyType.Segwit).WitHash.ScriptPubKey, profile));
    }

    [Fact]
    public void DustLimit_ScalesWithTheProfileRelayRate()
    {
        var profile = ChainProfiles.Mainnet with { DustRelaySatPerVByte = 6 };
        Assert.Equal(1092, TransactionFactory.DustLimitSats(new Key().PubKey.Hash.ScriptPubKey, profile));
    }

    [Fact]
    public void EveryProfile_DeclaresThePalladiumDustRelayRate()
    {
        Assert.All(new[] { ChainProfiles.Mainnet, ChainProfiles.Testnet, ChainProfiles.Regtest },
            p => Assert.Equal(3, p.DustRelaySatPerVByte));
    }

    [Fact]
    public void SendAll_WithBalanceBelowTheFee_IsRejectedAsInsufficientFunds()
    {
        var account = Account();
        var (utxos, txs) = Fund(account, 1, 50); // 50 sat cannot pay for a ~110 vB transaction

        var ex = Assert.Throws<WalletSpendException>(() => new TransactionFactory(account).Build(
            utxos, txs, account.GetReceiveAddress(5), amountSats: 0,
            feeRateSatPerVByte: 2, changeIndex: 0, tipHeight: 1_000, sendAll: true));
        Assert.Contains("Insufficient funds", ex.Message);
    }

    [Fact]
    public void SendAll_OverTheStandardSizeCap_IsRejectedWithGuidance()
    {
        var account = Account();
        // ~68 vB per P2WPKH input: 1,700 inputs exceed the 100,000 vB standard limit.
        var (utxos, txs) = Fund(account, 1_700, 10_000);

        var ex = Assert.Throws<WalletSpendException>(() => new TransactionFactory(account).Build(
            utxos, txs, account.GetReceiveAddress(5), amountSats: 0,
            feeRateSatPerVByte: 1, changeIndex: 0, tipHeight: 1_000, sendAll: true));
        Assert.Contains("Send-all cannot fit in one standard transaction", ex.Message);
    }

    [Fact]
    public void AmountAboveTheBalance_IsRejectedAsInsufficientFunds()
    {
        var account = Account();
        var (utxos, txs) = Fund(account, 1, 100_000);

        Assert.Throws<WalletSpendException>(() => new TransactionFactory(account).Build(
            utxos, txs, account.GetReceiveAddress(5), amountSats: 500_000,
            feeRateSatPerVByte: 2, changeIndex: 0, tipHeight: 1_000));
    }
}
