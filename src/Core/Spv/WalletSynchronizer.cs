using System.Collections.Concurrent;
using System.Threading;
using NBitcoin;
using PalladiumWallet.Core.Chain;
using PalladiumWallet.Core.Crypto;
using PalladiumWallet.Core.Net;
using PalladiumWallet.Core.Storage;
using PalladiumWallet.Core.Wallet;

namespace PalladiumWallet.Core.Spv;

/// <summary>Address derived and tracked during synchronisation.</summary>
public sealed record TrackedAddress(
    BitcoinAddress Address, string ScriptHash, bool IsChange, int Index)
{
    public Script ScriptPubKey => Address.ScriptPubKey;
}

/// <summary>Result of a synchronisation pass.</summary>
public sealed class SyncResult
{
    public required int TipHeight { get; init; }
    public required long ConfirmedSats { get; init; }
    public required long UnconfirmedSats { get; init; }

    /// <summary>
    /// Confirmed but not yet spendable: coinbase outputs below maturity or regular
    /// outputs below <see cref="Chain.ChainProfile.MinConfirmations"/>. Subset of
    /// <see cref="ConfirmedSats"/>.
    /// </summary>
    public required long ImmatureSats { get; init; }

    /// <summary>
    /// Confirmed and past its threshold, but not yet spendable because its Merkle proof
    /// hasn't been checked yet (§7.4 progressive verification catching up in the background).
    /// Subset of <see cref="ConfirmedSats"/> — NOT disjoint from <see cref="ImmatureSats"/>
    /// (an immature coinbase can also be unverified), so never subtract both from
    /// <see cref="ConfirmedSats"/> to get a spendable total — use <see cref="SpendableSats"/>.
    /// </summary>
    public required long PendingVerificationSats { get; init; }

    /// <summary>
    /// Sum of UTXOs that actually pass <see cref="Wallet.UtxoSpendability.IsSpendable"/> right
    /// now — the true spendable balance. Computed directly from the same gate coin selection
    /// uses, rather than by subtracting <see cref="ImmatureSats"/>/<see cref="PendingVerificationSats"/>
    /// from <see cref="ConfirmedSats"/>, since those two can overlap.
    /// </summary>
    public required long SpendableSats { get; init; }
    public required int NextReceiveIndex { get; init; }
    public required int NextChangeIndex { get; init; }
    public required IReadOnlyList<CachedTx> History { get; init; }
    public required IReadOnlyList<CachedUtxo> Utxos { get; init; }
    public required IReadOnlyList<TrackedAddress> Addresses { get; init; }
    public required IReadOnlyList<CachedAddress> AddressRows { get; init; }
    public required IReadOnlyDictionary<string, Transaction> Transactions { get; init; }
}

/// <summary>
/// Wallet synchronisation (blueprint §7.4).
/// </summary>
public sealed class WalletSynchronizer(IWalletAccount account, ElectrumClient client, int gapLimit = 20)
{
    /// <summary>Human-readable progress (for CLI and GUI status bar).</summary>
    public event Action<string>? Progress;

    /// <summary>
    /// Fires with a fresh, self-consistent snapshot as soon as transaction downloads finish
    /// (Merkle proofs may still be pending — see <see cref="CachedTx.Verified"/>/
    /// <see cref="CachedUtxo.Verified"/>) and again periodically as background verification
    /// progresses (§7.4). The wallet is usable after the first firing instead of waiting for
    /// every historical proof to be checked; <see cref="SyncOnceAsync"/>'s returned Task still
    /// only completes once verification is fully done, for callers that need the final state.
    /// </summary>
    public event Action<SyncResult>? PartialResult;

    private readonly ConcurrentDictionary<string, Transaction> _txCache = new();

    // txids known confirmed (height > 0) as of download time, independent of whether their
    // Merkle proof has been verified yet — lets ExportCaches persist raw tx bytes for a
    // confirmed transaction interrupted before verification, instead of forcing a
    // re-download on the next sync just because _verifiedAtHeight hasn't caught up. Every
    // entry here has txHeights[txid] > 0 at the time it was recorded, i.e. server-confirmed;
    // unconfirmed (mempool/RBF-able) transactions are deliberately never added.
    private readonly ConcurrentDictionary<string, byte> _confirmedTxids = new();

