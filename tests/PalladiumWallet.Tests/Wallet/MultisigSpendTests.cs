using NBitcoin;
using PalladiumWallet.Core.Chain;
using PalladiumWallet.Core.Crypto;
using PalladiumWallet.Core.Storage;
using PalladiumWallet.Core.Wallet;
using PalladiumWallet.Tests.Crypto;

namespace PalladiumWallet.Tests.Wallet;

/// <summary>
/// Spending from a 2-of-3 multisig wallet: the wallet builds a PSBT carrying its own signature, the other cosigners
/// sign their copies, the copies are combined and finalised. The transaction ends up valid under the script rules.
/// </summary>
public class MultisigSpendTests
{
    private static readonly Network Net = PalladiumNetworks.Regtest;

    private static (List<CachedUtxo> Utxos, Dictionary<string, Transaction> Txs, Transaction Funding) Fund(MultisigAccount account, long sats)
    {
        var funding = Net.CreateTransaction();
        funding.Inputs.Add(new TxIn(new OutPoint(uint256.One, 0)));
        funding.Outputs.Add(Money.Satoshis(sats), account.GetReceiveAddress(0));
        var txid = funding.GetHash().ToString();
        var utxo = new CachedUtxo
        {
            Txid = txid, Vout = 0, ValueSats = sats, Address = account.GetReceiveAddress(0).ToString(),
            AddressIndex = 0, Height = 100, Verified = true,
        };
        return ([utxo], new Dictionary<string, Transaction> { [txid] = funding }, funding);
    }

    private static BuiltTransaction Build(MultisigAccount account, List<CachedUtxo> utxos, Dictionary<string, Transaction> txs,
        decimal feeRate = 2, long amount = 30_000_000) =>
        new TransactionFactory(account).Build(utxos, txs,
            new Key().PubKey.GetAddress(ScriptPubKeyType.Segwit, Net), amount, feeRate, changeIndex: 0, tipHeight: 1_000);

    private static int Signatures(PSBT psbt) => psbt.Inputs.Max(i => i.PartialSigs.Count);

    [Fact]
    public void ThePsbtCarriesTheWalletsSignature_AndNeverPassesForACompleteTransaction()
    {
        var account = MultisigAccountTests.Account(ownWords: MultisigAccountTests.Words1);
        var (utxos, txs, _) = Fund(account, 100_000_000);

        var built = Build(account, utxos, txs);

        Assert.False(built.Signed);                                  // 1 of 2 required signatures: not broadcastable
        Assert.True(built.TxidIsFinal);                              // a witness-only spend: the txid will not change
        Assert.Equal(1, Signatures(built.Psbt));
        Assert.NotNull(built.Psbt.Inputs[0].WitnessScript);
        Assert.Equal(account.GetWitnessScript(false, 0), built.Psbt.Inputs[0].WitnessScript);
        Assert.NotNull(built.Psbt.Inputs[0].NonWitnessUtxo);         // the signers can verify the amount
    }

    [Fact]
    public void ThePsbtTellsEveryCosignerWhichKeysAreItsOwn_AndMarksTheChange()
    {
        var account = MultisigAccountTests.Account(ownWords: MultisigAccountTests.Words1);
        var (utxos, txs, _) = Fund(account, 100_000_000);

        var psbt = Build(account, utxos, txs).Psbt;

        Assert.Equal(3, psbt.Inputs[0].HDKeyPaths.Count);            // one origin per cosigner key
        Assert.Contains(psbt.Inputs[0].HDKeyPaths.Values, v => Convert.ToHexString(v.MasterFingerprint.ToBytes()).Equals("73C5DA0A", StringComparison.OrdinalIgnoreCase));
        var change = Assert.Single(psbt.Outputs, o => o.WitnessScript is not null);
        Assert.Equal(account.GetWitnessScript(true, 0), change.WitnessScript);
        Assert.Equal(3, change.HDKeyPaths.Count);
        Assert.Equal(3, psbt.GlobalXPubs.Count);
    }

    [Fact]
    public void TheSecondCosignerCompletesIt_AndTheTransactionIsValid()
    {
        var first = MultisigAccountTests.Account(ownWords: MultisigAccountTests.Words1);
        var (utxos, txs, funding) = Fund(first, 100_000_000);
        var psbt = Build(first, utxos, txs).Psbt;

        var second = MultisigAccountTests.Account(ownWords: MultisigAccountTests.Words2);
        var result = PsbtSigner.Sign(psbt, second);

        Assert.True(result.Complete);
        Assert.Contains(result.Outputs, o => o.IsMine);                // the change is recognised as the wallet's own
        var tx = result.FinalTransaction!;
        var builder = Net.CreateTransactionBuilder();
        builder.AddCoins(new ScriptCoin(new Coin(funding, 0), first.GetWitnessScript(false, 0)));
        Assert.True(builder.Verify(tx, out var errors), string.Join("; ", errors.Select(e => e.ToString())));
    }

