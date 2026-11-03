using NBitcoin;
using PalladiumWallet.Core.Chain;
using PalladiumWallet.Core.Crypto;

namespace PalladiumWallet.Tests.Crypto;

public class DerivationPathsEdgeCaseTests
{
    [Theory]
    [InlineData(ScriptKind.WrappedSegwitMultisig)]
    [InlineData(ScriptKind.NativeSegwitMultisig)]
    public void Multisig_Kinds_FailLoudlyInsteadOfDerivingAWrongWallet(ScriptKind kind) =>
        Assert.Throws<NotSupportedException>(() => DerivationPaths.ScriptPubKeyTypeFor(kind));

    [Fact]
    public void Multisig_UsesBip48Purpose_AndTaprootBip86()
    {
        Assert.Equal(48, DerivationPaths.PurposeFor(ScriptKind.NativeSegwitMultisig));
        Assert.Equal(48, DerivationPaths.PurposeFor(ScriptKind.WrappedSegwitMultisig));
        Assert.Equal(86, DerivationPaths.PurposeFor(ScriptKind.Taproot));
    }

    [Fact]
    public void UnknownScriptKind_IsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => DerivationPaths.PurposeFor((ScriptKind)999));
        Assert.Throws<ArgumentOutOfRangeException>(() => DerivationPaths.ScriptPubKeyTypeFor((ScriptKind)999));
    }

    [Fact]
    public void NegativeAccountOrIndex_IsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => DerivationPaths.AccountPath(ScriptKind.NativeSegwit, ChainProfiles.Mainnet, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => DerivationPaths.AddressSubPath(false, -1));
    }

    [Fact]
    public void AccountPath_UsesCoinTypeFromProfile()
    {
        Assert.Equal("84'/746'/0'", DerivationPaths.AccountPath(ScriptKind.NativeSegwit, ChainProfiles.Mainnet).ToString());
        Assert.Equal("49'/746'/2'", DerivationPaths.AccountPath(ScriptKind.WrappedSegwit, ChainProfiles.Mainnet, 2).ToString());
    }

    [Fact]
    public void KindFor_MapsAddressTypes()
    {
        var net = PalladiumNetworks.Regtest;
        var key = new Key();
        Assert.Equal(ScriptKind.NativeSegwit, DerivationPaths.KindFor(key.PubKey.GetAddress(ScriptPubKeyType.Segwit, net)));
        Assert.Equal(ScriptKind.WrappedSegwit, DerivationPaths.KindFor(key.PubKey.GetAddress(ScriptPubKeyType.SegwitP2SH, net)));
        Assert.Equal(ScriptKind.Legacy, DerivationPaths.KindFor(key.PubKey.GetAddress(ScriptPubKeyType.Legacy, net)));
        Assert.Equal(ScriptKind.Taproot, DerivationPaths.KindFor(key.PubKey.GetAddress(ScriptPubKeyType.TaprootBIP86, net)));
        Assert.Equal(ScriptKind.NativeSegwitMultisig,
            DerivationPaths.KindFor(key.PubKey.WitHash.ScriptPubKey.WitHash.GetAddress(net)));
    }
}