    // Concurrent: written incrementally by individual merkle-verification tasks as they
    // complete (§7.4 progressive verification), not just once after they all finish.
    private readonly ConcurrentDictionary<string, int> _verifiedAtHeight = new();
    private readonly ConcurrentDictionary<int, Task<string>> _headerFetches = new();

    // checkpoint height -> lowest height already proven to hash-chain UP to that checkpoint.
    // Each anchored header must reach a hardcoded hash above it: SHA-256 preimage resistance
    // is what makes it unforgeable (the opposite direction, from a checkpoint up to the header,
    // proves nothing when PoW is skipped, since anyone can build such a chain). Persisted across
    // sessions (see ExportCaches/PreloadCaches): without it, every restart re-walks and
    // re-verifies the header chain even though the header bytes themselves are cached.
    private readonly ConcurrentDictionary<int, int> _anchoredDownTo = new();

    // Serializes header-range downloads: concurrent AnchorToCheckpointAsync calls (one per tx
    // being verified) would otherwise race on overlapping ranges and issue duplicate range
    // requests. Fetches are network-bound and few (batches of up to 2016 headers), so
    // serializing them costs nothing that matters.
    private readonly SemaphoreSlim _headerRangeLock = new(1, 1);

    // Max headers requested per blockchain.block.headers call. The server may return fewer
    // (its own configured cap) — FetchHeaderRangeAsync loops on the actual count returned.
    private const int HeaderBatchSize = 2016;

    // Indices known from the previous sync: used by ScanChainAsync for incremental
    // discovery — already-used addresses are fetched in a single burst instead of
    // sequential batches, reducing round-trips from O(used/gapLimit) to O(1).
    private int _knownReceiveIndex;
    private int _knownChangeIndex;

    /// <summary>
    /// Pre-populates internal caches from data saved on disk.
    /// Call before SyncOnceAsync to avoid re-downloading already known transactions.
    /// </summary>
    public void PreloadCaches(
        Dictionary<string, string> rawTxHex,
        Dictionary<string, int> verifiedAt,
        Dictionary<int, string>? blockHeaders,
        int knownReceiveIndex,
        int knownChangeIndex,
        Network network,
        Dictionary<int, int>? anchoredDownTo = null)
    {
        foreach (var (txid, hex) in rawTxHex)
        {
            _txCache.TryAdd(txid, Transaction.Parse(hex, network));
            // ExportCaches only ever wrote confirmed transactions here (see its own
            // filter), so every preloaded entry is safe to mark confirmed too.
            _confirmedTxids.TryAdd(txid, 0);
        }
        // A cache written before the anchoring direction was fixed (ADR 0040) has no
        // anchoredDownTo: its "verified" claims rested on a forgeable check, so they are not
        // trusted and every proof is re-verified (the cached headers are reused, not refetched).
        if (anchoredDownTo is not null)
            foreach (var (txid, height) in verifiedAt)
                if (!_verifiedAtHeight.ContainsKey(txid))
                    _verifiedAtHeight[txid] = height;
        if (blockHeaders is not null)
            foreach (var (height, hex) in blockHeaders)
                _headerFetches.TryAdd(height, Task.FromResult(hex));
        // Only trust a preloaded anchor down to a height whose header is also cached: if the
        // header cache was cleared/corrupted independently, re-deriving the chain-of-hashes
        // check on next use (AnchorToCheckpointAsync re-fetches what's missing) is safer than
        // trusting a stale "already validated" claim against headers that may no longer match.
        if (anchoredDownTo is not null)
            foreach (var (checkpointHeight, downToHeight) in anchoredDownTo)
                if (downToHeight <= checkpointHeight
                    && _headerFetches.ContainsKey(downToHeight)
                    && _headerFetches.ContainsKey(checkpointHeight))
                    _anchoredDownTo.AddOrUpdate(checkpointHeight, downToHeight,
                        (_, existing) => Math.Min(existing, downToHeight));
        _knownReceiveIndex = knownReceiveIndex;
        _knownChangeIndex  = knownChangeIndex;
    }

