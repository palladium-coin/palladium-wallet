using NBitcoin;
using PalladiumWallet.Core.Chain;
using PalladiumWallet.Core.Crypto;
using PalladiumWallet.Core.Storage;
using PalladiumWallet.Core.Wallet;
using PalladiumWallet.Tests.Crypto;

namespace PalladiumWallet.Tests.Wallet;

/// <summary>
/// P2SH-wrapped (P2SH-P2WSH, BIP48 script type 1') multisig wallets. The addresses and the wallet ID are golden values
/// from Palladium Core v2.1.0 (<c>deriveaddresses</c> and <c>getdescriptorinfo</c> of <c>sh(wsh(sortedmulti(...)))</c>
/// over the same three cosigner keys, regtest): a value that differs from the node's is a blocking bug.
/// </summary>
public class WrappedMultisigTests
{
    private static readonly Network Net = PalladiumNetworks.Regtest;
    private static readonly ChainProfile Profile = ChainProfiles.Regtest;
    private const ScriptKind Wrapped = ScriptKind.WrappedSegwitMultisig;

    private static string KeyOf(string words)
    {
        Assert.True(Bip39.TryParse(words, out var mnemonic));
        var root = ExtKey.CreateFromSeed(Bip39.ToSeed(mnemonic!, null));
        var path = DerivationPaths.AccountPath(Wrapped, Profile);
        return new Cosigner(root.Neuter().PubKey.GetHDFingerPrint(), path, root.Derive(path).Neuter()).ToText(Wrapped, Profile);
    }

    private static MultisigAccount Account(string? ownWords) =>
        WalletLoader.NewMultisig(2, [KeyOf(MultisigAccountTests.Words1), KeyOf(MultisigAccountTests.Words2), KeyOf(MultisigAccountTests.Words3)],
            ownWords, null, Profile, Wrapped).Account;

    [Fact]
    public void TheCosignerKeysUseTheBip48ScriptTypeOneAndTheUpubHeader()
    {
        Assert.StartsWith("[73c5da0a/48'/1'/0'/1']Upub", KeyOf(MultisigAccountTests.Words1));
    }

    [Fact]
    public void TheAddresses_AndTheWalletId_MatchPalladiumCore()
    {
        var account = Account(MultisigAccountTests.Words1);
        Assert.Equal("oTH7iKGcgh5hBcfaoETbpoRP7hctCJbihX", account.GetReceiveAddress(0).ToString());
        Assert.Equal("oTuLvd8v5sqLxucUEkA6uHHfYNYzqokLPV", account.GetReceiveAddress(1).ToString());
        Assert.Equal("oPUQ6JzWVooJ8os2S1enjBo1f2scVPqtU6", account.GetChangeAddress(0).ToString());
        Assert.Equal("ryrrzpe2", account.WalletId);
        Assert.StartsWith("sh(wsh(sortedmulti(2,", account.ToDescriptor());
    }

    [Fact]
    public void ANativeAndAWrappedWalletOfTheSameSeedsAreDifferentWallets()
    {
        var native = MultisigAccountTests.Account();
        var wrapped = Account(MultisigAccountTests.Words1);
        Assert.NotEqual(native.GetReceiveAddress(0).ToString(), wrapped.GetReceiveAddress(0).ToString());
        Assert.NotEqual(native.WalletId, wrapped.WalletId);
    }

    [Fact]
    public void TheKindSurvivesSavingAndLoading()
    {
        var (doc, account) = WalletLoader.NewMultisig(2,
            [KeyOf(MultisigAccountTests.Words1), KeyOf(MultisigAccountTests.Words2), KeyOf(MultisigAccountTests.Words3)],
            MultisigAccountTests.Words1, null, Profile, Wrapped);

        Assert.Equal(nameof(ScriptKind.WrappedSegwitMultisig), doc.ScriptKind);
        var loaded = Assert.IsType<MultisigAccount>(WalletLoader.ToAccount(doc));
        Assert.True(loaded.IsWrapped);
        Assert.Equal(account.GetReceiveAddress(0), loaded.GetReceiveAddress(0));
        Assert.Equal(account.WalletId, loaded.WalletId);
    }

    [Fact]
    public void ANonMultisigKindIsRefused() =>
        Assert.Throws<InvalidDataException>(() => WalletLoader.NewMultisig(2,
            [KeyOf(MultisigAccountTests.Words1), KeyOf(MultisigAccountTests.Words2)], null, null, Profile, ScriptKind.NativeSegwit));

