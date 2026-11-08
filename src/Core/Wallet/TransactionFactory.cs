using NBitcoin;
using NBitcoin.Policy;
using PalladiumWallet.Core.Chain;
using PalladiumWallet.Core.Crypto;
using PalladiumWallet.Core.Storage;

namespace PalladiumWallet.Core.Wallet;

/// <summary>Result of a transaction build operation.</summary>
public sealed class BuiltTransaction
{
    public required Transaction Transaction { get; init; }
    public required Money Fee { get; init; }
    public required FeeRate FeeRate { get; init; }
    public required bool Signed { get; init; }

    /// <summary>
    /// True when <see cref="Txid"/> is the txid the signed transaction will have. The txid covers the
    /// scriptSig, and a legacy or wrapped-segwit input gets its scriptSig only when signed, so a
    /// watch-only transaction of those kinds has a provisional txid. Native segwit (empty scriptSig,
    /// witness outside the txid) is final either way.
    /// </summary>
    public required bool TxidIsFinal { get; init; }

    /// <summary>PSBT for watch-only/air-gapped/multisig flows (§6.5).</summary>
    public required PSBT Psbt { get; init; }

    public string ToHex() => Transaction.ToHex();
    public string Txid => Transaction.GetHash().ToString();
}

/// <summary>
/// Transaction construction and signing (blueprint §6) on top of NBitcoin primitives:
/// coin selection (manual or automatic), fixed fee rate, send-all with fee subtracted,
/// change on the internal chain, RBF on by default.
/// With a watch-only account produces an unsigned PSBT (§6.5).
/// </summary>
public sealed class TransactionFactory(IWalletAccount account)
{
    private const int MaxStandardTransactionVirtualSize = 100_000;

    private Network Network => PalladiumNetworks.For(account.Profile.Kind);

