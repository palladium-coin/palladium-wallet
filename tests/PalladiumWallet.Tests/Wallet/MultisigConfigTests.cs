using PalladiumWallet.Core.Chain;
using PalladiumWallet.Core.Crypto;
using PalladiumWallet.Core.Storage;
using PalladiumWallet.Core.Wallet;
using PalladiumWallet.Tests.Crypto;

namespace PalladiumWallet.Tests.Wallet;

/// <summary>The multisig configuration other tools exchange: the Coldcard/Sparrow text file and the descriptors.</summary>
public class MultisigConfigTests
{
    private static readonly ChainProfile Profile = ChainProfiles.Regtest;

    [Fact]
    public void TheTextListsPolicyFormatAndEveryKeyUnderItsDerivation()
    {
        var text = MultisigConfig.ToText(MultisigAccountTests.Account(), "Family vault");

        Assert.StartsWith("Name: Family vault\nPolicy: 2 of 3\nFormat: P2WSH\n\nDerivation: m/48'/1'/0'/2'\n", text);
        Assert.Contains("73C5DA0A: tpub", text);
        Assert.Contains("3F635A63: tpub", text);
        Assert.Contains("B8688DF1: tpub", text);
    }

    [Theory]
    [InlineData(ScriptKind.NativeSegwitMultisig)]
    [InlineData(ScriptKind.WrappedSegwitMultisig)]
    public void ExportingAndReadingBackGivesTheSameWallet(ScriptKind kind)
    {
        var keys = new[] { MultisigAccountTests.Words1, MultisigAccountTests.Words2, MultisigAccountTests.Words3 }
            .Select(w => Cosigner.FromMnemonic(w, null, kind, Profile).ToText(kind, Profile)).ToList();
        var original = WalletLoader.NewMultisig(2, keys, null, null, Profile, kind).Account;

        var (threshold, readKind, readKeys) = MultisigConfig.Parse(MultisigConfig.ToText(original, "x"), Profile);
        var copy = WalletLoader.NewMultisig(threshold, [.. readKeys], null, null, Profile, readKind).Account;

        Assert.Equal(kind, readKind);
        Assert.Equal(original.WalletId, copy.WalletId);                       // every key and the threshold survived
        Assert.Equal(original.GetReceiveAddress(0), copy.GetReceiveAddress(0));
    }

    [Fact]
    public void TheDescriptorsCarryTheirChecksums_AndTheReceiveOneIsTheWalletId()
    {
        var account = MultisigAccountTests.Account();
        var (receive, change) = MultisigConfig.Descriptors(account);

        Assert.EndsWith("#" + account.WalletId, receive);
        Assert.StartsWith("wsh(sortedmulti(2,", change);
        Assert.Contains("/1/*", change);
        Assert.NotEqual(receive[^8..], change[^8..]);
    }

    [Fact]
    public void AFileWithCommentsHardenedHAndLowercaseFieldsIsRead()
    {
        var k = Cosigner.FromMnemonic(MultisigAccountTests.Words1, null, ScriptKind.NativeSegwitMultisig, Profile);
        var k2 = Cosigner.FromMnemonic(MultisigAccountTests.Words2, null, ScriptKind.NativeSegwitMultisig, Profile);
        var net = PalladiumNetworks.For(Profile.Kind);
        var text = $"# exported by a signer\nname: x\npolicy: 2 of 2\nformat: p2wsh\n\nderivation: m/48h/1h/0h/2h\n" +
            $"{Convert.ToHexString(k.Fingerprint.ToBytes()).ToLowerInvariant()}: {k.Xpub.GetWif(net)}\n" +
            $"{Convert.ToHexString(k2.Fingerprint.ToBytes())}: {k2.Xpub.GetWif(net)}\n";

        var (threshold, kind, keys) = MultisigConfig.Parse(text, Profile);

        Assert.Equal((2, ScriptKind.NativeSegwitMultisig, 2), (threshold, kind, keys.Count));
    }

    [Theory]
    [InlineData("Policy: 2 of 3\n", "Format")]
    [InlineData("Format: P2WSH\n", "Policy")]
    [InlineData("Policy: two\nFormat: P2WSH\n", "Unreadable policy")]
    [InlineData("Policy: 2 of 2\nFormat: P2TR\n", "Unsupported format")]
    [InlineData("Policy: 2 of 2\nFormat: P2WSH\n73C5DA0A: tpub\n", "before any 'Derivation:'")]
    [InlineData("Policy: 2 of 2\nFormat: P2WSH\nDerivation: m/48'/1'/0'/2'\n73C5DA0A: tpub\n", "policy says 2 keys")]
    [InlineData("Policy: 1 of 1\nFormat: P2WSH\nDerivation: m/48'/1'/0'/2'\n73C5DA0A: tpubgarbage\n", "not a cosigner key")]
    [InlineData("Policy: 1 of 1\nFormat: P2WSH\nDerivation: m/48'/1'/0'/2'\nXYZ: tpub\n", "Unknown field")]
    [InlineData("just words", "Unreadable line")]
    public void AnUnusableConfigurationIsRefusedWithTheReason(string text, string reason)
    {
        var ex = Assert.Throws<InvalidDataException>(() => MultisigConfig.Parse(text, Profile));
        Assert.Contains(reason, ex.Message);
    }
}