    [Fact]
    public void OnlyOneSignatureLeavesItIncomplete_AndNothingIsFinal()
    {
        var first = MultisigAccountTests.Account(ownWords: MultisigAccountTests.Words1);
        var (utxos, txs, _) = Fund(first, 100_000_000);
        var psbt = Build(first, utxos, txs).Psbt;

        // The wallet that built it signs again: the same signature, still 1 of 2.
        var result = PsbtSigner.Sign(psbt, first);

        Assert.False(result.Complete);
        Assert.Null(result.FinalTransaction);
        Assert.Equal(1, Signatures(result.Psbt));
    }

    [Fact]
    public void TheCopiesOfTwoCosignersCombineIntoOneCompleteTransaction()
    {
        var first = MultisigAccountTests.Account(ownWords: MultisigAccountTests.Words1);
        var (utxos, txs, _) = Fund(first, 100_000_000);
        var original = Build(first, utxos, txs).Psbt;                  // signed by cosigner 1

        // Cosigner 3 receives the PSBT as text, and signs its own copy without finalising it.
        var copy = PSBT.Parse(original.ToBase64(), Net);
        var third = MultisigAccountTests.Account(ownWords: MultisigAccountTests.Words3);
        foreach (var input in copy.Inputs) input.PartialSigs.Clear();   // as if it came unsigned
        PsbtSigner.Sign(copy, third);
        Assert.Equal(1, Signatures(copy));

        var (combined, final) = PsbtSigner.Combine([original, copy]);

        Assert.Equal(2, Signatures(combined));
        Assert.NotNull(final);
        Assert.Equal(original.GetGlobalTransaction().GetHash(), final!.GetHash()); // same txid: witness data is not in it
    }

    [Fact]
    public void PsbtsOfDifferentTransactionsAreNotCombined()
    {
        var account = MultisigAccountTests.Account(ownWords: MultisigAccountTests.Words1);
        var (utxos, txs, _) = Fund(account, 100_000_000);
        var one = Build(account, utxos, txs, amount: 30_000_000).Psbt;
        var other = Build(account, utxos, txs, amount: 31_000_000).Psbt;

        var ex = Assert.Throws<WalletSpendException>(() => PsbtSigner.Combine([one, other]));
        Assert.Contains("not of the same transaction", ex.Message);
    }

    [Fact]
    public void AWalletWithoutAKey_BuildsAnUnsignedPsbt_AndCannotSign()
    {
        var watch = MultisigAccountTests.Account(ownWords: null);
        var (utxos, txs, _) = Fund(watch, 100_000_000);

        var built = Build(watch, utxos, txs);

        Assert.False(built.Signed);
        Assert.Equal(0, Signatures(built.Psbt));
        Assert.Throws<WalletSpendException>(() => PsbtSigner.Sign(built.Psbt, watch));
    }

    [Fact]
    public void APsbtFromAnotherCreator_WithoutTheWitnessScript_IsStillSigned()
    {
        var first = MultisigAccountTests.Account(ownWords: MultisigAccountTests.Words1);
        var (utxos, txs, _) = Fund(first, 100_000_000);
        var bare = PSBT.Parse(Build(first, utxos, txs).Psbt.ToBase64(), Net);
        foreach (var input in bare.Inputs)
        {
            input.PartialSigs.Clear();
            input.WitnessScript = null;       // a minimal creator leaves it out
            input.HDKeyPaths.Clear();
        }

        var result = PsbtSigner.Sign(bare, MultisigAccountTests.Account(ownWords: MultisigAccountTests.Words2));

        Assert.Equal(1, Signatures(result.Psbt));
        Assert.NotNull(result.Psbt.Inputs[0].WitnessScript);
    }

