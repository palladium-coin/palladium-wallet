using NBitcoin;
using PalladiumWallet.Core.Chain;
using PalladiumWallet.Core.Crypto;
using PalladiumWallet.Core.Wallet;

namespace PalladiumWallet.Tests.Wallet;

/// <summary>
/// Tests for ImportedKeyAccount and the new factory paths in WalletLoader
/// (blueprint §4.4 — imported WIF, xpub, xprv).
/// </summary>
public class ImportedKeyAccountTests
{
    private static ChainProfile Profile => ChainProfiles.Mainnet;
    private static Network Network => PalladiumNetworks.For(Profile.Kind);

    // Valid WIF key for PLM mainnet (prefix 0x80 = Compressed WIF "K"/"L")
    private static Key GenerateKey() => new Key();

    private static string ToWif(Key key) => key.GetWif(Network).ToString();

    // ---- ImportedKeyAccount ----

    [Fact]
    public void Account_importato_non_e_hd()
    {
        var key = GenerateKey();
        var addr = key.PubKey.GetAddress(ScriptPubKeyType.Segwit, Network);
        var account = new ImportedKeyAccount(
            [(addr, key)], ScriptKind.NativeSegwit, Profile);

        Assert.False(account.IsWatchOnly);
        Assert.Equal(ScriptKind.NativeSegwit, account.Kind);
        Assert.NotNull(account.FixedAddresses);
    }

    [Fact]
    public void GetAddress_restituisce_indirizzo_corretto()
    {
        var key = GenerateKey();
        var addr = key.PubKey.GetAddress(ScriptPubKeyType.Segwit, Network);
        var account = new ImportedKeyAccount([(addr, key)], ScriptKind.NativeSegwit, Profile);

        Assert.Equal(addr.ToString(), account.GetAddress(false, 0).ToString());
        Assert.Equal(addr.ToString(), account.GetReceiveAddress(0).ToString());
        // Change → first address
        Assert.Equal(addr.ToString(), account.GetChangeAddress(0).ToString());
    }

    [Fact]
    public void GetPrivateKey_restituisce_chiave_corretta()
    {
        var key = GenerateKey();
        var addr = key.PubKey.GetAddress(ScriptPubKeyType.Segwit, Network);
        var account = new ImportedKeyAccount([(addr, key)], ScriptKind.NativeSegwit, Profile);

        var retrieved = account.GetPrivateKey(false, 0);
        Assert.NotNull(retrieved);
        Assert.Equal(key.ToBytes(), retrieved!.ToBytes());
    }

    [Fact]
    public void GetPrivateKey_fuori_range_restituisce_null()
    {
        var key = GenerateKey();
        var addr = key.PubKey.GetAddress(ScriptPubKeyType.Segwit, Network);
        var account = new ImportedKeyAccount([(addr, key)], ScriptKind.NativeSegwit, Profile);

        Assert.Null(account.GetPrivateKey(false, 99));
        Assert.Null(account.GetPrivateKey(true, 0));
    }

    [Fact]
    public void Account_watch_only_se_nessuna_chiave_privata()
    {
        var key = GenerateKey();
        var addr = key.PubKey.GetAddress(ScriptPubKeyType.Segwit, Network);
        var account = new ImportedKeyAccount(
            [(addr, (Key?)null)], ScriptKind.NativeSegwit, Profile);

        Assert.True(account.IsWatchOnly);
        Assert.Null(account.GetPrivateKey(false, 0));
    }

    [Fact]
    public void Lista_vuota_viene_rifiutata()
    {
        Assert.Throws<ArgumentException>(
            () => new ImportedKeyAccount([], ScriptKind.NativeSegwit, Profile));
    }

