using NBitcoin;
using PalladiumWallet.Core.Chain;
using PalladiumWallet.Core.Crypto;
using PalladiumWallet.Core.Storage;
using PalladiumWallet.Core.Wallet;

namespace PalladiumWallet.Tests.Crypto;

/// <summary>
/// M-of-N P2WSH multisig accounts. The addresses are golden values from Palladium Core v2.1.0
/// (<c>deriveaddresses</c> of <c>wsh(sortedmulti(...))</c> over the same three cosigner keys, regtest): an
/// address that differs from the node's is a blocking bug.
/// </summary>
public class MultisigAccountTests
{
    internal const string Words1 = "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about";
    internal const string Words2 = "zoo zoo zoo zoo zoo zoo zoo zoo zoo zoo zoo wrong";
    internal const string Words3 = "legal winner thank year wave sausage worth useful legal winner thank yellow";

    internal static readonly ChainProfile Profile = ChainProfiles.Regtest;

    /// <summary>The key a cosigner shares: [fingerprint/48'/1'/0'/2']Vpub…, as printed by <c>cosigner-key</c>.</summary>
    internal static string KeyOf(string words)
    {
        Assert.True(Bip39.TryParse(words, out var mnemonic));
        var root = ExtKey.CreateFromSeed(Bip39.ToSeed(mnemonic!, null));
        var path = DerivationPaths.AccountPath(ScriptKind.NativeSegwitMultisig, Profile);
        return new Cosigner(root.Neuter().PubKey.GetHDFingerPrint(), path, root.Derive(path).Neuter())
            .ToText(ScriptKind.NativeSegwitMultisig, Profile);
    }

    internal static string[] AllKeys => [KeyOf(Words1), KeyOf(Words2), KeyOf(Words3)];

    internal static MultisigAccount Account(int threshold = 2, string? ownWords = Words1, IEnumerable<string>? keys = null) =>
        WalletLoader.NewMultisig(threshold, [.. keys ?? AllKeys], ownWords, null, Profile).Account;

    [Fact]
    public void TheKeysOfTheThreeCosignersAreTheOnesTheNodeWasGiven()
    {
        Assert.Equal("[73c5da0a/48'/1'/0'/2']Vpub5n95dMZrDHj6SeBgJ1oz4Fae2N2eJNuWK3VTKDb2dzGpMFLUHLmtyDfen7AaQxwQ5mZnMyXdVrkEaoMLVTH8FmVBRVWPGFYWhmtDUGehGmq", KeyOf(Words1));
        Assert.StartsWith("[3f635a63/48'/1'/0'/2']Vpub", KeyOf(Words2));
        Assert.StartsWith("[b8688df1/48'/1'/0'/2']Vpub", KeyOf(Words3));
    }

    [Fact]
    public void TwoOfThree_ReceiveAddresses_MatchPalladiumCore()
    {
        var account = Account();
        Assert.Equal("rplm1qmv9kucx4tjtyfwddc3698p2flxqvts89n8kllr0hvdv7qs4z476slcdr3q", account.GetReceiveAddress(0).ToString());
        Assert.Equal("rplm1q80kfzwjz9cu95wgvpd9qr8pmkasr8en00ldsy4f9fqryhug4swnsww8fjf", account.GetReceiveAddress(1).ToString());
        Assert.Equal("rplm1q82yt0ypaqzf4fhesr3zpqm3cxw29l4ep2he8tjtgqczgwc8c5yjsadacgf", account.GetReceiveAddress(2).ToString());
    }

    [Fact]
    public void TwoOfThree_ChangeAddresses_MatchPalladiumCore()
    {
        var account = Account();
        Assert.Equal("rplm1qp58f8l2cmpl8wx5ms7gcr7zfamsspr47rn45j4z3v6drakeffk6qmwuq8g", account.GetChangeAddress(0).ToString());
        Assert.Equal("rplm1q4p97zsp4e8emyqtruzvefap66nrmkhmy76uat3a45pznmp8hj6hqg9pdnw", account.GetChangeAddress(1).ToString());
    }

    [Theory]
    [InlineData(1, "rplm1qf0cgsken8mmg4cnp79jwxmd2rmtk60vas3l20y9fay5kzxrn3ntqvg9wvz")]
    [InlineData(3, "rplm1qlhdusr5ueshvur5dcuaz0xptz0yy8h3yg0v7kmuzv804hcp8959qg92qy7")]
    public void OtherThresholds_MatchPalladiumCore(int threshold, string expected) =>
        Assert.Equal(expected, Account(threshold).GetReceiveAddress(0).ToString());

    [Fact]
    public void TheAddress_DoesNotDependOnTheOrderTheCosignersWereEntered()
    {
        var reference = Account().GetReceiveAddress(0);
        foreach (var order in new[] { new[] { 2, 1, 0 }, new[] { 1, 0, 2 }, new[] { 2, 0, 1 } })
            Assert.Equal(reference, Account(keys: order.Select(i => AllKeys[i])).GetReceiveAddress(0));
    }

