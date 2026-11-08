using NBitcoin;
using PalladiumWallet.Core.Chain;

namespace PalladiumWallet.Core.Crypto;

/// <summary>
/// Account built from individually imported WIF keys (blueprint §4.4 — "Imported"):
/// fixed list of addresses, no HD derivation, no change chain.
/// Change always goes back to the first imported address.
/// </summary>
public sealed class ImportedKeyAccount : IWalletAccount
{
    private readonly (BitcoinAddress Address, Key? PrivateKey)[] _entries;

    public ScriptKind Kind { get; }
    public ChainProfile Profile { get; }
    public bool IsWatchOnly => _entries.All(e => e.PrivateKey is null);

    public IReadOnlyList<(BitcoinAddress Address, bool IsChange, int Index)>? FixedAddresses =>
        _entries.Select((e, i) => (e.Address, false, i)).ToList();

    public ImportedKeyAccount(
        IReadOnlyList<(BitcoinAddress Address, Key? PrivateKey)> entries,
        ScriptKind kind, ChainProfile profile)
    {
        if (entries.Count == 0)
            throw new ArgumentException("At least one address is required.", nameof(entries));
        // One entry per address: the same key or address imported twice would otherwise be tracked
        // twice (duplicate rows, and a scriptPubKey collision in the synchroniser). When one copy
        // has the private key and the other does not, the spendable one is kept.
        var unique = new List<(BitcoinAddress Address, Key? PrivateKey)>();
        var position = new Dictionary<Script, int>();
        foreach (var entry in entries)
        {
            if (position.TryGetValue(entry.Address.ScriptPubKey, out var at))
            {
                if (unique[at].PrivateKey is null && entry.PrivateKey is not null)
                    unique[at] = entry;
                continue;
            }
            position[entry.Address.ScriptPubKey] = unique.Count;
            unique.Add(entry);
        }
        _entries = [.. unique];
        Kind = kind;
        Profile = profile;
    }

    public BitcoinAddress GetAddress(bool isChange, int index)
    {
        if (isChange || index < 0 || index >= _entries.Length)
            return _entries[0].Address;
        return _entries[index].Address;
    }

    public BitcoinAddress GetReceiveAddress(int index) => GetAddress(false, index);

    /// <summary>Change always returns to the first imported address.</summary>
    public BitcoinAddress GetChangeAddress(int index) => _entries[0].Address;

    public PubKey? GetPublicKey(bool isChange, int index)
    {
        if (isChange || index < 0 || index >= _entries.Length)
            return null;
        return _entries[index].PrivateKey?.PubKey;
    }

    public Key? GetPrivateKey(bool isChange, int index)
    {
        if (isChange || index < 0 || index >= _entries.Length)
            return null;
        return _entries[index].PrivateKey;
    }
}