    [Fact]
    public void GetAddress_fuori_range_o_change_ricade_sul_primo_indirizzo()
    {
        var keys = Enumerable.Range(0, 2).Select(_ => GenerateKey()).ToList();
        var entries = keys
            .Select(k => (k.PubKey.GetAddress(ScriptPubKeyType.Segwit, Network), (Key?)k))
            .ToList();
        var account = new ImportedKeyAccount(entries, ScriptKind.NativeSegwit, Profile);
        var first = entries[0].Item1.ToString();

        // Fund safety: change and any out-of-range index must map to the first
        // address, never to an address the wallet does not control.
        Assert.Equal(first, account.GetAddress(isChange: true, 1).ToString());
        Assert.Equal(first, account.GetAddress(isChange: false, -1).ToString());
        Assert.Equal(first, account.GetAddress(isChange: false, 99).ToString());
        Assert.Equal(first, account.GetChangeAddress(5).ToString());
        Assert.Equal(entries[1].Item1.ToString(), account.GetAddress(isChange: false, 1).ToString());
    }

    [Fact]
    public void GetPublicKey_segue_gli_stessi_confini_di_GetPrivateKey()
    {
        var key = GenerateKey();
        var addr = key.PubKey.GetAddress(ScriptPubKeyType.Segwit, Network);
        var account = new ImportedKeyAccount([(addr, key)], ScriptKind.NativeSegwit, Profile);

        Assert.Equal(key.PubKey, account.GetPublicKey(false, 0));
        Assert.Null(account.GetPublicKey(true, 0));
        Assert.Null(account.GetPublicKey(false, -1));
        Assert.Null(account.GetPublicKey(false, 99));
    }

    [Fact]
    public void GetPublicKey_e_null_per_una_entry_watch_only()
    {
        var key = GenerateKey();
        var addr = key.PubKey.GetAddress(ScriptPubKeyType.Segwit, Network);
        var account = new ImportedKeyAccount([(addr, (Key?)null)], ScriptKind.NativeSegwit, Profile);

        Assert.Null(account.GetPublicKey(false, 0));
    }

