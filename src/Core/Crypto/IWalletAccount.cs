using NBitcoin;
using PalladiumWallet.Core.Chain;

namespace PalladiumWallet.Core.Crypto;

/// <summary>
/// Abstraction over all wallet account types (HD from seed, HD from imported xpub/xprv,
/// imported WIF keys). Allows WalletSynchronizer and TransactionFactory to operate
/// independently of the underlying keystore type (blueprint §4.4–§4.5).
/// </summary>
public interface IWalletAccount
{
    ScriptKind Kind { get; }
    ChainProfile Profile { get; }

    /// <summary>True if the account cannot sign (no private keys present).</summary>
    bool IsWatchOnly { get; }

    /// <summary>
    /// True when one wallet holds only some of the keys an input needs (M-of-N multisig): what it builds is
    /// always a PSBT to pass on, never a transaction complete on its own, even when it can sign its share.
    /// </summary>
    bool IsMultiSignature => false;

    BitcoinAddress GetAddress(bool isChange, int index);
    BitcoinAddress GetReceiveAddress(int index);
    BitcoinAddress GetChangeAddress(int index);

    /// <summary>Null if the public key cannot be derived from the account (pure watch-only addresses).</summary>
    PubKey? GetPublicKey(bool isChange, int index);

    /// <summary>Null if the account is watch-only or the index is out of range.</summary>
    Key? GetPrivateKey(bool isChange, int index);

    /// <summary>
    /// For accounts with fixed addresses (imported WIF keys) returns the full list
    /// to scan; null for HD accounts that use the gap limit.
    /// </summary>
    IReadOnlyList<(BitcoinAddress Address, bool IsChange, int Index)>? FixedAddresses { get; }
}
