namespace PalladiumWallet.Core.Chain;

/// <summary>
/// The wallet's three network profiles (blueprint §3). Mainnet values verified
/// against the node source (chainparams.cpp / pow.cpp) per the blueprint.
/// </summary>
public static class ChainProfiles
{
    public static ChainProfile Mainnet { get; } = new()
    {
        Kind = NetKind.Mainnet,
        NetName = "mainnet",
        CoinUnit = "PLM",
        WifPrefix = 0x80,
        AddrP2pkh = 55,   // addresses starting with 'P'
        AddrP2sh = 5,     // addresses starting with '3'
        SegwitHrp = "plm",
        Bolt11Hrp = "plm",
        // Mainnet reuses Bitcoin's genesis (blueprint §3).
        GenesisHash = "000000000019d6689c085ae165831e934ff763ae46a2a6c172b3f1b60a8ce26f",
        DefaultTcpPort = 50001,
        DefaultSslPort = 50002,
        NodeP2pPort = 2333,
        Bip44CoinType = 746,
        UriScheme = "palladium",
        ExplorerUrl = "https://explorer.palladium-coin.com/",
        SkipPowValidation = true,
        BlockTimeSeconds = 120,
        CoinbaseMaturity = 120,
        MinConfirmations = 6,
        DustRelaySatPerVByte = 3,
        ExtKeyHeaders = new Dictionary<ScriptKind, ExtKeyHeaders>
        {
            [ScriptKind.Legacy] = new(0x0488ade4, 0x0488b21e),                // xprv / xpub
            [ScriptKind.WrappedSegwit] = new(0x049d7878, 0x049d7cb2),         // yprv / ypub
            [ScriptKind.WrappedSegwitMultisig] = new(0x0295b005, 0x0295b43f), // Yprv / Ypub
            [ScriptKind.NativeSegwit] = new(0x04b2430c, 0x04b24746),          // zprv / zpub
            [ScriptKind.NativeSegwitMultisig] = new(0x02aa7a99, 0x02aa7ed3),  // Zprv / Zpub
            // No SLIP-132 for P2TR: the context is given by the m/86'/… path
            [ScriptKind.Taproot] = new(0x0488ade4, 0x0488b21e),               // xprv / xpub (BIP32 standard)
        },
        // Known indexing servers for first contact (§3/§9); other
        // peers are discovered via server.peers.subscribe.
        BootstrapServers =
        [
            new ServerEndpoint("173.212.224.67", 50001, 50002),
            new ServerEndpoint("144.91.120.225", 50001, 50002),
            new ServerEndpoint("66.94.115.80", 50001, 50002),
            new ServerEndpoint("89.117.149.130", 50001, 50002),
        ],
        // Real mainnet [height, hash, bits], pulled from a fully-synced palladiumd via
        // RPC (getblockhash/getblockheader) or verified against a trusted indexing server,
        // spaced every 20,000 blocks (~660h) plus recent ones added at each release to keep
        // the header-anchoring walk short. Anchors WalletSynchronizer's header-chain verification (§7.3):
        // bounds how far back a forged header chain must be walked to be caught, since
        // this LWMA chain cannot be PoW-validated locally (SkipPowValidation).
        Checkpoints =
        [
            new Checkpoint(20000, "00000000000018ffaa9a332cfb418b5c8c3f988cf26598e378bbea9e93d26f74", 0x1b00ffff),
            new Checkpoint(40000, "000000000000011ad2ae53d9647a2130d2b1c41b18d200455211f1fef2a4ffb7", 0x1a013d28),
            new Checkpoint(60000, "000000000000034288c737d62011855fb598cd5c6ebb5c46c08acdec17620c8b", 0x1a0390b5),
            new Checkpoint(80000, "00000000000003662534639c7eb1dd7166efd85c308be19b367467c015279361", 0x1a0577b1),
            new Checkpoint(100000, "0000000000000850eba93bbc491f085e2c79c0c30c497292858c72e90cae69a5", 0x1a2c39e4),
            new Checkpoint(120000, "0000000000004c421e06c84f08a947d994cb801b8ac7cade12d616209d851d43", 0x1a510836),
            new Checkpoint(140000, "00000000000075b1a095a5969a2ca729b646ab0e2b9a9bd72aa603b3b889c398", 0x1b00a257),
            new Checkpoint(160000, "000000000000028c8ba89e695f80fc78491bcf7e583fc7cd868e0a9c2973dfbe", 0x1a0ed8bb),
            new Checkpoint(180000, "00000000000003632ffdcf60ce3892f44613dbbfe761b14522e91a1e650c092f", 0x1a064544),
            new Checkpoint(200000, "000000000000221a9e16556453fc86308b260d95d80c14bafaf053a09374e7eb", 0x1a22c142),
            new Checkpoint(220000, "0000000000001f63d259df5b9b20182dbaea92f2858fe836b895b2a33430c6dd", 0x1a3311af),
            new Checkpoint(240000, "0000000000004c8a80484a1d6ab8a08460ac688445ccafe5cbac8b11bc471f11", 0x1a764de8),
            new Checkpoint(260000, "000000000000ab1b71140485359633ef991588613f520052ce87acb70a36b4de", 0x1b022691),
            new Checkpoint(280000, "00000000000678b07eda63cf01b099737ce832470d0e71769d157190f4d9ac9b", 0x1b118047),
            new Checkpoint(300000, "0000000000013acdf07a4fb988bbe9824c36eb421478a71c8196cf524dcba143", 0x1b01ddc1),
            new Checkpoint(320000, "000000000000079f2fb9866f1bb452ee5f47a35e0d494c6bc90331f582b07991", 0x1a0e8592),
            new Checkpoint(340000, "000000000000000e45f7fbcff239da7965e1bd58aea3a10aef2bc8afbca822be", 0x1a0328e2),
            new Checkpoint(360000, "000000000000016d60397423447eb42f8b4ba693fe16f1e64e9fdf58c94ca2a6", 0x1a020861),
            new Checkpoint(380000, "000000000000004ba0d45a0462501a12251947d27e52ec810edd69007b7acf90", 0x1a01568d),
            new Checkpoint(400000, "0000000000000010ac708514e8b837703233161099bf55400433cef32311f495", 0x1a01ce70),
            new Checkpoint(420000, "00000000000000351392487709e637bc7a9b0b2296a0e443f54e2af5b3f00e16", 0x1a01197e),
            new Checkpoint(440000, "00000000000001b09d7da81403a9b383a734305a8783cb3a0dbe009edea26a95", 0x1a0216c4),
            new Checkpoint(460000, "00000000000000ecc7413f638bfe7be80a36bacab858ce9a814f194d9df526d5", 0x1a07dd8f),
            new Checkpoint(468800, "000000000000052c61652eed72b441d8c1f1926710a8d691d101be4961dba105", 0x1a1838ee),
            new Checkpoint(475124, "00000000000009e66da1e1a430fd1932aa75bf513053df088764545d941f13ca", 0x1a1a98cb),
        ],
    };

