using NBitcoin;
using PalladiumWallet.Core.Chain;
using PalladiumWallet.Core.Crypto;
using PalladiumWallet.Core.Net;
using PalladiumWallet.Core.Spv;
using PalladiumWallet.Core.Storage;
using PalladiumWallet.Core.Wallet;

namespace PalladiumWallet.Tests.Wallet;

/// <summary>
/// Boundary values, unit formatting, derivation-path round trips and user-facing error messages
/// that the other suites did not pin down.
/// </summary>
public class BoundaryAndMessageTests
{
    private const string Words =
        "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about";

    private static readonly ChainProfile Profile = ChainProfiles.Mainnet;

    // ---- BlockHeaderInfo ----

    [Theory]
    [InlineData(0)]
    [InlineData(79)]
    [InlineData(81)]
    public void HeaderOfTheWrongLength_IsRejected(int length)
    {
        var ex = Assert.Throws<ArgumentException>(() => BlockHeaderInfo.Parse(new byte[length]));
        Assert.Contains("80", ex.Message);
    }

    // ---- CoinAmount ----

    [Fact]
    public void Format_AppendsTheUnitOnlyWhenOneIsGiven()
    {
        Assert.Equal("1.00000000", CoinAmount.Format(100_000_000));
        Assert.Equal("1.00000000 PLM", CoinAmount.Format(100_000_000, "PLM"));
    }

    [Fact]
    public void FormatIn_ZeroDecimalsAndZeroValues_AreRenderedCorrectly()
    {
        Assert.Equal("5", CoinAmount.FormatIn(5, "sat", withLabel: false));
        Assert.Equal("0", CoinAmount.FormatIn(0, "sat", withLabel: false));
        Assert.Equal("0.00000", CoinAmount.FormatIn(0, "mPLM", withLabel: false));
        Assert.Equal("0.00 µPLM", CoinAmount.FormatIn(0, "µPLM"));
    }

    [Fact]
    public void UnknownUnit_IsRejectedWithItsNameInTheMessage()
    {
        var ex = Assert.Throws<ArgumentException>(() => CoinAmount.FormatIn(1, "BTC"));
        Assert.Contains("BTC", ex.Message);
    }

    [Theory]
    [InlineData("0", 0)]
    [InlineData("0.0", 0)]
    public void ZeroIsAValidAmount_ButNegativeIsNot(string text, long expected)
    {
        Assert.True(CoinAmount.TryParseCoins(text, out var sats));
        Assert.Equal(expected, sats);
        Assert.False(CoinAmount.TryParseCoins("-1", out _));
        Assert.False(CoinAmount.TryParseCoins("-0.00000001", out _));
    }

    // ---- WalletLoader ----

    [Fact]
    public void CustomDerivationPath_SurvivesTheDocumentRoundTrip()
    {
        var custom = new KeyPath("44'/0'/7'");
        var (doc, account) = WalletLoader.NewFromMnemonic(Words, null, ScriptKind.NativeSegwit, Profile, custom);

        var reopened = WalletLoader.ToAccount(doc);

        Assert.Equal(custom.ToString(), doc.AccountPath);
        Assert.Equal(account.GetReceiveAddress(3), reopened.GetReceiveAddress(3));
        var (_, defaultAccount) = WalletLoader.NewFromMnemonic(Words, null, ScriptKind.NativeSegwit, Profile);
        Assert.NotEqual(defaultAccount.GetReceiveAddress(0), account.GetReceiveAddress(0));
    }

    [Fact]
    public void InvalidMnemonic_ProducesAMeaningfulError()
    {
        var ex = Assert.Throws<InvalidDataException>(() =>
            WalletLoader.NewFromMnemonic("abandon abandon", null, ScriptKind.NativeSegwit, Profile));
        Assert.Contains("mnemonic", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ScriptKindOverride_WinsOverTheKindDetectedFromTheKeyHeader()
    {
        Assert.True(Bip39.TryParse(Words, out var m));
        var hd = HdAccount.FromMnemonic(m!, null, ScriptKind.NativeSegwit, Profile);

        var (xpubDoc, _) = WalletLoader.NewFromXpub(hd.ToSlip132(), Profile, ScriptKind.Legacy);
        Assert.Equal(nameof(ScriptKind.Legacy), xpubDoc.ScriptKind);

        var (xprvDoc, _) = WalletLoader.NewFromXprv(hd.ToSlip132Private(), Profile, ScriptKind.Legacy);
        Assert.Equal(nameof(ScriptKind.Legacy), xprvDoc.ScriptKind);
    }

    [Fact]
    public void InvalidExtendedKeys_ProduceMeaningfulErrors()
    {
        var pub = Assert.Throws<InvalidDataException>(() => WalletLoader.NewFromXpub("garbage", Profile, ScriptKind.NativeSegwit));
        Assert.Contains("public", pub.Message, StringComparison.OrdinalIgnoreCase);
        var prv = Assert.Throws<InvalidDataException>(() => WalletLoader.NewFromXprv("garbage", Profile, ScriptKind.NativeSegwit));
        Assert.Contains("private", prv.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---- storage / net ----

    [Fact]
    public void WrongPasswordException_CarriesAUserFacingMessage() =>
        Assert.False(string.IsNullOrWhiteSpace(new WrongPasswordException().Message));

    [Fact]
    public void PinFile_ContainingJsonNull_IsTreatedAsEmpty_NotACrash()
    {
        var path = Path.Combine(Path.GetTempPath(), $"plm-pins-{Guid.NewGuid()}.json");
        try
        {
            File.WriteAllText(path, "null");
            Assert.False(new CertificatePinStore(path).Reset("host", 50002)); // nothing pinned, no exception
        }
        finally { File.Delete(path); }
    }
}