    /// <summary>
    /// Exports the current caches in a serialisable form for disk storage.
    /// Only confirmed transactions (height > 0) are included: unconfirmed ones
    /// may change (RBF) and must always be re-downloaded.
    /// </summary>
    public (Dictionary<string, string> RawTxHex,
            Dictionary<string, int> VerifiedAt,
            Dictionary<int, string> BlockHeaders,
            Dictionary<int, int> AnchoredDownTo)
        ExportCaches(Network network)
    {
        var rawHex = _confirmedTxids.Keys
            .Where(_txCache.ContainsKey)
            .ToDictionary(txid => txid, txid => _txCache[txid].ToHex());

        // Only already-completed headers: in-progress Task<string> instances
        // are not persisted (they will be re-fetched on the next sync if needed).
        var headers = new Dictionary<int, string>();
        foreach (var (height, task) in _headerFetches)
            if (task.IsCompletedSuccessfully)
                headers[height] = task.Result;

        return (rawHex, new Dictionary<string, int>(_verifiedAtHeight), headers,
            new Dictionary<int, int>(_anchoredDownTo));
    }

    public async Task<SyncResult> SyncOnceAsync(CancellationToken ct = default)
    {
        var tip = await client.SubscribeHeadersAsync(ct);
        Progress?.Invoke($"chain tip: {tip.Height}");

        // 1-2. Address scanning.
        var tracked = new List<TrackedAddress>();
        var historyByAddress = new Dictionary<string, IReadOnlyList<HistoryItem>>();
        int nextReceive, nextChange;

        if (account.FixedAddresses is { } fixedAddresses)
        {
            foreach (var (addr, isChange, idx) in fixedAddresses)
                tracked.Add(new TrackedAddress(addr, Scripthash.FromAddress(addr), isChange, idx));
            nextReceive = tracked.Count(t => !t.IsChange);
            nextChange  = 0;

            await Task.WhenAll(tracked.Select(t => RetryOnBusyAsync(async () =>
            {
                var h = await client.GetHistoryAsync(t.ScriptHash, ct);
                if (h.Count > 0) historyByAddress[t.ScriptHash] = h;
            }, ct)).Concat(tracked.Select(t =>
                RetryOnBusyAsync(() => client.SubscribeScripthashAsync(t.ScriptHash, ct), ct))));
        }
        else
        {
            // Receive and change chains in parallel (independent by definition).
            // ScanChainAsync uses _knownReceiveIndex/_knownChangeIndex for incremental
            // discovery: already-used addresses are fetched in a single burst.
            var receiveTask = ScanChainAsync(isChange: false, _knownReceiveIndex, ct);
            var changeTask  = ScanChainAsync(isChange: true,  _knownChangeIndex,  ct);
            var rxScan = await receiveTask;
            var chScan = await changeTask;

            tracked.AddRange(rxScan.Tracked);
            tracked.AddRange(chScan.Tracked);
            foreach (var (k, v) in rxScan.History) historyByAddress[k] = v;
            foreach (var (k, v) in chScan.History) historyByAddress[k] = v;
            nextReceive = rxScan.NextIndex;
            nextChange  = chScan.NextIndex;

            var gapAddresses = tracked.Where(t =>
                (!t.IsChange && t.Index >= nextReceive && t.Index < nextReceive + gapLimit) ||
                ( t.IsChange && t.Index >= nextChange  && t.Index < nextChange  + gapLimit)).ToList();
            if (gapAddresses.Count > 0)
                await Task.WhenAll(gapAddresses.Select(t =>
                    RetryOnBusyAsync(() => client.SubscribeScripthashAsync(t.ScriptHash, ct), ct)));
        }

        // 3. Merged history (txid → highest reported height).
        var txHeights = new Dictionary<string, int>();
        foreach (var item in historyByAddress.Values.SelectMany(h => h))
            txHeights[item.TxHash] = item.Height;

        // 4. Download missing transactions — needed to compute amounts/UTXOs locally.
        // Merkle-proof verification (5) is deliberately NOT awaited together with this: on a
        // wallet with thousands of transactions, downloads finish in seconds while proofs can
        // take much longer over a high-latency link, and the wallet has everything it needs to
        // show balance/history the moment downloads are done. Verification then continues in
        // the background (§7.4 progressive verification), firing PartialResult as proofs land,
        // while coin selection stays locked out of any UTXO until its own proof is checked
        // (CachedUtxo.Verified, enforced in UtxoSpendability.IsSpendable) — a malicious server
        // cannot get a fabricated balance spent just because it was shown early.
        var network  = PalladiumNetworks.For(account.Profile.Kind);
        var missing  = txHeights.Keys.Where(txid => !_txCache.ContainsKey(txid)).ToList();
        var toVerify = txHeights
            .Where(kv => kv.Value > 0
                && (!_verifiedAtHeight.TryGetValue(kv.Key, out var h) || h != kv.Value))
            .ToList();

        // Total/already-cached counts (not just this session's downloads): on a sync resumed
        // after an interruption, `missing` is often empty because everything was already
        // fetched last time (see _confirmedTxids/PreloadCaches) — reporting against the total
        // shows "n/n transactions" immediately instead of a misleading "0/0" before jumping
        // straight to proof verification.
        var totalTx       = txHeights.Count;
        var alreadyCached = totalTx - missing.Count;

        string DownloadVerifyStatus(int downloaded, int verified) =>
            $"transactions {downloaded}/{totalTx}, proofs {verified}/{toVerify.Count}…";

        if (missing.Count > 0 || toVerify.Count > 0)
            Progress?.Invoke(DownloadVerifyStatus(alreadyCached, 0));

        var dlDone = 0;
        await Task.WhenAll(missing.Select(txid => RetryOnBusyAsync(async () =>
        {
            var raw = await client.GetTransactionAsync(txid, ct);
            _txCache[txid] = Transaction.Parse(raw, network);
            if (txHeights[txid] > 0)
                _confirmedTxids.TryAdd(txid, 0);
            var n = Interlocked.Increment(ref dlDone);
            if (n % 50 == 0 || n == missing.Count)
                Progress?.Invoke(DownloadVerifyStatus(alreadyCached + n, 0));
        }, ct)));

        SyncResult BuildSnapshot() =>
            BuildResult(tip.Height, tracked, historyByAddress, txHeights, nextReceive, nextChange);

        PartialResult?.Invoke(BuildSnapshot());

        var merkDone = 0;
        var merkTasks = toVerify.Select(kv => RetryOnBusyAsync(async () =>
        {
            var (txid, height) = kv;
            var proofTask = client.GetMerkleAsync(txid, height, ct);
            // Anchor first: on a checkpointed height this fills _headerFetches[height]
            // via the batched range fetch (§7.3), so the header lookup below is a cache
            // hit instead of a second individual blockchain.block.header RPC per tx —
            // halves round-trips for this stage on mainnet, where it matters most on
            // high-latency mobile links. Falls back to an individual fetch when no
            // checkpoint covers this height (testnet/regtest today).
            await AnchorToCheckpointAsync(height, ct);
            var headerHex = await _headerFetches.GetOrAdd(height, h => client.GetBlockHeaderAsync(h, ct));
            var header    = BlockHeaderInfo.Parse(headerHex);
            var proof     = await proofTask;
            if (!MerkleProof.Verify(
                    uint256.Parse(txid), proof.Pos,
                    proof.Merkle.Select(uint256.Parse), header.MerkleRoot))
                throw new SpvVerificationException(
                    $"Invalid Merkle proof for {txid} (block {height}): server is not trustworthy.");
            _verifiedAtHeight[txid] = height;
            var n = Interlocked.Increment(ref merkDone);
            if (n % 50 == 0 || n == toVerify.Count)
                Progress?.Invoke(DownloadVerifyStatus(totalTx, n));
            if (n % PartialResultBatchSize == 0)
                PartialResult?.Invoke(BuildSnapshot());
        }, ct)).ToList();

        if (merkTasks.Count > 0)
            await Task.WhenAll(merkTasks);

        return BuildSnapshot();
    }

