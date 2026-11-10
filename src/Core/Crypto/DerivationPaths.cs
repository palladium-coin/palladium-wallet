using NBitcoin;
using PalladiumWallet.Core.Chain;

namespace PalladiumWallet.Core.Crypto;

/// <summary>
/// BIP44/49/84 derivation path construction (blueprint §4.2) and mapping of
/// ScriptKind → purpose / ScriptPubKeyType. Purposes are BIP protocol constants
/// (chain-independent); coin_type always comes from the ChainProfile.
/// </summary>
public static class DerivationPaths
{
    /// <summary>BIP44 purpose (P2PKH legacy).</summary>
    public const int PurposeLegacy = 44;

    /// <summary>BIP49 purpose (P2SH-P2WPKH).</summary>
    public const int PurposeWrappedSegwit = 49;

    /// <summary>BIP84 purpose (native P2WPKH).</summary>
    public const int PurposeNativeSegwit = 84;

    /// <summary>BIP48 purpose (multisig — next phase, §16 step 8).</summary>
    public const int PurposeMultisig = 48;

    /// <summary>BIP86 purpose (P2TR key-path, Taproot).</summary>
    public const int PurposeTaproot = 86;

    public static int PurposeFor(ScriptKind kind) => kind switch
    {
        ScriptKind.Legacy => PurposeLegacy,
        ScriptKind.WrappedSegwit => PurposeWrappedSegwit,
        ScriptKind.NativeSegwit => PurposeNativeSegwit,
        ScriptKind.WrappedSegwitMultisig or ScriptKind.NativeSegwitMultisig => PurposeMultisig,
        ScriptKind.Taproot => PurposeTaproot,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>
    /// NBitcoin scriptPubKey type for single-pubkey derivation.
    /// Multisig types are derived from a redeem script, not a pubkey — they will
    /// arrive with M-of-N wallet support (§4.5).
    /// </summary>
    public static ScriptPubKeyType ScriptPubKeyTypeFor(ScriptKind kind) => kind switch
    {
        ScriptKind.Legacy => ScriptPubKeyType.Legacy,
        ScriptKind.WrappedSegwit => ScriptPubKeyType.SegwitP2SH,
        ScriptKind.NativeSegwit => ScriptPubKeyType.Segwit,
        ScriptKind.Taproot => ScriptPubKeyType.TaprootBIP86,
        ScriptKind.WrappedSegwitMultisig or ScriptKind.NativeSegwitMultisig =>
            throw new NotSupportedException(
                "Multisig types are derived from a redeem script: support planned with M-of-N wallets (§4.5)."),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>
    /// Best-effort reverse mapping from an already-known address to a ScriptKind, used to
    /// label pure address imports (no derivation involved, so this is informational only).
    /// </summary>
    public static ScriptKind KindFor(BitcoinAddress address) => address switch
    {
        BitcoinWitPubKeyAddress => ScriptKind.NativeSegwit,
        TaprootAddress => ScriptKind.Taproot,
        BitcoinWitScriptAddress => ScriptKind.NativeSegwitMultisig,
        BitcoinScriptAddress => ScriptKind.WrappedSegwit,
        _ => ScriptKind.Legacy,
    };

    /// <summary>
    /// Account path relative to the root: purpose'/coin'/account' (§4.2).
    /// coin_type is taken from the profile (746 mainnet, 1 testnet).
    /// </summary>
    public static KeyPath AccountPath(ScriptKind kind, ChainProfile profile, int account = 0)
    {
        if (account < 0)
            throw new ArgumentOutOfRangeException(nameof(account));
        // BIP48 adds a fourth level for the script type: 1' P2SH-P2WSH, 2' P2WSH.
        var scriptType = kind switch
        {
            ScriptKind.NativeSegwitMultisig => "/2'",
            ScriptKind.WrappedSegwitMultisig => "/1'",
            _ => "",
        };
        return new KeyPath(
            $"{PurposeFor(kind)}'/{profile.Bip44CoinType}'/{account}'{scriptType}");
    }

    /// <summary>Non-hardened change/index sub-path under the account (change=0 receiving, change=1 change).</summary>
    public static KeyPath AddressSubPath(bool isChange, int index)
    {
        if (index < 0)
            throw new ArgumentOutOfRangeException(nameof(index));
        return new KeyPath($"{(isChange ? 1 : 0)}/{index}");
    }

    /// <summary>
    /// Parsing of custom derivation paths on import (Sparrow-like, §4.2):
    /// accepts the optional "m/" prefix and hardened markers ' or h.
    /// </summary>
    public static bool TryParse(string path, out KeyPath? keyPath)
    {
        keyPath = null;
        if (string.IsNullOrWhiteSpace(path))
            return false;
        try
        {
            keyPath = KeyPath.Parse(path.Trim());
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
