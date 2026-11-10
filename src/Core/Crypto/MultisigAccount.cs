using NBitcoin;
using PalladiumWallet.Core.Chain;

namespace PalladiumWallet.Core.Crypto;

/// <summary>
/// M-of-N native segwit (P2WSH) multisig account. Addresses are <c>wsh(sortedmulti(M, key₁…keyₙ))</c>:
/// every cosigner contributes the key at <c>change/index</c> under its account xpub, the keys are sorted
/// (BIP67), so the address does not depend on the order in which the cosigners were entered, and the
/// script is wrapped in a witness-script-hash output. The wallet holds the private key of at most one cosigner;
/// a transaction needs M such signatures, collected as a PSBT that travels between the cosigners.
/// </summary>
public sealed class MultisigAccount : IWalletAccount
{
    /// <summary>BIP45/48 limit: a standard multisig script holds at most 15 keys.</summary>
    public const int MaxCosigners = 15;

    private readonly ExtKey? _ownAccountXprv;

    public ScriptKind Kind => ScriptKind.NativeSegwitMultisig;
    public ChainProfile Profile { get; }

    /// <summary>Signatures required to spend.</summary>
    public int Threshold { get; }

    public IReadOnlyList<Cosigner> Cosigners { get; }

    /// <summary>Index in <see cref="Cosigners"/> of the key this wallet can sign with; null when it holds none.</summary>
    public int? OwnCosignerIndex { get; }

    public bool IsWatchOnly => _ownAccountXprv is null;

    /// <summary>A transaction of this wallet is never complete on its own: it leaves as a PSBT.</summary>
    public bool IsMultiSignature => true;

    public IReadOnlyList<(BitcoinAddress Address, bool IsChange, int Index)>? FixedAddresses => null;

    /// <param name="threshold">M, between 1 and the number of cosigners.</param>
    /// <param name="cosigners">All N keys, the wallet's own included.</param>
    /// <param name="ownAccountXprv">The account key of one cosigner (spendable), or null for a watch-only wallet.</param>
    /// <exception cref="ArgumentException">Inconsistent parameters; the message says which.</exception>
    public MultisigAccount(int threshold, IReadOnlyList<Cosigner> cosigners, ExtKey? ownAccountXprv, ChainProfile profile)
    {
        if (cosigners.Count < 2 || cosigners.Count > MaxCosigners)
            throw new ArgumentException($"A multisig wallet needs between 2 and {MaxCosigners} cosigners.", nameof(cosigners));
        if (threshold < 1 || threshold > cosigners.Count)
            throw new ArgumentException($"The threshold must be between 1 and {cosigners.Count}.", nameof(threshold));
        // The same key twice would let one person hold two of the required signatures.
        if (cosigners.Select(c => c.Xpub.ToBytes().ToHexString()).Distinct().Count() != cosigners.Count)
            throw new ArgumentException("The same cosigner key appears more than once.", nameof(cosigners));

        Profile = profile;
        Threshold = threshold;
        Cosigners = cosigners;
        if (ownAccountXprv is not null)
        {
            var own = ownAccountXprv.Neuter().ToBytes().ToHexString();
            var index = cosigners.ToList().FindIndex(c => c.Xpub.ToBytes().ToHexString() == own);
            if (index < 0)
                throw new ArgumentException("The private key does not belong to any of the cosigners.", nameof(ownAccountXprv));
            OwnCosignerIndex = index;
            _ownAccountXprv = ownAccountXprv;
        }
    }

    /// <summary>The N public keys of an address, sorted as they appear in the script (BIP67).</summary>
    public IReadOnlyList<PubKey> GetSortedPubKeys(bool isChange, int index)
    {
        var sub = DerivationPaths.AddressSubPath(isChange, index);
        return [.. Cosigners.Select(c => c.Xpub.Derive(sub).PubKey)
            .OrderBy(k => k.ToBytes(), ByteArrayComparer.Instance)];
    }

    /// <summary><c>OP_M key₁ … keyₙ OP_N OP_CHECKMULTISIG</c> of an address.</summary>
    public Script GetWitnessScript(bool isChange, int index) =>
        PayToMultiSigTemplate.Instance.GenerateScriptPubKey(Threshold, [.. GetSortedPubKeys(isChange, index)]);

    public BitcoinAddress GetAddress(bool isChange, int index) =>
        GetWitnessScript(isChange, index).WitHash.GetAddress(PalladiumNetworks.For(Profile.Kind));

    public BitcoinAddress GetReceiveAddress(int index) => GetAddress(false, index);
    public BitcoinAddress GetChangeAddress(int index) => GetAddress(true, index);

    /// <summary>No single public key stands for a multisig address.</summary>
    public PubKey? GetPublicKey(bool isChange, int index) => null;

    /// <summary>This wallet's own key for an address; null for a watch-only wallet.</summary>
    public Key? GetPrivateKey(bool isChange, int index) =>
        _ownAccountXprv?.Derive(DerivationPaths.AddressSubPath(isChange, index)).PrivateKey;

    /// <summary>The origin of every key of an address, to put in a PSBT: (public key, rooted path).</summary>
    public IEnumerable<(PubKey Key, RootedKeyPath Origin)> GetKeyOrigins(bool isChange, int index)
    {
        var sub = DerivationPaths.AddressSubPath(isChange, index);
        foreach (var c in Cosigners)
            yield return (c.Xpub.Derive(sub).PubKey, new RootedKeyPath(c.Fingerprint, c.AccountPath.Derive(sub)));
    }

    /// <summary>
    /// The output descriptor of the wallet, <c>wsh(sortedmulti(M,[fp/path]xpub/0/*,…))</c> with the chain's standard
    /// extended-key header, for the receive chain (<paramref name="isChange"/> false) or the change chain.
    /// Importable as a watch-only descriptor by Bitcoin-Core-derived nodes (which also add the checksum).
    /// </summary>
    public string ToDescriptor(bool isChange = false)
    {
        var network = PalladiumNetworks.For(Profile.Kind);
        // Canonical key order (by serialised key), so that the descriptor, and the wallet ID drawn from it, do not
        // depend on the order in which each cosigner happened to enter the keys. The addresses never did.
        var keys = string.Join(",", Cosigners.OrderBy(c => Convert.ToHexString(c.Xpub.ToBytes()), StringComparer.Ordinal)
            .Select(c => $"{c.ToDescriptorKey(network)}/{(isChange ? 1 : 0)}/*"));
        return $"wsh(sortedmulti({Threshold},{keys}))";
    }

    /// <summary>
    /// The wallet ID: the BIP380 checksum of the receive descriptor, 8 characters, equal to the one Bitcoin Core
    /// prints for the same descriptor. It depends on every cosigner key and on the threshold (not on the order the keys were
    /// entered in), so two cosigners who read the same ID have built the same wallet. It is the check that the cosigner keys
    /// were not altered on their way: the wallet cannot authenticate a key by itself, the people compare this code
    /// over a channel the attacker does not control.
    /// </summary>
    public string WalletId => DescriptorChecksum.Compute(ToDescriptor())!;

    private sealed class ByteArrayComparer : IComparer<byte[]>
    {
        public static readonly ByteArrayComparer Instance = new();
        public int Compare(byte[]? x, byte[]? y) => x.AsSpan().SequenceCompareTo(y.AsSpan());
    }
}

file static class HexExtensions
{
    public static string ToHexString(this byte[] bytes) => Convert.ToHexString(bytes);
}
