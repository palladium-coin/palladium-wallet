using NBitcoin;
using PalladiumWallet.Core.Net;
using PalladiumWallet.Core.Spv;

namespace PalladiumWallet.Core.Wallet;

/// <summary>An input of a transaction, with the spent output resolved from the server.</summary>
/// <param name="CoinbaseTag">
/// Printable ASCII runs (e.g. pool tags like "/slush/") extracted from the coinbase
/// scriptSig; null for non-coinbase inputs or if no printable text was found.
/// </param>
public sealed record TxInputInfo(
    string PrevTxid, uint PrevIndex, long? AmountSats, string? Address, bool IsMine, bool IsCoinbase,
    string? CoinbaseTag = null);

/// <summary>An output of a transaction.</summary>
/// <param name="OpReturnText">Decoded OP_RETURN payload (UTF-8, or hex if not valid text); null otherwise.</param>
public sealed record TxOutputInfo(
    uint Index, long AmountSats, string? Address, string ScriptType, string? OpReturnText, bool IsMine);

/// <summary>
/// Complete transaction data assembled by querying the server: the raw transaction
/// plus the outputs spent by each input (to derive amounts, addresses, and fees)
/// and the block header (for the timestamp). Everything the ElectrumX-like protocol
/// (§10) can tell us about a transaction.
/// </summary>
public sealed class TransactionDetails
{
    public required string Txid { get; init; }
    /// <summary>Block height; ≤0 = still in mempool.</summary>
    public required int Height { get; init; }
    public required int Confirmations { get; init; }
    /// <summary>Net effect on the wallet balance (delta computed during sync).</summary>
    public required long NetSats { get; init; }
    /// <summary>Transaction fee; null if any input amount cannot be resolved (e.g. coinbase).</summary>
    public required long? FeeSats { get; init; }
    public required int TotalSize { get; init; }
    public required int VirtualSize { get; init; }
    public required uint Version { get; init; }
    public required uint LockTime { get; init; }
    public required bool RbfSignaled { get; init; }
    /// <summary>Merkle proof verified during sync (§7.4).</summary>
    public required bool Verified { get; init; }
    public required DateTimeOffset? BlockTime { get; init; }
    public required long TotalOutSats { get; init; }
    public required long? TotalInSats { get; init; }
    public required IReadOnlyList<TxInputInfo> Inputs { get; init; }
    public required IReadOnlyList<TxOutputInfo> Outputs { get; init; }

    public bool IsCoinbase => Inputs.Count > 0 && Inputs[0].IsCoinbase;
    public bool IsIncoming => NetSats >= 0;
    /// <summary>Amount sent to external recipients (outputs not ours): the "sent" amount.</summary>
    public long SentToOthersSats => Outputs.Where(o => !o.IsMine).Sum(o => o.AmountSats);
    public long ReceivedSats => Outputs.Where(o => o.IsMine).Sum(o => o.AmountSats);
    public double? FeeRateSatPerVb => FeeSats is { } f && VirtualSize > 0 ? (double)f / VirtualSize : null;
    /// <summary>
    /// Counterparty addresses: external recipients for a send (outputs not ours),
    /// external senders for a receive (inputs not ours).
    /// </summary>
    public IReadOnlyList<string> CounterpartyAddresses => IsIncoming
        ? [.. Inputs.Where(i => !i.IsMine && i.Address is not null).Select(i => i.Address!).Distinct()]
        : [.. Outputs.Where(o => !o.IsMine && o.Address is not null).Select(o => o.Address!).Distinct()];
}

