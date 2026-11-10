using System.Text.RegularExpressions;
using NBitcoin;
using PalladiumWallet.Core.Chain;

namespace PalladiumWallet.Core.Crypto;

/// <summary>
/// One key of a multisig wallet: the account-level extended public key of a cosigner together with
/// its origin (master fingerprint and account path), so that a PSBT can tell each signer which key is its own.
/// Written and read as <c>[fingerprint/48'/746'/0'/2']Zpub…</c>, the key-origin form of output descriptors.
/// </summary>
public sealed record Cosigner(HDFingerprint Fingerprint, KeyPath AccountPath, ExtPubKey Xpub)
{
    // [8 hex digits, then any number of /index with an optional ' or h], then the extended key.
    private static readonly Regex Origin = new(
        @"^\[(?<fp>[0-9a-fA-F]{8})(?<path>(?:/[0-9]{1,10}['hH]?)*)\](?<key>[1-9A-HJ-NP-Za-km-z]{100,120})$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Parses <c>[fingerprint/path]xpub</c>. The extended key may use any header of the profile
    /// (xpub/tpub as in descriptors, or the SLIP-132 Zpub/Vpub variants). Never throws: false for any input
    /// that is not a well-formed key of this network whose origin is consistent with the key itself
    /// (the depth of the key is the length of the path, and its child number is the last path element).
    /// </summary>
    public static bool TryParse(string? text, ChainProfile profile, out Cosigner? cosigner)
    {
        cosigner = null;
        if (text is null)
            return false;
        var match = Origin.Match(text.Trim());
        if (!match.Success)
            return false;
        if (!Slip132.TryDecodePublic(match.Groups["key"].Value, profile, out var xpub, out _))
            return false;

        KeyPath path;
        try
        {
            var raw = match.Groups["path"].Value.Replace('h', '\'').Replace('H', '\'').TrimStart('/');
            path = raw.Length == 0 ? KeyPath.Empty : KeyPath.Parse(raw);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or OverflowException)
        {
            return false;
        }

        var fingerprint = HdAccount.ParseFingerprint(match.Groups["fp"].Value.ToLowerInvariant());
        if (fingerprint is null)
            return false;
        if (xpub!.Depth != path.Length || (path.Length > 0 && xpub.Child != path[^1]))
            return false;

        cosigner = new Cosigner(fingerprint.Value, path, xpub);
        return true;
    }

    /// <summary>The key-origin text of this cosigner, in the SLIP-132 form of <paramref name="kind"/> (Zpub/Vpub for P2WSH).</summary>
    public string ToText(ScriptKind kind, ChainProfile profile) =>
        $"[{Convert.ToHexString(Fingerprint.ToBytes()).ToLowerInvariant()}/{AccountPath}]{Slip132.Encode(Xpub, kind, profile)}";

    /// <summary>The same key in the standard xpub/tpub form, as an output descriptor writes it.</summary>
    public string ToDescriptorKey(Network network) =>
        $"[{Convert.ToHexString(Fingerprint.ToBytes()).ToLowerInvariant()}/{AccountPath}]{Xpub.GetWif(network)}";
}
