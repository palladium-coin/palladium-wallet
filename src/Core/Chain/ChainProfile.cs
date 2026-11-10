namespace PalladiumWallet.Core.Chain;

/// <summary>
/// Supported network type (single selector for the whole wallet, blueprint §3).
/// </summary>
public enum NetKind
{
    Mainnet,
    Testnet,
    Regtest,
}

/// <summary>
/// Supported script/address type (blueprint §4.3).
/// </summary>
public enum ScriptKind
{
    /// <summary>P2PKH legacy.</summary>
    Legacy,

    /// <summary>P2SH-P2WPKH (segwit wrapped).</summary>
    WrappedSegwit,

    /// <summary>P2WPKH (native segwit) — recommended default.</summary>
    NativeSegwit,

    /// <summary>P2SH-P2WSH (multisig wrapped).</summary>
    WrappedSegwitMultisig,

    /// <summary>P2WSH (multisig native).</summary>
    NativeSegwitMultisig,

    /// <summary>P2TR key-path only (Taproot, BIP86) — witness v1, bech32m.</summary>
    Taproot,
}

/// <summary>
/// Pair of BIP32 version headers (4-byte big-endian) to serialize
/// xprv/xpub and SLIP-132 variants (y/z/Y/Z) — blueprint §3.
/// </summary>
public readonly record struct ExtKeyHeaders(uint Private, uint Public)
{
    /// <summary>Private header as 4 big-endian bytes (to prepend to the BIP32 payload).</summary>
    public byte[] PrivateBytes() => ToBytes(Private);

    /// <summary>Public header as 4 big-endian bytes.</summary>
    public byte[] PublicBytes() => ToBytes(Public);

    internal static byte[] ToBytes(uint header) =>
    [
        (byte)(header >> 24),
        (byte)(header >> 16),
        (byte)(header >> 8),
        (byte)header,
    ];
}

/// <summary>
/// Bootstrap indexing server: host + TCP port + SSL port (blueprint §3/§9).
/// </summary>
public readonly record struct ServerEndpoint(string Host, int TcpPort, int SslPort);

/// <summary>
/// Chain checkpoint: anchors trust when PoW validation is disabled
/// (blueprint §7.3). Target serialized as compact "bits".
/// </summary>
public readonly record struct Checkpoint(int Height, string BlockHash, uint Bits);

/// <summary>
/// Network/chain profile (blueprint §3): all chain-specific constants,
/// centralized in one place. No magic numbers elsewhere in the code.
/// </summary>
public sealed record ChainProfile
{
    public required NetKind Kind { get; init; }

    /// <summary>Network name, also used as the data subfolder.</summary>
    public required string NetName { get; init; }

    /// <summary>Unit symbol (e.g. PLM).</summary>
    public required string CoinUnit { get; init; }

    /// <summary>WIF prefix for private keys.</summary>
    public required byte WifPrefix { get; init; }

    /// <summary>Version byte for P2PKH addresses.</summary>
    public required byte AddrP2pkh { get; init; }

    /// <summary>Version byte for P2SH addresses.</summary>
    public required byte AddrP2sh { get; init; }

    /// <summary>Human-readable part bech32/bech32m.</summary>
    public required string SegwitHrp { get; init; }

    /// <summary>HRP for BOLT11 invoices (Lightning, later phase — §11).</summary>
    public required string Bolt11Hrp { get; init; }

    /// <summary>Genesis block hash (hex, explorer byte order).</summary>
    public required string GenesisHash { get; init; }

    /// <summary>TCP port of the indexing server (not the node's P2P port).</summary>
    public required int DefaultTcpPort { get; init; }

    /// <summary>SSL port of the indexing server.</summary>
    public required int DefaultSslPort { get; init; }

    /// <summary>Node's P2P port — informational only: the SPV wallet does not use it.</summary>
    public required int NodeP2pPort { get; init; }

    /// <summary>SLIP-0044 coin type in derivation paths (wallet convention).</summary>
    public required int Bip44CoinType { get; init; }

    /// <summary>BIP21 payment URI scheme (without the colon).</summary>
    public required string UriScheme { get; init; }

    /// <summary>Default block explorer.</summary>
    public required string ExplorerUrl { get; init; }

    /// <summary>
    /// The chain uses LWMA (per-block retargeting, 2 minutes): an SPV client cannot
    /// recompute it, so bits/target validation must be skipped and trust anchored
    /// to checkpoints (§3/§7). For the reference chain it is always true.
    /// </summary>
    public required bool SkipPowValidation { get; init; }

    /// <summary>Target block time in seconds (LWMA v2: 120s).</summary>
    public required int BlockTimeSeconds { get; init; }

    /// <summary>Blocks a coinbase output must wait before it can be spent.</summary>
    public required int CoinbaseMaturity { get; init; }

    /// <summary>Minimum confirmations required for a regular UTXO to be spendable.</summary>
    public required int MinConfirmations { get; init; }

    /// <summary>
    /// Dust relay fee of the nodes, in sat/vB (Core's <c>DUST_RELAY_TX_FEE</c>, 3000 sat/kvB;
    /// Palladium Core v2.1.0, <c>src/policy/policy.h</c>). It is node relay policy, not consensus:
    /// an output below <see cref="DustLimitSats"/> is refused by the network, so the wallet refuses it first.
    /// </summary>
    public required int DustRelaySatPerVByte { get; init; }

    /// <summary>BIP32/SLIP-132 headers for each script type.</summary>
    public required IReadOnlyDictionary<ScriptKind, ExtKeyHeaders> ExtKeyHeaders { get; init; }

    /// <summary>Indexing servers for first contact (bootstrap).</summary>
    public required IReadOnlyList<ServerEndpoint> BootstrapServers { get; init; }

    /// <summary>Hardcoded checkpoints, updated at every release (§7.3).</summary>
    public required IReadOnlyList<Checkpoint> Checkpoints { get; init; }

    /// <summary>
    /// True for a block above the newest checkpoint of a profile that has checkpoints: its header
    /// cannot be anchored (ADR 0040), so only the server-supplied header and the Merkle proof back
    /// it. False for confirmed history the anchoring covers, and for profiles without any
    /// checkpoint (testnet/regtest), where nothing is anchored by design and the notion does not apply.
    /// </summary>
    public bool IsAboveNewestCheckpoint(int height) =>
        height > 0 && Checkpoints.Count > 0 && height > Checkpoints.Max(c => c.Height);
}
