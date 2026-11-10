using NBitcoin;
using PalladiumWallet.Core.Chain;
using PalladiumWallet.Core.Crypto;

namespace PalladiumWallet.Core.Wallet;

/// <summary>An input of a PSBT as the signer sees it.</summary>
/// <param name="AmountVerified">True when the amount comes from the full previous transaction, whose hash matches the outpoint.</param>
public sealed record PsbtInputInfo(string PrevTxid, uint PrevIndex, long? AmountSats, bool AmountVerified, bool Signed);

/// <summary>An output of a PSBT; <paramref name="IsMine"/> marks the wallet's own addresses (change).</summary>
public sealed record PsbtOutputInfo(string? Address, long AmountSats, bool IsMine);

/// <summary>Outcome of <see cref="PsbtSigner.Sign"/>.</summary>
public sealed class PsbtSignResult
{
    public required PSBT Psbt { get; init; }
    public required IReadOnlyList<PsbtInputInfo> Inputs { get; init; }
    public required IReadOnlyList<PsbtOutputInfo> Outputs { get; init; }

    /// <summary>Sum of inputs minus outputs; null when an input amount is unknown.</summary>
    public long? FeeSats { get; init; }

    /// <summary>Every input is signed and the PSBT could be finalised.</summary>
    public bool Complete => FinalTransaction is not null;

    /// <summary>The broadcastable transaction when <see cref="Complete"/>.</summary>
    public Transaction? FinalTransaction { get; init; }
}

/// <summary>
/// Signs a PSBT with the keys of a spendable account (the offline half of the watch-only flow,
/// blueprint §6.5). The PSBT comes from outside, so it is checked before anything is signed:
/// only SIGHASH_ALL, and any full previous transaction must match its outpoint.
/// </summary>
public static class PsbtSigner
{
    /// <summary>Addresses per chain tried when matching keys to inputs (receive and change).</summary>
    public const int DefaultScan = 100;

    /// <exception cref="WalletSpendException">Watch-only account, unsupported sighash, or inconsistent previous transaction.</exception>
    public static PsbtSignResult Sign(PSBT psbt, IWalletAccount account, int scan = DefaultScan)
    {
        if (account.IsWatchOnly)
            throw new WalletSpendException("This wallet is watch-only: it cannot sign.");
        var network = PalladiumNetworks.For(account.Profile.Kind);

        for (var i = 0; i < psbt.Inputs.Count; i++)
        {
            var input = psbt.Inputs[i];
            if (input.SighashType is { } sighash && sighash != SigHash.All)
                throw new WalletSpendException(
                    $"Input {i} asks for sighash {sighash}: refusing to sign (only SIGHASH_ALL is accepted).");
            if (input.NonWitnessUtxo is { } previous && previous.GetHash() != input.PrevOut.Hash)
                throw new WalletSpendException(
                    $"Input {i}: the attached previous transaction does not match the outpoint it spends.");
        }

        // What the PSBT claims, read before signing (signing may drop the supporting data).
        var claimed = psbt.Inputs.Select(i => (i.PrevOut, Amount: i.GetTxOut()?.Value.Satoshi,
            Verified: i.NonWitnessUtxo is not null)).ToList();

        var (keys, mine) = KeysAndScripts(account, scan);
        if (account is MultisigAccount multisig)
            AddWitnessScripts(psbt, multisig, scan);
        psbt.SignWithKeys([.. keys]);

        var inputs = new List<PsbtInputInfo>();
        long inputSum = 0;
        var allAmountsKnown = true;
        for (var i = 0; i < psbt.Inputs.Count; i++)
        {
            var input = psbt.Inputs[i];
            var (prevOut, amount, verified) = claimed[i];
            if (amount is null) allAmountsKnown = false; else inputSum += amount.Value;
            inputs.Add(new PsbtInputInfo(
                prevOut.Hash.ToString(), prevOut.N, amount, verified,
                Signed: input.IsFinalized() || input.PartialSigs.Count > 0 || input.TaprootKeySignature is not null));
        }

        var global = psbt.GetGlobalTransaction();
        var outputs = global.Outputs.Select(o => new PsbtOutputInfo(
            o.ScriptPubKey.GetDestinationAddress(network)?.ToString(), o.Value.Satoshi, mine.Contains(o.ScriptPubKey))).ToList();
        long? fee = allAmountsKnown ? inputSum - outputs.Sum(o => o.AmountSats) : null;

        Transaction? final = null;
        if (psbt.TryFinalize(out _))
            final = psbt.ExtractTransaction();

        return new PsbtSignResult { Psbt = psbt, Inputs = inputs, Outputs = outputs, FeeSats = fee, FinalTransaction = final };
    }

