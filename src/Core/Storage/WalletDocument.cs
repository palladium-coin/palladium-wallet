using System.Text.Json;
using System.Text.Json.Serialization;

namespace PalladiumWallet.Core.Storage;

/// <summary>
/// Wallet file schema (blueprint §8), versioned to allow automatic
/// migrations on opening. Encryption (when there is a password) wraps
/// the entire document via <see cref="EncryptedFile"/>.
/// </summary>
public sealed class WalletDocument
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    /// <summary>Network name (mainnet/testnet/regtest), as ChainProfile.NetName.</summary>
    public required string Network { get; set; }

    /// <summary>Script type (name of the ScriptKind enum).</summary>
    public required string ScriptKind { get; set; }

    /// <summary>BIP39 mnemonic in plaintext in the document (the document must be encrypted!); null if watch-only.</summary>
    public string? Mnemonic { get; set; }

    /// <summary>BIP39 extension word (§4.1); null if absent.</summary>
    public string? Passphrase { get; set; }

    /// <summary>Account path (e.g. "84'/746'/0'"); null for imported WIF.</summary>
    public string? AccountPath { get; set; }

    /// <summary>Account xpub in SLIP-132 for HD wallets; null for imported WIF.</summary>
    public string? AccountXpub { get; set; }

    /// <summary>Account xprv in SLIP-132; present only for import from xprv without seed.</summary>
    public string? AccountXprv { get; set; }

    public string? MasterFingerprint { get; set; }

    /// <summary>Imported WIF keys (in plaintext in the document — must be encrypted!).</summary>
    public List<string>? WifKeys { get; set; }

    /// <summary>Watch-only addresses with no associated private key (pure address import).</summary>
    public List<string>? WatchAddresses { get; set; }

    /// <summary>Gap limit for address scanning (§5), configurable.</summary>
    public int GapLimit { get; set; } = 20;

    /// <summary>Labels by address/txid (§12).</summary>
    public Dictionary<string, string> Labels { get; set; } = [];

    /// <summary>Contact address book (name + blockchain address).</summary>
    public List<StoredContact> Contacts { get; set; } = [];

    /// <summary>Cache of the last synced state (balance/history viewable offline).</summary>
    public SyncCache? Cache { get; set; }

    public bool IsWatchOnly =>
        Mnemonic is null &&
        AccountXprv is null &&
        (WifKeys is null || WifKeys.Count == 0);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static WalletDocument FromJson(string json)
    {
        var doc = JsonSerializer.Deserialize<WalletDocument>(json, JsonOptions)
            ?? throw new InvalidDataException("Invalid wallet file.");
        return Migrate(doc);
    }

    /// <summary>Schema migrations on open (§8). For now only v1 exists.</summary>
    private static WalletDocument Migrate(WalletDocument doc) => doc.Version switch
    {
        CurrentVersion => doc,
        _ => throw new InvalidDataException(
            $"Wallet file version {doc.Version} is not supported (max {CurrentVersion})."),
    };
}

/// <summary>Contatto in rubrica: nome leggibile + indirizzo blockchain.</summary>
public sealed class StoredContact
{
    public required string Name { get; set; }
    public required string Address { get; set; }
}

/// <summary>Stato sincronizzato persistito: permette di mostrare saldo/storico offline.</summary>
public sealed class SyncCache
{
    public int TipHeight { get; set; }
    public long ConfirmedSats { get; set; }
    public long UnconfirmedSats { get; set; }

    /// <summary>Confirmed but not yet spendable (coinbase immature or under min confirmations). Subset of ConfirmedSats.</summary>
    public long ImmatureSats { get; set; }

    /// <summary>
    /// Confirmed, past its threshold, but its Merkle proof isn't checked yet. Subset of
    /// ConfirmedSats — NOT disjoint from ImmatureSats, so never subtract both from
    /// ConfirmedSats to get a spendable total; use SpendableSats instead.
    /// </summary>
    public long PendingVerificationSats { get; set; }

