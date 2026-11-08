using NBitcoin;
using PalladiumWallet.Core.Chain;
using PalladiumWallet.Core.Crypto;
using PalladiumWallet.Core.Storage;

namespace PalladiumWallet.Core.Wallet;

/// <summary>
/// Wallet type factory (blueprint §4.5): reconstructs the correct account type
/// from a document (HD from seed, HD from imported xprv, imported WIF, watch-only).
/// </summary>
public static class WalletLoader
{
    public static ChainProfile ProfileOf(WalletDocument doc) =>
        ChainProfiles.For(Enum.Parse<NetKind>(doc.Network, ignoreCase: true));

    public static IWalletAccount ToAccount(WalletDocument doc)
    {
        var profile = ProfileOf(doc);
        var kind = Enum.Parse<ScriptKind>(doc.ScriptKind);
        var network = PalladiumNetworks.For(profile.Kind);

        // 1. HD from seed (most common case)
        if (doc.Mnemonic is { } words)
        {
            if (!Bip39.TryParse(words, out var mnemonic))
                throw new InvalidDataException("Invalid mnemonic in wallet file.");
            var path = KeyPath.Parse(doc.AccountPath ?? DerivationPaths.AccountPath(kind, profile).ToString());
            return HdAccount.FromSeed(Bip39.ToSeed(mnemonic!, doc.Passphrase), kind, profile, path);
        }

        // 2. HD from imported xprv (spendable, no seed)
        if (doc.AccountXprv is { } xprvStr)
        {
            if (!Slip132.TryDecodePrivate(xprvStr, profile, out var xprv, out _))
                throw new InvalidDataException("Xprv in wallet file is invalid for this network.");
            var path = doc.AccountPath is { Length: > 0 } p ? KeyPath.Parse(p) : null;
            return HdAccount.FromAccountXprv(xprv!, kind, profile, path);
        }

        // 3. Imported WIF keys
        if (doc.WifKeys is { Count: > 0 } wifKeys)
        {
            var entries = wifKeys.Select(wif =>
            {
                var secret = new BitcoinSecret(wif, network);
                var addr = secret.PrivateKey.PubKey
                    .GetAddress(DerivationPaths.ScriptPubKeyTypeFor(kind), network);
                return (addr, (Key?)secret.PrivateKey);
            }).ToList();
            return new ImportedKeyAccount(entries, kind, profile);
        }

        // 4. Watch-only imported addresses (no keys, no HD derivation)
        if (doc.WatchAddresses is { Count: > 0 } watchAddresses)
        {
            var entries = watchAddresses.Select(a =>
                (BitcoinAddress.Create(a.Trim(), network), (Key?)null)).ToList();
            return new ImportedKeyAccount(entries, kind, profile);
        }

        // 5. Watch-only from xpub
        if (doc.AccountXpub is null)
            throw new InvalidDataException("Wallet file has no xpub and no seed.");
        if (!Slip132.TryDecodePublic(doc.AccountXpub, profile, out var xpub, out _))
            throw new InvalidDataException("Xpub in wallet file is invalid for this network.");
        var accountPath = doc.AccountPath is { Length: > 0 } ap ? KeyPath.Parse(ap) : null;
        return HdAccount.FromAccountXpub(xpub!, kind, profile, accountPath);
    }

    /// <summary>Creates the document for a new seed wallet.</summary>
    public static (WalletDocument Doc, HdAccount Account) NewFromMnemonic(
        string words, string? passphrase, ScriptKind kind, ChainProfile profile, KeyPath? customPath = null)
    {
        if (!Bip39.TryParse(words, out var mnemonic))
            throw new InvalidDataException("Invalid mnemonic (wrong words or checksum).");

        var account = customPath is null
            ? HdAccount.FromMnemonic(mnemonic!, passphrase, kind, profile)
            : HdAccount.FromSeed(Bip39.ToSeed(mnemonic!, passphrase), kind, profile, customPath);

        var doc = new WalletDocument
        {
            Network = profile.NetName,
            ScriptKind = kind.ToString(),
            Mnemonic = words.Trim(),
            Passphrase = passphrase,
            AccountPath = account.AccountPath.ToString(),
            AccountXpub = account.ToSlip132(),
            MasterFingerprint = Convert.ToHexString(account.MasterFingerprint.ToBytes()).ToLowerInvariant(),
        };
        return (doc, account);
    }