    [Fact]
    public void EveryCosignerOfTheSameKeysDerivesTheSameWalletWhateverItsOwnKey()
    {
        var addresses = new[] { Words1, Words2, Words3, null }
            .Select(own => Account(ownWords: own).GetReceiveAddress(0).ToString()).Distinct();
        Assert.Single(addresses);
    }

    [Fact]
    public void TheDescriptor_IsTheOneTheNodeAccepts_WithStandardKeys()
    {
        var descriptor = Account().ToDescriptor();
        Assert.StartsWith("wsh(sortedmulti(2,[", descriptor);
        Assert.Contains("[73c5da0a/48'/1'/0'/2']tpub", descriptor);
        Assert.EndsWith("/0/*))", descriptor);
        Assert.EndsWith("/1/*))", Account().ToDescriptor(isChange: true));
    }

    [Fact]
    public void TheWalletKnowsItsOwnShare_AndAWatchOnlyCopyHasNone()
    {
        var own = Account(ownWords: Words2);
        Assert.False(own.IsWatchOnly);
        Assert.Equal(1, own.OwnCosignerIndex);
        Assert.NotNull(own.GetPrivateKey(false, 0));
        Assert.Contains(own.GetPrivateKey(false, 0)!.PubKey, own.GetSortedPubKeys(false, 0));

        var watch = Account(ownWords: null);
        Assert.True(watch.IsWatchOnly);
        Assert.Null(watch.GetPrivateKey(false, 0));
        Assert.True(watch.IsMultiSignature);
        Assert.Null(watch.GetPublicKey(false, 0)); // no single key stands for a multisig address
    }

    [Fact]
    public void ThePublicKeysInTheScript_AreSortedAsBip67Requires()
    {
        var keys = Account().GetSortedPubKeys(false, 7).Select(k => Convert.ToHexString(k.ToBytes())).ToList();
        Assert.Equal(keys.OrderBy(k => k, StringComparer.Ordinal).ToList(), keys);
    }

    [Fact]
    public void TheAccountPath_FollowsBip48_WithTheScriptTypeLevel()
    {
        Assert.Equal("48'/1'/0'/2'", DerivationPaths.AccountPath(ScriptKind.NativeSegwitMultisig, ChainProfiles.Regtest).ToString());
        Assert.Equal("48'/746'/1'/2'", DerivationPaths.AccountPath(ScriptKind.NativeSegwitMultisig, ChainProfiles.Mainnet, 1).ToString());
        Assert.Equal("48'/746'/0'/1'", DerivationPaths.AccountPath(ScriptKind.WrappedSegwitMultisig, ChainProfiles.Mainnet).ToString());
        Assert.Equal("84'/746'/0'", DerivationPaths.AccountPath(ScriptKind.NativeSegwit, ChainProfiles.Mainnet).ToString()); // unchanged
    }