    /// <summary>The actual spendable balance: sum of UTXOs passing UtxoSpendability.IsSpendable.</summary>
    public long SpendableSats { get; set; }
    public int NextReceiveIndex { get; set; }
    public int NextChangeIndex { get; set; }
    public List<CachedTx> History { get; set; } = [];
    public List<CachedUtxo> Utxos { get; set; } = [];
    public List<CachedAddress> Addresses { get; set; } = [];

    /// <summary>
    /// The outpoints the user has frozen. The synchronizer rebuilds every UTXO from the chain, so
    /// the flag is not stored anywhere else: it is read from here and handed to
    /// WalletSynchronizer.FrozenOutpoints before each sync (GUI and CLI alike).
    /// </summary>
    public HashSet<(string Txid, int Vout)> FrozenOutpoints() =>
        Utxos.Where(u => u.Frozen).Select(u => (u.Txid, u.Vout)).ToHashSet();

    /// <summary>
    /// Raw transaction cache (txid → hex). Avoids re-downloading confirmed
    /// transactions on every launch: confirmed txs are immutable by definition.
    /// May be partially populated after an interrupted sync (e.g. -101 error);
    /// the synchronizer resumes from where it left off.
    /// </summary>
    public Dictionary<string, string>? RawTxHex { get; set; }

    /// <summary>
    /// Already-verified Merkle proofs (txid → block height). Avoids re-verifying
    /// the same proofs on every launch: confirmations are immutable.
    /// </summary>
    public Dictionary<string, int>? VerifiedAt { get; set; }

    /// <summary>
    /// Raw headers by height (height → hex). Immutable: never re-fetched once saved.
    /// Eliminates GetBlockHeader calls for Merkle proofs already verified in
    /// subsequent syncs.
    /// </summary>
    public Dictionary<int, string>? BlockHeaders { get; set; }

    /// <summary>
    /// Checkpoint height → lowest height already proven to hash-chain up to it (§7.3, ADR 0040).
    /// Avoids re-walking and re-verifying the header chain on every launch once the headers
    /// themselves are cached. A cache without this field predates the fix of the anchoring
    /// direction: its verified-proof claims are not trusted and get re-verified.
    /// </summary>
    public Dictionary<int, int>? AnchoredDownTo { get; set; }
}

/// <summary>Scanned address with its own balance and transaction count (address view).</summary>
public sealed class CachedAddress
{
    public required string Address { get; set; }
    public bool IsChange { get; set; }
    public int Index { get; set; }
    public long BalanceSats { get; set; }
    public int TxCount { get; set; }
}

public sealed class CachedTx
{
    public required string Txid { get; set; }
    public int Height { get; set; }
    public long DeltaSats { get; set; }

    /// <summary>
    /// True once this tx's Merkle proof has actually been checked against a
    /// checkpoint-anchored header (not merely "confirmed" — a confirmed tx can still
    /// be pending its own proof while background verification catches up). Always
    /// false for unconfirmed (mempool) entries, which have no proof to check yet.
    /// </summary>
    public bool Verified { get; set; }
}

public sealed class CachedUtxo
{
    public required string Txid { get; set; }
    public int Vout { get; set; }
    public long ValueSats { get; set; }
    public required string Address { get; set; }
    public bool IsChange { get; set; }
    public int AddressIndex { get; set; }
    public int Height { get; set; }
    public bool IsCoinbase { get; set; }
    public bool Frozen { get; set; }

    /// <summary>
    /// True once the owning tx's Merkle proof has been checked (see <see cref="CachedTx.Verified"/>).
    /// Gates spendability in <see cref="Wallet.UtxoSpendability.IsSpendable"/>: a server can report a
    /// fake confirmed UTXO before its proof is checked, so coin selection must never touch it early.
    /// </summary>
    public bool Verified { get; set; }
}
