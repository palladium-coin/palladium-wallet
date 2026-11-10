using System.Text;
using System.Text.Json;
using NBitcoin;
using PalladiumWallet.Core.Chain;
using PalladiumWallet.Core.Crypto;
using PalladiumWallet.Core.Net;
using PalladiumWallet.Core.Spv;
using PalladiumWallet.Core.Storage;
using PalladiumWallet.Core.Wallet;

namespace PalladiumWallet.Fuzz;

/// <summary>
/// Fuzz targets over the parsers that consume untrusted input (server responses,
/// wallet files, user-pasted keys/addresses/amounts). Each target enforces the
/// parser's error contract: the exception types documented as its failure mode
/// are swallowed, anything else escapes and is reported as a crash by the fuzzer.
/// </summary>
public static class FuzzTargets
{
    public static readonly IReadOnlyDictionary<string, Action<byte[]>> All =
        new Dictionary<string, Action<byte[]>>
        {
            ["header"] = Header,
            ["merkle"] = Merkle,
            ["slip132"] = Slip132Keys,
            ["bip39"] = Bip39Mnemonic,
            ["address"] = Address,
            ["coinamount"] = CoinAmountParse,
            ["walletdoc"] = WalletDoc,
            ["encfile"] = EncFile,
            ["peers"] = Peers,
            ["headerrange"] = HeaderRangeReply,
            ["cosignerkey"] = CosignerKey,
            ["psbt"] = PsbtInput,
            ["multisigconfig"] = MultisigConfigFile,
        };

    /// <summary>Runs one input through a named target (used by replay and the regression test).</summary>
    public static void Run(string target, byte[] data) => All[target](data);

    private static string Utf8(byte[] data) => Encoding.UTF8.GetString(data);

    // 80-byte block headers arrive from the server both as raw bytes and as hex.
    // Contract: exactly 80 bytes never throws; any other length is ArgumentException;
    // the hex overload may also reject non-hex text with FormatException.
    private static void Header(byte[] data)
    {
        try { BlockHeaderInfo.Parse(data); }
        catch (ArgumentException) when (data.Length != BlockHeaderInfo.Size) { }

        try { BlockHeaderInfo.Parse(Utf8(data)); }
        catch (FormatException) { }
        catch (ArgumentException) { }
    }

    // Merkle proof verification runs on server-supplied txid/pos/branch.
    // Contract: never throws, for any position (including negative) and branch.
    private static void Merkle(byte[] data)
    {
        if (data.Length < 68)
            return;
        var txid = new uint256(data.AsSpan(0, 32));
        var position = BitConverter.ToInt32(data, 32);
        var root = new uint256(data.AsSpan(36, 32));
        var branch = new List<uint256>();
        for (var offset = 68; offset + 32 <= data.Length; offset += 32)
            branch.Add(new uint256(data.AsSpan(offset, 32)));

        MerkleProof.Verify(txid, position, branch, root);
    }

    // SLIP-132 extended keys are pasted by the user on import.
    // Contract: the Try* pair never throws, on any profile.
    private static void Slip132Keys(byte[] data)
    {
        var text = Utf8(data);
        foreach (var profile in new[] { ChainProfiles.Mainnet, ChainProfiles.Testnet, ChainProfiles.Regtest })
        {
            Slip132.TryDecodePublic(text, profile, out _, out _);
            Slip132.TryDecodePrivate(text, profile, out _, out _);
        }
    }

    // Mnemonics are pasted by the user on restore. Contract: TryParse never throws,
    // with auto-detected and explicit wordlist language alike.
    private static void Bip39Mnemonic(byte[] data)
    {
        var text = Utf8(data);
        Bip39.TryParse(text, out _);
        if (data.Length > 0)
            Bip39.TryParse(text, out _, (MnemonicLanguage)(data[0] % 8));
    }

    // Recipient addresses are pasted by the user (or scanned from a QR).
    // Contract: NBitcoin rejects anything invalid with FormatException.
    private static void Address(byte[] data)
    {
        try { BitcoinAddress.Create(Utf8(data), PalladiumNetworks.Mainnet); }
        catch (FormatException) { }
    }

    // Amounts are typed by the user in the currently selected unit.
    // Contract: TryParse* never throws for any of the official units.
    private static void CoinAmountParse(byte[] data)
    {
        var text = Utf8(data);
        foreach (var unit in CoinAmount.Units)
            CoinAmount.TryParseIn(text, unit, out _);
        CoinAmount.TryParseCoins(text, out _);
    }