    // Rebuilding the full snapshot (UTXOs/history/address rows) is O(wallet size); firing it on
    // every single verified proof would make the background verification phase itself O(n²) for
    // a wallet with thousands of transactions. Batching keeps "verified" badges catching up
    // visibly without that cost.
    private const int PartialResultBatchSize = 200;

    private bool IsTxVerified(string txid, int height) =>
        height <= 0 || (_verifiedAtHeight.TryGetValue(txid, out var vh) && vh == height);

    /// <summary>
    /// Assembles a <see cref="SyncResult"/> from the current state of <see cref="_txCache"/> and
    /// <see cref="_verifiedAtHeight"/>. Callable multiple times per sync (§7.4): once as soon as
    /// transaction downloads finish (proofs still pending), and again as verification progresses,
    /// each time reflecting whichever transactions have been proof-checked so far.
    /// </summary>
    private SyncResult BuildResult(
        int tipHeight,
        List<TrackedAddress> tracked,
        Dictionary<string, IReadOnlyList<HistoryItem>> historyByAddress,
        Dictionary<string, int> txHeights,
        int nextReceive,
        int nextChange)
    {
        var transactions = txHeights.Keys.ToDictionary(txid => txid, txid => _txCache[txid]);

        // 6. Local UTXO reconstruction.
        var byScript = tracked.ToDictionary(t => t.ScriptPubKey, t => t);
        var spent = transactions.Values
            .SelectMany(tx => tx.Inputs)
            .Select(i => i.PrevOut)
            .ToHashSet();

        var utxos = new List<CachedUtxo>();
        foreach (var (txid, tx) in transactions)
        {
            var height = txHeights[txid];
            var verifiedTx = IsTxVerified(txid, height);
            for (var vout = 0; vout < tx.Outputs.Count; vout++)
            {
                var output = tx.Outputs[vout];
                if (!byScript.TryGetValue(output.ScriptPubKey, out var addr))
                    continue;
                if (spent.Contains(new OutPoint(tx, vout)))
                    continue;
                utxos.Add(new CachedUtxo
                {
                    Txid          = txid,
                    Vout          = vout,
                    ValueSats     = output.Value.Satoshi,
                    Address       = addr.Address.ToString(),
                    IsChange      = addr.IsChange,
                    AddressIndex  = addr.Index,
                    Height        = height,
                    IsCoinbase    = tx.IsCoinBase,
                    Verified      = verifiedTx,
                });
            }
        }

        // 7. Delta per history entry.
        var history = new List<CachedTx>();
        foreach (var (txid, tx) in transactions)
        {
            var height = txHeights[txid];
            var received = tx.Outputs
                .Where(o => byScript.ContainsKey(o.ScriptPubKey))
                .Sum(o => o.Value.Satoshi);
            var sentSats = tx.Inputs
                .Where(i => transactions.TryGetValue(i.PrevOut.Hash.ToString(), out var prev)
                    && byScript.ContainsKey(prev.Outputs[i.PrevOut.N].ScriptPubKey))
                .Sum(i => transactions[i.PrevOut.Hash.ToString()].Outputs[i.PrevOut.N].Value.Satoshi);
            history.Add(new CachedTx
            {
                Txid      = txid,
                Height    = height,
                DeltaSats = received - sentSats,
                Verified  = IsTxVerified(txid, height),
            });
        }
        history.Sort((a, b) =>
        {
            var ha = a.Height <= 0 ? int.MaxValue : a.Height;
            var hb = b.Height <= 0 ? int.MaxValue : b.Height;
            return hb.CompareTo(ha);
        });

        var balanceByAddress = utxos
            .GroupBy(u => u.Address)
            .ToDictionary(g => g.Key, g => g.Sum(u => u.ValueSats));
        var addressRows = tracked
            .OrderBy(t => t.IsChange).ThenBy(t => t.Index)
            .Select(t => new CachedAddress
            {
                Address      = t.Address.ToString(),
                IsChange     = t.IsChange,
                Index        = t.Index,
                BalanceSats  = balanceByAddress.GetValueOrDefault(t.Address.ToString()),
                TxCount      = historyByAddress.TryGetValue(t.ScriptHash, out var h) ? h.Count : 0,
            })
            .ToList();

        return new SyncResult
        {
            TipHeight        = tipHeight,
            ConfirmedSats    = utxos.Where(u => u.Height > 0).Sum(u => u.ValueSats),
            UnconfirmedSats  = utxos.Where(u => u.Height <= 0).Sum(u => u.ValueSats),
            ImmatureSats     = utxos.Where(u =>
                u.Height > 0 && u.Confirmations(tipHeight) < u.RequiredConfirmations(account.Profile))
                .Sum(u => u.ValueSats),
            PendingVerificationSats = utxos.Where(u => u.Height > 0 && !u.Verified).Sum(u => u.ValueSats),
            SpendableSats    = utxos.Where(u => u.IsSpendable(account.Profile, tipHeight)).Sum(u => u.ValueSats),
            NextReceiveIndex = nextReceive,
            NextChangeIndex  = nextChange,
            History          = history,
            Utxos            = utxos,
            Addresses        = tracked,
            AddressRows      = addressRows,
            Transactions     = transactions,
        };
    }