    [Fact]
    public void FixedAddresses_copre_tutti_gli_indirizzi()
    {
        var keys = Enumerable.Range(0, 3).Select(_ => GenerateKey()).ToList();
        var entries = keys
            .Select(k => (k.PubKey.GetAddress(ScriptPubKeyType.Segwit, Network), (Key?)k))
            .ToList();
        var account = new ImportedKeyAccount(entries, ScriptKind.NativeSegwit, Profile);

        var fixed_ = account.FixedAddresses!;
        Assert.Equal(3, fixed_.Count);
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(entries[i].Item1.ToString(), fixed_[i].Address.ToString());
            Assert.False(fixed_[i].IsChange);
            Assert.Equal(i, fixed_[i].Index);
        }
    }

    // ---- WalletLoader.NewFromWif ----

    [Fact]
    public void NewFromWif_crea_account_con_indirizzi_corretti()
    {
        var key = GenerateKey();
        var wif = ToWif(key);
        var (doc, account) = WalletLoader.NewFromWif([wif], ScriptKind.NativeSegwit, Profile);

        Assert.Null(doc.Mnemonic);
        Assert.NotNull(doc.WifKeys);
        Assert.Single(doc.WifKeys!);
        Assert.False(doc.IsWatchOnly);
        Assert.Equal(ScriptKind.NativeSegwit.ToString(), doc.ScriptKind);

        var expected = key.PubKey.GetAddress(ScriptPubKeyType.Segwit, Network).ToString();
        Assert.Equal(expected, account.GetReceiveAddress(0).ToString());
    }

    [Fact]
    public void NewFromWif_roundtrip_persiste_e_ricarica()
    {
        var key = GenerateKey();
        var wif = ToWif(key);
        var (doc, original) = WalletLoader.NewFromWif([wif], ScriptKind.NativeSegwit, Profile);

        var reloaded = WalletLoader.ToAccount(doc);
        Assert.IsType<ImportedKeyAccount>(reloaded);
        Assert.Equal(
            original.GetReceiveAddress(0).ToString(),
            reloaded.GetReceiveAddress(0).ToString());
        Assert.False(reloaded.IsWatchOnly);
    }

    [Fact]
    public void NewFromWif_wif_invalido_lancia_eccezione()
    {
        Assert.Throws<InvalidDataException>(
            () => WalletLoader.NewFromWif(["not-a-wif"], ScriptKind.NativeSegwit, Profile));
    }

    // ---- WalletLoader.NewFromXpub ----

    [Fact]
    public void NewFromXpub_produce_account_watch_only()
    {
        // Create an HD account, export the zpub, re-import as watch-only xpub.
        var (_, hdFull) = WalletLoader.NewFromMnemonic(
            "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about",
            null, ScriptKind.NativeSegwit, Profile);

        var zpub = hdFull.ToSlip132();
        var (doc, woAccount) = WalletLoader.NewFromXpub(zpub, Profile);

        Assert.True(woAccount.IsWatchOnly);
        Assert.Null(doc.Mnemonic);
        Assert.Equal(
            hdFull.GetReceiveAddress(0).ToString(),
            woAccount.GetReceiveAddress(0).ToString());
    }

    // ---- WalletLoader.NewFromXprv ----

    [Fact]
    public void NewFromXprv_produce_account_spendibile()
    {
        var (_, hdFull) = WalletLoader.NewFromMnemonic(
            "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about",
            null, ScriptKind.NativeSegwit, Profile);

        var zprv = hdFull.ToSlip132Private();
        var (doc, xprvAccount) = WalletLoader.NewFromXprv(zprv, Profile);

        Assert.False(xprvAccount.IsWatchOnly);
        Assert.NotNull(doc.AccountXprv);
        Assert.Null(doc.Mnemonic);
        Assert.Equal(
            hdFull.GetReceiveAddress(0).ToString(),
            xprvAccount.GetReceiveAddress(0).ToString());
        Assert.NotNull(xprvAccount.GetPrivateKey(false, 0));
    }

    [Fact]
    public void NewFromXprv_roundtrip_persiste_e_ricarica()
    {
        var (_, hdFull) = WalletLoader.NewFromMnemonic(
            "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about",
            null, ScriptKind.NativeSegwit, Profile);

        var zprv = hdFull.ToSlip132Private();
        var (doc, _) = WalletLoader.NewFromXprv(zprv, Profile);

        var reloaded = WalletLoader.ToAccount(doc);
        Assert.IsType<HdAccount>(reloaded);
        Assert.False(reloaded.IsWatchOnly);
        Assert.Equal(
            hdFull.GetReceiveAddress(0).ToString(),
            reloaded.GetReceiveAddress(0).ToString());
    }

    [Fact]
    public void The_same_address_imported_twice_is_one_entry_and_the_spendable_copy_wins()
    {
        var key = GenerateKey();
        var addr = key.PubKey.GetAddress(ScriptPubKeyType.Segwit, Network);
        var other = GenerateKey().PubKey.GetAddress(ScriptPubKeyType.Segwit, Network);

        var account = new ImportedKeyAccount(
            [(addr, null), (other, null), (addr, key), (addr, key)], ScriptKind.NativeSegwit, Profile);

        Assert.Equal(2, account.FixedAddresses!.Count);
        Assert.Equal(addr, account.GetAddress(false, 0));
        Assert.Equal(other, account.GetAddress(false, 1));
        Assert.NotNull(account.GetPrivateKey(false, 0)); // the copy with the key replaced the watch-only one
        Assert.False(account.IsWatchOnly);
    }

    [Fact]
    public void Importing_the_same_wif_or_address_twice_stores_it_once()
    {
        var key = GenerateKey();
        var (wifDoc, wifAccount) = WalletLoader.NewFromWif([ToWif(key), ToWif(key)], ScriptKind.NativeSegwit, Profile);
        Assert.Single(wifDoc.WifKeys!);
        Assert.Single(wifAccount.FixedAddresses!);

        var addr = key.PubKey.GetAddress(ScriptPubKeyType.Segwit, Network).ToString();
        var (addrDoc, addrAccount) = WalletLoader.NewFromAddresses([addr, addr], Profile);
        Assert.Single(addrDoc.WatchAddresses!);
        Assert.Single(addrAccount.FixedAddresses!);
    }
}
