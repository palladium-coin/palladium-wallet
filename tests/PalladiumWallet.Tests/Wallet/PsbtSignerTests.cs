using NBitcoin;
using PalladiumWallet.Core.Chain;
using PalladiumWallet.Core.Crypto;
using PalladiumWallet.Core.Storage;
using PalladiumWallet.Core.Wallet;

namespace PalladiumWallet.Tests.Wallet;

/// <summary>The offline half of the watch-only flow: sign a PSBT that came from outside.</summary>
public class PsbtSignerTests
{
    private static readonly ChainProfile Profile = ChainProfiles.Regtest;
    private static readonly Network Net = PalladiumNetworks.Regtest;
    private const string Words = "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about";
    private const string OtherWords = "legal winner thank year wave sausage worth useful legal winner thank yellow";

    private static HdAccount Seeded(string words = Words, ScriptKind kind = ScriptKind.NativeSegwit)
    {
        Assert.True(Bip39.TryParse(words, out var m));
        return HdAccount.FromMnemonic(m!, null, kind, Profile);
    }

    /// <summary>A PSBT built by the watch-only twin of <paramref name="signer"/>, funded at receive/2 (and change/0 when asked).</summary>
    private static (BuiltTransaction Built, Transaction Funding) Unsigned(HdAccount signer, long sats = 5_000_000)
    {
        var watchOnly = HdAccount.FromAccountXpub(signer.AccountXpub, signer.Kind, Profile, signer.AccountPath, signer.MasterFingerprint);
        var funding = Net.CreateTransaction();
        funding.Inputs.Add(new TxIn(new OutPoint(uint256.One, 0)));
        funding.Outputs.Add(Money.Satoshis(sats), signer.GetReceiveAddress(2));
        var id = funding.GetHash().ToString();
        var utxos = new List<CachedUtxo>
        {
            new() { Txid = id, Vout = 0, ValueSats = sats, Address = signer.GetReceiveAddress(2).ToString(),
                    AddressIndex = 2, Height = 100, Verified = true },
        };
        var built = new TransactionFactory(watchOnly).Build(utxos, new Dictionary<string, Transaction> { [id] = funding },
            new Key().PubKey.GetAddress(ScriptPubKeyType.Segwit, Net), 1_000_000, 2m, changeIndex: 4, tipHeight: 200);
        Assert.False(built.Signed);
        return (built, funding);
    }

    private static bool Verifies(Transaction tx, Transaction funding, HdAccount account)
    {
        ICoin coin = new Coin(new OutPoint(funding, 0), funding.Outputs[0]);
        if (account.Kind == ScriptKind.WrappedSegwit)
            coin = ((Coin)coin).ToScriptCoin(account.GetPublicKey(false, 2)!.WitHash.ScriptPubKey);
        var builder = Net.CreateTransactionBuilder();
        builder.AddCoins(coin);
        return builder.Verify(tx);
    }

    [Theory]
    [InlineData(ScriptKind.NativeSegwit)]
    [InlineData(ScriptKind.WrappedSegwit)]
    [InlineData(ScriptKind.Legacy)]
    public void A_psbt_from_the_watch_only_twin_is_signed_and_finalised(ScriptKind kind)
    {
        var signer = Seeded(kind: kind);
        var (built, funding) = Unsigned(signer);
        var psbt = PSBT.Parse(built.Psbt.ToBase64(), Net); // as it arrives from outside

        var result = PsbtSigner.Sign(psbt, signer);

        Assert.True(result.Complete);
        Assert.All(result.Inputs, i => Assert.True(i.Signed));
        Assert.True(Verifies(result.FinalTransaction!, funding, signer)); // signatures are valid
        // The transaction that was shown is the one that got signed: same inputs and outputs, in the same
        // order. The txid is final before signing only for native segwit: legacy and wrapped inputs
        // carry a scriptSig, which is part of the txid.
        var shown = built.Transaction;
        var final = result.FinalTransaction!;
        Assert.Equal(shown.Inputs.Select(i => i.PrevOut), final.Inputs.Select(i => i.PrevOut));
        Assert.Equal(shown.Outputs.Select(o => (o.Value, o.ScriptPubKey)), final.Outputs.Select(o => (o.Value, o.ScriptPubKey)));
        if (kind == ScriptKind.NativeSegwit)
            Assert.Equal(shown.GetHash(), final.GetHash());
    }

    [Fact]
    public void The_summary_shows_amounts_fee_and_marks_the_change_as_yours()
    {
        var signer = Seeded();
        var (built, _) = Unsigned(signer);

        var result = PsbtSigner.Sign(PSBT.Parse(built.Psbt.ToBase64(), Net), signer);

        var input = Assert.Single(result.Inputs);
        Assert.Equal(5_000_000, input.AmountSats);
        Assert.True(input.AmountVerified); // the previous transaction is attached and matches the outpoint
        Assert.Equal(built.Fee.Satoshi, result.FeeSats);
        var change = Assert.Single(result.Outputs, o => o.IsMine);
        Assert.Equal(signer.GetChangeAddress(4).ToString(), change.Address);
        var payee = Assert.Single(result.Outputs, o => !o.IsMine);
        Assert.Equal(1_000_000, payee.AmountSats);
    }

