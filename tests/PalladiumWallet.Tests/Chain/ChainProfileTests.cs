using NBitcoin;
using NBitcoin.DataEncoders;
using PalladiumWallet.Core.Chain;

namespace PalladiumWallet.Tests.Chain;

public class ChainProfileTests
{
    [Fact]
    public void Mainnet_ha_le_costanti_del_blueprint()
    {
        var p = ChainProfiles.Mainnet;

        Assert.Equal("PLM", p.CoinUnit);
        Assert.Equal(0x80, p.WifPrefix);
        Assert.Equal(55, p.AddrP2pkh);
        Assert.Equal(5, p.AddrP2sh);
        Assert.Equal("plm", p.SegwitHrp);
        Assert.Equal("000000000019d6689c085ae165831e934ff763ae46a2a6c172b3f1b60a8ce26f", p.GenesisHash);
        Assert.Equal(50001, p.DefaultTcpPort);
        Assert.Equal(50002, p.DefaultSslPort);
        Assert.Equal(746, p.Bip44CoinType);
        Assert.Equal("palladium", p.UriScheme);
        Assert.True(p.SkipPowValidation); // mandatory: the chain uses LWMA (§3)
        Assert.Equal(120, p.BlockTimeSeconds);
    }

    [Fact]
    public void Testnet_e_regtest_hanno_i_valori_del_blueprint()
    {
        var t = ChainProfiles.Testnet;
        Assert.Equal(0xff, t.WifPrefix);
        Assert.Equal(127, t.AddrP2pkh);
        Assert.Equal(115, t.AddrP2sh);
        Assert.Equal("tplm", t.SegwitHrp);
        Assert.Equal(1, t.Bip44CoinType);

        var r = ChainProfiles.Regtest;
        Assert.Equal("rplm", r.SegwitHrp);
    }

    [Fact]
    public void Selettore_di_rete_restituisce_il_profilo_giusto()
    {
        Assert.Same(ChainProfiles.Mainnet, ChainProfiles.For(NetKind.Mainnet));
        Assert.Same(ChainProfiles.Testnet, ChainProfiles.For(NetKind.Testnet));
        Assert.Same(ChainProfiles.Regtest, ChainProfiles.For(NetKind.Regtest));
    }

    [Theory]
    [InlineData(ScriptKind.Legacy, "xprv", "xpub")]
    [InlineData(ScriptKind.WrappedSegwit, "yprv", "ypub")]
    [InlineData(ScriptKind.WrappedSegwitMultisig, "Yprv", "Ypub")]
    [InlineData(ScriptKind.NativeSegwit, "zprv", "zpub")]
    [InlineData(ScriptKind.NativeSegwitMultisig, "Zprv", "Zpub")]
    public void Header_estesi_mainnet_producono_i_prefissi_slip132_attesi(
        ScriptKind kind, string privPrefix, string pubPrefix)
    {
        var headers = ChainProfiles.Mainnet.ExtKeyHeaders[kind];
        Assert.StartsWith(privPrefix, EncodeWithHeader(headers.Private));
        Assert.StartsWith(pubPrefix, EncodeWithHeader(headers.Public));
    }

    [Fact]
    public void Header_estesi_testnet_producono_tprv_tpub()
    {
        var headers = ChainProfiles.Testnet.ExtKeyHeaders[ScriptKind.Legacy];
        Assert.StartsWith("tprv", EncodeWithHeader(headers.Private));
        Assert.StartsWith("tpub", EncodeWithHeader(headers.Public));
    }

    [Fact]
    public void Rete_sconosciuta_lancia_ArgumentException()
    {
        Assert.ThrowsAny<ArgumentException>(() => ChainProfiles.For((NetKind)99));
    }

    [Fact]
    public void I_tre_profili_sono_istanze_distinte()
    {
        Assert.NotSame(ChainProfiles.Mainnet, ChainProfiles.Testnet);
        Assert.NotSame(ChainProfiles.Mainnet, ChainProfiles.Regtest);
        Assert.NotSame(ChainProfiles.Testnet, ChainProfiles.Regtest);
    }

    [Fact]
    public void Tutti_i_profili_hanno_gli_stessi_porti_tcp_ssl()
    {
        foreach (var profile in new[] { ChainProfiles.Mainnet, ChainProfiles.Testnet, ChainProfiles.Regtest })
        {
            Assert.Equal(50001, profile.DefaultTcpPort);
            Assert.Equal(50002, profile.DefaultSslPort);
        }
    }

    [Fact]
    public void Coin_type_mainnet_e_746()
    {
        Assert.Equal(746, ChainProfiles.Mainnet.Bip44CoinType);
    }

    [Fact]
    public void Coin_type_testnet_e_1()
    {
        Assert.Equal(1, ChainProfiles.Testnet.Bip44CoinType);
    }

    // Serialize header (4-byte BE) + 74-byte BIP32 payload and encode Base58Check:
    // the resulting textual prefix depends only on the header.
    private static string EncodeWithHeader(uint header)
    {
        var data = new byte[78];
        data[0] = (byte)(header >> 24);
        data[1] = (byte)(header >> 16);
        data[2] = (byte)(header >> 8);
        data[3] = (byte)header;
        return Encoders.Base58Check.EncodeData(data);
    }

    [Theory]
    [InlineData(0, false)]        // unconfirmed
    [InlineData(1, false)]
    [InlineData(100, false)]      // at the newest checkpoint: still anchored
    [InlineData(101, true)]
    [InlineData(5_000_000, true)]
    public void IsAboveNewestCheckpoint_flags_only_heights_the_anchoring_cannot_cover(int height, bool expected)
    {
        var profile = ChainProfiles.Mainnet with
        {
            Checkpoints = [new Checkpoint(50, "00", 0), new Checkpoint(100, "00", 0)],
        };
        Assert.Equal(expected, profile.IsAboveNewestCheckpoint(height));
    }

    [Fact]
    public void A_profile_without_checkpoints_never_reports_an_unanchored_height() =>
        Assert.False((ChainProfiles.Mainnet with { Checkpoints = [] }).IsAboveNewestCheckpoint(1_000_000));
}