    /// <summary>
    /// Anchors a header at <paramref name="height"/> to the nearest hardcoded checkpoint at
    /// or below it (§7.3): downloads every intervening header and verifies an unbroken
    /// prev-hash chain from the checkpoint's known-good hash up to this height. Without this,
    /// the Merkle proof above only proves a transaction belongs to *some* header the server
    /// handed over — on this LWMA chain the wallet cannot recompute PoW to catch a forged one,
    /// so the checkpoint is the only fixed point of truth. A no-op when the network profile has
    /// no checkpoint at or below <paramref name="height"/> (e.g. testnet/regtest today, or
    /// mainnet heights below the first checkpoint).
    /// </summary>
    private async Task AnchorToCheckpointAsync(int height, CancellationToken ct)
    {
        var profile = account.Profile;
        // The checkpoint ABOVE the height (lowest one at or above it): the header must hash-chain
        // up to it. Above the last checkpoint there is nothing to anchor to — a documented limit,
        // narrowed by adding a recent checkpoint at every release (SECURITY.md).
        Checkpoint? upper = null;
        foreach (var c in profile.Checkpoints)
            if (c.Height >= height && (upper is not { } best || c.Height < best.Height))
                upper = c;
        if (upper is not { } cp)
            return;

        _anchoredDownTo.TryGetValue(cp.Height, out var anchoredFrom);
        var alreadyAnchored = _anchoredDownTo.ContainsKey(cp.Height);
        if (alreadyAnchored && anchoredFrom <= height)
            return;

        // Headers [height .. top]: top is the checkpoint itself, or the lowest height already
        // proven to reach it (its header is trusted, so only the new segment is walked).
        var top = alreadyAnchored ? anchoredFrom : cp.Height;
        await FetchHeaderRangeAsync(height, top, ct);

        var headers = Enumerable.Range(height, top - height + 1)
            .Select(h => BlockHeaderInfo.Parse(_headerFetches[h].Result))
            .ToArray();

        if (!alreadyAnchored && !headers[^1].MatchesCheckpoint(cp))
            throw new SpvVerificationException(
                $"Header at checkpoint height {cp.Height} does not match the hardcoded hash: server is not trustworthy.");

        for (var i = 1; i < headers.Length; i++)
            if (!headers[i].IsValidChild(headers[i - 1].Hash, profile))
                throw new SpvVerificationException(
                    $"Broken header chain at height {height + i}: server is not trustworthy.");

        _anchoredDownTo.AddOrUpdate(cp.Height, height, (_, existing) => Math.Min(existing, height));
    }