    /// <summary>
    /// Builds (and signs if possible) a transaction.
    /// </summary>
    /// <param name="utxos">Selected UTXOs (coin control §6.2) or all spendable ones.</param>
    /// <param name="transactions">Source transactions for the UTXOs (txid → tx), from sync.</param>
    /// <param name="destination">Recipient address.</param>
    /// <param name="amountSats">Amount; ignored when <paramref name="sendAll"/> is true.</param>
    /// <param name="feeRateSatPerVByte">Fixed fee rate in sat/vByte (§6.4).</param>
    /// <param name="changeIndex">Index of the next change address (internal chain).</param>
    /// <param name="tipHeight">Current chain tip height, used to enforce confirmation thresholds.</param>
    /// <param name="sendAll">Send all: fee subtracted from the amount (§6.1).</param>
    public BuiltTransaction Build(
        IReadOnlyList<CachedUtxo> utxos,
        IReadOnlyDictionary<string, Transaction> transactions,
        BitcoinAddress destination,
        long amountSats,
        decimal feeRateSatPerVByte,
        int changeIndex,
        int tipHeight,
        bool sendAll = false)
    {
        // Checked here, not only in the UI: the watch-only path and the CLI go through this too.
        if (!sendAll)
        {
            if (amountSats <= 0)
                throw new WalletSpendException("Amount must be greater than zero.");
            var dust = DustLimitSats(destination.ScriptPubKey);
            if (amountSats < dust)
                throw new WalletSpendException($"Amount is below the dust limit ({dust} sat).");
        }

        var profile = account.Profile;
        var spendable = utxos.Where(u => u.IsSpendable(profile, tipHeight)).ToList();

        if (spendable.Count == 0)
        {
            var reasons = new System.Text.StringBuilder();

            var mempool = utxos.Where(u => !u.Frozen && u.Height <= 0).ToList();
            if (mempool.Count > 0)
                reasons.Append($"{mempool.Count} output(s) unconfirmed ({CoinAmount.Format(mempool.Sum(u => u.ValueSats))} in mempool). ");

            var immature = utxos.Where(u =>
                !u.Frozen && u.Height > 0 && u.IsCoinbase &&
                u.Confirmations(tipHeight) < u.RequiredConfirmations(profile)).ToList();
            if (immature.Count > 0)
            {
                var bestImmatureConf = immature.Max(u => u.Confirmations(tipHeight));
                var threshold = profile.CoinbaseMaturity + 1;
                reasons.Append($"{immature.Count} coinbase output(s) not yet mature ({bestImmatureConf}/{threshold} confirmations). ");
            }

            var underConf = utxos.Where(u =>
                !u.Frozen && u.Height > 0 && !u.IsCoinbase &&
                u.Confirmations(tipHeight) < u.RequiredConfirmations(profile)).ToList();
            if (underConf.Count > 0)
            {
                var bestUnderConf = underConf.Max(u => u.Confirmations(tipHeight));
                reasons.Append($"{underConf.Count} output(s) need {profile.MinConfirmations} confirmations ({bestUnderConf} so far). ");
            }

            var unverified = utxos.Where(u =>
                !u.Frozen && u.Height > 0 && !u.Verified &&
                u.Confirmations(tipHeight) >= u.RequiredConfirmations(profile)).ToList();
            if (unverified.Count > 0)
                reasons.Append($"{unverified.Count} output(s) confirmed but still awaiting Merkle-proof verification " +
                    $"({CoinAmount.Format(unverified.Sum(u => u.ValueSats))}). ");

            throw new WalletSpendException(reasons.Length > 0
                ? $"No spendable UTXOs: {reasons.ToString().TrimEnd()}"
                : "No spendable UTXOs selected.");
        }

        var ordered = spendable
            .OrderByDescending(u => u.ValueSats)
            .ThenBy(u => u.Height)
            .ThenBy(u => u.Txid, StringComparer.Ordinal)
            .ThenBy(u => u.Vout)
            .ToList();

        var feeRate = new FeeRate(Money.Satoshis(feeRateSatPerVByte * 1000m), 1000);

        if (sendAll)
        {
            try
            {
                return BuildWithSelectedUtxos(
                    ordered, transactions, destination, amountSats, feeRate, changeIndex, sendAll: true, totalSpendableCount: ordered.Count);
            }
            catch (TransactionTooLargeException ex)
            {
                throw new WalletSpendException(ex.Message);
            }
        }

        NotEnoughFundsException? lastInsufficientFunds = null;
        TransactionTooLargeException? tooLarge = null;
        BuiltTransaction? best = null;
        var low = 1;
        var high = ordered.Count;
        while (low <= high)
        {
            var count = low + ((high - low) / 2);
            try
            {
                best = BuildWithSelectedUtxos(
                    ordered.Take(count).ToList(), transactions, destination, amountSats, feeRate, changeIndex,
                    sendAll: false, totalSpendableCount: ordered.Count);
                high = count - 1;
            }
            catch (NotEnoughFundsException ex)
            {
                lastInsufficientFunds = ex;
                low = count + 1;
            }
            catch (TransactionTooLargeException ex)
            {
                tooLarge = ex;
                high = count - 1;
            }
        }

        if (best is not null)
            return best;

        if (tooLarge is not null)
            throw new WalletSpendException(tooLarge.Message);

        throw new WalletSpendException(lastInsufficientFunds is null
            ? "Insufficient funds."
            : $"Insufficient funds: {lastInsufficientFunds.Message}");
    }