    // ----- the constructor refuses wallets that would be unsafe or meaningless -----

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(-1)]
    public void AThresholdOutOfRange_IsRefused(int threshold) =>
        Assert.Throws<InvalidDataException>(() => Account(threshold));

    [Fact]
    public void FewerThanTwoCosigners_AreRefused() =>
        Assert.Throws<InvalidDataException>(() => Account(1, null, [AllKeys[0]]));

    [Fact]
    public void TheSameKeyTwice_IsRefused_BecauseOnePersonWouldHoldTwoSignatures()
    {
        var ex = Assert.Throws<InvalidDataException>(() => Account(2, null, [AllKeys[0], AllKeys[1], AllKeys[0]]));
        Assert.Contains("more than once", ex.Message);
    }

    [Fact]
    public void ASeedThatIsNoneOfTheCosigners_IsRefused()
    {
        var ex = Assert.Throws<InvalidDataException>(() =>
            Account(2, "letter advice cage absurd amount doctor acoustic avoid letter advice cage above", AllKeys));
        Assert.Contains("None of the cosigner keys comes from this seed", ex.Message);
    }

    [Fact]
    public void AnInvalidMnemonic_IsRefused() =>
        Assert.Throws<InvalidDataException>(() => Account(2, "not a mnemonic", AllKeys));

    [Fact]
    public void ACosignerKeyThatDoesNotParse_IsRefused_NamingTheExpectedForm()
    {
        var ex = Assert.Throws<InvalidDataException>(() => Account(2, null, [AllKeys[0], "xpub-nonsense"]));
        Assert.Contains("[fingerprint/path]", ex.Message);
    }

    // ----- the wallet file -----

    [Fact]
    public void TheWalletFile_RoundTrips_AndKeepsTheSigningKeyOnlyWhenOneWasGiven()
    {
        var (doc, _) = WalletLoader.NewMultisig(2, AllKeys, Words3, null, Profile);
        var reloaded = (MultisigAccount)WalletLoader.ToAccount(WalletDocument.FromJson(doc.ToJson()));
        Assert.Equal(Account().GetReceiveAddress(0), reloaded.GetReceiveAddress(0));
        Assert.Equal(2, reloaded.OwnCosignerIndex);
        Assert.Equal(2, reloaded.Threshold);

        var (watchDoc, _) = WalletLoader.NewMultisig(2, AllKeys, null, null, Profile);
        Assert.Null(watchDoc.Mnemonic);
        Assert.True(watchDoc.IsWatchOnly);
        Assert.True(((MultisigAccount)WalletLoader.ToAccount(watchDoc)).IsWatchOnly);
    }

    [Fact]
    public void TheCosignerKeysInTheFile_AreWrittenInOneCanonicalForm()
    {
        var standard = Account().Cosigners.Select(c => c.ToDescriptorKey(PalladiumNetworks.Regtest)).ToList();
        var (doc, _) = WalletLoader.NewMultisig(2, standard, null, null, Profile); // tpub form in, Vpub form out
        Assert.Equal(AllKeys.OrderBy(k => k), doc.Cosigners!.OrderBy(k => k));
    }

    // ----- parsing a cosigner key: pasted from someone else, never trusted -----

    [Fact]
    public void ACosignerKey_ParsesInBothTheSlip132AndTheStandardForm()
    {
        Assert.True(Cosigner.TryParse(AllKeys[0], Profile, out var slip132));
        var standard = $"[73c5da0a/48h/1h/0h/2h]{slip132!.Xpub.GetWif(PalladiumNetworks.Regtest)}"; // 'h' as descriptors may write it
        Assert.True(Cosigner.TryParse(standard, Profile, out var viaStandard));
        Assert.Equal(slip132.Xpub.ToBytes(), viaStandard!.Xpub.ToBytes());
        Assert.Equal(slip132.AccountPath, viaStandard.AccountPath);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("[zzzzzzzz/48'/1'/0'/2']Vpub5n95dMZrDHj6SeBgJ1oz4Fae2N2eJNuWK3VTKDb2dzGpMFLUHLmtyDfen7AaQxwQ5mZnMyXdVrkEaoMLVTH8FmVBRVWPGFYWhmtDUGehGmq")]
    [InlineData("73c5da0a/48'/1'/0'/2'Vpub5n95dMZrDHj6SeBgJ1oz4Fae2N2eJNuWK3VTKDb2dzGpMFLUHLmtyDfen7AaQxwQ5mZnMyXdVrkEaoMLVTH8FmVBRVWPGFYWhmtDUGehGmq")] // no brackets
    [InlineData("[73c5da0a/48'/1'/0'/2']Vpub5n95dMZrDHj6SeBgJ1oz4Fae2N2eJNuWK3VTKDb2dzGpMFLUHLmtyDfen7AaQxwQ5mZnMyXdVrkEaoMLVTH8FmVBRVWPGFYWhmtDUGehGmX")] // bad checksum
    public void AMalformedCosignerKey_IsRejected_WithoutThrowing(string? text) =>
        Assert.False(Cosigner.TryParse(text, Profile, out _));

    [Fact]
    public void AnOriginThatDoesNotMatchTheKey_IsRejected()
    {
        var key = AllKeys[0];
        // The key is at depth 4, child 2': a shorter, a longer or a different last step contradicts it.
        Assert.False(Cosigner.TryParse(key.Replace("/48'/1'/0'/2']", "/48'/1'/0']"), Profile, out _));
        Assert.False(Cosigner.TryParse(key.Replace("/48'/1'/0'/2']", "/48'/1'/0'/2'/0']"), Profile, out _));
        Assert.False(Cosigner.TryParse(key.Replace("/48'/1'/0'/2']", "/48'/1'/0'/1']"), Profile, out _));
    }

    [Fact]
    public void AKeyOfAnotherNetwork_IsRejected() =>
        Assert.False(Cosigner.TryParse(AllKeys[0], ChainProfiles.Mainnet, out _));

    // ----- the wallet ID: the check the cosigners make by comparing it, since a key cannot be authenticated by the wallet -----

    [Fact]
    public void TheWalletId_IsThePalladiumCoreChecksumOfTheSameDescriptor()
    {
        // getdescriptorinfo on the node, for the descriptor this wallet prints.
        Assert.Equal("zdl27u8e", Account().WalletId);
    }

    [Fact]
    public void TheWalletId_DoesNotDependOnTheOrderTheKeysWereEntered_ButOnEveryKeyAndTheThreshold()
    {
        var reference = Account().WalletId;
        Assert.Equal(reference, Account(keys: [AllKeys[2], AllKeys[0], AllKeys[1]]).WalletId);
        Assert.Equal(reference, Account(ownWords: null).WalletId);
        Assert.NotEqual(reference, Account(threshold: 3).WalletId);
        // One key replaced by another cosigner's: what an attacker who swaps a key in transit produces.
        var attacker = KeyOf("letter advice cage absurd amount doctor acoustic avoid letter advice cage above");
        Assert.NotEqual(reference, Account(2, null, [AllKeys[0], AllKeys[1], attacker]).WalletId);
    }
}