    /// <summary>
    /// Ensures every height in [<paramref name="fromHeight"/>, <paramref name="toHeightInclusive"/>]
    /// is present in <see cref="_headerFetches"/>, downloading gaps with
    /// blockchain.block.headers (§7.3) instead of one blockchain.block.header call per height.
    /// </summary>
    private async Task FetchHeaderRangeAsync(int fromHeight, int toHeightInclusive, CancellationToken ct)
    {
        await _headerRangeLock.WaitAsync(ct);
        try
        {
            var h = fromHeight;
            while (h <= toHeightInclusive)
            {
                if (_headerFetches.ContainsKey(h)) { h++; continue; }

                var requested = Math.Min(HeaderBatchSize, toHeightInclusive - h + 1);
                var range = await client.GetBlockHeadersAsync(h, requested, ct);
                if (range.Count == 0)
                    throw new SpvVerificationException(
                        $"Server returned no headers starting at height {h}: server is not trustworthy.");

                for (var i = 0; i < range.Count; i++)
                {
                    var headerHex = range.Hex.Substring(i * BlockHeaderInfo.Size * 2, BlockHeaderInfo.Size * 2);
                    _headerFetches.TryAdd(h + i, Task.FromResult(headerHex));
                }
                h += range.Count;
            }
        }
        finally
        {
            _headerRangeLock.Release();
        }
    }