    private BuiltTransaction BuildWithSelectedUtxos(
        IReadOnlyList<CachedUtxo> selectedUtxos,
        IReadOnlyDictionary<string, Transaction> transactions,
        BitcoinAddress destination,
        long amountSats,
        FeeRate feeRate,
        int changeIndex,
        bool sendAll,
        int totalSpendableCount)
    {
        var coins = selectedUtxos.Select(u => CoinFor(u, transactions)).ToList();

        var builder = Network.CreateTransactionBuilder();
        builder.SetVersion(2);
        // RBF sequence to allow fee bumping (§6.6).
        builder.OptInRBF = true;
        builder.AddCoins(coins);
        builder.SetChange(account.GetChangeAddress(changeIndex));
        builder.SendEstimatedFees(feeRate);

        if (sendAll)
            builder.Send(destination, coins.Sum(c => (Money)c.Amount)).SubtractFees();
        else
            builder.Send(destination, Money.Satoshis(amountSats));

        var signingKeys = account.IsWatchOnly
            ? []
            : selectedUtxos.Select(u => account.GetPrivateKey(u.IsChange, u.AddressIndex)).OfType<Key>().ToArray();
        if (signingKeys.Length > 0)
            builder.AddKeys(signingKeys);

        Transaction tx;
        try
        {
            tx = builder.BuildTransaction(sign: !account.IsWatchOnly);
        }
        catch (NotEnoughFundsException ex) when (ex.Message.Contains("size would be too high", StringComparison.OrdinalIgnoreCase))
        {
            // NBitcoin's coin selector refuses to assemble a combination over the standard size
            // cap itself and reports it through NotEnoughFundsException rather than ever handing
            // back an oversized transaction — the GetVirtualSize() check below is unreachable for
            // this case and exists only as a defense-in-depth net for other NBitcoin versions.
            throw new TransactionTooLargeException(
                BuildTooLargeMessage(selectedUtxos.Count, totalSpendableCount, sendAll));
        }
        catch (NotEnoughFundsException) when (!sendAll)
        {
            throw;
        }
        catch (NotEnoughFundsException ex)
        {
            throw new WalletSpendException($"Insufficient funds: {ex.Message}");
        }

        if (tx.GetVirtualSize() > MaxStandardTransactionVirtualSize)
            throw new TransactionTooLargeException(BuildTooLargeMessage(selectedUtxos.Count, totalSpendableCount, sendAll, tx.GetVirtualSize()));

        // Send-all computes the amount inside the builder, so its dust check runs on the result.
        if (sendAll && tx.Outputs.FirstOrDefault(o => o.ScriptPubKey == destination.ScriptPubKey) is { } paid
            && paid.Value.Satoshi < DustLimitSats(destination.ScriptPubKey))
            throw new WalletSpendException(
                $"Send-all leaves {paid.Value.Satoshi} sat for the recipient, below the dust limit ({DustLimitSats(destination.ScriptPubKey)} sat).");

        if (!account.IsWatchOnly)
        {
            if (!builder.Verify(tx, out TransactionPolicyError[] errors))
                throw new WalletSpendException(
                    "Invalid transaction: " + string.Join("; ", errors.Select(e => e.ToString())));
        }

        return new BuiltTransaction
        {
            Transaction = tx,
            Fee = GetFee(tx, coins),
            FeeRate = feeRate,
            Signed = !account.IsWatchOnly,
            TxidIsFinal = !account.IsWatchOnly || account.Kind == ScriptKind.NativeSegwit,
            // Built from the very transaction returned above. A second BuildPSBT run would assemble
            // another transaction (the builder shuffles output order), so the txid shown to the user
            // would not be the one of the PSBT that gets signed.
            Psbt = WithOrigins(WithPreviousTransactions(
                ToPsbt(tx, coins, signingKeys), transactions), selectedUtxos, changeIndex),
        };
    }

    /// <summary>
    /// The coin for a UTXO. A P2SH-wrapped segwit output (ScriptKind.WrappedSegwit) needs its
    /// redeem script (the P2WPKH of the key) attached: without it NBitcoin cannot estimate the
    /// input size nor sign it. It comes from the public key, so watch-only accounts get it too.
    /// </summary>
    private Coin CoinFor(CachedUtxo u, IReadOnlyDictionary<string, Transaction> transactions)
    {
        var coin = new Coin(
            new OutPoint(uint256.Parse(u.Txid), (uint)u.Vout),
            transactions[u.Txid].Outputs[u.Vout]);
        return account.Kind == ScriptKind.WrappedSegwit
            && account.GetPublicKey(u.IsChange, u.AddressIndex) is { } key
            ? coin.ToScriptCoin(key.WitHash.ScriptPubKey)
            : coin;
    }

    /// <summary>The PSBT of <paramref name="tx"/> (signatures stripped, then re-added with the keys, if any).</summary>
    private PSBT ToPsbt(Transaction tx, IReadOnlyList<Coin> coins, Key[] signingKeys)
    {
        var unsigned = tx.Clone();
        unsigned.RemoveSignatures();
        var psbt = PSBT.FromTransaction(unsigned, Network).AddCoins([.. coins]);
        return signingKeys.Length > 0 ? psbt.SignWithKeys(signingKeys) : psbt;
    }