    private static (List<CachedUtxo> Utxos, Dictionary<string, Transaction> Txs, Transaction Funding) Fund(MultisigAccount account)
    {
        var funding = Net.CreateTransaction();
        funding.Inputs.Add(new TxIn(new OutPoint(uint256.One, 0)));
        funding.Outputs.Add(Money.Satoshis(100_000_000), account.GetReceiveAddress(0));
        var txid = funding.GetHash().ToString();
        var utxo = new CachedUtxo
        {
            Txid = txid, Vout = 0, ValueSats = 100_000_000, Address = account.GetReceiveAddress(0).ToString(),
            AddressIndex = 0, Height = 100, Verified = true,
        };
        return ([utxo], new Dictionary<string, Transaction> { [txid] = funding }, funding);
    }

    private static BuiltTransaction Build(MultisigAccount account, List<CachedUtxo> utxos, Dictionary<string, Transaction> txs) =>
        new TransactionFactory(account).Build(utxos, txs,
            new Key().PubKey.GetAddress(ScriptPubKeyType.Segwit, Net), 30_000_000, 2, changeIndex: 0, tipHeight: 1_000);

    [Fact]
    public void ThePsbtCarriesBothScripts_ItsTxidIsNotFinal_AndTheChangeIsMarked()
    {
        var account = Account(MultisigAccountTests.Words1);
        var (utxos, txs, _) = Fund(account);

        var built = Build(account, utxos, txs);

        Assert.False(built.Signed);
        Assert.False(built.TxidIsFinal);                       // the scriptSig (the redeem script) joins on finalisation
        Assert.Equal(account.GetWitnessScript(false, 0), built.Psbt.Inputs[0].WitnessScript);
        Assert.Equal(account.GetRedeemScript(false, 0), built.Psbt.Inputs[0].RedeemScript);
        Assert.Single(built.Psbt.Inputs[0].PartialSigs);
        var change = Assert.Single(built.Psbt.Outputs, o => o.WitnessScript is not null);
        Assert.Equal(account.GetRedeemScript(true, 0), change.RedeemScript);
    }

    [Fact]
    public void TheSecondCosignerCompletesIt_AndTheTransactionIsValid()
    {
        var first = Account(MultisigAccountTests.Words1);
        var (utxos, txs, funding) = Fund(first);
        var psbt = Build(first, utxos, txs).Psbt;

        var result = PsbtSigner.Sign(psbt, Account(MultisigAccountTests.Words2));

        Assert.True(result.Complete);
        Assert.Contains(result.Outputs, o => o.IsMine);
        var tx = result.FinalTransaction!;
        Assert.NotEmpty(tx.Inputs[0].ScriptSig.ToBytes());     // the redeem script push
        var builder = Net.CreateTransactionBuilder();
        builder.AddCoins(new Coin(funding, 0).ToScriptCoin(first.GetWitnessScript(false, 0)));
        Assert.True(builder.Verify(tx, out var errors), string.Join("; ", errors.Select(e => e.ToString())));
    }

    [Fact]
    public void ACosignerSignsAPsbtThatLacksBothScripts_AndAForeignRedeemScriptIsRefused()
    {
        var first = Account(MultisigAccountTests.Words1);
        var (utxos, txs, _) = Fund(first);
        var bare = PSBT.Parse(Build(first, utxos, txs).Psbt.ToBase64(), Net);
        foreach (var input in bare.Inputs) { input.PartialSigs.Clear(); input.WitnessScript = null; input.RedeemScript = null; }

        var second = PsbtSigner.Sign(bare, Account(MultisigAccountTests.Words2));
        Assert.Single(second.Psbt.Inputs[0].PartialSigs);
        Assert.Equal(first.GetRedeemScript(false, 0), second.Psbt.Inputs[0].RedeemScript);

        var tampered = PSBT.Parse(Build(first, utxos, txs).Psbt.ToBase64(), Net);
        tampered.Inputs[0].RedeemScript = new Key().PubKey.WitHash.ScriptPubKey;
        var ex = Assert.Throws<WalletSpendException>(() => PsbtSigner.Sign(tampered, Account(MultisigAccountTests.Words2)));
        Assert.Contains("redeem script", ex.Message);
    }
}