    /// <summary>
    /// Scans one chain (receiving or change).
    ///
    /// Phase 1 — known addresses (0..fromIndex-1): all GetHistoryAsync calls are
    /// fired in a single parallel burst, with no sequential batching. A wallet
    /// with 100 used addresses costs 1 RTT instead of 5 sequential gap-limit rounds.
    ///
    /// Phase 2 — discovery from fromIndex onwards: gap-limit batching as before,
    /// required to know when to stop.
    /// </summary>
    private async Task<(int NextIndex,
                         List<TrackedAddress> Tracked,
                         Dictionary<string, IReadOnlyList<HistoryItem>> History)>
        ScanChainAsync(bool isChange, int fromIndex, CancellationToken ct)
    {
        var tracked = new List<TrackedAddress>();
        var history = new Dictionary<string, IReadOnlyList<HistoryItem>>();

        // Phase 1: single burst for all already-known addresses.
        if (fromIndex > 0)
        {
            var known = Enumerable.Range(0, fromIndex).Select(i =>
            {
                var addr = account.GetAddress(isChange, i);
                return new TrackedAddress(addr, Scripthash.FromAddress(addr), isChange, i);
            }).ToList();
            tracked.AddRange(known);

            var knownHistories = await Task.WhenAll(
                known.Select(t => RetryOnBusyAsync(
                    () => client.GetHistoryAsync(t.ScriptHash, ct), ct)));
            for (var i = 0; i < known.Count; i++)
                if (knownHistories[i].Count > 0)
                    history[known[i].ScriptHash] = knownHistories[i];
        }

        // Phase 2: gap-limit discovery from fromIndex onwards.
        var consecutiveEmpty = 0;
        var index      = fromIndex;
        var firstUnused = fromIndex;

        while (consecutiveEmpty < gapLimit)
        {
            var batch = Enumerable.Range(index, gapLimit).Select(i =>
            {
                var addr = account.GetAddress(isChange, i);
                return new TrackedAddress(addr, Scripthash.FromAddress(addr), isChange, i);
            }).ToList();
            index += batch.Count;
            tracked.AddRange(batch);

            var histories = await Task.WhenAll(
                batch.Select(t => RetryOnBusyAsync(
                    () => client.GetHistoryAsync(t.ScriptHash, ct), ct)));

            for (var i = 0; i < batch.Count && consecutiveEmpty < gapLimit; i++)
            {
                if (histories[i].Count == 0)
                {
                    consecutiveEmpty++;
                }
                else
                {
                    consecutiveEmpty = 0;
                    firstUnused = batch[i].Index + 1;
                    history[batch[i].ScriptHash] = histories[i];
                }
            }
        }

        return (firstUnused, tracked, history);
    }

    // Counts "server busy" retries across the whole sync: surfaced via Progress so a slow
    // sync can be diagnosed as server-side throttling (exponential backoff eating the time)
    // rather than guessed at from wall-clock numbers alone.
    private int _busyRetries;

    private async Task RetryOnBusyAsync(Func<Task> op, CancellationToken ct)
    {
        var delay = 200;
        for (var attempt = 0; ; attempt++)
        {
            try   { await op(); return; }
            catch (ElectrumServerException ex)
                when (IsBusy(ex) && attempt < 7)
            {
                ReportBusyRetry(delay);
                await Task.Delay(delay, ct);
                delay = Math.Min(delay * 2, 5_000);
            }
        }
    }

    private async Task<T> RetryOnBusyAsync<T>(Func<Task<T>> op, CancellationToken ct)
    {
        var delay = 200;
        for (var attempt = 0; ; attempt++)
        {
            try   { return await op(); }
            catch (ElectrumServerException ex)
                when (IsBusy(ex) && attempt < 7)
            {
                ReportBusyRetry(delay);
                await Task.Delay(delay, ct);
                delay = Math.Min(delay * 2, 5_000);
            }
        }
    }

    private void ReportBusyRetry(int delayMs)
    {
        var n = Interlocked.Increment(ref _busyRetries);
        if (n == 1 || n % 20 == 0)
            Progress?.Invoke($"server busy, retry #{n} (waiting {delayMs}ms)…");
    }

    private static bool IsBusy(ElectrumServerException ex) =>
        ex.Message.Contains("-102") ||
        ex.Message.Contains("-101") ||
        ex.Message.Contains("server busy", StringComparison.OrdinalIgnoreCase) ||
        ex.Message.Contains("excessive resource usage", StringComparison.OrdinalIgnoreCase);
}

/// <summary>SPV verification failed: server data contradicts the proofs (§17).</summary>
public sealed class SpvVerificationException(string message) : Exception(message);