    /// <summary>
    /// A PSBT from another creator may not carry the witness script of a multisig input. The wallet knows it
    /// for each of its own addresses, so it fills in the ones whose output script it recognises.
    /// </summary>
    private static void AddWitnessScripts(PSBT psbt, MultisigAccount account, int scan)
    {
        var scripts = new Dictionary<Script, Script>();
        foreach (var isChange in new[] { false, true })
            for (var index = 0; index < scan; index++)
                scripts[account.GetAddress(isChange, index).ScriptPubKey] = account.GetWitnessScript(isChange, index);
        for (var i = 0; i < psbt.Inputs.Count; i++)
        {
            var input = psbt.Inputs[i];
            var spk = input.GetTxOut()?.ScriptPubKey;
            if (spk is null || !scripts.TryGetValue(spk, out var witnessScript))
                // Signing an input of some other wallet would lend this wallet's signature to a spend it cannot
                // check: the PSBT's own witness script is attacker-controlled, and says nothing about this wallet.
                throw new WalletSpendException(
                    $"Input {i} does not spend an address of this multisig wallet (or its amount is unknown): refusing to sign.");
            input.WitnessScript ??= witnessScript;
            if (input.WitnessScript != witnessScript)
                throw new WalletSpendException($"Input {i} carries a witness script that is not this wallet's own: refusing to sign.");
        }
    }

    /// <summary>
    /// Merges the signatures of several PSBTs of the same transaction (the cosigners' copies) into one, and
    /// finalises it when it holds enough of them.
    /// </summary>
    /// <exception cref="WalletSpendException">The PSBTs are not of the same transaction.</exception>
    public static (PSBT Combined, Transaction? Final) Combine(IReadOnlyList<PSBT> psbts)
    {
        if (psbts.Count == 0)
            throw new WalletSpendException("Nothing to combine.");
        var combined = psbts[0].Clone();
        foreach (var other in psbts.Skip(1))
        {
            if (other.GetGlobalTransaction().GetHash() != combined.GetGlobalTransaction().GetHash())
                throw new WalletSpendException(
                    "These PSBTs are not of the same transaction: refusing to combine them.");
            combined.Combine(other);
        }
        // Finalising a clone keeps the combined PSBT itself open, so it can still be passed on to a cosigner.
        var attempt = combined.Clone();
        Transaction? final = attempt.TryFinalize(out _) ? attempt.ExtractTransaction() : null;
        return (combined, final);
    }

    private static (List<Key> Keys, HashSet<Script> Mine) KeysAndScripts(IWalletAccount account, int scan)
    {
        var keys = new List<Key>();
        var mine = new HashSet<Script>();
        if (account.FixedAddresses is { } fixedAddresses)
        {
            foreach (var (address, isChange, index) in fixedAddresses)
            {
                mine.Add(address.ScriptPubKey);
                if (account.GetPrivateKey(isChange, index) is { } key)
                    keys.Add(key);
            }
            return (keys, mine);
        }
        foreach (var isChange in new[] { false, true })
            for (var index = 0; index < scan; index++)
            {
                mine.Add(account.GetAddress(isChange, index).ScriptPubKey);
                if (account.GetPrivateKey(isChange, index) is { } key)
                    keys.Add(key);
            }
        return (keys, mine);
    }
}