    [Fact]
    public void TheFeeIsNotUnderstated_AgainstTheSizeOfTheFinalTransaction()
    {
        var first = MultisigAccountTests.Account(ownWords: MultisigAccountTests.Words1);
        var (utxos, txs, _) = Fund(first, 100_000_000);
        var built = Build(first, utxos, txs, feeRate: 5);
        var final = PsbtSigner.Sign(built.Psbt, MultisigAccountTests.Account(ownWords: MultisigAccountTests.Words2)).FinalTransaction!;

        var effective = built.Fee.Satoshi / (decimal)final.GetVirtualSize();
        // The estimate may not pay less than 95% of the rate asked for, or the node would hold it back.
        Assert.True(effective >= 5m * 0.95m, $"effective rate {effective} sat/vB for a requested 5");
        Assert.True(effective < 5m * 1.5m, $"effective rate {effective} sat/vB wastes fee");
    }

    // ----- a multisig wallet signs only what it can recognise as its own -----

    [Fact]
    public void AnInputOfAnotherMultisigWallet_IsNotSigned()
    {
        var mine = MultisigAccountTests.Account(ownWords: MultisigAccountTests.Words1);
        var other = MultisigAccountTests.Account(threshold: 3, ownWords: null);   // same keys, another script
        var (utxos, txs, _) = Fund(other, 100_000_000);
        var psbt = Build(other, utxos, txs).Psbt;
        foreach (var input in psbt.Inputs) input.PartialSigs.Clear();

        var ex = Assert.Throws<WalletSpendException>(() => PsbtSigner.Sign(psbt, mine));
        Assert.Contains("does not spend an address of this multisig wallet", ex.Message);
    }

    [Fact]
    public void AnInputWithoutItsAmount_IsNotSigned()
    {
        var mine = MultisigAccountTests.Account(ownWords: MultisigAccountTests.Words1);
        var (utxos, txs, _) = Fund(mine, 100_000_000);
        var psbt = PSBT.Parse(Build(mine, utxos, txs).Psbt.ToBase64(), Net);
        foreach (var input in psbt.Inputs)
        {
            input.PartialSigs.Clear();
            input.NonWitnessUtxo = null;
            input.WitnessUtxo = null;
        }

        Assert.Throws<WalletSpendException>(() => PsbtSigner.Sign(psbt, mine));
    }

    [Fact]
    public void AWitnessScriptThatIsNotTheWalletsOwn_IsNotSigned()
    {
        var mine = MultisigAccountTests.Account(ownWords: MultisigAccountTests.Words1);
        var (utxos, txs, _) = Fund(mine, 100_000_000);
        var psbt = PSBT.Parse(Build(mine, utxos, txs).Psbt.ToBase64(), Net);
        foreach (var input in psbt.Inputs)
        {
            input.PartialSigs.Clear();
            input.WitnessScript = PayToMultiSigTemplate.Instance.GenerateScriptPubKey(1, new Key().PubKey, mine.GetPrivateKey(false, 0)!.PubKey);
        }

        var ex = Assert.Throws<WalletSpendException>(() => PsbtSigner.Sign(psbt, mine));
        Assert.Contains("not this wallet's own", ex.Message);
    }

    /// <summary>
    /// The shape other wallets and hardware signers export: the spent output only as witness_utxo (no previous
    /// transaction), no global xpubs, no key origins, no scripts. Our signer must still recognise its inputs by
    /// the output script, add the scripts itself and produce a valid signature.
    /// </summary>
    [Fact]
    public void ABarePsbtInTheShapeOfOtherWalletsIsSignedAndCompletes()
    {
        var first = MultisigAccountTests.Account(ownWords: MultisigAccountTests.Words1);
        var (utxos, txs, funding) = Fund(first, 100_000_000);
        var psbt = Build(first, utxos, txs).Psbt;
        var bare = PSBT.FromTransaction(psbt.GetGlobalTransaction(), Net);
        bare.Inputs[0].WitnessUtxo = funding.Outputs[0];

        var result = PsbtSigner.Sign(bare, MultisigAccountTests.Account(ownWords: MultisigAccountTests.Words2));
        Assert.Single(result.Psbt.Inputs[0].PartialSigs);
        Assert.Equal(100_000_000 - result.Outputs.Sum(o => o.AmountSats), result.FeeSats);   // the amount comes from witness_utxo

        // Cosigner 1's signature, made on the original PSBT, joins it: the transaction is complete and valid.
        var (_, final) = PsbtSigner.Combine([result.Psbt, psbt]);
        Assert.NotNull(final);
        var builder = Net.CreateTransactionBuilder();
        builder.AddCoins(new ScriptCoin(new Coin(funding, 0), first.GetWitnessScript(false, 0)));
        Assert.True(builder.Verify(final!, out var errors), string.Join("; ", errors.Select(e => e.ToString())));
    }
}