/// <summary>
/// Fetches all data for a single transaction from the server (blueprint §10):
/// the raw transaction and the outputs spent by its inputs, to reconstruct
/// amounts, fees, and addresses that the server does not summarise.
/// </summary>
public static class TransactionInspector
{
    public static async Task<TransactionDetails> FetchAsync(
        ElectrumClient client, Network network, string txid, int tipHeight, int height,
        IReadOnlySet<string> ownedAddresses, long netSats, bool verified,
        IReadOnlyDictionary<string, Transaction>? cache = null,
        CancellationToken ct = default)
    {
        async Task<Transaction> GetTx(string id)
        {
            if (cache is not null && cache.TryGetValue(id, out var hit))
                return hit;
            return await client.GetVerifiedTransactionAsync(id, network, ct);
        }

        async Task<Transaction?> GetTxOrNull(string id)
        {
            try { return await GetTx(id); }
            catch { return null; }
        }

        async Task<DateTimeOffset?> GetBlockTimeOrNull()
        {
            try
            {
                var header = BlockHeaderInfo.Parse(await client.GetBlockHeaderAsync(height, ct));
                return DateTimeOffset.FromUnixTimeSeconds(header.Timestamp);
            }
            catch { return null; }
        }

        string? AddrOf(Script s)
        {
            try { return s.GetDestinationAddress(network)?.ToString(); }
            catch { return null; }
        }

        var tx = await GetTx(txid);

        var outputs = new List<TxOutputInfo>(tx.Outputs.Count);
        for (var i = 0; i < tx.Outputs.Count; i++)
        {
            var o = tx.Outputs[i];
            var addr = AddrOf(o.ScriptPubKey);
            var opReturn = addr is null ? OpReturnTextOf(o.ScriptPubKey) : null;
            outputs.Add(new TxOutputInfo(
                (uint)i, o.Value.Satoshi, addr, ScriptType(o.ScriptPubKey), opReturn,
                addr is not null && ownedAddresses.Contains(addr)));
        }

        var rbf = tx.Inputs.Any(i => i.Sequence.IsRBF);

        // Input transactions are needed for amounts/addresses/fees. Fetched in
        // parallel (unique ids, concurrent requests supported by ElectrumClient):
        // sequential fetching costs one round-trip per input. The block header
        // is also fetched in parallel.
        var prevTxids = tx.IsCoinBase
            ? []
            : tx.Inputs.Select(i => i.PrevOut.Hash.ToString()).Distinct().ToList();

        var prevFetch = prevTxids.ToDictionary(id => id, id => GetTxOrNull(id));
        var headerTask = height > 0 ? GetBlockTimeOrNull() : Task.FromResult<DateTimeOffset?>(null);
        await Task.WhenAll(prevFetch.Values.Cast<Task>().Append(headerTask));

        var prevTxs = prevFetch.ToDictionary(kv => kv.Key, kv => kv.Value.Result);
        var blockTime = await headerTask;

        var inputs = new List<TxInputInfo>(tx.Inputs.Count);
        var feeKnown = !tx.IsCoinBase;
        long inSum = 0;
        foreach (var inp in tx.Inputs)
        {
            if (tx.IsCoinBase)
            {
                inputs.Add(new TxInputInfo("", inp.PrevOut.N, null, null, false, true, CoinbaseTagOf(inp.ScriptSig.ToBytes())));
                continue;
            }

            long? amt = null;
            string? addr = null;
            var prev = prevTxs.GetValueOrDefault(inp.PrevOut.Hash.ToString());
            if (prev is not null && inp.PrevOut.N < prev.Outputs.Count)
            {
                var po = prev.Outputs[(int)inp.PrevOut.N];
                amt = po.Value.Satoshi;
                addr = AddrOf(po.ScriptPubKey);
                inSum += po.Value.Satoshi;
            }
            else feeKnown = false;

            inputs.Add(new TxInputInfo(
                inp.PrevOut.Hash.ToString(), inp.PrevOut.N, amt, addr,
                addr is not null && ownedAddresses.Contains(addr), false));
        }

        var outSum = tx.Outputs.Sum(o => o.Value.Satoshi);

        return new TransactionDetails
        {
            Txid = txid,
            Height = height,
            Confirmations = height > 0 && tipHeight >= height ? tipHeight - height + 1 : 0,
            NetSats = netSats,
            FeeSats = feeKnown ? inSum - outSum : null,
            TotalSize = tx.ToBytes().Length,
            VirtualSize = tx.GetVirtualSize(),
            Version = tx.Version,
            LockTime = tx.LockTime.Value,
            RbfSignaled = rbf,
            Verified = verified,
            BlockTime = blockTime,
            TotalOutSats = outSum,
            TotalInSats = feeKnown ? inSum : null,
            Inputs = inputs,
            Outputs = outputs,
        };
    }

    private static string ScriptType(Script script)
    {
        try
        {
            var t = StandardScripts.GetTemplateFromScriptPubKey(script);
            return t is null
                ? "nonstandard"
                : t.GetType().Name.Replace("PayTo", "").Replace("Template", "");
        }
        catch { return "—"; }
    }

    /// <summary>Decodes an OP_RETURN output's pushed data as UTF-8 text, falling back to hex if it isn't valid text.</summary>
    private static string? OpReturnTextOf(Script script)
    {
        if (!script.IsUnspendable) return null;
        try
        {
            var data = script.ToOps().Skip(1)
                .Where(op => op.PushData is { Length: > 0 })
                .SelectMany(op => op.PushData)
                .ToArray();
            return data.Length == 0 ? null : DecodeUtf8OrHex(data);
        }
        catch { return null; }
    }

    private static string DecodeUtf8OrHex(byte[] data)
    {
        var text = System.Text.Encoding.UTF8.GetString(data);
        var looksLikeText = !text.Contains('�') && text.All(c => !char.IsControl(c) || c is '\n' or '\r' or '\t');
        return looksLikeText ? text : "0x" + Convert.ToHexString(data);
    }

    /// <summary>
    /// Extracts printable-ASCII runs (≥4 chars) from a coinbase scriptSig, e.g. pool
    /// tags like "/slush/" embedded among the binary BIP34 height and extranonce.
    /// </summary>
    private static string? CoinbaseTagOf(byte[] scriptSig)
    {
        var runs = new List<string>();
        var run = new System.Text.StringBuilder();
        void Flush()
        {
            if (run.Length >= 4) runs.Add(run.ToString());
            run.Clear();
        }
        foreach (var b in scriptSig)
        {
            if (b is >= 0x20 and <= 0x7E) run.Append((char)b);
            else Flush();
        }
        Flush();
        return runs.Count == 0 ? null : string.Join(" ", runs);
    }
}