    // The plaintext wallet document is read from disk at every open.
    // Contract: malformed JSON or schema is JsonException/InvalidDataException.
    private static void WalletDoc(byte[] data)
    {
        try { WalletDocument.FromJson(Utf8(data)); }
        catch (JsonException) { }
        catch (InvalidDataException) { }
    }

    // The encrypted container wraps the document on disk; a tampered file must
    // fail with the two typed exceptions, never a raw parser error, and
    // IsEncrypted (the format sniffer) must never throw at all.
    private static void EncFile(byte[] data)
    {
        var text = Utf8(data);
        EncryptedFile.IsEncrypted(text);
        try { EncryptedFile.Decrypt(text, "fuzz-password"); }
        catch (WrongPasswordException) { }
        catch (InvalidDataException) { }
    }

    // server.peers.subscribe responses are attacker-controlled JSON.
    // Contract: any well-formed JSON of any shape parses to a (possibly empty)
    // peer list without throwing.
    private static void Peers(byte[] data)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(data); }
        catch (JsonException) { return; }
        using (doc)
            ElectrumApi.ParsePeers(doc.RootElement);
    }

    // blockchain.block.headers replies are attacker-controlled: a count and a hex string.
    // Input layout: int32 count, int32 requested, then the hex text. Contract: HeaderRange.Split returns
    // exactly `count` headers of 160 characters within the request, or throws SpvVerificationException.
    private static void HeaderRangeReply(byte[] data)
    {
        if (data.Length < 8)
            return;
        var count = BitConverter.ToInt32(data, 0);
        var requested = Math.Abs(BitConverter.ToInt32(data, 4) % 2001);
        var hex = Utf8(data[8..]);
        try
        {
            var headers = HeaderRange.Split(new HeaderRangeResponse(count, hex), requested, 0);
            if (headers.Length != count || count < 1 || count > requested || headers.Any(h => h.Length != BlockHeaderInfo.Size * 2))
                throw new InvalidOperationException("HeaderRange.Split returned a result outside its contract.");
        }
        catch (SpvVerificationException) { }
    }

    // A cosigner key ([fingerprint/path]xpub) is pasted from another person and never trusted.
    // Contract: TryParse never throws, for any text, on any network; a result it accepts is consistent
    // (the origin's depth is the key's depth) and survives a round trip through its text form.
    private static void CosignerKey(byte[] data)
    {
        var text = Utf8(data);
        foreach (var profile in new[] { ChainProfiles.Mainnet, ChainProfiles.Regtest })
        {
            if (!Cosigner.TryParse(text, profile, out var cosigner))
                continue;
            if (cosigner!.Xpub.Depth != cosigner.AccountPath.Length)
                throw new InvalidOperationException("TryParse accepted a key whose origin contradicts its depth.");
            if (!Cosigner.TryParse(cosigner.ToText(ScriptKind.NativeSegwitMultisig, profile), profile, out var again)
                || !again!.Xpub.ToBytes().SequenceEqual(cosigner.Xpub.ToBytes()))
                throw new InvalidOperationException("An accepted cosigner key does not survive its own text form.");
        }
    }

    // A PSBT file or pasted text comes from another wallet or a signer and is never trusted.
    // Contract: PsbtFormat.Parse throws only FormatException, for any bytes; one it accepts survives its binary form.
    private static void PsbtInput(byte[] data)
    {
        PSBT psbt;
        try { psbt = PsbtFormat.Parse(data, PalladiumNetworks.Regtest); }
        catch (FormatException) { return; }
        PsbtFormat.Parse(PsbtFormat.ToBinary(psbt), PalladiumNetworks.Regtest);
    }

    // The multisig configuration file another tool exported.
    // Contract: Parse throws only InvalidDataException, for any text; what it accepts lists exactly the keys it counted,
    // each one a cosigner key of the network.
    private static void MultisigConfigFile(byte[] data)
    {
        try
        {
            var (threshold, _, cosigners) = MultisigConfig.Parse(Utf8(data), ChainProfiles.Regtest);
            if (cosigners.Count == 0 || threshold < 0)
                throw new InvalidOperationException("Parse accepted a configuration without keys.");
            foreach (var c in cosigners)
                if (!Cosigner.TryParse(c, ChainProfiles.Regtest, out _))
                    throw new InvalidOperationException("Parse accepted a key that TryParse refuses.");
        }
        catch (InvalidDataException) { }
    }
}