    /// <summary>
    /// Attaches to every input the full transaction it spends. Legacy inputs need it (BIP174), and
    /// for segwit inputs it lets an offline signer check the amount it is about to sign: a bare
    /// witness_utxo amount cannot be verified, and a lying one turns into fee.
    /// </summary>
    private static PSBT WithPreviousTransactions(PSBT psbt, IReadOnlyDictionary<string, Transaction> transactions)
    {
        foreach (var input in psbt.Inputs)
            if (input.NonWitnessUtxo is null
                && transactions.TryGetValue(input.PrevOut.Hash.ToString(), out var previous))
                input.NonWitnessUtxo = previous;
        return psbt;
    }

    /// <summary>
    /// Adds the BIP32 origin of every key the transaction involves (inputs and the change output),
    /// plus the account xpub, so an external signer can recognise its keys and the change. Only when
    /// the master fingerprint is really known: a made-up origin would mislead a signer, so a wallet
    /// imported from a bare xpub (no fingerprint given) leaves the PSBT without it.
    /// </summary>
    private PSBT WithOrigins(PSBT psbt, IReadOnlyList<CachedUtxo> inputs, int changeIndex)
    {
        if (account is not HdAccount { HasKnownOrigin: true } hd)
            return psbt;

        var wanted = inputs.Select(u => (u.IsChange, Index: u.AddressIndex))
            .Append((IsChange: true, Index: changeIndex))
            .Distinct();
        foreach (var (isChange, index) in wanted)
        {
            var key = hd.GetPublicKey(isChange, index);
            if (key is null) continue;
            var path = hd.AccountPath.Derive(DerivationPaths.AddressSubPath(isChange, index));
            psbt.AddKeyPath(key, new RootedKeyPath(hd.MasterFingerprint, path),
                hd.GetAddress(isChange, index).ScriptPubKey);
        }
        psbt.GlobalXPubs[hd.AccountXpub.GetWif(Network)] = new RootedKeyPath(hd.MasterFingerprint, hd.AccountPath);
        return psbt;
    }

    private static string BuildTooLargeMessage(
        int selectedInputCount,
        int totalSpendableCount,
        bool sendAll,
        int? actualVirtualSize = null)
    {
        var prefix = sendAll
            ? "Send-all cannot fit in one standard transaction"
            : "Transaction cannot fit in one standard transaction";
        var size = actualVirtualSize is { } vsize ? $"{vsize} vB exceeds" : "Estimated size exceeds";

        return $"{prefix}: {size} the {MaxStandardTransactionVirtualSize} vB standard relay limit " +
            $"with {selectedInputCount}/{totalSpendableCount} spendable input(s). Send a smaller amount or consolidate in multiple smaller transactions.";
    }

    /// <summary>
    /// Smallest payment output worth relaying, computed as Bitcoin Core does: the cost of spending
    /// the output (serialized output plus the input that would spend it) at the 3 sat/vB dust
    /// relay rate. That gives 546 sat for P2PKH and 294 sat for P2WPKH. Palladium's relay policy is
    /// assumed to match; a node with a different policy would reject a transaction that passes here.
    /// </summary>
    internal static long DustLimitSats(Script scriptPubKey)
    {
        const int DustRelaySatPerVByte = 3;
        var script = scriptPubKey.ToBytes();
        var outputSize = 8 + 1 + script.Length; // value, script length (single byte), script
        // Witness program: version opcode (OP_0 or OP_1..OP_16) then one push of the whole program.
        var isWitness = script.Length is >= 4 and <= 42
            && (script[0] == 0x00 || script[0] is >= 0x51 and <= 0x60)
            && script[1] == script.Length - 2;
        var inputSize = isWitness ? 67 : 148;
        return DustRelaySatPerVByte * (outputSize + inputSize);
    }

    private static Money GetFee(Transaction tx, IReadOnlyList<Coin> coins)
    {
        var spentOutpoints = tx.Inputs.Select(i => i.PrevOut).ToHashSet();
        var inputSum = coins.Where(c => spentOutpoints.Contains(c.Outpoint))
            .Sum(c => (Money)c.Amount);
        return inputSum - tx.Outputs.Sum(o => o.Value);
    }

    private sealed class TransactionTooLargeException(string message) : Exception(message);
}

/// <summary>Error during transaction construction/signing (funds, policy, parameters).</summary>
public sealed class WalletSpendException(string message) : Exception(message);