    public static ChainProfile Testnet { get; } = Mainnet with
    {
        Kind = NetKind.Testnet,
        NetName = "testnet",
        MinConfirmations = 1,
        WifPrefix = 0xff,
        AddrP2pkh = 127,
        AddrP2sh = 115,
        SegwitHrp = "tplm",
        Bolt11Hrp = "tplm",
        // TODO: verify against the node's chainparams.cpp (the blueprint does not report it;
        // here Bitcoin testnet3 genesis is assumed, to be confirmed).
        GenesisHash = "000000000933ea01ad0ee984209779baaec3ced90fa3f408719526f8d77f4943",
        NodeP2pPort = 12333,
        Bip44CoinType = 1,
        ExtKeyHeaders = new Dictionary<ScriptKind, ExtKeyHeaders>
        {
            // tprv/tpub from the blueprint; the testnet SLIP-132 variants (u/v/U/V) are
            // the standard Electrum values.
            [ScriptKind.Legacy] = new(0x04358394, 0x043587cf),                // tprv / tpub
            [ScriptKind.WrappedSegwit] = new(0x044a4e28, 0x044a5262),         // uprv / upub
            [ScriptKind.WrappedSegwitMultisig] = new(0x024285b5, 0x024289ef), // Uprv / Upub
            [ScriptKind.NativeSegwit] = new(0x045f18bc, 0x045f1cf6),          // vprv / vpub
            [ScriptKind.NativeSegwitMultisig] = new(0x02575048, 0x02575483),  // Vprv / Vpub
            [ScriptKind.Taproot] = new(0x04358394, 0x043587cf),               // tprv / tpub (BIP32 standard)
        },
        BootstrapServers = [],
        // TODO: populate from a synced testnet palladiumd (§7.3) — left empty because
        // WalletSynchronizer already treats "no checkpoint at or below this height" as
        // a no-op, so an empty array is safe, just unanchored.
        Checkpoints = [],
    };

    public static ChainProfile Regtest { get; } = Testnet with
    {
        Kind = NetKind.Regtest,
        NetName = "regtest",
        SegwitHrp = "rplm",
        Bolt11Hrp = "rplm",
        // TODO: verify against the node's chainparams.cpp (Bitcoin regtest genesis assumed).
        GenesisHash = "0f9188f13cb7b2c71f2a335e3a4fc328bf5beb436012afca590b1a11466e2206",
        NodeP2pPort = 28444,
        // Regtest is regenerated locally on demand: hardcoded checkpoints make no sense here.
        Checkpoints = [],
    };

    public static ChainProfile For(NetKind kind) => kind switch
    {
        NetKind.Mainnet => Mainnet,
        NetKind.Testnet => Testnet,
        NetKind.Regtest => Regtest,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}