    /// <summary>
    /// Creates the document from an imported SLIP-132 xpub (watch-only).
    /// Detects the ScriptKind from the SLIP-132 header; <paramref name="kindOverride"/> overrides
    /// it for ambiguous prefixes (xpub can be Legacy or Taproot).
    /// </summary>
    public static (WalletDocument Doc, HdAccount Account) NewFromXpub(
        string slip132, ChainProfile profile, ScriptKind? kindOverride = null)
    {
        if (!Slip132.TryDecodePublic(slip132.Trim(), profile, out var xpub, out var detectedKind))
            throw new InvalidDataException("Extended public key is invalid or unrecognised for this network.");
        var kind = kindOverride ?? detectedKind;
        var account = HdAccount.FromAccountXpub(xpub!, kind, profile);

        var doc = new WalletDocument
        {
            Network = profile.NetName,
            ScriptKind = kind.ToString(),
            AccountPath = account.AccountPath.ToString(),
            AccountXpub = slip132.Trim(),
        };
        return (doc, account);
    }

    /// <summary>
    /// Creates the document from an imported SLIP-132 xprv (spendable without seed).
    /// The document contains the xprv in plaintext — it must be encrypted.
    /// </summary>
    public static (WalletDocument Doc, HdAccount Account) NewFromXprv(
        string slip132, ChainProfile profile, ScriptKind? kindOverride = null)
    {
        if (!Slip132.TryDecodePrivate(slip132.Trim(), profile, out var xprv, out var detectedKind))
            throw new InvalidDataException("Extended private key is invalid or unrecognised for this network.");
        var kind = kindOverride ?? detectedKind;
        var account = HdAccount.FromAccountXprv(xprv!, kind, profile);

        var doc = new WalletDocument
        {
            Network = profile.NetName,
            ScriptKind = kind.ToString(),
            AccountPath = account.AccountPath.ToString(),
            AccountXpub = account.ToSlip132(),
            AccountXprv = slip132.Trim(),
        };
        return (doc, account);
    }

    /// <summary>
    /// Creates the document from one or more imported WIF keys.
    /// The document contains the WIF keys in plaintext — it must be encrypted.
    /// </summary>
    public static (WalletDocument Doc, ImportedKeyAccount Account) NewFromWif(
        IReadOnlyList<string> wifKeys, ScriptKind kind, ChainProfile profile)
    {
        if (wifKeys.Count == 0)
            throw new InvalidDataException("At least one WIF key is required.");

        var network = PalladiumNetworks.For(profile.Kind);
        var entries = new List<(BitcoinAddress, Key?)>();
        var wifStrings = new List<string>();

        foreach (var raw in wifKeys)
        {
            BitcoinSecret secret;
            try
            {
                secret = new BitcoinSecret(raw.Trim(), network);
            }
            catch (Exception ex)
            {
                throw new InvalidDataException($"Invalid WIF key: {ex.Message}");
            }
            var addr = secret.PrivateKey.PubKey
                .GetAddress(DerivationPaths.ScriptPubKeyTypeFor(kind), network);
            if (entries.Any(e => e.Item1.ScriptPubKey == addr.ScriptPubKey))
                continue; // same key twice: tracked and stored once
            entries.Add((addr, secret.PrivateKey));
            wifStrings.Add(raw.Trim());
        }

        var account = new ImportedKeyAccount(entries, kind, profile);
        var doc = new WalletDocument
        {
            Network = profile.NetName,
            ScriptKind = kind.ToString(),
            WifKeys = wifStrings,
        };
        return (doc, account);
    }

    /// <summary>
    /// Creates the document from one or more plain addresses, with no private key at all
    /// (pure watch-only import — the account can never sign, unlike xpub- or WIF-based accounts
    /// which can be upgraded later by supplying the matching key). ScriptKind is informational
    /// only here (no derivation happens from a fixed address list) and is auto-detected from the
    /// first address unless <paramref name="kindOverride"/> is given.
    /// </summary>
    public static (WalletDocument Doc, ImportedKeyAccount Account) NewFromAddresses(
        IReadOnlyList<string> addresses, ChainProfile profile, ScriptKind? kindOverride = null)
    {
        if (addresses.Count == 0)
            throw new InvalidDataException("At least one address is required.");

        var network = PalladiumNetworks.For(profile.Kind);
        var entries = new List<(BitcoinAddress, Key?)>();
        var addressStrings = new List<string>();

        foreach (var raw in addresses)
        {
            BitcoinAddress addr;
            try
            {
                addr = BitcoinAddress.Create(raw.Trim(), network);
            }
            catch (Exception ex)
            {
                throw new InvalidDataException($"Invalid address: {ex.Message}");
            }
            if (entries.Any(e => e.Item1.ScriptPubKey == addr.ScriptPubKey))
                continue; // same address twice: tracked and stored once
            entries.Add((addr, null));
            addressStrings.Add(raw.Trim());
        }

        var kind = kindOverride ?? DerivationPaths.KindFor(entries[0].Item1);
        var account = new ImportedKeyAccount(entries, kind, profile);
        var doc = new WalletDocument
        {
            Network = profile.NetName,
            ScriptKind = kind.ToString(),
            WatchAddresses = addressStrings,
        };
        return (doc, account);
    }
}