    [Fact]
    public void The_psbt_built_by_the_wallet_carries_the_full_previous_transaction()
    {
        var signer = Seeded();
        var (built, funding) = Unsigned(signer);

        var parsed = PSBT.Parse(built.Psbt.ToBase64(), Net);

        Assert.Equal(funding.GetHash(), Assert.Single(parsed.Inputs).NonWitnessUtxo!.GetHash());
    }

    [Fact]
    public void A_wallet_that_does_not_own_the_inputs_signs_nothing()
    {
        var (built, _) = Unsigned(Seeded());

        var result = PsbtSigner.Sign(PSBT.Parse(built.Psbt.ToBase64(), Net), Seeded(OtherWords));

        Assert.False(result.Complete);
        Assert.All(result.Inputs, i => Assert.False(i.Signed));
    }

    [Fact]
    public void A_watch_only_wallet_cannot_sign()
    {
        var signer = Seeded();
        var (built, _) = Unsigned(signer);
        var watchOnly = HdAccount.FromAccountXpub(signer.AccountXpub, ScriptKind.NativeSegwit, Profile);

        Assert.Throws<WalletSpendException>(() => PsbtSigner.Sign(built.Psbt, watchOnly));
    }

    [Theory]
    [InlineData(SigHash.None)]
    [InlineData(SigHash.Single)]
    [InlineData(SigHash.All | SigHash.AnyoneCanPay)]
    public void An_input_asking_for_a_sighash_other_than_all_is_refused(SigHash sighash)
    {
        // SIGHASH_NONE/SINGLE/ANYONECANPAY would let whoever holds the PSBT change the outputs.
        var signer = Seeded();
        var (built, _) = Unsigned(signer);
        var psbt = PSBT.Parse(built.Psbt.ToBase64(), Net);
        psbt.Inputs[0].SighashType = sighash;

        var ex = Assert.Throws<WalletSpendException>(() => PsbtSigner.Sign(psbt, signer));
        Assert.Contains("sighash", ex.Message);
        Assert.Empty(psbt.Inputs[0].PartialSigs); // nothing was signed
    }

    [Fact]
    public void A_previous_transaction_that_does_not_match_the_outpoint_is_refused()
    {
        // A lying previous transaction is how an offline signer is made to misjudge the fee.
        var signer = Seeded();
        var (built, _) = Unsigned(signer);
        var psbt = PSBT.Parse(built.Psbt.ToBase64(), Net);
        var other = Net.CreateTransaction();
        other.Outputs.Add(Money.Satoshis(99_000_000), signer.GetReceiveAddress(2));
        psbt.Inputs[0].NonWitnessUtxo = other;

        var ex = Assert.Throws<WalletSpendException>(() => PsbtSigner.Sign(psbt, signer));
        Assert.Contains("does not match", ex.Message);
    }

    [Fact]
    public void Imported_key_accounts_sign_too()
    {
        var key = new Key();
        var address = key.PubKey.GetAddress(ScriptPubKeyType.Segwit, Net);
        var account = new ImportedKeyAccount([(address, key)], ScriptKind.NativeSegwit, Profile);
        var funding = Net.CreateTransaction();
        funding.Inputs.Add(new TxIn(new OutPoint(uint256.One, 0)));
        funding.Outputs.Add(Money.Satoshis(3_000_000), address);
        var id = funding.GetHash().ToString();
        var utxos = new List<CachedUtxo>
        {
            new() { Txid = id, Vout = 0, ValueSats = 3_000_000, Address = address.ToString(), Height = 100, Verified = true },
        };
        var watchOnly = new ImportedKeyAccount([(address, null)], ScriptKind.NativeSegwit, Profile);
        var built = new TransactionFactory(watchOnly).Build(utxos, new Dictionary<string, Transaction> { [id] = funding },
            new Key().PubKey.GetAddress(ScriptPubKeyType.Segwit, Net), 1_000_000, 2m, changeIndex: 0, tipHeight: 200);

        var result = PsbtSigner.Sign(PSBT.Parse(built.Psbt.ToBase64(), Net), account);

        Assert.True(result.Complete);
        Assert.Contains(result.Outputs, o => o.IsMine); // change returns to the imported address
    }

    [Fact]
    public void The_psbt_holds_exactly_the_transaction_the_user_was_shown()
    {
        // The builder shuffles outputs: two separate builds used to differ in about a third of the runs.
        var mismatches = 0;
        for (var n = 0; n < 40; n++)
        {
            var (built, _) = Unsigned(Seeded());
            var fromPsbt = built.Psbt.GetGlobalTransaction();
            if (fromPsbt.GetHash() != built.Transaction.GetHash()) mismatches++;
        }
        Assert.Equal(0, mismatches);
    }
}
