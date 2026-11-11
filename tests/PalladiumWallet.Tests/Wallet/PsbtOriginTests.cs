using NBitcoin;
using PalladiumWallet.Core.Chain;
using PalladiumWallet.Core.Crypto;
using PalladiumWallet.Core.Storage;
using PalladiumWallet.Core.Wallet;

namespace PalladiumWallet.Tests.Wallet;

/// <summary>The PSBT carries the BIP32 origin an external signer needs, when it is really known.</summary>
public class PsbtOriginTests
{
    private static readonly ChainProfile Profile = ChainProfiles.Regtest;
    private static readonly Network Net = PalladiumNetworks.Regtest;
    private const string Words = "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about";

    private static HdAccount Seeded(ScriptKind kind = ScriptKind.NativeSegwit)
    {
        Assert.True(Bip39.TryParse(Words, out var m));
        return HdAccount.FromMnemonic(m!, null, kind, Profile);
    }

    private static (List<CachedUtxo>, Dictionary<string, Transaction>) Fund(HdAccount a, int index = 3)
    {
        var tx = Net.CreateTransaction();
        tx.Inputs.Add(new TxIn(new OutPoint(uint256.One, 0)));
        tx.Outputs.Add(Money.Satoshis(5_000_000), a.GetReceiveAddress(index));
        var id = tx.GetHash().ToString();
        return ([new CachedUtxo { Txid = id, Vout = 0, ValueSats = 5_000_000, Address = a.GetReceiveAddress(index).ToString(),
                    AddressIndex = index, Height = 100, Verified = true }],
                new() { [id] = tx });
    }

    private static BuiltTransaction Build(HdAccount a, int changeIndex = 7)
    {
        var (utxos, txs) = Fund(a);
        return new TransactionFactory(a).Build(utxos, txs, new Key().PubKey.GetAddress(ScriptPubKeyType.Segwit, Net),
            1_000_000, 2m, changeIndex, tipHeight: 200);
    }

    [Theory]
    [InlineData(ScriptKind.NativeSegwit)]
    [InlineData(ScriptKind.WrappedSegwit)]
    [InlineData(ScriptKind.Legacy)]
    public void Inputs_change_and_the_account_xpub_carry_their_origin(ScriptKind kind)
    {
        var seeded = Seeded(kind);
        var watchOnly = HdAccount.FromAccountXpub(seeded.AccountXpub, kind, Profile, seeded.AccountPath, seeded.MasterFingerprint);
        var psbt = Build(watchOnly).Psbt;

        // input: receive/3
        var inKey = seeded.GetPublicKey(false, 3)!;
        var input = Assert.Single(psbt.Inputs);
        Assert.True(input.HDKeyPaths.TryGetValue(inKey, out var inPath));
        Assert.Equal(seeded.MasterFingerprint, inPath.MasterFingerprint);
        Assert.Equal(seeded.AccountPath.Derive(new KeyPath("0/3")), inPath.KeyPath);

        // change output: change/7, so the signer can tell it is ours
        var changeKey = seeded.GetPublicKey(true, 7)!;
        var changeOut = Assert.Single(psbt.Outputs, o => o.HDKeyPaths.ContainsKey(changeKey));
        Assert.Equal(seeded.AccountPath.Derive(new KeyPath("1/7")), changeOut.HDKeyPaths[changeKey].KeyPath);
        Assert.Single(psbt.Outputs.Where(o => o.HDKeyPaths.Count > 0)); // the payee carries none

        // account xpub with its origin
        var (xpub, origin) = Assert.Single(psbt.GlobalXPubs);
        Assert.Equal(seeded.AccountXpub, xpub.ExtPubKey);
        Assert.Equal(seeded.AccountPath, origin.KeyPath);
    }

    [Fact]
    public void A_signed_seed_wallet_psbt_has_the_same_origin()
    {
        var psbt = Build(Seeded()).Psbt;
        Assert.NotEmpty(Assert.Single(psbt.Inputs).HDKeyPaths);
    }

    [Fact]
    public void A_watch_only_xpub_without_a_fingerprint_gets_no_made_up_origin()
    {
        var seeded = Seeded();
        var bare = HdAccount.FromAccountXpub(seeded.AccountXpub, ScriptKind.NativeSegwit, Profile);
        Assert.False(bare.HasKnownOrigin);

        var psbt = Build(bare).Psbt;

        Assert.Empty(Assert.Single(psbt.Inputs).HDKeyPaths);
        Assert.All(psbt.Outputs, o => Assert.Empty(o.HDKeyPaths));
        Assert.Empty(psbt.GlobalXPubs);
    }

    [Fact]
    public void The_fingerprint_given_on_import_survives_the_wallet_file()
    {
        var seeded = Seeded();
        var xpub = seeded.ToSlip132();

        var (doc, _) = WalletLoader.NewFromXpub(xpub, Profile, ScriptKind.NativeSegwit, seeded.AccountPath, seeded.MasterFingerprint);
        var reloaded = (HdAccount)WalletLoader.ToAccount(WalletDocument.FromJson(doc.ToJson()));

        Assert.True(reloaded.HasKnownOrigin);
        Assert.Equal(seeded.MasterFingerprint, reloaded.MasterFingerprint);
        Assert.Equal(seeded.AccountPath, reloaded.AccountPath);
        Assert.NotEmpty(Assert.Single(Build(reloaded).Psbt.Inputs).HDKeyPaths);

        var (bareDoc, _) = WalletLoader.NewFromXpub(xpub, Profile, ScriptKind.NativeSegwit);
        Assert.Null(bareDoc.MasterFingerprint);
        Assert.False(((HdAccount)WalletLoader.ToAccount(bareDoc)).HasKnownOrigin);
    }

    [Theory]
    [InlineData("73c5da0a", true)]
    [InlineData("73C5DA0A", true)]
    [InlineData("73c5da0", false)]
    [InlineData("73c5da0aa", false)]
    [InlineData("zzzzzzzz", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void ParseFingerprint_accepts_exactly_eight_hex_digits(string? text, bool valid) =>
        Assert.Equal(valid, HdAccount.ParseFingerprint(text) is not null);

    [Fact]
    public void The_origin_survives_serialisation()
    {
        var psbt = Build(Seeded()).Psbt;
        var parsed = PSBT.Parse(psbt.ToBase64(), Net);

        Assert.NotEmpty(Assert.Single(parsed.Inputs).HDKeyPaths);
        Assert.Single(parsed.Outputs.Where(o => o.HDKeyPaths.Count > 0));
        Assert.Single(parsed.GlobalXPubs);
    }
}
