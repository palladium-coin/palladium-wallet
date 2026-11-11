using System.Text;
using NBitcoin;
using PalladiumWallet.Core.Chain;
using PalladiumWallet.Core.Crypto;

namespace PalladiumWallet.Core.Wallet;

/// <summary>
/// The multisig configuration as other tools exchange it: the text file Coldcard (and Sparrow, which exports the same
/// form) use to register a multisig wallet on a signer, and the output descriptors with their checksums.
/// A signer shows or checks the same data, so the configuration can be compared there.
/// </summary>
public static class MultisigConfig
{
    /// <summary>The Coldcard-style text: name, policy, script format, and one fingerprint/xpub line per cosigner under its derivation.</summary>
    public static string ToText(MultisigAccount account, string name)
    {
        var network = PalladiumNetworks.For(account.Profile.Kind);
        var sb = new StringBuilder();
        sb.Append("Name: ").Append(name).Append('\n');
        sb.Append("Policy: ").Append(account.Threshold).Append(" of ").Append(account.Cosigners.Count).Append('\n');
        sb.Append("Format: ").Append(account.IsWrapped ? "P2SH-P2WSH" : "P2WSH").Append('\n');
        // One block per distinct derivation, in cosigner order of first appearance.
        foreach (var group in account.Cosigners.GroupBy(c => c.AccountPath.ToString()))
        {
            sb.Append('\n').Append("Derivation: m/").Append(group.Key).Append('\n');
            foreach (var c in group)
                sb.Append(Convert.ToHexString(c.Fingerprint.ToBytes())).Append(": ").Append(c.Xpub.GetWif(network)).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>The receive and change descriptors, each with its BIP380 checksum.</summary>
    public static (string Receive, string Change) Descriptors(MultisigAccount account) =>
        (WithChecksum(account.ToDescriptor(false)), WithChecksum(account.ToDescriptor(true)));

    private static string WithChecksum(string descriptor) => $"{descriptor}#{DescriptorChecksum.Compute(descriptor)}";

    /// <summary>
    /// Reads a configuration text into what <see cref="WalletLoader.NewMultisig"/> takes. Lenient about case, blank lines,
    /// <c>#</c> comments and <c>h</c> or <c>'</c> for hardened; strict about everything that changes the wallet.
    /// </summary>
    /// <exception cref="InvalidDataException">The text is not a configuration of this network; the message says why.</exception>
    public static (int Threshold, ScriptKind Kind, IReadOnlyList<string> Cosigners) Parse(string text, ChainProfile profile)
    {
        int? threshold = null, count = null;
        ScriptKind? kind = null;
        string? derivation = null;
        var cosigners = new List<string>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            var colon = line.IndexOf(':');
            if (colon < 0)
                throw new InvalidDataException($"Unreadable line: '{Shorten(line)}'.");
            var key = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            switch (key.ToLowerInvariant())
            {
                case "name":
                    break;
                case "policy":
                {
                    var parts = value.Split(" of ", StringSplitOptions.TrimEntries);
                    if (parts.Length != 2 || !int.TryParse(parts[0], out var m) || !int.TryParse(parts[1], out var n))
                        throw new InvalidDataException($"Unreadable policy '{Shorten(value)}': expected 'M of N'.");
                    if (m < 1 || m > n)
                        throw new InvalidDataException($"The policy '{m} of {n}' is not possible: M must be between 1 and N.");
                    (threshold, count) = (m, n);
                    break;
                }
                case "format":
                    kind = value.ToUpperInvariant() switch
                    {
                        "P2WSH" => ScriptKind.NativeSegwitMultisig,
                        "P2SH-P2WSH" or "P2WSH-P2SH" => ScriptKind.WrappedSegwitMultisig,
                        _ => throw new InvalidDataException($"Unsupported format '{Shorten(value)}': P2WSH and P2SH-P2WSH are supported."),
                    };
                    break;
                case "derivation":
                    derivation = value.Replace('h', '\'').Replace('H', '\'').TrimStart('m', 'M').TrimStart('/');
                    break;
                default:
                {
                    if (derivation is null)
                        throw new InvalidDataException($"The key of {Shorten(key)} comes before any 'Derivation:' line.");
                    if (key.Length != 8 || !key.All(Uri.IsHexDigit))
                        throw new InvalidDataException($"Unknown field '{Shorten(key)}'.");
                    cosigners.Add($"[{key.ToLowerInvariant()}/{derivation}]{value}");
                    break;
                }
            }
        }

        if (threshold is null || kind is null)
            throw new InvalidDataException("The configuration needs a 'Policy:' and a 'Format:' line.");
        if (count != cosigners.Count)
            throw new InvalidDataException($"The policy says {count} keys, the file lists {cosigners.Count}.");
        foreach (var c in cosigners)
            if (!Cosigner.TryParse(c, profile, out _))
                throw new InvalidDataException($"'{Shorten(c)}' is not a cosigner key for {profile.NetName}.");
        return (threshold.Value, kind.Value, cosigners);
    }

    private static string Shorten(string s) => s.Length > 40 ? s[..40] + "…" : s;
}
