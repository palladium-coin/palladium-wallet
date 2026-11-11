using NBitcoin;
using PalladiumWallet.Core.Chain;

namespace PalladiumWallet.Core.Crypto;

/// <summary>
/// Single-sig HD account (blueprint §4.2/§4.4): extended account key (xprv,
/// or xpub-only = watch-only) + script type + network profile. Derives receiving
/// (change=0) and change (change=1) addresses on-demand by index.
/// Addresses are always derived from the account xpub (non-hardened sub-path),
/// so watch-only works by construction. The full keystore
/// (encryption, factory from wallet file, §4.5) arrives with persistence (§8).
/// </summary>
public sealed class HdAccount : IWalletAccount
{
    private readonly ExtKey? _accountXprv;

    public ScriptKind Kind { get; }
    public ChainProfile Profile { get; }

    /// <summary>Account path relative to the root (e.g. 84'/746'/0', or custom).</summary>
    public KeyPath AccountPath { get; }

    /// <summary>
    /// Master key fingerprint: together with <see cref="AccountPath"/> forms the
    /// origin info required by PSBTs (§6.5). Zero if unknown (imported xpub
    /// without metadata).
    /// </summary>
    public HDFingerprint MasterFingerprint { get; }

    public ExtPubKey AccountXpub { get; }

    /// <summary>
    /// True when <see cref="MasterFingerprint"/> is a real one (seed wallet, or supplied on import).
    /// Without it the PSBT carries no BIP32 origin information rather than a made-up one.
    /// </summary>
    public bool HasKnownOrigin => MasterFingerprint.ToBytes().Any(b => b != 0);

    /// <summary>Parses the 8-hex-digit fingerprint stored in the wallet file; null if absent or malformed.</summary>
    public static HDFingerprint? ParseFingerprint(string? hex)
    {
        if (hex is not { Length: 8 })
            return null;
        try { return new HDFingerprint(Convert.FromHexString(hex)); }
        catch (FormatException) { return null; }
    }

    /// <summary>True if the account only knows the xpub: can build but not sign (§4.5).</summary>
    public bool IsWatchOnly => _accountXprv is null;

    private HdAccount(ExtKey? accountXprv, ExtPubKey accountXpub, ScriptKind kind,
        ChainProfile profile, KeyPath accountPath, HDFingerprint masterFingerprint)
    {
        // Validate the mapping immediately (multisig types are not supported here).
        DerivationPaths.ScriptPubKeyTypeFor(kind);
        _accountXprv = accountXprv;
        AccountXpub = accountXpub;
        Kind = kind;
        Profile = profile;
        AccountPath = accountPath;
        MasterFingerprint = masterFingerprint;
    }

    /// <summary>Main case: BIP39 mnemonic (+ optional passphrase) → standard account.</summary>
    public static HdAccount FromMnemonic(Mnemonic mnemonic, string? passphrase,
        ScriptKind kind, ChainProfile profile, int account = 0) =>
        FromSeed(Bip39.ToSeed(mnemonic, passphrase), kind, profile, account);

    public static HdAccount FromSeed(byte[] seed, ScriptKind kind, ChainProfile profile, int account = 0) =>
        FromSeed(seed, kind, profile, DerivationPaths.AccountPath(kind, profile, account));

    /// <summary>
    /// Import with a custom derivation path (§4.2, Sparrow-like):
    /// <paramref name="kind"/> only determines the generated address type.
    /// </summary>
    public static HdAccount FromSeed(byte[] seed, ScriptKind kind, ChainProfile profile, KeyPath accountPath)
    {
        var root = ExtKey.CreateFromSeed(seed);
        // The account is always derived from the xprv: hardened path levels
        // cannot be derived from an xpub alone.
        var accountXprv = root.Derive(accountPath);
        return new HdAccount(accountXprv, accountXprv.Neuter(), kind, profile,
            accountPath, root.Neuter().PubKey.GetHDFingerPrint());
    }

    /// <summary>
    /// Watch-only from account xpub (§4.4): fingerprint and path are known only
    /// if supplied by the importer (required for PSBT origin info).
    /// </summary>
    public static HdAccount FromAccountXpub(ExtPubKey accountXpub, ScriptKind kind,
        ChainProfile profile, KeyPath? accountPath = null, HDFingerprint? masterFingerprint = null) =>
        new(null, accountXpub, kind, profile,
            accountPath ?? DerivationPaths.AccountPath(kind, profile),
            masterFingerprint ?? default);

    /// <summary>Spendable import from account xprv.</summary>
    public static HdAccount FromAccountXprv(ExtKey accountXprv, ScriptKind kind,
        ChainProfile profile, KeyPath? accountPath = null, HDFingerprint? masterFingerprint = null) =>
        new(accountXprv, accountXprv.Neuter(), kind, profile,
            accountPath ?? DerivationPaths.AccountPath(kind, profile),
            masterFingerprint ?? default);

    public BitcoinAddress GetAddress(bool isChange, int index) =>
        GetPublicKey(isChange, index)!.GetAddress(
            DerivationPaths.ScriptPubKeyTypeFor(Kind),
            PalladiumNetworks.For(Profile.Kind));

    public BitcoinAddress GetReceiveAddress(int index) => GetAddress(isChange: false, index);

    public BitcoinAddress GetChangeAddress(int index) => GetAddress(isChange: true, index);

    public PubKey? GetPublicKey(bool isChange, int index) =>
        AccountXpub.Derive(DerivationPaths.AddressSubPath(isChange, index)).PubKey;

    /// <summary>Private key for an address; null if watch-only (§17).</summary>
    public Key? GetPrivateKey(bool isChange, int index) =>
        IsWatchOnly ? null : GetExtPrivateKey(isChange, index).PrivateKey;

    /// <summary>HD accounts use the gap limit: no fixed address list.</summary>
    public IReadOnlyList<(BitcoinAddress Address, bool IsChange, int Index)>? FixedAddresses => null;

    /// <summary>
    /// Extended private key for an address. Throws if watch-only: no private key
    /// can be derived from public keys alone (§17).
    /// </summary>
    public ExtKey GetExtPrivateKey(bool isChange, int index) =>
        (_accountXprv ?? throw new InvalidOperationException("Watch-only account: cannot sign."))
            .Derive(DerivationPaths.AddressSubPath(isChange, index));

    /// <summary>Account xpub in SLIP-132 format (xpub/ypub/zpub according to Kind).</summary>
    public string ToSlip132() => Slip132.Encode(AccountXpub, Kind, Profile);

    /// <summary>Account xprv in SLIP-132 format. Throws if watch-only.</summary>
    public string ToSlip132Private() =>
        Slip132.Encode(
            _accountXprv ?? throw new InvalidOperationException("Watch-only account: no private key."),
            Kind, Profile);
}
